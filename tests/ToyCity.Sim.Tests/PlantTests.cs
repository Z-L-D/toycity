using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 4.1, part 1: the plant layer.</summary>
public class PlantTests(ITestOutputHelper output)
{
    static World City(ulong seed = 42)
    {
        var w = World.Create(Fixture.Content, seed);
        w.Funds = 5_000_000;
        return w;
    }

    static int Wooded(World w) => Enumerable.Range(0, w.N).Count(i => w.Canopy[i] > 0);

    [Fact]
    public void NewGroundHasWoodsClearingsAndNothingOnTheWater()
    {
        foreach (ulong seed in new ulong[] { 1, 42, 7777 })
        {
            var w = City(seed);
            int land = Enumerable.Range(0, w.N).Count(i => !w.IsWater(i));
            int wooded = Wooded(w), closed = Enumerable.Range(0, w.N).Count(i => w.Canopy[i] >= 6);
            var kinds = Enumerable.Range(0, w.N).Where(i => w.Canopy[i] > 0).GroupBy(i => w.PlantAt(i)).ToDictionary(g => g.Key, g => g.Count());
            output.WriteLine($"seed {seed}: {wooded} of {land} land cells wooded, {closed} closed; " + string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}")));
            // Woods, but plenty of open land too.
            Assert.InRange(wooded * 100 / land, 15, 70);
            Assert.True(closed > 100);
            Assert.True(kinds.GetValueOrDefault(PlantKind.Broadleaf) > 0);
            for (int i = 0; i < w.N; i++)
            {
                Assert.InRange(w.Canopy[i], 0, World.FullCanopy);
                if (w.IsWater(i)) Assert.Equal(0, w.Canopy[i]);
                Assert.Equal(w.Canopy[i] == 0, w.Plant[i] == 0);
            }
        }
    }

