using System.Net.WebSockets;
using Google.Protobuf;
using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>How a tile host finds its region: the region host's /tile-link, the tile, the key, and where its own players connect.</summary>
public sealed record FollowOptions(Uri Leader, int Col, int Row, string Key, string Address);

/// <summary>
/// Federated hosting, the tile host's side (part 8). This server runs one city of a region whose clock another server
/// keeps. It serves that city's players as usual, but its day starts when the region's host says so (with what the
/// region tells the city) and ends by reporting every action the city applied; sign-ins and region-wide messages
/// (deals, founding, the clock, moving) go to the region's host, and the region map comes from it.
/// </summary>
public sealed partial class RegionHost
{
    readonly FollowOptions? _follow;
    ClientWebSocket? _leader;
    readonly SemaphoreSlim _leaderSend = new(1, 1);
    readonly Dictionary<uint, TaskCompletionSource<P.LeaderMessage>> _asked = new();
    uint _linkSeq;
    int _nextConn;
    P.RegionState? _leaderState;
    readonly TaskCompletionSource _welcomed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Players reach the region's host here: from its welcome, else worked out from the link's address.</summary>
    string _leaderAddress = "";
    /// <summary>How much of the city's log the region host has: everything after it goes in the next day's report.</summary>
    int _reported;

    bool Following => _follow != null;

