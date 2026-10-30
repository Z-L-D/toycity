using System.Diagnostics;
using System.Net.WebSockets;
using Google.Protobuf;
using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>
/// Owns the running region: its one clock, a <see cref="SimHost"/> for each founded city, the connections and the
/// save folder. Each connection is in one city at a time and can move between them without reconnecting.
/// <para>
/// Locks: <see cref="_gate"/> first, then cities' gates in tile order. The clock holds all of them while it steps
/// the region; a city's own messages take only that city's gate.
/// </para>
/// </summary>
public sealed class RegionHost : IDisposable
{
    readonly object _gate = new();
    readonly ContentDb _content;
    readonly string _saveDir;
    readonly ILogger<RegionHost> _log;
    readonly HostConfig _host;
    readonly AccountStore? _accounts;
    readonly Thread _thread;
    volatile bool _running = true;
    Region _region;
    SimHost?[] _cities;
    readonly Dictionary<int, ByteString> _pictures = new();
    string _regionSig = "";

    /// <summary>Milliseconds per tick (one day) at speeds 1, 2 and 3: a game year takes an hour, twenty minutes or five.</summary>
    static readonly int[] TickMs = { 0, 10_000, 3_333, 833 };

    /// <summary>How long a mayor who isn't an admin may hold the whole region paused.</summary>
    public const int MayorPauseSeconds = 120;

    /// <summary>Sign-in attempts a connection gets before the server hangs up.</summary>
    const int MaxSignInFailures = 5;
    /// <summary>The largest message a client may send; anything bigger is hostile or broken.</summary>
    const int MaxMessageBytes = 1 << 20;
    /// <summary>Unreadable messages a connection may send before it is dropped.</summary>
    const int MaxMalformed = 3;

    // The clock. Each player who set a speed asks for it; the region runs at the slowest ask of anyone online.
    readonly Dictionary<string, (int Speed, long At)> _asks = new(StringComparer.Ordinal);
    int _hostSpeed = 1;
    volatile int _speed = 1;
    string _speedBy = "";
    readonly Stopwatch _clock = Stopwatch.StartNew();

    // Who is connected, anywhere in the region, by name: connections each.
    readonly Dictionary<string, int> _online = new(StringComparer.Ordinal);

    public RegionHost(ContentDb content, string saveDir, ILogger<RegionHost> log, HostConfig? host = null, AccountStore? accounts = null)
    {
        _content = content;
        _saveDir = saveDir;
        _log = log;
        _host = host ?? HostConfig.Solo();
        _accounts = accounts ?? (_host.Dedicated ? new AccountStore(Path.Combine(saveDir, "accounts.json")) : null);
        Directory.CreateDirectory(saveDir);
        _region = StartingRegion(content);
        _cities = new SimHost?[_region.Tiles.Length];
        lock (_gate) OpenCities();
        _thread = new Thread(Loop) { IsBackground = true, Name = "region" };
        _thread.Start();
    }

    /// <summary>A new server's region: Toy Valley, three tiles by three, with Toyville founded in the middle.</summary>
    static Region StartingRegion(ContentDb content)
    {
        var r = Region.Create(content, "Toy Valley", 2026, 3, 3, 128);
        r.Found(1, 1, "Toyville", SimHost.LocalPlayer, starter: true);
        return r;
    }

    public Region Region => _region;

    /// <summary>The region's speed, 0 (paused) to 3.</summary>
    public int Speed => _speed;

    public bool Online(string player)
    {
        lock (_online) return _online.TryGetValue(player, out int n) && n > 0;
    }

    void CountOnline(string player, int delta)
    {
        if (player.Length == 0) return;
        lock (_online)
        {
            int n = (_online.TryGetValue(player, out int v) ? v : 0) + delta;
            if (n > 0) _online[player] = n; else _online.Remove(player);
        }
    }

    // ------------------------------------------------------------------
    // Cities
    // ------------------------------------------------------------------

    /// <summary>Makes a host for every founded city that hasn't one. Caller holds the gate.</summary>
    void OpenCities()
    {
        for (int i = 0; i < _region.Tiles.Length; i++)
            if (_region.Tiles[i].City != null && _cities[i] == null)
                _cities[i] = new SimHost(_content, _saveDir, _log, _host, _accounts, this, _region.Tiles[i]);
    }

