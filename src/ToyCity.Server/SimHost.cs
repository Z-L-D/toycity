using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Google.Protobuf;
using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>
/// Owns the one running city: a fixed-tick loop on its own thread, the connected clients and the save folder.
/// Every touch of the world happens under <see cref="_gate"/>.
/// </summary>
public sealed partial class SimHost : IDisposable
{
    readonly object _gate = new();
    readonly ContentDb _content;
    readonly string _saveDir;
    readonly ILogger<SimHost> _log;
    readonly HostConfig _host;
    readonly AccountStore? _accounts;
    readonly List<Client> _clients = new();
    readonly Thread _thread;
    volatile bool _running = true;
    World _world;
    int _speed = 1;
    // Alerts are recomputed at most twice a second and sent only when they change.
    string _alertSig = "";
    P.AlertList _alerts = new();
    long _alertsAt = -1;
    string _civicSig = "";
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Milliseconds per tick (one day) at speeds 1, 2 and 3: a game year takes an hour, twenty minutes or five.</summary>
    static readonly int[] TickMs = { 0, 10_000, 3_333, 833 };

    /// <summary>Who a solo player is: the name every city before Beta 2 gave its mayor.</summary>
    public const string LocalPlayer = "player";

    /// <summary>Sign-in attempts a connection gets before the server hangs up.</summary>
    const int MaxSignInFailures = 5;

    public SimHost(ContentDb content, string saveDir, ILogger<SimHost> log, HostConfig? host = null, AccountStore? accounts = null)
    {
        _content = content;
        _saveDir = saveDir;
        _log = log;
        _host = host ?? HostConfig.Solo();
        _accounts = accounts ?? (_host.Dedicated ? new AccountStore(Path.Combine(saveDir, "accounts.json")) : null);
        Directory.CreateDirectory(saveDir);
        UseRemoteSideGames();
        _world = NewWorld("Toyville", 2026, 128, starter: true);
        EnsureMayor();
        ApplyHostRules();
        LoadAgentTiers();
        StartAwayTimer();
        _thread = new Thread(Loop) { IsBackground = true, Name = "sim" };
        _thread.Start();
    }

    World NewWorld(string name, ulong seed, int size, bool starter, bool benchmark = false)
    {
        var w = World.Create(_content, seed, size, name);
        if (benchmark) BenchmarkCity.Apply(w);
        else if (starter) StarterTown.Apply(w);
        w.ApplyPending();
        AttachSideGames(w);
        return w;
    }

    sealed class Client
    {
        public required WebSocket Socket;
        public readonly DeltaTracker Tracker = new();
        public readonly SemaphoreSlim SendLock = new(1, 1);
        /// <summary>Who this connection plays as; empty until signed in.</summary>
        public string Player = "";
        public bool Admin;
        public int SignInFailures;
        public int Malformed;
        public readonly RateLimiter Limiter = new(burst: 200, perSecond: 60);
        /// <summary>Set while a forecast runs for this client: one at a time.</summary>
        public int Forecasting;
        public bool SignedIn => Player.Length > 0;
    }

