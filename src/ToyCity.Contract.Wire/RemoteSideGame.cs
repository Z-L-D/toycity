using System.Collections.Concurrent;
using System.Net.Sockets;
using Google.Protobuf;
using C = ToyCity.Contract;
using W = ToyCity.Protocol.Contract;

namespace ToyCity.Protocol;

/// <summary>
/// A side game running in another process, seen by the city as if it were in its own: each call goes over the socket
/// (protocol/sidegamehost.proto) and waits for the answer. A tick or a command that isn't answered by the deadline is
/// aborted on both sides and reported as <see cref="C.SideGameMissedException"/>, so the city stays deterministic.
/// One instance is one game on the host; a copy of the city (a forecast, say) opens its own.
/// </summary>
public sealed class RemoteSideGame : C.ISideGame, IDisposable
{
    /// <summary>How long opening, saving, loading and aborting may take: these aren't held to the tick's deadline.</summary>
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(15);

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly BlockingCollection<W.HostReply> _replies = new();
    readonly TimeSpan _deadline;
    readonly Dictionary<string, byte[]> _views = new();
    ulong _seq;
    volatile bool _broken;
    string _why = "";

    public C.Manifest Manifest { get; }
    public string Address { get; }

    public RemoteSideGame(string host, int port, string gameId, TimeSpan deadline)
    {
        Address = $"{host}:{port}";
        _deadline = deadline;
        _tcp = new TcpClient { NoDelay = true };
        if (!_tcp.ConnectAsync(host, port).Wait(Slow)) { _tcp.Dispose(); throw new IOException($"the side-game host at {Address} didn't answer"); }
        _stream = _tcp.GetStream();
        new Thread(ReadLoop) { IsBackground = true, Name = $"side game {gameId} at {Address}" }.Start();
        var r = Call(new W.CityCall { Open = new W.Open { Game = gameId, ContractVersion = C.ContractInfo.Version } }, null, Slow)
            ?? throw new IOException($"the side-game host at {Address} didn't open {gameId}: {_why}");
        if (r.KindCase != W.HostReply.KindOneofCase.Manifest) throw new IOException($"the side-game host at {Address} can't run {gameId}: {r.Error}");
        Manifest = ContractWire.FromWire(r.Manifest);
    }

    void ReadLoop()
    {
        try
        {
            while (Framing.Read(_stream, W.HostReply.Parser) is { } m) _replies.Add(m);
            _why = "the host closed the connection";
        }
        catch (Exception e) { _why = e.Message; }
        _broken = true;
        _replies.CompleteAdding();
    }

    static W.CityShape Shape(C.ICityQueries city) => new() { Width = city.Width, Height = city.Height };

    public void NewGame(ulong seed) => Must(new W.CityCall { NewGame = seed }, "start a new game");

    public void Tick(C.CityToGame input, C.ICityQueries city, C.GameToCity output)
    {
        var r = Call(new W.CityCall { Tick = new W.TickCall { Input = ContractWire.ToWire(input), City = Shape(city) } }, city, _deadline)
            ?? throw new C.SideGameMissedException($"{Manifest.Name} didn't finish its day in time ({_why}).");
        if (r.KindCase != W.HostReply.KindOneofCase.Output) throw new C.SideGameMissedException($"{Manifest.Name} failed its day: {r.Error}");
        var o = ContractWire.FromWire(r.Output);
        output.Lines.AddRange(o.Lines);
        foreach (var (k, v) in o.JobsByLease) output.JobsByLease[k] = v;
        output.Incidents.AddRange(o.Incidents);
        foreach (var (k, v) in o.Paid) output.Paid[k] = v;
        foreach (var (k, v) in o.RevenueByLease) output.RevenueByLease[k] = v;
        output.LeaseRequests.AddRange(o.LeaseRequests);
        output.LeaseReleases.AddRange(o.LeaseReleases);
        output.AuthorityActions.AddRange(o.AuthorityActions);
        output.Responses.AddRange(o.Responses);
        output.Holding.AddRange(o.Holding);
        foreach (var (k, v) in o.FreightPicked) output.FreightPicked[k] = v;
        foreach (var (k, v) in o.FreightDropped) output.FreightDropped[k] = v;
        output.ServiceQuality = o.ServiceQuality;
        output.Status = o.Status;
    }

    public C.CommandResult Command(string seat, byte[] payload, C.ICityQueries city)
    {
        var r = Call(new W.CityCall { Command = new W.CommandCall { Seat = seat, Payload = ByteString.CopyFrom(payload), City = Shape(city) } }, city, _deadline)
            ?? throw new C.SideGameMissedException($"{Manifest.Name} didn't answer in time ({_why}).");
        return r.KindCase == W.HostReply.KindOneofCase.Result ? new C.CommandResult(r.Result.Ok, r.Result.Message) : new C.CommandResult(false, r.Error);
    }

