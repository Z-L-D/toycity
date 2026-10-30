using System.Net.WebSockets;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// Malformed, oversized, out-of-bounds and flooding messages thrown at a real server. Each misbehaving connection may
/// be dropped; the server, the city and everyone else carry on.
/// </summary>
public class FuzzTests : IDisposable
{
    readonly ServerProcess _server = new();
    public void Dispose() => _server.Dispose();

    /// <summary>A raw connection that reads (and ignores) everything the server sends, so the server never waits on it.</summary>
    sealed class Raw : IDisposable
    {
        public readonly ClientWebSocket Socket = new();
        public volatile bool Closed;

        public Raw(Uri ws)
        {
            Socket.ConnectAsync(ws, CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
            _ = Task.Run(async () =>
            {
                var buf = new byte[1 << 16];
                try
                {
                    while (Socket.State == WebSocketState.Open)
                        if ((await Socket.ReceiveAsync(buf, CancellationToken.None)).MessageType == WebSocketMessageType.Close) break;
                }
                catch { }
                Closed = true;
            });
        }

        public bool Send(byte[] data)
        {
            try { return Socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None).Wait(TimeSpan.FromSeconds(10)); }
            catch { Closed = true; return false; }
        }

        public bool WaitClosed(double seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!Closed && DateTime.UtcNow < until) Thread.Sleep(20);
            return Closed;
        }