    /// <summary>The largest message a client may send; anything bigger is hostile or broken.</summary>
    const int MaxMessageBytes = 1 << 20;
    /// <summary>Unreadable messages a connection may send before it is dropped.</summary>
    const int MaxMalformed = 3;

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        long next = 0;
        while (_running)
        {
            int speed = _speed;
            if (speed == 0) { Thread.Sleep(50); next = sw.ElapsedMilliseconds; continue; }
            long now = sw.ElapsedMilliseconds;
            if (now < next) { Thread.Sleep((int)Math.Min(20, next - now)); continue; }
            next = Math.Max(next + TickMs[speed], now - 1000);

            List<(Client, P.ServerMessage)> outbox;
            lock (_gate)
            {
                _world.Step();
                Agents().Daily();
                if (_world.Day == 1 && _world.Month == 1) AutoSave();
                outbox = BuildDeltas();
            }
            foreach (var (c, m) in outbox) _ = SendAsync(c, m);
        }
    }

    void AutoSave()
    {
        try { File.WriteAllBytes(SavePath("autosave"), Mapping.Save(_world)); }
        catch (Exception e) { _log.LogWarning(e, "autosave failed"); }
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
        string civicSig = $"{_world.HeadlineSeq}/{_world.Approval}/{_world.TypeUnlocked.Count(u => u)}/{string.Join(",", _world.Prompts.Select(p => p.Id * 8 + (int)p.Status))}/{_world.Tick / World.DaysPerMonth}/{string.Join(",", _world.Seats.Select(s => $"{s.Owner}:{(int)s.Level}:{(int)s.Style}:{s.SpendCap}:{s.Report?.ToTick}:{(int)s.AwayLevel}:{(int)s.AwayStyle}:{s.AutoAway}"))}/{string.Join(",", _world.Leases.Select(l => $"{l.Id}:{l.Terms.GetHashCode()}:{l.PendingMonths}:{l.Arrears}:{l.BadStanding}:{l.CureMonthsLeft}:{l.Jobs}:{l.LastRevenue}:{l.EvictMonths}"))}/{_world.SideGames.Count}/{_world.ConsentSig()}/{string.Join(",", _world.Standings.Select(s => $"{s.Rating}:{s.Statue}:{s.ExclusiveUntil > _world.Tick}"))}/{_world.RolesSig()}";
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

    static async Task SendAsync(Client c, P.ServerMessage m)
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

    public async Task RunClientAsync(WebSocket socket, CancellationToken ct)
    {
        var client = new Client { Socket = socket };
        bool solo = !_host.Dedicated;
        if (solo) { client.Player = LocalPlayer; client.Admin = true; }
        await SendAsync(client, new P.ServerMessage
        {
            Welcome = new P.Welcome
            {
                ProtocolVersion = Mapping.ProtocolVersion, ServerName = _host.Name, SignInRequired = !solo,
                InviteRequired = _host.Join == JoinPolicy.Invite, Player = client.Player, Admin = client.Admin,
            },
        });
        if (solo) await JoinAsync(client);

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
                    await SendAsync(client, Error(0, "Could not read that message."));
                    continue;
                }
                try
                {
                    if (msg.BodyCase == P.ClientMessage.BodyOneofCase.SignIn)
                    {
                        if (!await SignInAsync(client, msg)) break;
                    }
                    else if (!client.SignedIn)
                    {
                        if (msg.BodyCase != P.ClientMessage.BodyOneofCase.Hello) await SendAsync(client, Error(msg.Seq, "Sign in first."));
                    }
                    else await HandleAsync(client, msg);
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
            List<(Client, P.ServerMessage)> outbox;
            lock (_gate)
            {
                _clients.Remove(client);
                Left(client);
                outbox = PresenceIfChanged().ToList();
            }
            foreach (var (c, m) in outbox) _ = SendAsync(c, m);
        }
    }

    /// <summary>Joins a signed-in connection to the city: it gets the whole state now and every delta from here on.</summary>
    async Task JoinAsync(Client client)
    {
        P.ServerMessage full;
        List<(Client, P.ServerMessage)> others;
        lock (_gate)
        {
            if (_host.Dedicated) { Returned(client.Player); BringConsent(client.Player); }
            _clients.Add(client);
            full = FullStateMessage(client);
            // Everyone else hears of the newcomer (and of any seats they took back); the newcomer gets it all below.
            _presenceSig = "";
            others = BuildDeltas(forceAlerts: true).Where(x => x.Item1 != client).ToList();
        }
        foreach (var (c, m) in others) _ = SendAsync(c, m);
        await SendAsync(client, full);
        P.Presence presence;
        lock (_gate) presence = Presence();
        await SendAsync(client, new P.ServerMessage { Presence = presence });
        await SendAsync(client, new P.ServerMessage { Saves = ListSaves() });
        P.AlertList alerts;
        lock (_gate) alerts = Mapping.Alerts(_world, _world.Alerts());
        await SendAsync(client, new P.ServerMessage { Alerts = alerts });
        P.CivicState civicNow;
        lock (_gate) civicNow = CivicFor(Mapping.Civic(_world), client.Player);
        await SendAsync(client, new P.ServerMessage { Civic = civicNow });
        P.AgentRoster rosterNow;
        List<P.ServerMessage> viewsNow;
        lock (_gate) { rosterNow = Roster(Agents()); viewsNow = ViewsFor(ChangedViews(all: true), client.Player).ToList(); }
        await SendAsync(client, new P.ServerMessage { Agents = rosterNow });
        foreach (var v in viewsNow) await SendAsync(client, v);
    }

    /// <summary>Answers a SignIn. False when the connection has failed too often and should be closed.</summary>
    async Task<bool> SignInAsync(Client client, P.ClientMessage msg)
    {
        var s = msg.SignIn;
        if (client.SignedIn) { await SendAsync(client, Error(msg.Seq, $"Already signed in as {client.Player}.")); return true; }
        if (_accounts == null) { await SendAsync(client, Error(msg.Seq, "This server has no accounts.")); return true; }
        SignInResult r;
        if (s.Token.Length > 0) r = _accounts.SignInWithToken(s.Token);
        else if (s.Create)
        {
            string name = s.Name.Trim();
            r = _host.MayCreate(name, s.InviteCode) is { } no ? new SignInResult(null, null, no) : _accounts.Create(name, s.Password);
            if (r.Ok)
            {
                _host.PromoteFirst(r.Name!);
                lock (_gate) EnsureMayor();
            }
        }
        else r = _accounts.SignIn(s.Name.Trim(), s.Password);
        if (!r.Ok)
        {
            client.SignInFailures++;
            await SendAsync(client, Error(msg.Seq, r.Error!));
            if (client.SignInFailures < MaxSignInFailures) return true;
            _log.LogInformation("closing a connection after {N} failed sign-ins", client.SignInFailures);
            return false;
        }
        client.Player = r.Name!;
        client.Admin = _host.IsAdmin(r.Name!);
        _log.LogInformation("{Player} signed in", client.Player);
        await SendAsync(client, new P.ServerMessage { ReplyTo = msg.Seq, SignedIn = new P.SignedIn { Name = client.Player, Token = r.Token!, Admin = client.Admin } });
        await JoinAsync(client);
        return true;
    }

    static P.ServerMessage Error(uint seq, string text) => new() { ReplyTo = seq, Error = new P.Error { Message = text } };

    async Task HandleAsync(Client c, P.ClientMessage msg)
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
                        _world.Enqueue(a with { By = c.Player });
                        _world.ApplyPending();
                        if (a is SetConsentAction) KeepConsent(c.Player);
                        // A side game's commands can come several a second (a scene running): they don't resend the civic state.
                        broadcast = BuildDeltas(forceAlerts: a is not SideGameAction);
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
                case P.ClientMessage.BodyOneofCase.Speed or P.ClientMessage.BodyOneofCase.Load or P.ClientMessage.BodyOneofCase.NewCity
                    when !c.Admin:
                case P.ClientMessage.BodyOneofCase.Save when !c.Admin && msg.Save.Name.Length > 0:
                    reply = Error(msg.Seq, "Only this server's admins can save, load, start a new city or change the speed.");
                    break;
                case P.ClientMessage.BodyOneofCase.Speed:
                    _speed = Math.Clamp(msg.Speed.Speed, 0, 3);
                    broadcast = BuildDeltas();
                    break;
                case P.ClientMessage.BodyOneofCase.Save:
                    if (msg.Save.Name.Length > 0)
                    {
                        if (!ValidName(msg.Save.Name)) { reply = Error(msg.Seq, "Save names can use letters, numbers, spaces, - and _."); break; }
                        File.WriteAllBytes(SavePath(msg.Save.Name), Mapping.Save(_world));
                        _world.Notices.Add(new SimNotice($"Saved as \"{msg.Save.Name}\".", "good"));
                        broadcast = BuildDeltas();
                    }
                    reply = new P.ServerMessage { Saves = ListSaves() };
                    break;
                case P.ClientMessage.BodyOneofCase.Load:
                    try
                    {
                        if (!ValidName(msg.Load.Name)) throw new InvalidDataException("bad save name");
                        var loaded = Mapping.Load(_content, File.ReadAllBytes(SavePath(msg.Load.Name)));
                        _world.Dispose();
                        _world = loaded;
                        AttachSideGames(_world);
                        Arrived(c.Player);
                        broadcast = _clients.Select(cl => (cl, FullStateMessage(cl))).Concat(BuildDeltas(forceAlerts: true)).ToList();
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or InvalidProtocolBufferException)
                    {
                        reply = Error(msg.Seq, $"Could not load \"{msg.Load.Name}\": {e.Message}");
                    }
                    break;
                case P.ClientMessage.BodyOneofCase.NewCity:
                {
                    var n = msg.NewCity;
                    int size = n.Benchmark ? BenchmarkCity.Size : n.Size is 64 or 128 or 256 ? n.Size : 128;
                    ulong seed = n.Seed != 0 ? n.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                    _world.Dispose();
                    _world = NewWorld(string.IsNullOrWhiteSpace(n.Name) ? "New City" : n.Name.Trim(), seed, size, n.StarterTown, n.Benchmark);
                    Arrived(c.Player);
                    broadcast = _clients.Select(cl => (cl, FullStateMessage(cl))).Concat(BuildDeltas(forceAlerts: true)).ToList();
                    break;
                }
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

    [GeneratedRegex("^[A-Za-z0-9 _-]{1,40}$")]
    private static partial Regex SaveName();

    static bool ValidName(string name) => SaveName().IsMatch(name);

    string SavePath(string name) => Path.Combine(_saveDir, name + ".toycity");

    P.SaveList ListSaves()
    {
        var list = new P.SaveList();
        foreach (var f in Directory.GetFiles(_saveDir, "*.toycity").OrderBy(f => f, StringComparer.Ordinal))
            list.Names.Add(Path.GetFileNameWithoutExtension(f));
        return list;
    }

    public void Dispose()
    {
        _running = false;
        _awayTimer?.Dispose();
        _thread.Join(2000);
    }
}
