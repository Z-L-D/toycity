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

    /// <summary>Milliseconds per tick (one day) at speeds 1, 2 and 3.</summary>
    static readonly int[] TickMs = { 0, 500, 150, 50 };

    public SimHost(ContentDb content, string saveDir, ILogger<SimHost> log)
    {
        _content = content;
        _saveDir = saveDir;
        _log = log;
        Directory.CreateDirectory(saveDir);
        _world = NewWorld("Toyville", 2026, 128, starter: true);
        LoadAgentTiers();
        _thread = new Thread(Loop) { IsBackground = true, Name = "sim" };
        _thread.Start();
    }

    World NewWorld(string name, ulong seed, int size, bool starter)
    {
        var w = World.Create(_content, seed, size, name);
        if (starter) StarterTown.Apply(w);
        w.ApplyPending();
        return w;
    }

    sealed class Client
    {
        public required WebSocket Socket;
        public readonly DeltaTracker Tracker = new();
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }

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
        foreach (var c in _clients) list.Add((c, new P.ServerMessage { Delta = c.Tracker.Next(_world, _speed, notices) }));
        if (forceAlerts || _clock.ElapsedMilliseconds - _alertsAt >= 500)
        {
            _alertsAt = _clock.ElapsedMilliseconds;
            var alerts = Mapping.Alerts(_world.Alerts());
            string sig = alerts.ToString();
            if (forceAlerts || sig != _alertSig)
            {
                _alertSig = sig;
                _alerts = alerts;
                foreach (var c in _clients) list.Add((c, new P.ServerMessage { Alerts = alerts }));
            }
        }
        // Petitions, deals, unlocks and headlines change at most monthly or on an action, so a cheap signature will do.
        string civicSig = $"{_world.HeadlineSeq}/{_world.Approval}/{_world.TypeUnlocked.Count(u => u)}/{string.Join(",", _world.Prompts.Select(p => p.Id * 8 + (int)p.Status))}/{_world.Tick / World.DaysPerMonth}";
        if (forceAlerts || civicSig != _civicSig)
        {
            _civicSig = civicSig;
            var civic = Mapping.Civic(_world);
            foreach (var c in _clients) list.Add((c, new P.ServerMessage { Civic = civic }));
        }
        if (RosterIfChanged() is { } roster)
            foreach (var c in _clients) list.Add((c, new P.ServerMessage { Agents = roster }));
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
        P.ServerMessage full;
        lock (_gate)
        {
            _clients.Add(client);
            full = FullStateMessage(client);
        }
        await SendAsync(client, new P.ServerMessage { Welcome = new P.Welcome { ProtocolVersion = Mapping.ProtocolVersion, ServerName = "ToyCity" } });
        await SendAsync(client, full);
        await SendAsync(client, new P.ServerMessage { Saves = ListSaves() });
        P.AlertList alerts;
        lock (_gate) alerts = Mapping.Alerts(_world.Alerts());
        await SendAsync(client, new P.ServerMessage { Alerts = alerts });
        P.CivicState civicNow;
        lock (_gate) civicNow = Mapping.Civic(_world);
        await SendAsync(client, new P.ServerMessage { Civic = civicNow });
        P.AgentRoster rosterNow;
        lock (_gate) rosterNow = Roster(Agents());
        await SendAsync(client, new P.ServerMessage { Agents = rosterNow });

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var data = ms.ToArray();
                ms.SetLength(0);
                P.ClientMessage msg;
                try { msg = P.ClientMessage.Parser.ParseFrom(data); }
                catch (InvalidProtocolBufferException) { await SendAsync(client, Error(0, "Could not read that message.")); continue; }
                await HandleAsync(client, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_gate) _clients.Remove(client);
        }
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
                        _world.Enqueue(a);
                        if (_speed == 0)
                        {
                            _world.ApplyPending();
                            broadcast = BuildDeltas(forceAlerts: true);
                        }
                    }
                    else reply = Error(msg.Seq, "Unknown action.");
                    break;
                case P.ClientMessage.BodyOneofCase.Why:
                    reply = new P.ServerMessage { Why = Mapping.Why(_world.Why(msg.Why.X, msg.Why.Y)) };
                    break;
                case P.ClientMessage.BodyOneofCase.Inspect:
                    reply = new P.ServerMessage { Inspect = InspectWithResidents(msg.Inspect.X, msg.Inspect.Y) };
                    break;
                case P.ClientMessage.BodyOneofCase.Overlay:
                    reply = new P.ServerMessage { Overlay = Mapping.Overlay(_world.Overlay((OverlayKind)(int)msg.Overlay.Kind)) };
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
                        _world = Mapping.Load(_content, File.ReadAllBytes(SavePath(msg.Load.Name)));
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
                    int size = n.Size is 64 or 128 or 256 ? n.Size : 128;
                    ulong seed = n.Seed != 0 ? n.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                    _world = NewWorld(string.IsNullOrWhiteSpace(n.Name) ? "New City" : n.Name.Trim(), seed, size, n.StarterTown);
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
                    // Copy the city under the gate, then run the copy on a worker so the live city keeps ticking.
                    var snapshot = _world.Snapshot();
                    var plan = msg.Forecast.Plan.Select(Mapping.ToSim).Where(a => a != null).Select(a => a!).ToList();
                    int months = msg.Forecast.Months is > 0 and <= 120 ? msg.Forecast.Months : 12;
                    _ = Task.Run(async () =>
                    {
                        P.ServerMessage m;
                        try
                        {
                            var copy = World.FromSnapshot(_content, snapshot);
                            m = new P.ServerMessage { Forecast = Mapping.Forecast(copy.RunForecast(months, plan), plan.Count > 0) };
                        }
                        catch (Exception e) { _log.LogWarning(e, "forecast failed"); m = Error(0, "The forecast failed."); }
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
        _thread.Join(2000);
    }
}