    /// <summary>A view is only a picture: when the host is slow, the last one it sent will do.</summary>
    public byte[] View(string seat)
    {
        var r = Call(new W.CityCall { View = seat }, null, _deadline);
        if (r?.KindCase == W.HostReply.KindOneofCase.Data) return _views[seat] = r.Data.ToByteArray();
        return _views.GetValueOrDefault(seat) ?? Array.Empty<byte>();
    }

    public void Save(BinaryWriter w)
    {
        var r = Call(new W.CityCall { Save = true }, null, Slow);
        if (r?.KindCase != W.HostReply.KindOneofCase.Data) throw new IOException($"{Manifest.Name} couldn't be saved: {r?.Error ?? _why}");
        w.Write(r.Data.Span);
    }

    public void Load(BinaryReader r)
    {
        var rest = new MemoryStream();
        r.BaseStream.CopyTo(rest);
        Must(new W.CityCall { Load = ByteString.CopyFrom(rest.ToArray()) }, "load");
    }

    void Must(W.CityCall call, string what)
    {
        var r = Call(call, null, Slow);
        if (r?.KindCase != W.HostReply.KindOneofCase.Done) throw new IOException($"{Manifest.Name} couldn't {what}: {r?.Error ?? _why}");
    }

    /// <summary>
    /// Sends one call and waits for its answer, answering the game's questions about the city on the way. Null when
    /// the deadline passed (the call is then aborted on the host) or the connection is gone.
    /// </summary>
    W.HostReply? Call(W.CityCall call, C.ICityQueries? city, TimeSpan limit)
    {
        if (_broken) return null;
        call.Seq = ++_seq;
        lock (_stream)
        {
            try { Framing.Write(_stream, call); }
            catch (Exception e) { _why = e.Message; _broken = true; return null; }
        }
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !TryTake(left, out var m))
            {
                if (!_broken) { _why = $"no answer within {limit.TotalMilliseconds:0} ms"; Abort(call.Seq); }
                return null;
            }
            if (m.Seq != call.Seq) continue;  // the late answer to a call already aborted
            if (m.KindCase != W.HostReply.KindOneofCase.Query) return m;
            if (city == null) { Abort(call.Seq); _why = "the game asked about the city outside a tick"; return null; }
            Send(new W.CityCall { Seq = call.Seq, Answer = Answer(m.Query, city) });
        }
    }

    bool TryTake(TimeSpan wait, out W.HostReply m)
    {
        try { return _replies.TryTake(out m!, wait); }
        catch (InvalidOperationException) { m = null!; return false; }  // completed: the connection is gone
    }

    static W.QueryAnswer Answer(W.Query q, C.ICityQueries city)
    {
        var a = new W.QueryAnswer();
        switch (q.KindCase)
        {
            case W.Query.KindOneofCase.Route:
                if (city.Route(q.Route.FromCell, q.Route.ToCell) is { } route) a.Route = ContractWire.ToWire(route);
                break;
            case W.Query.KindOneofCase.Track:
                if (city.RouteFor((C.TransitMode)q.Track.Mode, q.Track.FromBuilding, q.Track.ToBuilding) is { } track) a.Route = ContractWire.ToWire(track);
                break;
            case W.Query.KindOneofCase.DistrictOf: a.District = city.DistrictOf(q.DistrictOf); break;
            case W.Query.KindOneofCase.Hash: a.Hash = city.Hash(q.Hash.A, q.Hash.B, q.Hash.C, q.Hash.D); break;
            case W.Query.KindOneofCase.Area:
                int w = Math.Clamp(q.Area.W, 0, 256), h = Math.Clamp(q.Area.H, 0, 256);
                a.Area = ByteString.CopyFrom(city.Area(q.Area.X0, q.Area.Y0, w, h).Select(k => (byte)k).ToArray());
                break;
        }
        return a;
    }

    /// <summary>Tells the host to undo a call and waits until it has, so the two sides agree. A host that won't is cut off.</summary>
    void Abort(ulong seq)
    {
        Send(new W.CityCall { Seq = seq, Abort = true });
        var until = DateTime.UtcNow + Slow;
        while (!_broken)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !TryTake(left, out var m)) break;
            if (m.Seq == seq && m.KindCase == W.HostReply.KindOneofCase.Aborted) return;
        }
        _why = "the host didn't undo a late call";
        _broken = true;
        try { _tcp.Close(); } catch { }
    }

    void Send(W.CityCall c)
    {
        lock (_stream)
        {
            try { Framing.Write(_stream, c); }
            catch (Exception e) { _why = e.Message; _broken = true; }
        }
    }

    public void Dispose()
    {
        _broken = true;
        try { _tcp.Close(); } catch { }
        _tcp.Dispose();
    }
}