        public void Dispose() => Socket.Dispose();
    }

    /// <summary>Fills a message's fields with nonsense: extreme numbers, odd text, random enums, nested messages, repeats.</summary>
    static void Fill(IMessage m, Random r, int depth)
    {
        var fields = m.Descriptor.Fields.InDeclarationOrder();
        // For a oneof, set just one of its fields, as a real sender would; others at random.
        var oneofs = m.Descriptor.Oneofs.ToDictionary(o => o, o => o.Fields[r.Next(o.Fields.Count)]);
        foreach (var f in fields)
        {
            if (f.ContainingOneof is { } o && oneofs[o] != f) continue;
            if (f.ContainingOneof == null && r.Next(3) == 0) continue;
            if (f.IsMap) continue;
            if (f.IsRepeated)
            {
                var list = (System.Collections.IList)f.Accessor.GetValue(m);
                for (int i = r.Next(4); i > 0; i--) if (Value(f, r, depth) is { } v) list.Add(v);
                continue;
            }
            if (Value(f, r, depth) is { } val) f.Accessor.SetValue(m, val);
        }
    }

    static readonly int[] Ints = { 0, 1, -1, 2, 5, 63, 64, 127, 128, 255, 256, 1000, int.MaxValue, int.MinValue, 100_000 };

    static object? Value(FieldDescriptor f, Random r, int depth)
    {
        int Int() => r.Next(3) == 0 ? r.Next(-300, 300) : Ints[r.Next(Ints.Length)];
        return f.FieldType switch
        {
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => Int(),
            FieldType.UInt32 or FieldType.Fixed32 => (uint)Int(),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => r.Next(4) == 0 ? long.MaxValue * (r.Next(2) * 2 - 1) : (long)Int(),
            FieldType.UInt64 or FieldType.Fixed64 => (ulong)Int(),
            FieldType.Bool => r.Next(2) == 0,
            FieldType.Float => (float)Int(),
            FieldType.Double => (double)Int(),
            FieldType.String => Text(r),
            FieldType.Bytes => ByteString.CopyFrom(Enumerable.Range(0, r.Next(200)).Select(_ => (byte)r.Next(256)).ToArray()),
            FieldType.Enum => Enum.ToObject(f.EnumType.ClrType, r.Next(4) == 0 ? Int() : f.EnumType.Values[r.Next(f.EnumType.Values.Count)].Number),
            FieldType.Message when depth < 4 => Nested(f, r, depth),
            _ => null,
        };
    }

    static IMessage Nested(FieldDescriptor f, Random r, int depth)
    {
        var m = f.MessageType.Parser.ParseFrom(Array.Empty<byte>());
        Fill(m, r, depth + 1);
        return m;
    }

    static readonly string[] Words = { "", "player", "mayor", "transport", "road", "residential", "bus-stop", "../../etc/passwd", "a b", "💥", "Robert'); DROP TABLE" };

    static string Text(Random r) => r.Next(5) switch
    {
        0 => new string('x', r.Next(5000)),
        1 => new string(Enumerable.Range(0, r.Next(40)).Select(_ => (char)r.Next(0, 0xD7FF)).ToArray()),
        _ => Words[r.Next(Words.Length)],
    };

    /// <summary>Message kinds the fuzzer sends. Leaves out new cities and forecasts, which are slow on purpose, and sign-in.</summary>
    static readonly ClientMessage.BodyOneofCase[] Kinds =
    {
        ClientMessage.BodyOneofCase.Action, ClientMessage.BodyOneofCase.Action, ClientMessage.BodyOneofCase.Action, ClientMessage.BodyOneofCase.Action,
        ClientMessage.BodyOneofCase.Why, ClientMessage.BodyOneofCase.Overlay, ClientMessage.BodyOneofCase.Inspect, ClientMessage.BodyOneofCase.Save,
        ClientMessage.BodyOneofCase.Load, ClientMessage.BodyOneofCase.Speed, ClientMessage.BodyOneofCase.Demand,
        ClientMessage.BodyOneofCase.Encyclopedia, ClientMessage.BodyOneofCase.Ordinances, ClientMessage.BodyOneofCase.Advisors, ClientMessage.BodyOneofCase.Hello,
    };

    static ClientMessage Random(Random r)
    {
        var kind = Kinds[r.Next(Kinds.Length)];
        var field = ClientMessage.Descriptor.Fields[(int)kind];
        var body = field.MessageType.Parser.ParseFrom(Array.Empty<byte>());
        Fill(body, r, 0);
        var m = new ClientMessage { Seq = (uint)r.Next() };
        field.Accessor.SetValue(m, body);
        return m;
    }

    void ServerStillPlays()
    {
        using var client = new CityClient(_server.Ws, "after");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!client.City.Loaded && DateTime.UtcNow < until) { client.Poll(); Thread.Sleep(10); }
        Assert.True(client.City.Loaded, "no city after the fuzzing");
        client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        long day0 = client.City.Stats.Day + 31L * client.City.Stats.Month;
        while (client.City.Stats.Day + 31L * client.City.Stats.Month == day0 && DateTime.UtcNow < until) { client.Poll(); Thread.Sleep(10); }
        Assert.NotEqual(day0, client.City.Stats.Day + 31L * client.City.Stats.Month);
    }

    [Fact]
    public void WellFormedNonsenseNeverDropsTheSenderOrStopsTheCity()
    {
        var r = new Random(20261029);
        for (int round = 0; round < 6; round++)
        {
            using var raw = new Raw(_server.Ws);
            for (int i = 0; i < 150; i++)
            {
                Assert.True(raw.Send(Random(r).ToByteArray()), $"round {round}, message {i}: the server stopped reading");
                if (i % 40 == 39) Thread.Sleep(800);  // stay under the rate limit
            }
            Thread.Sleep(300);
            Assert.False(raw.Closed, $"round {round}: a well-formed message made the server drop its sender");
        }
        ServerStillPlays();
    }

    [Fact]
    public void GarbageFloodsAndGiantMessagesDropOnlyTheirSender()
    {
        using var bystander = new CityClient(_server.Ws, "bystander");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!bystander.City.Loaded && DateTime.UtcNow < until) { bystander.Poll(); Thread.Sleep(10); }

        var r = new Random(7);
        using (var garbage = new Raw(_server.Ws))
        {
            for (int i = 0; i < 5 && !garbage.Closed; i++) garbage.Send(Enumerable.Range(0, 64).Select(_ => (byte)r.Next(256)).ToArray());
            Assert.True(garbage.WaitClosed(), "unreadable messages didn't drop the sender");
        }
        using (var giant = new Raw(_server.Ws))
        {
            giant.Send(new byte[3 << 20]);
            Assert.True(giant.WaitClosed(), "a 3 MB message didn't drop the sender");
        }
        using (var flood = new Raw(_server.Ws))
        {
            var hello = new ClientMessage { Hello = new Hello { ClientName = "flood" } }.ToByteArray();
            for (int i = 0; i < 2000 && !flood.Closed; i++) flood.Send(hello);
            Assert.True(flood.WaitClosed(), "a flood didn't drop the sender");
        }
        // The player who behaved is still connected and still hears the city.
        long tick = bystander.City.Stats.Day;
        bystander.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        until = DateTime.UtcNow.AddSeconds(20);
        while (bystander.City.Stats.Day == tick && DateTime.UtcNow < until) { bystander.Poll(); Thread.Sleep(10); }
        Assert.NotEqual(tick, bystander.City.Stats.Day);
        ServerStillPlays();
    }
}