    /// <summary>A tile host: joins the region at <paramref name="follow"/> and runs the city it's given.</summary>
    public RegionHost(ContentDb content, string saveDir, ILogger<RegionHost> log, HostConfig host, FollowOptions follow)
    {
        _content = content;
        _saveDir = saveDir;
        _log = log;
        _host = host;
        _follow = follow;
        Directory.CreateDirectory(saveDir);
        var ws = new UriBuilder(follow.Leader) { Path = "/ws", Query = "" };
        _leaderAddress = ws.Uri.ToString();
        _region = Region.Create(content, "…", 1, 1, 1, 64);
        _cities = new SimHost?[1];
        InitLinks();
        _ = Task.Run(FollowAsync);
        if (!_welcomed.Task.Wait(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException($"the region host at {follow.Leader} didn't take this tile host");
    }

    async Task FollowAsync()
    {
        while (_running)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(_follow!.Leader, CancellationToken.None);
                _leader = ws;
                await SendLeaderAsync(new P.LinkMessage { Hello = new P.TileHello { Col = _follow.Col, Row = _follow.Row, Key = _follow.Key, Address = _follow.Address } });
                var buffer = new byte[256 * 1024];
                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && _running)
                {
                    var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var m = P.LeaderMessage.Parser.ParseFrom(ms.ToArray());
                    ms.SetLength(0);
                    await FromLeaderAsync(m);
                }
            }
            catch (Exception e) when (e is WebSocketException or InvalidProtocolBufferException or IOException)
            {
                _log.LogWarning("the link to the region host failed: {Message}", e.Message);
            }
            catch (Exception e)
            {
                _log.LogError(e, "the link to the region host broke");
            }
            finally
            {
                _leader = null;
                ws.Dispose();
            }
            if (_running)
            {
                _log.LogWarning("lost the region host; trying again");
                await Task.Delay(1000);
            }
        }
    }

    async Task SendLeaderAsync(P.LinkMessage m)
    {
        var ws = _leader;
        if (ws == null) return;
        var bytes = m.ToByteArray();
        await _leaderSend.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
        finally { _leaderSend.Release(); }
    }

    /// <summary>Asks the region host something and waits for its answer.</summary>
    async Task<P.LeaderMessage?> AskLeaderAsync(P.LinkMessage m)
    {
        var tcs = new TaskCompletionSource<P.LeaderMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_asked) { m.Seq = ++_linkSeq; _asked[m.Seq] = tcs; }
        await SendLeaderAsync(m);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        lock (_asked) _asked.Remove(m.Seq);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    async Task FromLeaderAsync(P.LeaderMessage m)
    {
        if (m.ReplyTo != 0)
        {
            TaskCompletionSource<P.LeaderMessage>? tcs;
            lock (_asked) _asked.TryGetValue(m.ReplyTo, out tcs);
            if (tcs != null && m.BodyCase != P.LeaderMessage.BodyOneofCase.Welcome) { tcs.TrySetResult(m); return; }
        }
        switch (m.BodyCase)
        {
            case P.LeaderMessage.BodyOneofCase.Welcome:
                Welcome(m.Welcome);
                break;
            case P.LeaderMessage.BodyOneofCase.DayStart:
                await DayAsync(m.DayStart);
                break;
            case P.LeaderMessage.BodyOneofCase.Region:
            {
                var outbox = WithAll(() =>
                {
                    _leaderState = Addressed(m.Region);
                    _speed = m.Region.Speed;
                    return Cities.SelectMany(c => c.Clients.Select(cl => (cl, new P.ServerMessage { Region = _leaderState }))).ToList();
                });
                foreach (var (c, msg) in outbox) await SimHost.SendAsync(c, msg);
                break;
            }
            case P.LeaderMessage.BodyOneofCase.Reply:
            {
                var reply = m.Reply.Message;
                if (reply.Region != null) reply.Region = Addressed(reply.Region);
                if (reply.Redirect is { Address.Length: 0 } rd) rd.Address = _leaderAddress;
                var to = WithAll(() => Cities.SelectMany(c => c.Clients).FirstOrDefault(c => c.Conn == m.Reply.Conn));
                if (to != null) await SimHost.SendAsync(to, reply);
                break;
            }
        }
    }

    /// <summary>The region host's map with every tile's address filled in: its own tiles are reached at its /ws.</summary>
    P.RegionState Addressed(P.RegionState s)
    {
        s = s.Clone();
        foreach (var t in s.Tiles)
            if (t.Address.Length == 0 && !(t.Col == _follow!.Col && t.Row == _follow.Row)) t.Address = _leaderAddress;
        return s;
    }

    /// <summary>The region host took this tile host, with the city as it has it. Again after a reconnect.</summary>
    void Welcome(P.TileWelcome w)
    {
        if (w.Error.Length > 0)
        {
            _log.LogError("the region host refused this tile host: {Why}", w.Error);
            return;
        }
        if (w.LeaderAddress.Length > 0) _leaderAddress = w.LeaderAddress;
        var city = Mapping.Load(_content, w.Save.ToByteArray());
        var region = Region.Restore(_content, w.Name, w.Seed, w.Cols, w.Rows, w.TileSize, w.Tick, new[] { city });
        region.Follower = true;
        var outbox = WithAll(() =>
        {
            _reported = city.Log.Count;
            return Replace(region, "");
        });
        foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
        _log.LogInformation("running {City} for the region {Region} from day {Tick}", city.Name, w.Name, w.Tick);
        _welcomed.TrySetResult();
    }

    /// <summary>One day, when the region host says: what the region tells the city, then the step, then the report.</summary>
    async Task DayAsync(P.DayStart ds)
    {
        var (outbox, end) = WithAll<(List<(Client, P.ServerMessage)>, P.DayEnd?)>(() =>
        {
            var w = _region.Cities.FirstOrDefault();
            if (w == null || _region.Tick != ds.Tick)
            {
                _log.LogError("the region host's day {Theirs} isn't this host's {Mine}", ds.Tick, _region.Tick);
                return (new(), null);
            }
            foreach (var r in ds.Actions)
                if (Mapping.Action(r) is { } a) w.Enqueue(a);
            foreach (var n in ds.Notices) w.Notices.Add(new SimNotice(n.Text, n.Severity, n.X, n.Y));
            _speed = ds.Speed;
            Region.StepCities(new[] { w });
            _region.EndDay();
            var end = new P.DayEnd { Tick = ds.Tick };
            for (int k = _reported; k < w.Log.Count; k++) end.Log.Add(Mapping.Record(w.Log[k]));
            _reported = w.Log.Count;
            if (w.Day == 1) { end.Monthly = true; end.Hash = w.StateHash(); }
            lock (_online) end.Online.AddRange(_online.Keys);
            var list = new List<(Client, P.ServerMessage)>();
            foreach (var c in Cities) list.AddRange(c.AfterStep());
            return (list, end);
        });
        if (end != null) await SendLeaderAsync(new P.LinkMessage { DayEnd = end });
        foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
    }

    P.RegionState FollowerState() => _leaderState ?? new P.RegionState { Name = _region.Name, Cols = _region.Cols, Rows = _region.Rows, Tick = _region.Tick, HereCol = _follow!.Col, HereRow = _follow.Row };

    /// <summary>A tile host's players sign in against the region's accounts.</summary>
    async Task<SignInResult?> FollowerSignInAsync(P.SignIn s)
    {
        var answer = await AskLeaderAsync(new P.LinkMessage { SignIn = s });
        if (answer == null) return new SignInResult(null, null, "The region's host didn't answer. Try again in a moment.");
        if (answer.SignedIn is { } ok) return new SignInResult(ok.Name, ok.Token, null) { Admin = ok.Admin };
        return new SignInResult(null, null, answer.Error?.Message ?? "Couldn't sign in.");
    }

    /// <summary>Region-wide messages from a tile host's player: the map is answered here, the rest goes to the region host.</summary>
    async Task<bool> FollowerRegionAsync(Client c, P.ClientMessage msg)
    {
        switch (msg.BodyCase)
        {
            case P.ClientMessage.BodyOneofCase.Region:
            case P.ClientMessage.BodyOneofCase.GoTo when msg.GoTo.Col == _follow!.Col && msg.GoTo.Row == _follow.Row:
            {
                var state = WithAll(FollowerState);
                await SimHost.SendAsync(c, new P.ServerMessage { ReplyTo = msg.Seq, Region = state });
                return true;
            }
            case P.ClientMessage.BodyOneofCase.Speed or P.ClientMessage.BodyOneofCase.Save or P.ClientMessage.BodyOneofCase.Load
                or P.ClientMessage.BodyOneofCase.NewCity or P.ClientMessage.BodyOneofCase.NewRegion or P.ClientMessage.BodyOneofCase.FoundCity
                or P.ClientMessage.BodyOneofCase.GoTo or P.ClientMessage.BodyOneofCase.Deal or P.ClientMessage.BodyOneofCase.Mayor:
                if (_leader == null) { await SimHost.SendAsync(c, SimHost.Error(msg.Seq, "The region's host can't be reached just now.")); return true; }
                await SendLeaderAsync(new P.LinkMessage { Forward = new P.LinkForward { Conn = c.Conn, Player = c.Player, Admin = c.Admin, Message = msg } });
                return true;
            default:
                return false;
        }
    }
}
