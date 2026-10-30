using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Google.Protobuf;
using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>
/// Runs one city of the region: its connected clients, its side games and its views. The region host steps every
/// city's day together and routes each connection to the city it's in. Every touch of the world happens under
/// <see cref="_gate"/>.
/// </summary>
public sealed partial class SimHost : IDisposable
{
    internal readonly object _gate = new();
    readonly ContentDb _content;
    readonly string _saveDir;
    readonly ILogger _log;
    readonly HostConfig _host;
    readonly AccountStore? _accounts;
    readonly RegionHost _region;
    readonly List<Client> _clients = new();
    World _world;
    // Alerts are recomputed at most twice a second and sent only when they change.
    string _alertSig = "";
    P.AlertList _alerts = new();
    long _alertsAt = -1;
    string _civicSig = "";
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Who a solo player is: the name every city before Beta 2 gave its mayor.</summary>
    public const string LocalPlayer = "player";

    /// <summary>The region's speed: one clock for every city in it.</summary>
    int _speed => _region.Speed;

    public RegionTile Tile { get; }
    public World World => _world;

    internal SimHost(ContentDb content, string saveDir, ILogger log, HostConfig host, AccountStore? accounts, RegionHost region, RegionTile tile)
    {
        _content = content;
        _saveDir = saveDir;
        _log = log;
        _host = host;
        _accounts = accounts;
        _region = region;
        Tile = tile;
        _world = tile.City ?? throw new ArgumentException("open land has no city to host", nameof(tile));
        UseRemoteSideGames();
        AttachSideGames(_world);
        EnsureMayor();
        ApplyHostRules();
        LoadAgentTiers();
        StartAwayTimer();
    }

    /// <summary>The clients in this city now. Caller holds the gate.</summary>
    internal IReadOnlyList<Client> Clients => _clients;

    /// <summary>
    /// After the region's day: the city's commuters move on, and every client here gets its delta. Caller holds the
    /// gate (the region host holds every city's while it steps them).
    /// </summary>
    internal List<(Client, P.ServerMessage)> AfterStep()
    {
        Agents().Daily();
        return BuildDeltas();
    }

    /// <summary>One delta per client, with the notices since the last tick, plus alerts when they change. Caller holds the gate.</summary>
    List<(Client, P.ServerMessage)> BuildDeltas(bool forceAlerts = false)
    {
        var notices = _world.Notices.ToList();
        _world.Notices.Clear();
        var list = new List<(Client, P.ServerMessage)>();
        foreach (var c in _clients) list.Add((c, new P.ServerMessage { Delta = c.Tracker.Next(_world, _speed, notices.Where(n => n.To.Length == 0 || n.To == c.Player)) }));
        if (forceAlerts || _clock.ElapsedMilliseconds - _alertsAt >= 500)
        {
            _alertsAt = _clock.ElapsedMilliseconds;
            var alerts = Mapping.Alerts(_world, _world.Alerts());
            string sig = alerts.ToString();
            if (forceAlerts || sig != _alertSig)
            {
                _alertSig = sig;
                _alerts = alerts;
                foreach (var c in _clients) list.Add((c, new P.ServerMessage { Alerts = alerts }));
            }
        }
        // Petitions, deals, unlocks and headlines change at most monthly or on an action, so a cheap signature will do.
        string civicSig = $"{_world.HeadlineSeq}/{_world.Approval}/{_world.TypeUnlocked.Count(u => u)}/{string.Join(",", _world.Prompts.Select(p => p.Id * 8 + (int)p.Status))}/{_world.Tick / World.DaysPerMonth}/{string.Join(",", _world.Seats.Select(s => $"{s.Owner}:{(int)s.Level}:{(int)s.Style}:{s.SpendCap}:{s.Report?.ToTick}:{(int)s.AwayLevel}:{(int)s.AwayStyle}:{s.AutoAway}"))}/{string.Join(",", _world.Leases.Select(l => $"{l.Id}:{l.Terms.GetHashCode()}:{l.PendingMonths}:{l.Arrears}:{l.BadStanding}:{l.CureMonthsLeft}:{l.Jobs}:{l.LastRevenue}:{l.EvictMonths}"))}/{_world.SideGames.Count}/{_world.ConsentSig()}/{string.Join(",", _world.Standings.Select(s => $"{s.Rating}:{s.Statue}:{s.ExclusiveUntil > _world.Tick}"))}/{_world.RolesSig()}/{string.Join(",", _world.Offers.Select(o => o.Id * 8 + (int)o.Status))}/{_world.Delinks.Count}/{string.Join(",", _world.Tenders.Select(t => $"{t.Id}:{(int)t.Status}:{t.Bids.Count}"))}:{(_world.Tenders.Any(t => t.Status == TenderStatus.Open) ? _world.Tick : 0)}/{_world.FreightExportDaily}:{_world.FreightDeliveredDaily}";
        if (forceAlerts || civicSig != _civicSig)
        {
            _civicSig = civicSig;
            var civic = Mapping.Civic(_world);
            foreach (var c in _clients) list.Add((c, new P.ServerMessage { Civic = CivicFor(civic, c.Player) }));
        }
        if (RosterIfChanged() is { } roster)
            foreach (var c in _clients) list.Add((c, new P.ServerMessage { Agents = roster }));
        var views = ChangedViews(all: false);
        foreach (var c in _clients)
            foreach (var view in ViewsFor(views, c.Player)) list.Add((c, view));
        list.AddRange(PresenceIfChanged());
        return list;
    }

