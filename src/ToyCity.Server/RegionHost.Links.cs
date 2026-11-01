using System.Net.WebSockets;
using Google.Protobuf;
using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>A tile host running one of this region's cities, as the region's host sees it.</summary>
sealed class TileLink
{
    public required WebSocket Socket;
    public int Index;
    /// <summary>Where players reach the tile host's /ws.</summary>
    public string Address = "";
    public readonly SemaphoreSlim SendLock = new(1, 1);
    /// <summary>When it was last heard from, on the region host's clock.</summary>
    public long LastHeard;
    public volatile bool Dead;
    /// <summary>Its answer for the day being stepped, once it arrives.</summary>
    public volatile P.DayEnd? DayEnd;
    /// <summary>What the region told the city for the day being stepped: given back to the check copy if the tile host never answers.</summary>
    public List<SimAction> Inbound = new();
    /// <summary>The tile host's players, by its number for each connection.</summary>
    public readonly Dictionary<int, Client> Proxies = new();
    /// <summary>Who is on the tile host now, from its last answer.</summary>
    public HashSet<string> Online = new(StringComparer.Ordinal);
    /// <summary>Stands in for the tile host's players when the region map goes out to everyone.</summary>
    public Client? Map;
}

/// <summary>
/// Federated hosting, the region host's side (part 8). A tile host joins over /tile-link with the region's link key and
/// takes one founded city; from then on this host keeps a check copy of that city and no longer runs it for players.
/// Each day it sends the tile host what the region tells the city, waits at a barrier for every tile host's answer
/// (the actions its city applied), replays them on the check copy, and compares state hashes monthly. A tile host that
/// stops answering holds the region; after <see cref="HostConfig.TakeOverSeconds"/> a computer mayor runs its city here
/// until it's back.
/// </summary>
public sealed partial class RegionHost
{
    TileLink?[] _links = Array.Empty<TileLink?>();
    /// <summary>Human mayors whose cities a computer runs here while their tile host is away: given back when it returns.</summary>
    readonly Dictionary<int, string> _coveredMayors = new();
    string _waiting = "", _stopped = "";
    /// <summary>A desync stops the region for good: nothing may start it again until a load.</summary>
    volatile bool _halted;

    int IndexOf(RegionTile t) => t.Row * _region.Cols + t.Col;

    void InitLinks()
    {
        _links = new TileLink?[_region.Tiles.Length];
        _region.IsRemote = w => _region.TileOf(w) is { } t && _links[IndexOf(t)] != null;
    }

    bool OnlineOnTileHosts(string player) => _links.Any(l => l is { Dead: false } && l.Online.Contains(player));

