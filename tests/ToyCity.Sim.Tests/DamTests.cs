using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>River round 2, step 3: the river brush and dams.</summary>
public class DamTests(ITestOutputHelper output)
{
    static World Valley(ulong seed = 42)
    {
        var w = World.Create(Fixture.Content, seed);
        w.Funds = 5_000_000;
        return w;
    }

    /// <summary>A line across a river, bank to bank, that a dam of this rise can be built on.</summary>
    static PlaceDamAction? Crossing(World w, int rise) => Crossings(w, rise).FirstOrDefault();

    static IEnumerable<PlaceDamAction> Crossings(World w, int rise)
    {
        foreach (bool across in new[] { true, false })
        for (int a = 8; a < (across ? w.HeightCells : w.Width) - 8; a += 3)
        {
            int len = across ? w.Width : w.HeightCells;
            for (int b = 4; b < len - 12; b++)
            {
                int I(int k) => across ? w.Idx(k, a) : w.Idx(a, k);
                if (w.IsWater(I(b)) || !w.IsRiver(I(b + 1))) continue;
                int e = b + 1;
                while (e < len - 1 && w.IsRiver(I(e))) e++;
                if (e - b > 8 || w.IsWater(I(e))) continue;
                // Out onto the banks a few cells either side.
                int s0 = Math.Max(0, b - 3), s1 = Math.Min(len - 1, e + 3);
                var dam = across ? new PlaceDamAction(s0, a, s1, a, rise) : new PlaceDamAction(a, s0, a, s1, rise);
                if (w.ValidateDam(dam, out _, out _, out _, out var pool, out _, out _) == null && pool.Count > 20) yield return dam;
            }
        }
    }

    [Fact]
    public void TheBrushDigsARiverWhoseWaterOnlyFalls()
    {
        var w = Valley();
        // From a river cell out across dry land.
        int start = Enumerable.Range(0, w.N).First(i => w.IsRiver(i) && i % w.Width is > 30 and < 90 && i / w.Width is > 30 and < 90
            && Enumerable.Range(1, 20).All(k => !w.IsWater(w.Idx(i % w.Width + k, i / w.Width)) && w.Path[w.Idx(i % w.Width + k, i / w.Width)] == 0 && w.BuildingAt[w.Idx(i % w.Width + k, i / w.Width)] == 0));
        int sx = start % w.Width, sy = start / w.Width;
        int river = w.River[start];
        long funds = w.Funds;
        w.Enqueue(new DigRiverAction(sx, sy, sx + 20, sy, 1, 2));
        w.Step();
        Assert.True(w.Funds < funds, "digging costs money");
        output.WriteLine(string.Join(" ", Enumerable.Range(sx - 1, 23).Select(x => $"{w.River[w.Idx(x, sy)]}/{w.Height[w.Idx(x, sy)]}")));
        int last = int.MaxValue;
        for (int x = sx; x <= sx + 20; x++)
        {
            int i = w.Idx(x, sy);
            Assert.True(w.IsRiver(i), $"({x},{sy}) is river");
            Assert.True(w.River[i] <= river, "it starts at the river it's dug from");
            Assert.True(w.River[i] <= last, "its water only falls along the stroke");
            last = w.River[i];
        }
        output.WriteLine($"river {river}, dug water {w.River[w.Idx(sx + 20, sy)]}, bed {w.Height[w.Idx(sx + 20, sy)]}");
        Assert.Equal(w.River[w.Idx(sx + 20, sy)] - 2, w.Height[w.Idx(sx + 20, sy)]);
    }

