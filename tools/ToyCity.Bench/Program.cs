using System.Diagnostics;
using ToyCity.Sim;

// Headless benchmark: builds the starter town and runs it for some years, printing a yearly report.
// Usage: dotnet run --project tools/ToyCity.Bench -- [years=10] [size=128] [seed=42]
int years = args.Length > 0 ? int.Parse(args[0]) : 10;
int size = args.Length > 1 ? int.Parse(args[1]) : 128;
ulong seed = args.Length > 2 ? ulong.Parse(args[2]) : 42;

var content = ContentDb.LoadDefault();
var w = World.Create(content, seed, size, "Benchville");
if (Environment.GetEnvironmentVariable("TOYCITY_BIG") == "1")
{
    // Stress mode: a starter town on every dry 40x40 block, paid for directly.
    w.Funds = 50_000_000;
    int towns = 0;
    for (int oy = 2; oy + 38 < w.HeightCells; oy += 40)
    for (int ox = 2; ox + 38 < w.Width; ox += 40)
    {
        bool dry = true;
        for (int y = oy; y < oy + 36 && dry; y++)
            for (int x = ox; x < ox + 38; x++) if (w.IsWater(w.Idx(x, y))) { dry = false; break; }
        if (!dry) continue;
        foreach (var a in StarterTown.Build(ox, oy)) w.Enqueue(a);
        towns++;
    }
    Console.WriteLine($"stress mode: {towns} towns");
}
else if (!StarterTown.Apply(w)) { Console.WriteLine("No flat site for the starter town on this seed."); return 1; }

var sw = Stopwatch.StartNew();
long slowest = 0;
for (int y = 0; y < years; y++)
{
    for (int d = 0; d < World.DaysPerMonth * World.MonthsPerYear; d++)
    {
        long t0 = Stopwatch.GetTimestamp();
        w.Step();
        slowest = Math.Max(slowest, Stopwatch.GetTimestamp() - t0);
        foreach (var n in w.Notices) if (n.Severity != "info") Console.WriteLine($"  [{w.Year}-{w.Month:00}] {n.Text}");
        w.Notices.Clear();
    }
    int buildings = w.Buildings.Count(b => b != null);
    int abandoned = w.Buildings.Count(b => b is { Abandoned: true });
    Console.WriteLine($"{w.Year}: pop {w.Population,6} jobs {w.Jobs,6} employed {w.Workers,6} funds {w.Funds,8} " +
                      $"buildings {buildings,5} abandoned {abandoned,3} demand R{w.Demand[0],4} C{w.Demand[3],4} I{w.Demand[6],4} " +
                      $"power {w.PowerDemand}/{w.PowerCapacity} water {w.WaterDemand}/{w.WaterCapacity}");
}
if (Environment.GetEnvironmentVariable("TOYCITY_WHY") == "1") Diag.DumpStuckLots(w);
double ms = sw.Elapsed.TotalMilliseconds;
int days = years * 360;
Console.WriteLine($"{days} days in {ms:F0} ms: {days / (ms / 1000):F0} days/s, slowest day {slowest * 1000.0 / Stopwatch.Frequency:F2} ms");
Console.WriteLine($"state hash {w.StateHash():x16}");
return 0;

static partial class Diag
{
    /// <summary>Prints the why-report for the first few zoned lots of each zone that have not grown.</summary>
    public static void DumpStuckLots(World w, int perZone = 2)
    {
        var shown = new int[4];
        for (int i = 0; i < w.N; i++)
        {
            var z = World.ZoneOf(w.ZoneCode[i]);
            if (z == Zone.None || shown[(int)z] >= perZone) continue;
            var r = w.Why(i % w.Width, i / w.Width);
            if (r.Requirements.All(q => q.Met)) continue;
            shown[(int)z]++;
            Console.WriteLine($"  {r.Subject} @ {r.X},{r.Y}: {r.Summary}");
            foreach (var q in r.Requirements.Where(q => !q.Met)) Console.WriteLine($"    - {q.Label}: {q.Detail}");
            Console.WriteLine("    factors: " + string.Join(", ", r.Factors.Select(f => $"{f.Label} {f.Value:+0;-0}")));
        }
    }
}