    static async Task SendLinkAsync(TileLink l, P.LeaderMessage m)
    {
        var bytes = m.ToByteArray();
        await l.SendLock.WaitAsync();
        try
        {
            if (l.Socket.State == WebSocketState.Open)
                await l.Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
        finally { l.SendLock.Release(); }
    }

    // ------------------------------------------------------------------
    // One day
    // ------------------------------------------------------------------

    /// <summary>
    /// Steps the region one day: this host's cities here, the tile hosts' on their machines, with a barrier at the end
    /// of the day. <paramref name="after"/> runs with every gate held once the day is done.
    /// </summary>
    List<(Client, P.ServerMessage)> StepDay(Func<List<(Client, P.ServerMessage)>> after)
    {
        if (_halted) return new();
        // The region's own gate is held all day, so nothing region-wide (a founding, a deal, a load) lands mid-day; the
        // cities' gates are let go while waiting for the tile hosts, so players here can still act.
        lock (_gate)
        {
            long tick = _region.Tick;
            var starts = WithAll(() =>
            {
                KeepPace();
                _region.BeginDay();
                var list = new List<(TileLink, P.LeaderMessage)>();
                foreach (var l in _links)
                {
                    if (l == null) continue;
                    var w = _region.Tiles[l.Index].City!;
                    l.DayEnd = null;
                    // Silence counts from the day's start: a tile host has nothing to say between days.
                    l.LastHeard = Math.Max(l.LastHeard, _clock.ElapsedMilliseconds);
                    l.Inbound = w.TakePending();
                    var ds = new P.DayStart { Tick = tick, Speed = _speed };
                    foreach (var a in l.Inbound) ds.Actions.Add(Mapping.Record(new LoggedAction(tick, a)));
                    foreach (var n in w.Notices) ds.Notices.Add(new P.Notice { Text = n.Text, Severity = n.Severity, X = n.X, Y = n.Y });
                    w.Notices.Clear();
                    list.Add((l, new P.LeaderMessage { DayStart = ds }));
                }
                Region.StepCities(Cities.Select(c => c.World).ToList());
                return list;
            });
            foreach (var (l, m) in starts) _ = SendLinkAsync(l, m);
            var late = AwaitDayEnds(tick);
            return WithAll(() =>
            {
                var list = new List<(Client, P.ServerMessage)>();
                foreach (var l in _links)
                {
                    if (l == null) continue;
                    if (late.Contains(l)) { list.AddRange(TakeOver(l)); continue; }
                    if (ReplayDay(l, tick) is { } desync) { list.AddRange(desync); break; }
                }
                if (!_halted) list.AddRange(CheckLedgers(tick));
                _region.EndDay();
                list.AddRange(after());
                return list;
            });
        }
    }

    /// <summary>
    /// Waits for every tile host's answer for the day. A slow one holds the region (and the region map says who it
    /// waits for); one silent past the take-over time is returned, for a computer mayor to run its city here.
    /// </summary>
    List<TileLink> AwaitDayEnds(long tick)
    {
        bool told = false;
        while (_running)
        {
            var missing = _links.Where(l => l != null && (l.DayEnd == null || l.DayEnd.Tick != tick)).Select(l => l!).ToList();
            if (missing.Count == 0) break;
            long now = _clock.ElapsedMilliseconds;
            var gone = missing.Where(l => now - l.LastHeard > _host.TakeOverSeconds * 1000L).ToList();
            if (gone.Count == missing.Count)
            {
                if (told) ClearWaiting();
                return gone;
            }
            if (!told && missing.Any(l => now - l.LastHeard > 2000))
            {
                told = true;
                _waiting = $"Waiting for {string.Join(" and ", missing.Select(l => _region.Tiles[l.Index].Name))}'s host…";
                var outbox = WithAll(() => RegionIfChanged(force: true));
                foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
            }
            Thread.Sleep(5);
        }
        if (told) ClearWaiting();
        return new();
    }

    void ClearWaiting()
    {
        _waiting = "";
        _regionSig = "";
    }

    /// <summary>Plays the tile host's day on the check copy and, on the first of the month, compares the two. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)>? ReplayDay(TileLink l, long tick)
    {
        var t = _region.Tiles[l.Index];
        var w = t.City!;
        var end = l.DayEnd!;
        l.Online = new HashSet<string>(end.Online, StringComparer.Ordinal);
        // Anything the region gave the city since this morning goes with tomorrow's message, not into today.
        var later = w.TakePending();
        foreach (var r in end.Log)
            if (Mapping.Action(r) is { } a) w.Enqueue(a);
        w.Step();
        w.Notices.Clear();
        foreach (var a in later) w.Enqueue(a);
        if (!end.Monthly) return null;
        ulong mine = w.StateHash();
        if (mine == end.Hash) return null;
        return Halt(t, tick, end.Hash, mine);
    }

    /// <summary>
    /// Every day, after the tile hosts' days are replayed: what each city sends across a border its neighbour must
    /// receive. With every city (its own and the check copies) here, the region host can see both sides. A ledger
    /// that doesn't balance stops the region as a desync would. Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> CheckLedgers(long tick)
    {
        if (_region.Unbalanced() is not { } why)
        {
            if (tick % World.DaysPerMonth == 0)
            {
                _ledgerMonths++;
                _crossBorder = _region.Tiles.Where(t => t.City != null).Sum(t => t.City!.Flows.Sum(f => f.CommuteOut + f.ShoppersOut + f.TouristsOut + f.FreightOut));
            }
            return new();
        }
        _halted = true;
        _hostSpeed = 0;
        _asks.Clear();
        Recompute();
        string file = Path.Combine(_saveDir, $"ledger-{tick}.txt");
        try { File.WriteAllLines(file, new[] { $"Border ledgers in {_region.Name} on day {tick} don't balance.", why, "The region stopped here. Load a save to carry on." }); }
        catch (IOException) { }
        _stopped = $"The border ledgers don't balance (day {tick}): {why}. The region has stopped; the report is {Path.GetFileName(file)}.";
        _log.LogError("ledger: {Text}", _stopped);
        var list = Cities.SelectMany(c => c.Notice(_stopped, "warning")).ToList();
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    int _ledgerMonths, _crossBorder;

    /// <summary>A tile host and the check copy disagree: stop the region with a report rather than let it drift. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> Halt(RegionTile t, long tick, ulong theirs, ulong mine)
    {
        _halted = true;
        _hostSpeed = 0;
        _asks.Clear();
        Recompute();
        string file = Path.Combine(_saveDir, $"desync-{t.Col}-{t.Row}-{tick}.txt");
        var w = t.City!;
        var lines = new List<string>
        {
            $"Desync in {_region.Name}: {t.Name} (tile {t.Col},{t.Row}) on day {tick}.",
            $"Its tile host's state hash: {theirs:x16}. The region host's check copy: {mine:x16}.",
            "The region stopped here. Load a save to carry on.",
            "",
            "The city's last actions, as the check copy applied them:",
        };
        lines.AddRange(w.Log.TakeLast(60).Select(a => $"  day {a.Tick}: {a.Action}"));
        try { File.WriteAllLines(file, lines); } catch (IOException) { }
        _stopped = $"{t.Name}'s host and the region's copy of it disagree (day {tick}). The region has stopped; the report is {Path.GetFileName(file)}.";
        _log.LogError("desync: {Text}", _stopped);
        var list = Cities.SelectMany(c => c.Notice(_stopped, "warning")).ToList();
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    /// <summary>
    /// A tile host stopped answering: its city runs here from the check copy, with what the region told it today, and
    /// a computer mayor takes the seat of a mayor who was playing there. Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> TakeOver(TileLink l)
    {
        var t = _region.Tiles[l.Index];
        var w = t.City!;
        _links[l.Index] = null;
        l.Dead = true;
        _ = l.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "taken over", CancellationToken.None).ContinueWith(_ => { });
        var later = w.TakePending();
        foreach (var a in l.Inbound) w.Enqueue(a);
        if (!World.IsComputer(t.Mayor) && t.Mayor.Length > 0)
        {
            _coveredMayors[l.Index] = t.Mayor;
            _region.HandToComputer(t, AutopilotStyle.Profit);
        }
        w.ApplyPending();
        w.Step();
        foreach (var a in later) w.Enqueue(a);
        OpenCities();
        string text = $"{t.Name}'s host stopped answering. {t.Mayor} runs it on this host until it's back.";
        _log.LogWarning("{Text}", text);
        var list = Cities.SelectMany(c => c.Notice(text, "warning")).ToList();
        // The city here hasn't had its after-step yet today; it gets it with the rest.
        return list;
    }

    // ------------------------------------------------------------------
    // The link
    // ------------------------------------------------------------------

    /// <summary>A tile host's connection, from its hello to its last word.</summary>
    public async Task RunLinkAsync(WebSocket socket, CancellationToken ct)
    {
        var link = new TileLink { Socket = socket, LastHeard = _clock.ElapsedMilliseconds };
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        bool joined = false;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var msg = P.LinkMessage.Parser.ParseFrom(ms.ToArray());
                ms.SetLength(0);
                link.LastHeard = _clock.ElapsedMilliseconds;
                switch (msg.BodyCase)
                {
                    case P.LinkMessage.BodyOneofCase.Hello when !joined:
                    {
                        var welcome = Join(link, msg.Hello);
                        await SendLinkAsync(link, new P.LeaderMessage { ReplyTo = msg.Seq, Welcome = welcome.Welcome });
                        foreach (var (c, m) in welcome.Outbox) _ = SimHost.SendAsync(c, m);
                        if (welcome.Welcome.Error.Length > 0) return;
                        joined = true;
                        break;
                    }
                    case P.LinkMessage.BodyOneofCase.DayEnd when joined:
                        link.DayEnd = msg.DayEnd;
                        break;
                    case P.LinkMessage.BodyOneofCase.SignIn when joined:
                        _ = Task.Run(() => LinkSignIn(link, msg.Seq, msg.SignIn));
                        break;
                    case P.LinkMessage.BodyOneofCase.Forward when joined:
                        _ = Task.Run(() => LinkForward(link, msg.Forward));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (InvalidProtocolBufferException) { _log.LogWarning("a tile host sent something unreadable"); }
        finally
        {
            link.Dead = true;
            if (joined) _log.LogWarning("{City}'s tile host disconnected", _region.Tiles.ElementAtOrDefault(link.Index)?.Name);
        }
    }

    (P.TileWelcome Welcome, List<(Client, P.ServerMessage)> Outbox) Join(TileLink link, P.TileHello hello)
    {
        P.TileWelcome No(string why) => new() { Error = why };
        if (_host.LinkKey.Length == 0 || hello.Key != _host.LinkKey) return (No("This region doesn't take that link key."), new());
        lock (_gate)
            return WithAll(() =>
            {
                var t = _region.TileAt(hello.Col, hello.Row);
                if (t?.City is not { } w) return (No("There's no city on that tile. Found one first."), new List<(Client, P.ServerMessage)>());
                int i = IndexOf(t);
                if (_links[i] is { Dead: false }) return (No($"{t.Name} already has a tile host."), new List<(Client, P.ServerMessage)>());
                if (_halted) return (No("The region has stopped after a desync."), new List<(Client, P.ServerMessage)>());
                var list = new List<(Client, P.ServerMessage)>();
                link.Index = i;
                link.Address = hello.Address;
                // Its mayor gets the seat back from the computer that covered for them.
                if (_coveredMayors.Remove(i, out var mayor) && World.IsComputer(t.Mayor)) _region.TakeOver(t, mayor);
                // Players here in that city go to the tile host.
                if (_cities[i] is { } host)
                {
                    w.ApplyPending();
                    foreach (var c in host.Clients.ToList())
                    {
                        host.Leave(c);
                        list.Add((c, new P.ServerMessage { Redirect = new P.Redirect { Address = link.Address, Col = t.Col, Row = t.Row } }));
                    }
                    host.Dispose();
                    _cities[i] = null;
                }
                _links[i] = link;
                link.Map = new Client { Socket = null!, Relay = m => m.Region != null ? SendLinkAsync(link, new P.LeaderMessage { Region = m.Region }) : Task.CompletedTask };
                _log.LogInformation("{City} is run by a tile host at {Address}", t.Name, link.Address);
                list.AddRange(Cities.SelectMany(c => c.Notice($"{t.Name} is run by its own host now.", "info")));
                list.AddRange(RegionIfChanged(force: true));
                var welcome = new P.TileWelcome
                {
                    Name = _region.Name, Seed = _region.Seed, Cols = _region.Cols, Rows = _region.Rows, TileSize = _region.TileSize, Tick = _region.Tick,
                    Save = ByteString.CopyFrom(Mapping.Save(w)), LeaderAddress = _host.PublicAddress,
                };
                return (welcome, list);
            });
    }

    void LinkSignIn(TileLink link, uint seq, P.SignIn s)
    {
        if (_accounts == null) { _ = SendLinkAsync(link, new P.LeaderMessage { ReplyTo = seq, Error = new P.Error { Message = "This region has no accounts." } }); return; }
        SignInResult r;
        if (s.Token.Length > 0) r = _accounts.SignInWithToken(s.Token);
        else if (s.Create)
        {
            string name = s.Name.Trim();
            r = _host.MayCreate(name, s.InviteCode) is { } no ? new SignInResult(null, null, no) : _accounts.Create(name, s.Password);
            if (r.Ok) _host.PromoteFirst(r.Name!);
        }
        else r = _accounts.SignIn(s.Name.Trim(), s.Password);
        _ = SendLinkAsync(link, r.Ok
            ? new P.LeaderMessage { ReplyTo = seq, SignedIn = new P.SignedIn { Name = r.Name!, Token = r.Token!, Admin = _host.IsAdmin(r.Name!) } }
            : new P.LeaderMessage { ReplyTo = seq, Error = new P.Error { Message = r.Error! } });
    }

    async Task LinkForward(TileLink link, P.LinkForward f)
    {
        Client proxy;
        lock (link.Proxies)
        {
            if (!link.Proxies.TryGetValue(f.Conn, out proxy!))
            {
                int conn = f.Conn;
                proxy = new Client { Socket = null!, Relay = m => SendLinkAsync(link, new P.LeaderMessage { Reply = new P.LinkReply { Conn = conn, Message = m } }) };
                link.Proxies[f.Conn] = proxy;
            }
        }
        // The tile host vouches for who its players are: it signed them in against this region's accounts.
        proxy.Player = f.Player;
        proxy.Admin = f.Admin && _host.IsAdmin(f.Player);
        proxy.RemoteTile = _region.Tiles.ElementAtOrDefault(link.Index);
        try { await HandleRegionAsync(proxy, f.Message); }
        catch (Exception e) { _log.LogWarning(e, "a forwarded {Kind} message failed", f.Message.BodyCase); }
    }
}