    [Fact]
    public void ADamFillsAReservoirThatHoldsAndLetsGo()
    {
        var w = Valley();
        var dam = Crossing(w, 3);
        Assert.NotNull(dam);
        var rivers = (byte[])w.River.Clone();
        var heights = (byte[])w.Height.Clone();
        w.ValidateDam(dam!, out long cost, out int crest, out var wall, out var pool, out _, out _);
        output.WriteLine($"dam {dam}: crest {crest}, {wall.Count} cells, reservoir {pool.Count} cells, ${cost:N0}");
        w.Enqueue(dam!);
        w.Step();
        Assert.All(wall, i => Assert.Equal(crest, w.Dam[i]));
        Assert.All(pool, i => Assert.Equal(crest - 1, w.River[i]));
        Assert.All(pool, i => Assert.True(w.IsRiver(i)));
        var held = w.DamHeld;
        Assert.All(pool, i => Assert.Equal(RiverFlow.Reservoir, held[i]));
        Assert.Contains(Enumerable.Range(0, w.N), i => held[i] == RiverFlow.Regulated);
        // The river below never floods: at a great flood's flow it runs at most HeldMost.
        Assert.Equal(RiverFlow.HeldMost, RiverFlow.FlowAt(500, RiverFlow.Regulated));
        Assert.Equal(RiverFlow.HeldLeast, RiverFlow.FlowAt(25, RiverFlow.Regulated));

        // The city saves and loads with it.
        var again = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Dam, again.Dam);
        Assert.Equal(w.Snapshot(), again.Snapshot());

        // Bulldozed, the water goes back down to the river it was.
        int x = wall[wall.Count / 2] % w.Width, y = wall[wall.Count / 2] / w.Width;
        w.Enqueue(new BulldozeAction(new CellRect(x, y, x, y)));
        w.Step();
        Assert.All(wall, i => Assert.Equal(0, w.Dam[i]));
        Assert.Equal(rivers, w.River);
        Assert.All(wall, i => Assert.Equal(heights[i] < rivers[i], w.IsRiver(i)));
    }

    [Fact]
    public void AHydroPlantMakesPowerAtADamsFoot()
    {
        // The first dam with ground beside it a plant can stand on.
        var w = Valley();
        foreach (var d in Crossings(Valley(), 4).Take(12))
        {
            w = Valley();
            w.Enqueue(d);
            w.Step();
            var cells = Enumerable.Range(0, w.N).Where(i => w.Dam[i] != 0).ToList();
            if (cells.Any(i => Enumerable.Range(-4, 7).Any(dy => Enumerable.Range(-4, 7).Any(dx => w.ValidatePlacement(new PlaceBuildingAction(i % w.Width + dx, i / w.Width + dy, "hydro-plant"), out _) == null)))) break;
        }
        // Nowhere near a dam, it's refused.
        var site = StarterTown.FindSite(w, 4)!.Value;
        w.Enqueue(new PlaceBuildingAction(site.X, site.Y, "hydro-plant"));
        w.Step();
        Assert.DoesNotContain(w.Buildings, b => b != null && w.Content.Types[b.Type].Hydro);
        // Against the dam, on a bank: the first spot touching it that fits.
        var wall = Enumerable.Range(0, w.N).Where(i => w.Dam[i] != 0).ToList();
        var why = new List<string>();
        var spot = wall.SelectMany(i => Enumerable.Range(-4, 7).SelectMany(dy => Enumerable.Range(-4, 7).Select(dx => new PlaceBuildingAction(i % w.Width + dx, i / w.Width + dy, "hydro-plant"))))
            .FirstOrDefault(p => { var e = w.ValidatePlacement(p, out _); if (e != null) why.Add(e); return e == null; });
        if (spot == null) output.WriteLine(string.Join("\n", why.Distinct()));
        Assert.NotNull(spot);
        w.Enqueue(spot!);
        w.Step();
        var plant = w.Buildings.FirstOrDefault(b => b != null && w.Content.Types[b.Type].Hydro);
        Assert.NotNull(plant);
        int pct = w.HydroPct(plant!);
        output.WriteLine($"hydro plant at ({plant!.X},{plant.Y}) runs at {pct}%");
        Assert.InRange(pct, 1, 100);
        w.Run(2);
        Assert.True(w.PowerCapacity > 0, "it makes power");
    }

    [Fact]
    public void ADamCantLeakRoundItsEnd()
    {
        var w = Valley();
        var dam = Crossing(w, 3);
        Assert.NotNull(dam);
        // Just the river cells and one bank: at a big rise the water runs round the open end.
        var high = dam! with { Rise = World.DamHighest };
        var line = World.LineCells(high.X0, high.Y0, high.X1, high.Y1);
        var cut = line.Take(line.Count - 2).ToList();
        var shortDam = high with { X1 = cut[^1].X, Y1 = cut[^1].Y };
        var err = w.ValidateDam(shortDam, out _, out _, out _, out _, out _, out _);
        output.WriteLine(err ?? "built");
        Assert.NotNull(err);
    }
}
