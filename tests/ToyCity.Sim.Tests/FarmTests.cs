using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 4.1, part 2: farm country.</summary>
public class FarmTests(ITestOutputHelper output)
{
    static World Farmed(ulong seed = 1)
    {
        var w = World.Create(Fixture.Content, seed, farms: true);
        w.Funds = 5_000_000;
        return w;
    }

    static int Count(World w, int what) => Enumerable.Range(0, w.N).Count(i => FarmLand.What(w.Farm[i]) == what);

    [Fact]
    public void FarmCountryFarmsTheFertileLowGroundOnAGrid()
    {
        int total = 0;
        foreach (ulong seed in new ulong[] { 1, 42, 7777 }) // 42 is all mountains: no farms
        {
            var w = Farmed(seed);
            int fields = Count(w, FarmLand.Field), tracks = Count(w, FarmLand.Track), yards = Count(w, FarmLand.Yard);
            output.WriteLine($"seed {seed}: {fields} field cells, {tracks} track cells, {yards} farmstead cells");
            total += fields;
            for (int i = 0; i < w.N; i++)
            {
                if (w.Farm[i] == 0) continue;
                Assert.False(w.IsWater(i));
                Assert.Equal(0, w.Canopy[i]);
                int x = i % w.Width, y = i / w.Width;
                // Tracks only on the grid's lines; fields only off them, on good ground.
                bool line = x % FarmLand.Block == 0 || y % FarmLand.Block == 0;
                if (FarmLand.What(w.Farm[i]) == FarmLand.Track) Assert.True(line);
                if (FarmLand.What(w.Farm[i]) == FarmLand.Field) { Assert.False(line); Assert.True(w.FertilityAt(i) >= 45); }
            }
            // A plain map is never farmed.
            Assert.DoesNotContain(World.Create(Fixture.Content, seed).Farm, f => f != 0);
        }
        Assert.True(total > 500);
    }

    [Fact]
    public void FertilityFavoursFlatLowGroundNearRivers()
    {
        var w = World.Create(Fixture.Content, 1);
        var land = Enumerable.Range(0, w.N).Where(i => !w.IsWater(i)).ToList();
        double Avg(Func<int, bool> f) => land.Where(f).Select(w.FertilityAt).DefaultIfEmpty(0).Average();
        double low = Avg(i => w.Height[i] - w.WaterLevel <= 3), high = Avg(i => w.Height[i] - w.WaterLevel >= 30);
        output.WriteLine($"low ground {low:F0}, high ground {high:F0}");
        Assert.True(low > high + 15);
        Assert.All(land.Where(i => w.WaterDistance[i] <= 1), i => Assert.Equal(0, w.FertilityAt(i)));
    }

    [Fact]
    public void BuildingZoningAndBulldozingTakeTheFarm()
    {
        var w = Farmed();
        int field = Enumerable.Range(0, w.N).First(i => FarmLand.What(w.Farm[i]) == FarmLand.Field && i % w.Width + 6 < w.Width);
        int x = field % w.Width, y = field / w.Width;
        w.Enqueue(new PlacePathAction(x, y, x + 3, y, "road"));
        w.Step();
        Assert.Contains(Enumerable.Range(x, 4), c => w.Path[w.Idx(c, y)] != 0);
        foreach (int c in Enumerable.Range(x, 4)) if (w.Path[w.Idx(c, y)] != 0) Assert.Equal(0, w.Farm[w.Idx(c, y)]);

        int before = w.FieldCells;
        var some = Enumerable.Range(0, w.N).Where(i => FarmLand.What(w.Farm[i]) == FarmLand.Field).Skip(40).First();
        int sx = some % w.Width, sy = some / w.Width;
        long funds = w.Funds;
        w.Enqueue(new BulldozeAction(new CellRect(sx, sy, sx, sy)));
        w.Step();
        Assert.Equal(0, w.Farm[some]);
        Assert.True(w.FieldCells < before);
        // Fields don't grow back into woods.
        w.Run(World.DaysPerMonth * 24);
        Assert.All(Enumerable.Range(0, w.N).Where(i => FarmLand.What(w.Farm[i]) == FarmLand.Field), i => Assert.Equal(0, w.Canopy[i]));
    }

    [Fact]
    public void FarmsPayARateAndShowInTheQueryPanel()
    {
        var w = Farmed();
        w.Run(World.DaysPerMonth + 1);
        Assert.Contains(w.LastBudget, l => l.Item1 == "Farm rates" && l.Item2 > 0);
        int field = Enumerable.Range(0, w.N).First(i => FarmLand.What(w.Farm[i]) == FarmLand.Field);
        Assert.StartsWith("A field of", w.FarmName(field));
        Assert.DoesNotContain(World.Create(Fixture.Content, 1).LastBudget, l => l.Item1 == "Farm rates");
    }

    [Fact]
    public void FarmsAreSavedAndReplayTheSame()
    {
        // Not Farmed(): its extra funds aren't in the log, so a replay wouldn't have them.
        var w = World.Create(Fixture.Content, 1, farms: true);
        var f = Enumerable.Range(0, w.N).First(i => FarmLand.What(w.Farm[i]) == FarmLand.Field);
        w.Enqueue(new BulldozeAction(new CellRect(f % w.Width, f / w.Width, f % w.Width + 2, f / w.Width)));
        w.Run(World.DaysPerMonth * 2);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Farm, back.Farm);
        Assert.True(back.FarmCountry);
        Assert.Equal(w.StateHash(), back.StateHash());
        var replay = World.Replay(Fixture.Content, 1, 128, w.Name, w.Log, w.Tick, farms: true);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }

    [Fact]
    public void TheGridRunsOnAcrossARegionsTiles()
    {
        using var r = Region.Create(Fixture.Content, "Plains", 7, cols: 2, rows: 1, tileSize: 64);
        r.Farms = true;
        var a = r.Found(0, 0, "West", "ann");
        var b = r.Found(1, 0, "East", "bob");
        Assert.True(a.FarmCountry && b.FarmCountry);
        output.WriteLine($"west {a.FieldCells} field cells, east {b.FieldCells}");
        Assert.True(a.FieldCells > 0 && b.FieldCells > 0);
        // The east tile's first column is a grid line (64 is four blocks): every farm cell on it is a track.
        for (int y = 0; y < 64; y++)
            if (b.Farm[b.Idx(0, y)] != 0) Assert.Equal(FarmLand.Track, FarmLand.What(b.Farm[b.Idx(0, y)]));
        // A tile replays from its founding with the same farms.
        r.Run(30);
        using var replay = r.ReplayTile(r.TileAt(1, 0)!, r.Tick);
        Assert.Equal(b.StateHash(), replay.StateHash());
    }
}
