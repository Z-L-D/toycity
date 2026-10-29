using Google.Protobuf;
using Google.Protobuf.Reflection;
using ToyCity.Protocol;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Sim.Tests;

/// <summary>Thousands of nonsense actions straight into the sim: nothing throws, and the city replays the same.</summary>
public class ActionFuzzTests
{
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

    [Fact]
    public void NonsenseActionsAreRefusedOrAppliedButNeverBreakTheCity()
    {
        var r = new Random(29);
        var w = TransportTests.Town(401, days: 30);
        var field = P.Action.Descriptor;
        int applied = 0;
        for (int day = 0; day < 120; day++)
        {
            for (int i = 0; i < 25; i++)
            {
                var a = new P.Action();
                Fill(a, r, 0);
                if (Mapping.ToSim(a) is not { } act) continue;
                // As the server would send it: from the mayor, a stranger or nobody.
                w.Enqueue(act with { By = r.Next(3) switch { 0 => "player", 1 => "mallory", _ => "" } });
                applied++;
            }
            w.Step();
        }
        Assert.True(applied > 2000, $"only {applied} actions");
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
        var file = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), file.StateHash());
    }
}