    internal static async Task SendAsync(Client c, P.ServerMessage m)
    {
        var bytes = m.ToByteArray();
        await c.SendLock.WaitAsync();
        try
        {
            if (c.Socket.State == WebSocketState.Open)
                await c.Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
        finally { c.SendLock.Release(); }
    }

    P.ServerMessage FullStateMessage(Client c)
    {
        var m = new P.ServerMessage { Full = Mapping.FullState(_world, _speed) };
        c.Tracker.Reset(_world);
        return m;
    }

    /// <summary>
    /// Brings a signed-in connection into this city: everything it needs to draw the city now, in order. Everyone
    /// else here hears of the newcomer. Caller holds the gate; the messages are sent after it's let go.
    /// </summary>
    internal (List<P.ServerMessage> Mine, List<(Client, P.ServerMessage)> Others) Join(Client client)
    {
        if (_host.Dedicated) { Returned(client.Player); BringConsent(client.Player); }
        client.Host = this;
        _clients.Add(client);
        var mine = new List<P.ServerMessage> { FullStateMessage(client) };
        // Everyone else hears of the newcomer (and of any seats they took back); the newcomer gets it all below.
        _presenceSig = "";
        var others = BuildDeltas(forceAlerts: true).Where(x => x.Item1 != client).ToList();
        mine.Add(new P.ServerMessage { Presence = Presence() });
        mine.Add(new P.ServerMessage { Saves = _region.ListSaves() });
        mine.Add(new P.ServerMessage { Alerts = Mapping.Alerts(_world, _world.Alerts()) });
        mine.Add(new P.ServerMessage { Civic = CivicFor(Mapping.Civic(_world), client.Player) });
        mine.Add(new P.ServerMessage { Agents = Roster(Agents()) });
        mine.AddRange(ViewsFor(ChangedViews(all: true), client.Player));
        return (mine, others);
    }

    /// <summary>A connection has left this city, closed or gone to another. Caller holds the gate.</summary>
    internal List<(Client, P.ServerMessage)> Leave(Client client)
    {
        _clients.Remove(client);
        if (client.Host == this) client.Host = null;
        Left(client);
        return PresenceIfChanged().ToList();
    }

    /// <summary>Deltas for everyone here now: after the speed changed, say. Caller holds the gate.</summary>
    internal List<(Client, P.ServerMessage)> Deltas(bool forceAlerts = false) => BuildDeltas(forceAlerts);

    /// <summary>Tells everyone in this city something. Caller holds the gate.</summary>
    internal List<(Client, P.ServerMessage)> Notice(string text, string kind)
    {
        _world.Notices.Add(new SimNotice(text, kind));
        return BuildDeltas();
    }

    /// <summary>A new account was made: a city still run by the solo "player" goes to the first admin. Caller holds the gate.</summary>
    internal void AccountMade() => EnsureMayor();

    internal static P.ServerMessage Error(uint seq, string text) => new() { ReplyTo = seq, Error = new P.Error { Message = text } };

    internal async Task HandleAsync(Client c, P.ClientMessage msg)
    {
        P.ServerMessage? reply = null;
        List<(Client, P.ServerMessage)>? broadcast = null;
        lock (_gate)
        {
            switch (msg.BodyCase)
            {
                case P.ClientMessage.BodyOneofCase.Action:
                    if (Mapping.ToSim(msg.Action) is { } a)
                    {
                        // Applied now, not at the next tick: at a slow speed a day is many seconds. It is logged at the
                        // current tick, before that tick's step, which is where a replay applies it too.
                        _world.MakeCopies = true;
                        _world.Enqueue(a with { By = c.Player });
                        _world.ApplyPending();
                        if (a is SetConsentAction) KeepConsent(c.Player);
                        // A side game's commands can come several a second (a scene running): they don't resend the civic state.
                        broadcast = BuildDeltas(forceAlerts: a is not SideGameAction);
                        broadcast.AddRange(TakeCopies());
                    }
                    else reply = Error(msg.Seq, "Unknown action.");
                    break;
                case P.ClientMessage.BodyOneofCase.Why:
                    reply = new P.ServerMessage { Why = Mapping.Why(_world, _world.Why(msg.Why.X, msg.Why.Y)) };
                    break;
                case P.ClientMessage.BodyOneofCase.Inspect:
                    reply = new P.ServerMessage { Inspect = InspectWithResidents(msg.Inspect.X, msg.Inspect.Y) };
                    break;
                case P.ClientMessage.BodyOneofCase.Overlay:
                    reply = new P.ServerMessage { Overlay = Mapping.Overlay(_world.Overlay((OverlayKind)(int)msg.Overlay.Kind)) };
                    break;
                case P.ClientMessage.BodyOneofCase.Demand:
                    reply = new P.ServerMessage { Demand = Mapping.Demand(_world.DemandReport()) };
                    break;
                case P.ClientMessage.BodyOneofCase.Advisors:
                    reply = new P.ServerMessage { Advisors = Mapping.Advisors(_world) };
                    break;
                case P.ClientMessage.BodyOneofCase.Ordinances:
                    reply = new P.ServerMessage { Ordinances = Mapping.Ordinances(_world.OrdinanceViews()) };
                    break;
                case P.ClientMessage.BodyOneofCase.Encyclopedia:
                    reply = new P.ServerMessage { Encyclopedia = Mapping.Encyclopedia(_world.Encyclopedia()) };
                    break;
                case P.ClientMessage.BodyOneofCase.Forecast:
                {
                    // One at a time per client: each copies the whole city.
                    if (Interlocked.Exchange(ref c.Forecasting, 1) == 1) { reply = Error(msg.Seq, "A forecast is already running."); break; }
                    // Copy the city under the gate, then run the copy on a worker so the live city keeps ticking.
                    var snapshot = _world.Snapshot();
                    var plan = msg.Forecast.Plan.Take(200).Select(Mapping.ToSim).Where(a => a != null).Select(a => a!).ToList();
                    int months = msg.Forecast.Months is > 0 and <= 120 ? msg.Forecast.Months : 12;
                    _ = Task.Run(async () =>
                    {
                        P.ServerMessage m;
                        try
                        {
                            using var copy = World.FromSnapshot(_content, snapshot);
                            m = new P.ServerMessage { Forecast = Mapping.Forecast(copy.RunForecast(months, plan), plan.Count > 0) };
                        }
                        catch (Exception e) { _log.LogWarning(e, "forecast failed"); m = Error(0, "The forecast failed."); }
                        finally { Volatile.Write(ref c.Forecasting, 0); }
                        m.ReplyTo = msg.Seq;
                        await SendAsync(c, m);
                    });
                    break;
                }
                case P.ClientMessage.BodyOneofCase.Hello:
                    break;
            }
        }
        if (reply != null)
        {
            reply.ReplyTo = msg.Seq;
            await SendAsync(c, reply);
        }
        if (broadcast != null)
            foreach (var (cl, m) in broadcast) await SendAsync(cl, m);
    }

    /// <summary>
    /// Copies of the city made by de-links: each is kept in the saves, under a name that says whose copy it is, and
    /// sent to its player on every connection they have open. Caller holds the gate.
    /// </summary>
    List<(Client, P.ServerMessage)> TakeCopies()
    {
        var list = new List<(Client, P.ServerMessage)>();
        foreach (var (player, copy) in _world.Copies)
        {
            using (copy)
            {
                string name = CopyName(player);
                byte[] save = Mapping.Save(copy);
                try { File.WriteAllBytes(SavePath(name), save); }
                catch (IOException e) { _log.LogWarning(e, "couldn't keep {Player}'s copy", player); }
                var msg = new P.ServerMessage { DelinkCopy = new P.DelinkCopy { Name = name, Save = ByteString.CopyFrom(save) } };
                foreach (var c in _clients.Where(c => c.Player == player)) list.Add((c, msg));
            }
        }
        _world.Copies.Clear();
        return list;
    }

    /// <summary>A save name for a player's copy: the city, the player and the day, cut to what save names allow.</summary>
    string CopyName(string player)
    {
        string clean(string t) => new string(t.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
        string tail = $" {clean(player)} copy {_world.Tick}";
        string city = clean(_world.Name);
        if (city.Length + tail.Length > 40) city = city[..Math.Max(0, 40 - tail.Length)].TrimEnd();
        string name = (city + tail).Trim();
        return name.Length > 40 ? name[..40] : name;
    }

    [GeneratedRegex("^[A-Za-z0-9 _-]{1,40}$")]
    private static partial Regex SaveName();

    internal static bool ValidName(string name) => SaveName().IsMatch(name);

    string SavePath(string name) => Path.Combine(_saveDir, name + ".toycity");

    /// <summary>Stops this city's timers. The world itself belongs to the region and is disposed with it.</summary>
    public void Dispose() => _awayTimer?.Dispose();
}
