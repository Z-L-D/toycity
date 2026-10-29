using System.Net.Sockets;
using C = ToyCity.Contract;
using W = ToyCity.Protocol.Contract;

namespace ToyCity.Protocol;

/// <summary>
/// The host's end of protocol/sidegamehost.proto: accepts cities' connections and runs one game for each, answering
/// their calls in order. Before every call that changes the game it keeps a saved copy, so an aborted call can be
/// undone exactly.
/// </summary>
public sealed class SideGameServer
{
    readonly Func<string, C.ISideGame?> _make;
    readonly Action<string>? _log;

    /// <param name="make">A fresh game for an id, or null when this host doesn't run it.</param>
    public SideGameServer(Func<string, C.ISideGame?> make, Action<string>? log = null)
    {
        _make = make;
        _log = log;
    }

    public async Task ServeAsync(TcpListener listener, CancellationToken stop)
    {
        listener.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                client.NoDelay = true;
                new Thread(() => Session(client)) { IsBackground = true, Name = "side-game session" }.Start();
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    /// <summary>Thrown inside a game's question to the city when the city has given up on the call.</summary>
    sealed class AbortedException : Exception;

    void Session(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        C.ISideGame? game = null;
        string id = "";
        // The game as it was before the last call that changed it, and that call's seq.
        byte[]? before = null;
        ulong beforeSeq = 0;
        try
        {
            while (Framing.Read(s, W.CityCall.Parser) is { } call)
            {
                var reply = new W.HostReply { Seq = call.Seq };
                if (game == null)
                {
                    if (call.KindCase != W.CityCall.KindOneofCase.Open) { reply.Error = "open a game first"; Framing.Write(s, reply); continue; }
                    id = call.Open.Game;
                    game = call.Open.ContractVersion == C.ContractInfo.Version ? _make(id) : null;
                    if (game == null) reply.Error = call.Open.ContractVersion != C.ContractInfo.Version
                        ? $"this host speaks contract v{C.ContractInfo.Version}, not v{call.Open.ContractVersion}" : $"this host doesn't run '{id}'";
                    else { reply.Manifest = ContractWire.ToWire(game.Manifest); _log?.Invoke($"a city opened {id}"); }
                    Framing.Write(s, reply);
                    continue;
                }
                if (call.KindCase is W.CityCall.KindOneofCase.Tick or W.CityCall.KindOneofCase.Command or W.CityCall.KindOneofCase.NewGame or W.CityCall.KindOneofCase.Load)
                {
                    before = SaveOf(game);
                    beforeSeq = call.Seq;
                }
                try
                {
                    switch (call.KindCase)
                    {
                        case W.CityCall.KindOneofCase.NewGame: game.NewGame(call.NewGame); reply.Done = true; break;
                        case W.CityCall.KindOneofCase.Load: game = Fresh(id, call.Load.ToByteArray()); reply.Done = true; break;
                        case W.CityCall.KindOneofCase.Save: reply.Data = Google.Protobuf.ByteString.CopyFrom(SaveOf(game)); break;
                        case W.CityCall.KindOneofCase.View: reply.Data = Google.Protobuf.ByteString.CopyFrom(game.View(call.View)); break;
                        case W.CityCall.KindOneofCase.Tick:
                        {
                            var output = new C.GameToCity();
                            game.Tick(ContractWire.FromWire(call.Tick.Input), new Queries(s, call.Seq, call.Tick.City), output);
                            reply.Output = ContractWire.ToWire(output);
                            break;
                        }
                        case W.CityCall.KindOneofCase.Command:
                        {
                            var r = game.Command(call.Command.Seat, call.Command.Payload.ToByteArray(), new Queries(s, call.Seq, call.Command.City));
                            reply.Result = new W.CommandResult { Ok = r.Ok, Message = r.Message };
                            break;
                        }
                        case W.CityCall.KindOneofCase.Abort:
                            // Undo the call, whether it finished or was cut short; a call already undone stays undone.
                            if (before != null && beforeSeq == call.Seq) { game = Fresh(id, before); before = null; }
                            reply.Aborted = true;
                            break;
                        case W.CityCall.KindOneofCase.Answer:
                            continue;  // the answer to a question from a call since aborted
                        default:
                            reply.Error = "unknown call";
                            break;
                    }
                }
                catch (AbortedException)
                {
                    game = Fresh(id, before!);
                    before = null;
                    reply = new W.HostReply { Seq = call.Seq, Aborted = true };
                }
                catch (Exception e) when (e is not IOException)
                {
                    if (before != null && beforeSeq == call.Seq) { game = Fresh(id, before); before = null; }
                    reply = new W.HostReply { Seq = call.Seq, Error = $"{id} failed: {e.Message}" };
                    _log?.Invoke($"{id}: {e}");
                }
                Framing.Write(s, reply);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException) { _log?.Invoke($"a city's connection for {id} ended: {e.Message}"); }
    }

    static byte[] SaveOf(C.ISideGame g)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) g.Save(w);
        return ms.ToArray();
    }

    C.ISideGame Fresh(string id, byte[] state)
    {
        var g = _make(id) ?? throw new InvalidOperationException($"'{id}' went away");
        using var r = new BinaryReader(new MemoryStream(state));
        g.Load(r);
        return g;
    }

    /// <summary>The city as the game sees it from here: each question goes back over the connection and waits.</summary>
    sealed class Queries(NetworkStream s, ulong seq, W.CityShape shape) : C.ICityQueries
    {
        public int Width => shape.Width;
        public int Height => shape.Height;

        public C.StreetRoute? Route(int fromCell, int toCell)
        {
            var a = Ask(new W.Query { Route = new W.RouteQuery { FromCell = fromCell, ToCell = toCell } });
            return a.Route == null ? null : ContractWire.FromWire(a.Route);
        }

        public int DistrictOf(int cell) => Ask(new W.Query { DistrictOf = cell }).District;

        public ulong Hash(int a, int b, int c, int d) => Ask(new W.Query { Hash = new W.HashQuery { A = a, B = b, C = c, D = d } }).Hash;

        public C.CellKind[] Area(int x0, int y0, int w, int h)
        {
            var bytes = Ask(new W.Query { Area = new W.AreaQuery { X0 = x0, Y0 = y0, W = w, H = h } }).Area.Span;
            var cells = new C.CellKind[Math.Max(0, w) * Math.Max(0, h)];
            for (int i = 0; i < cells.Length && i < bytes.Length; i++) cells[i] = (C.CellKind)bytes[i];
            return cells;
        }

        W.QueryAnswer Ask(W.Query q)
        {
            Framing.Write(s, new W.HostReply { Seq = seq, Query = q });
            while (true)
            {
                var m = Framing.Read(s, W.CityCall.Parser) ?? throw new IOException("the city went away");
                if (m.Seq != seq) continue;
                if (m.KindCase == W.CityCall.KindOneofCase.Abort) throw new AbortedException();
                if (m.KindCase == W.CityCall.KindOneofCase.Answer) return m.Answer;
            }
        }
    }
}