    [Fact]
    public void TreesGrowNoHigherThanTheirKindLives()
    {
        // Mountain maps among these: broadleaf gives way to conifers, conifers end at the treeline, scrub above it.
        int high = 0;
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6 })
        {
            var w = World.Create(Fixture.Content, seed, 256);
            int cw = w.Width + 1;
            for (int i = 0; i < w.N; i++)
            {
                int c = i / w.Width * cw + i % w.Width;
                int above = (w.Corner[c] + w.Corner[c + 1] + w.Corner[c + cw] + w.Corner[c + cw + 1]) / 4 - w.WaterLevel * 20;
                if (above > World.TreelineDm) high++;
                if (w.Canopy[i] == 0) continue;
                var k = w.PlantAt(i);
                Assert.True(above <= World.ScrubLineDm, $"seed {seed}: {k} at {above / 10} m");
                if (k is PlantKind.Broadleaf or PlantKind.Orchard) Assert.True(above <= World.BroadleafLineDm, $"seed {seed}: broadleaf at {above / 10} m");
                if (k is PlantKind.Conifer or PlantKind.Reeds) Assert.True(above <= World.TreelineDm, $"seed {seed}: {k} at {above / 10} m");
            }
        }
        output.WriteLine($"{high} cells above the treeline");
        Assert.True(high > 0);
    }

    [Fact]
    public void BuildingTakesTheTreesAndBulldozingClearsThem()
    {
        var w = City();
        // A row of wood cells to build a road through.
        int row = Enumerable.Range(10, w.HeightCells - 20).First(y => Enumerable.Range(10, 30).Count(x => w.Canopy[w.Idx(x, y)] > 0 && !w.IsWater(w.Idx(x, y))) >= 12);
        int before = Enumerable.Range(10, 30).Count(x => w.Canopy[w.Idx(x, row)] > 0);
        w.Enqueue(new BulldozeAction(new CellRect(10, row, 39, row)));
        w.Step();
        Assert.Equal(0, Enumerable.Range(10, 30).Count(x => w.Canopy[w.Idx(x, row)] > 0));
        output.WriteLine($"cleared {before} cells of trees on row {row}");

        // A wooded cell a path goes through loses its trees the same day.
        var w2 = City();
        int y2 = Enumerable.Range(10, w2.HeightCells - 20).First(y => Enumerable.Range(20, 10).All(x => w2.Canopy[w2.Idx(x, y)] > 0));
        w2.Enqueue(new PlacePathAction(20, y2, 29, y2, "road"));
        w2.Step();
        var paved = Enumerable.Range(20, 10).Where(x => w2.Path[w2.Idx(x, y2)] != 0).ToList();
        Assert.NotEmpty(paved);
        Assert.All(paved, x => Assert.Equal(0, w2.Canopy[w2.Idx(x, y2)]));
    }

    [Fact]
    public void ThePlanterPlantsOpenLandAndChargesForIt()
    {
        var w = City();
        // An open, dry patch.
        var (ox, oy) = Open(w, 4);
        long funds = w.Funds;
        w.Enqueue(new PlantTreesAction(new CellRect(ox, oy, ox + 3, oy + 3)));
        w.Step();
        for (int y = oy; y < oy + 4; y++)
        for (int x = ox; x < ox + 4; x++)
        {
            Assert.Equal(World.PlantedCanopy, w.Canopy[w.Idx(x, y)]);
            Assert.NotEqual(PlantKind.None, w.PlantAt(w.Idx(x, y)));
        }
        Assert.True(funds - w.Funds >= 16 * World.TreeCostPerCell);

        var was = w.PlantAt(w.Idx(ox, oy));
        funds = w.Funds;
        w.Enqueue(new PlantTreesAction(new CellRect(ox, oy, ox + 1, oy), PlantKind.Orchard));
        w.Step();
        // Already planted that thick: nothing more to plant there, and nothing spent.
        Assert.Equal(was, w.PlantAt(w.Idx(ox, oy)));
        Assert.Contains(w.Notices, n => n.Text.Contains("nowhere there to plant"));
    }

    static (int X, int Y) Open(World w, int size)
    {
        for (int y = 8; y < w.HeightCells - 8 - size; y++)
        for (int x = 8; x < w.Width - 8 - size; x++)
        {
            bool ok = true;
            for (int dy = 0; dy < size && ok; dy++)
            for (int dx = 0; dx < size && ok; dx++)
            {
                int i = w.Idx(x + dx, y + dy);
                ok = !w.IsWater(i) && w.Canopy[i] == 0 && w.SuitedKind(i) == PlantKind.Broadleaf;
            }
            if (ok) return (x, y);
        }
        throw new InvalidOperationException("no open land");
    }

    [Fact]
    public void OpenLandGrowsBackFromTheWoodsBesideIt()
    {
        var w = City();
        var cleared = new CellRect(30, 30, 50, 50);
        w.Enqueue(new BulldozeAction(cleared));
        w.Step();
        int CountIn() { int n = 0; for (int y = 30; y <= 50; y++) for (int x = 30; x <= 50; x++) if (w.Canopy[w.Idx(x, y)] > 0) n++; return n; }
        Assert.Equal(0, CountIn());
        w.Run(World.DaysPerMonth * 36);
        int back = CountIn();
        output.WriteLine($"after three years {back} of 441 cleared cells have trees again");
        Assert.True(back > 10);
        // But it doesn't fill a whole clearing in that time.
        Assert.True(back < 441);
    }

    [Fact]
    public void TreesLiftLandValueAndHomesDesirability()
    {
        var w = City();
        int wood = Enumerable.Range(0, w.N).First(i => !w.IsWater(i) && w.GreeneryAt(i) >= 150 && w.BuildingAt[i] == 0);
        var lv = new List<Factor>();
        w.LandValueOf(wood, lv);
        Assert.Contains(lv, f => f.Label == "Trees" && f.Value > 0);
        var des = new List<Factor>();
        w.DesirabilityOf(wood, Zone.Residential, des);
        Assert.Contains(des, f => f.Label == "Trees" && f.Value > 0);
    }

    [Fact]
    public void PlantsAreSavedAndLoadedAndReplayTheSame()
    {
        var w = City();
        var (ox, oy) = Open(w, 3);
        w.Enqueue(new PlantTreesAction(new CellRect(ox, oy, ox + 2, oy + 2), PlantKind.Conifer));
        w.Run(World.DaysPerMonth * 4);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Plant, back.Plant);
        Assert.Equal(w.Canopy, back.Canopy);
        Assert.Equal(w.StateHash(), back.StateHash());
        back.Run(World.DaysPerMonth * 2);
        w.Run(World.DaysPerMonth * 2);
        Assert.Equal(w.StateHash(), back.StateHash());
        var replay = World.Replay(Fixture.Content, 42, 128, w.Name, w.Log, w.Tick);
        Assert.Equal(w.Canopy, replay.Canopy);
    }

    [Fact]
    public void WoodsCarryOnAcrossARegionsTileBorders()
    {
        var r = Region.Create(Fixture.Content, "Woods", 9, cols: 2, rows: 1, tileSize: 64);
        var a = r.Found(0, 0, "West", "ann");
        var b = r.Found(1, 0, "East", "bob");
        // The last column of the west tile and the first of the east are neighbours: their woods should agree about as
        // well as any two neighbouring columns do.
        int close = 0;
        for (int y = 0; y < 64; y++)
            if (Math.Abs(a.Canopy[a.Idx(63, y)] - b.Canopy[b.Idx(0, y)]) <= 2) close++;
        output.WriteLine($"{close} of 64 border rows within two crowns of each other");
        Assert.True(close >= 48);
    }
}