    IEnumerable<SimHost> Cities => _cities.Where(c => c != null).Select(c => c!);

    /// <summary>Runs <paramref name="f"/> holding the region's gate and every city's, in tile order.</summary>
    T WithAll<T>(Func<T> f)
    {
        lock (_gate)
        {
            var held = Cities.ToList();
            int taken = 0;
            try
            {
                foreach (var c in held) { Monitor.Enter(c._gate); taken++; }
                return f();
            }
            finally
            {
                for (int k = taken - 1; k >= 0; k--) Monitor.Exit(held[k]._gate);
            }
        }
    }

    /// <summary>The city a new connection lands in: the one asked for, else one the player is mayor of, else the first.</summary>
    SimHost Landing(string player, (int Col, int Row)? asked)
    {
        if (asked is { } a && _region.TileAt(a.Col, a.Row) is { } t && _cities[t.Row * _region.Cols + t.Col] is { } wanted) return wanted;
        return Cities.FirstOrDefault(c => c.Tile.Mayor == player) ?? Cities.First();
    }

    // ------------------------------------------------------------------
    // The clock
    // ------------------------------------------------------------------

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        long next = 0;
        while (_running)
        {
            ExpirePauses();
            int speed = _speed;
            if (speed == 0) { Thread.Sleep(50); next = sw.ElapsedMilliseconds; continue; }
            long now = sw.ElapsedMilliseconds;
            if (now < next) { Thread.Sleep((int)Math.Min(20, next - now)); continue; }
            next = Math.Max(next + TickMs[speed], now - 1000);

            var outbox = WithAll(() =>
            {
                _region.Step();
                var list = new List<(Client, P.ServerMessage)>();
                foreach (var c in Cities) list.AddRange(c.AfterStep());
                var first = _region.Cities.FirstOrDefault();
                if (first != null && first.Day == 1)
                {
                    // Pictures change slowly: made again monthly.
                    if (first.Month == 1) AutoSave();
                    _pictures.Clear();
                    _regionSig = "";
                }
                list.AddRange(RegionIfChanged());
                return list;
            });
            foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
        }
    }

    void AutoSave()
    {
        try { WriteSave("autosave"); }
        catch (Exception e) { _log.LogWarning(e, "autosave failed"); }
    }

    /// <summary>Lets a mayor's pause go after <see cref="MayorPauseSeconds"/>; an admin's holds.</summary>
    void ExpirePauses()
    {
        List<(Client, P.ServerMessage)>? outbox = null;
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            bool changed = false;
            foreach (var (p, ask) in _asks.ToList())
            {
                if (ask.Speed == 0 && now - ask.At > MayorPauseSeconds * 1000L && !IsAdmin(p)) { _asks.Remove(p); changed = true; }
            }
            if (changed || _speedDirty)
            {
                _speedDirty = false;
                if (Recompute()) outbox = WithAll(Broadcast);
            }
        }
        if (outbox != null) foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
    }

    volatile bool _speedDirty;

    bool IsAdmin(string player) => _host.Dedicated ? _host.IsAdmin(player) : player == SimHost.LocalPlayer;

    /// <summary>The slowest speed any online player asked for, or the host's. True when it changed. Caller holds the gate.</summary>
    bool Recompute()
    {
        int speed = _hostSpeed;
        string by = "";
        foreach (var (p, ask) in _asks.OrderBy(a => a.Key, StringComparer.Ordinal))
            if (Online(p) && ask.Speed < speed) { speed = ask.Speed; by = p; }
        bool changed = speed != _speed || by != _speedBy;
        _speed = speed;
        _speedBy = by;
        return changed;
    }

    /// <summary>
    /// A player sets the speed. An admin sets the region's own; a mayor (of any city here) asks for one, and the
    /// region never runs faster than its slowest ask. Returns why not, or null.
    /// </summary>
    string? SetSpeed(Client c, int speed)
    {
        speed = Math.Clamp(speed, 0, 3);
        bool mayor = _region.Tiles.Any(t => t.Mayor == c.Player);
        if (!c.Admin && !mayor) return "Only a mayor or this server's admins can change the speed.";
        if (c.Admin)
        {
            _hostSpeed = speed;
            // An admin's choice clears asks for anything slower than it, so pressing play gets the region going.
            foreach (var p in _asks.Where(a => a.Value.Speed < speed).Select(a => a.Key).ToList()) _asks.Remove(p);
            _asks.Remove(c.Player);
        }
        else if (speed >= _hostSpeed) _asks.Remove(c.Player);
        else _asks[c.Player] = (speed, _clock.ElapsedMilliseconds);
        Recompute();
        return null;
    }

    // ------------------------------------------------------------------
    // The region map
    // ------------------------------------------------------------------

    /// <summary>The region map as seen from one city. Caller holds every gate.</summary>
    P.RegionState State(SimHost here)
    {
        var s = RegionMapping.State(_region, here.Tile.Col, here.Tile.Row, _speed,
            t => _cities[t.Row * _region.Cols + t.Col]?.Clients.Where(c => c.SignedIn).Select(c => c.Player).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                 ?? Enumerable.Empty<string>(),
            _pictures);
        s.SpeedSetBy = _speedBy;
        return s;
    }

    /// <summary>The region map to everyone, when something on it changed. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> RegionIfChanged(bool force = false)
    {
        var list = new List<(Client, P.ServerMessage)>();
        string sig = $"{_speed}/{_speedBy}/{_region.Tick / World.DaysPerMonth}/" + string.Join(",", _region.Tiles.Select(t =>
            $"{t.Name}:{t.Mayor}:{string.Join("+", _cities[t.Row * _region.Cols + t.Col]?.Clients.Select(c => c.Player).Distinct().OrderBy(n => n, StringComparer.Ordinal) ?? Enumerable.Empty<string>())}"));
        if (!force && sig == _regionSig) return list;
        _regionSig = sig;
        foreach (var city in Cities)
        {
            var m = new P.ServerMessage { Region = State(city) };
            foreach (var c in city.Clients) list.Add((c, m));
        }
        return list;
    }

    List<(Client, P.ServerMessage)> Broadcast() => RegionIfChanged(force: true);

    // ------------------------------------------------------------------
    // Connections
    // ------------------------------------------------------------------

    public async Task RunClientAsync(WebSocket socket, (int Col, int Row)? tile, CancellationToken ct)
    {
        var client = new Client { Socket = socket };
        bool solo = !_host.Dedicated;
        if (solo) { client.Player = SimHost.LocalPlayer; client.Admin = true; }
        await SimHost.SendAsync(client, new P.ServerMessage
        {
            Welcome = new P.Welcome
            {
                ProtocolVersion = Mapping.ProtocolVersion, ServerName = _host.Name, SignInRequired = !solo,
                InviteRequired = _host.Join == JoinPolicy.Invite, Player = client.Player, Admin = client.Admin,
            },
        });
        if (solo) await EnterAsync(client, tile);

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        string? drop = null;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > MaxMessageBytes) { drop = "message too large"; break; }
                if (!r.EndOfMessage) continue;
                var data = ms.ToArray();
                ms.SetLength(0);
                if (!client.Limiter.Take()) { drop = "too many messages"; break; }
                P.ClientMessage msg;
                try { msg = P.ClientMessage.Parser.ParseFrom(data); }
                catch (InvalidProtocolBufferException)
                {
                    if (++client.Malformed >= MaxMalformed) { drop = "unreadable messages"; break; }
                    await SimHost.SendAsync(client, SimHost.Error(0, "Could not read that message."));
                    continue;
                }
                try
                {
                    if (msg.BodyCase == P.ClientMessage.BodyOneofCase.SignIn)
                    {
                        if (!await SignInAsync(client, msg, tile)) break;
                    }
                    else if (!client.SignedIn)
                    {
                        if (msg.BodyCase != P.ClientMessage.BodyOneofCase.Hello) await SimHost.SendAsync(client, SimHost.Error(msg.Seq, "Sign in first."));
                    }
                    else if (!await HandleRegionAsync(client, msg) && client.Host is { } city) await city.HandleAsync(client, msg);
                }
                catch (Exception e) when (e is not OperationCanceledException and not WebSocketException)
                {
                    // A message the server can't handle drops the client that sent it, never the server.
                    _log.LogWarning(e, "dropping {Player}: a {Kind} message failed", client.Player, msg.BodyCase);
                    drop = "a message the server couldn't handle";
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (drop != null)
            {
                _log.LogWarning("dropped a connection ({Player}): {Why}", client.Player.Length > 0 ? client.Player : "not signed in", drop);
                try
                {
                    var status = drop == "message too large" ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.PolicyViolation;
                    await socket.CloseOutputAsync(status, drop, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { }
            }
            var outbox = WithAll(() =>
            {
                if (client.Host == null) return new List<(Client, P.ServerMessage)>();
                CountOnline(client.Player, -1);
                var list = client.Host.Leave(client);
                _speedDirty = true;
                list.AddRange(RegionIfChanged());
                return list;
            });
            foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
        }
    }

    /// <summary>Puts a signed-in connection in its first city.</summary>
    async Task EnterAsync(Client client, (int Col, int Row)? tile)
    {
        var (mine, others) = WithAll(() =>
        {
            CountOnline(client.Player, 1);
            _speedDirty = true;
            var (m, o) = Landing(client.Player, tile).Join(client);
            m.Add(new P.ServerMessage { Region = State(client.Host!) });
            o.AddRange(RegionIfChanged().Where(x => x.Item1 != client));
            return (m, o);
        });
        foreach (var (c, m) in others) _ = SimHost.SendAsync(c, m);
        foreach (var m in mine) await SimHost.SendAsync(client, m);
    }

    /// <summary>Answers a SignIn. False when the connection has failed too often and should be closed.</summary>
    async Task<bool> SignInAsync(Client client, P.ClientMessage msg, (int Col, int Row)? tile)
    {
        var s = msg.SignIn;
        if (client.SignedIn) { await SimHost.SendAsync(client, SimHost.Error(msg.Seq, $"Already signed in as {client.Player}.")); return true; }
        if (_accounts == null) { await SimHost.SendAsync(client, SimHost.Error(msg.Seq, "This server has no accounts.")); return true; }
        SignInResult r;
        if (s.Token.Length > 0) r = _accounts.SignInWithToken(s.Token);
        else if (s.Create)
        {
            string name = s.Name.Trim();
            r = _host.MayCreate(name, s.InviteCode) is { } no ? new SignInResult(null, null, no) : _accounts.Create(name, s.Password);
            if (r.Ok)
            {
                _host.PromoteFirst(r.Name!);
                WithAll(() => { foreach (var c in Cities) c.AccountMade(); return 0; });
            }
        }
        else r = _accounts.SignIn(s.Name.Trim(), s.Password);
        if (!r.Ok)
        {
            client.SignInFailures++;
            await SimHost.SendAsync(client, SimHost.Error(msg.Seq, r.Error!));
            if (client.SignInFailures < MaxSignInFailures) return true;
            _log.LogInformation("closing a connection after {N} failed sign-ins", client.SignInFailures);
            return false;
        }
        client.Player = r.Name!;
        client.Admin = _host.IsAdmin(r.Name!);
        _log.LogInformation("{Player} signed in", client.Player);
        await SimHost.SendAsync(client, new P.ServerMessage { ReplyTo = msg.Seq, SignedIn = new P.SignedIn { Name = client.Player, Token = r.Token!, Admin = client.Admin } });
        await EnterAsync(client, tile);
        return true;
    }

    /// <summary>Region-wide messages: the clock, saves, new cities and moving between them. False for a city's own.</summary>
    async Task<bool> HandleRegionAsync(Client c, P.ClientMessage msg)
    {
        P.ServerMessage? reply = null;
        List<(Client, P.ServerMessage)>? broadcast = null;
        List<P.ServerMessage>? mine = null;
        switch (msg.BodyCase)
        {
            case P.ClientMessage.BodyOneofCase.Load or P.ClientMessage.BodyOneofCase.NewCity or P.ClientMessage.BodyOneofCase.NewRegion when !c.Admin:
            case P.ClientMessage.BodyOneofCase.Save when !c.Admin && msg.Save.Name.Length > 0:
                reply = SimHost.Error(msg.Seq, "Only this server's admins can save, load, or start a new city or region.");
                break;
            case P.ClientMessage.BodyOneofCase.Speed:
                broadcast = WithAll(() =>
                {
                    if (SetSpeed(c, msg.Speed.Speed) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var list = new List<(Client, P.ServerMessage)>();
                    foreach (var city in Cities) list.AddRange(city.Deltas());
                    list.AddRange(RegionIfChanged());
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.Save:
                if (msg.Save.Name.Length > 0)
                {
                    if (!SimHost.ValidName(msg.Save.Name)) { reply = SimHost.Error(msg.Seq, "Save names can use letters, numbers, spaces, - and _."); break; }
                    broadcast = WithAll(() =>
                    {
                        WriteSave(msg.Save.Name);
                        return Cities.SelectMany(city => city.Notice($"Saved as \"{msg.Save.Name}\".", "good")).ToList();
                    });
                }
                reply = new P.ServerMessage { Saves = ListSaves() };
                break;
            case P.ClientMessage.BodyOneofCase.Load:
                try
                {
                    if (!SimHost.ValidName(msg.Load.Name)) throw new InvalidDataException("bad save name");
                    var loaded = ReadSave(msg.Load.Name);
                    broadcast = WithAll(() => Replace(loaded, c.Player));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or InvalidProtocolBufferException)
                {
                    reply = SimHost.Error(msg.Seq, $"Could not load \"{msg.Load.Name}\": {e.Message}");
                }
                break;
            case P.ClientMessage.BodyOneofCase.NewCity:
            {
                // A lone city: a region of one tile, as every city was before regions.
                var n = msg.NewCity;
                int size = n.Benchmark ? BenchmarkCity.Size : n.Size is 64 or 128 or 256 ? n.Size : 128;
                ulong seed = n.Seed != 0 ? n.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                var w = World.Create(_content, seed, size, string.IsNullOrWhiteSpace(n.Name) ? "New City" : n.Name.Trim());
                if (n.Benchmark) BenchmarkCity.Apply(w);
                else if (n.StarterTown) StarterTown.Apply(w);
                w.ApplyPending();
                broadcast = WithAll(() => Replace(Region.Single(w), c.Player));
                break;
            }
            case P.ClientMessage.BodyOneofCase.NewRegion:
            {
                var n = msg.NewRegion;
                int cols = Math.Clamp(n.Cols, 1, Region.MaxSide), rows = Math.Clamp(n.Rows, 1, Region.MaxSide);
                int size = n.TileSize is 64 or 128 or 256 ? n.TileSize : 128;
                ulong seed = n.Seed != 0 ? n.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                var r = Region.Create(_content, string.IsNullOrWhiteSpace(n.Name) ? "New Region" : n.Name.Trim(), seed, cols, rows, size);
                // Someone has to be somewhere: the middle tile always has a city, a starter town if asked for.
                r.Found(cols / 2, rows / 2, string.IsNullOrWhiteSpace(n.CityName) ? "New City" : n.CityName.Trim()[..Math.Min(40, n.CityName.Trim().Length)], c.Player, n.StarterTown);
                broadcast = WithAll(() => Replace(r, c.Player));
                break;
            }
            case P.ClientMessage.BodyOneofCase.FoundCity:
            {
                var f = msg.FoundCity;
                string name = string.IsNullOrWhiteSpace(f.Name) ? "New City" : f.Name.Trim();
                if (name.Length > 40) name = name[..40];
                broadcast = WithAll(() =>
                {
                    if (_region.CantFound(f.Col, f.Row) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    if (!c.Admin && _host.Dedicated && _region.Tiles.Count(t => t.Mayor == c.Player) >= 3)
                    {
                        reply = SimHost.Error(msg.Seq, "You're mayor of three cities here already.");
                        return null;
                    }
                    _region.Found(f.Col, f.Row, name, c.Player, f.StarterTown);
                    _pictures.Clear();
                    OpenCities();
                    var list = Cities.SelectMany(city => city.Notice($"{c.Player} founded {name} in the region.", "info")).ToList();
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            }
            case P.ClientMessage.BodyOneofCase.GoTo:
            {
                var g = msg.GoTo;
                var (m, o) = WithAll<(List<P.ServerMessage>?, List<(Client, P.ServerMessage)>?)>(() =>
                {
                    var t = _region.TileAt(g.Col, g.Row);
                    if (t == null || !t.Founded) { reply = SimHost.Error(msg.Seq, "There's no city there yet. Found one first."); return (null, null); }
                    var to = _cities[t.Row * _region.Cols + t.Col]!;
                    var list = new List<(Client, P.ServerMessage)>();
                    if (c.Host == to) return (new List<P.ServerMessage> { new() { Region = State(to) } }, list);
                    if (c.Host != null) list.AddRange(c.Host.Leave(c));
                    var (mm, oo) = to.Join(c);
                    list.AddRange(oo);
                    mm.Add(new P.ServerMessage { Region = State(to) });
                    list.AddRange(RegionIfChanged().Where(x => x.Item1 != c));
                    return (mm, list);
                });
                mine = m;
                broadcast = o;
                break;
            }
            case P.ClientMessage.BodyOneofCase.Region:
                reply = WithAll(() => c.Host == null ? null : new P.ServerMessage { Region = State(c.Host) });
                break;
            default:
                return false;
        }
        if (reply != null)
        {
            reply.ReplyTo = msg.Seq;
            await SimHost.SendAsync(c, reply);
        }
        if (mine != null)
        {
            if (mine.Count > 0) mine[0].ReplyTo = msg.Seq;
            foreach (var m in mine) await SimHost.SendAsync(c, m);
        }
        if (broadcast != null)
            foreach (var (cl, m) in broadcast) await SimHost.SendAsync(cl, m);
        return true;
    }

    /// <summary>
    /// Puts another region in place of this one (a load, a new city or region). Everyone moves to the same tile in the
    /// new region if it has a city, else to one they're mayor of, else to the first. Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> Replace(Region next, string by)
    {
        var old = _cities;
        var oldRegion = _region;
        var moving = Cities.SelectMany(city => city.Clients.Select(cl => (cl, city.Tile.Col, city.Tile.Row))).ToList();
        foreach (var city in Cities) foreach (var (cl, _, _) in moving.Where(x => x.cl.Host == city).ToList()) city.Leave(cl);
        _region = next;
        _cities = new SimHost?[next.Tiles.Length];
        _pictures.Clear();
        _regionSig = "";
        OpenCities();
        foreach (var city in old) city?.Dispose();
        oldRegion.Dispose();
        // Every city that arrived gets a real mayor and the host's minimums (logged in each).
        var list = new List<(Client, P.ServerMessage)>();
        foreach (var city in Cities) city.Arrived(by);
        // Monitor.Enter the new cities' gates is not needed: nobody can reach them until this returns.
        foreach (var (cl, col, row) in moving)
        {
            var (mine, _) = Landing(cl.Player, (col, row)).Join(cl);
            list.AddRange(mine.Select(m => (cl, m)));
        }
        foreach (var city in Cities) list.AddRange(city.Deltas(forceAlerts: true));
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    // ------------------------------------------------------------------
    // Saves
    // ------------------------------------------------------------------

    const string RegionExt = ".toyregion", CityExt = ".toycity";

    /// <summary>A lone city saves as a city, as before regions; a region of several tiles saves whole. Caller holds every gate.</summary>
    void WriteSave(string name)
    {
        byte[] bytes;
        string ext;
        if (_region.Tiles.Length == 1 && _region.Tiles[0].City is { } only) { bytes = Mapping.Save(only); ext = CityExt; }
        else { bytes = RegionMapping.Save(_region); ext = RegionExt; }
        // One name, one save: a region save replaces a city save of the same name and the other way round.
        File.Delete(Path.Combine(_saveDir, name + (ext == CityExt ? RegionExt : CityExt)));
        File.WriteAllBytes(Path.Combine(_saveDir, name + ext), bytes);
    }

    Region ReadSave(string name)
    {
        string region = Path.Combine(_saveDir, name + RegionExt);
        if (File.Exists(region)) return RegionMapping.Load(_content, File.ReadAllBytes(region));
        return Region.Single(Mapping.Load(_content, File.ReadAllBytes(Path.Combine(_saveDir, name + CityExt))));
    }

    public P.SaveList ListSaves()
    {
        var list = new P.SaveList();
        foreach (var name in Directory.GetFiles(_saveDir, "*" + CityExt).Concat(Directory.GetFiles(_saveDir, "*" + RegionExt))
                     .Select(Path.GetFileNameWithoutExtension).Distinct().OrderBy(f => f, StringComparer.Ordinal))
            list.Names.Add(name!);
        return list;
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
        foreach (var c in Cities) c.Dispose();
    }
}
