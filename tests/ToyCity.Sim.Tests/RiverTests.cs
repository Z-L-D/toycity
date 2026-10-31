namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.1 part 5: rivers, bridges and floods.</summary>
public class RiverTests
{
    [Fact]
    public void RiversRunDownhillToTheSeaOrTheEdge()
    {
        var g = World.GenerateGround(42, 128, 128);
        int rivers = 0;
        for (int i = 0; i < g.Rivers.Length; i++)
        {
            if (g.Rivers[i] == 0) continue;
            rivers++;
            // Never climbing on the way down; the last cell is at the edge or the sea's side.
            int c = i, steps = 0;
            while (g.Downstream[c] >= 0)
            {
                Assert.True(g.Rivers[g.Downstream[c]] <= g.Rivers[c], $"the river climbs from {c} to {g.Downstream[c]}");
                c = g.Downstream[c];
                Assert.True(++steps < 128 * 128);
            }
        }
        Assert.InRange(rivers, 40, 2000);
        // In a city, every river cell is water, a step deep.
        var w = World.Create(Fixture.Content, 42, 128, "Riverton");
        Assert.Equal(g.Rivers, w.River);
        for (int i = 0; i < w.N; i++)
            if (w.River[i] != 0 && w.River[i] > w.Height[i]) Assert.True(w.IsWater(i));
        Assert.True(Enumerable.Range(0, w.N).Count(w.IsRiver) > 40);
    }

    [Fact]
    public void ARiverRunsOnAcrossABorder()
    {
        using var r = Region.Create(Fixture.Content, "Rivers", 7, 3, 3, 64);
        int wide = 3 * 64;
        // Some river crosses from one tile into the next.
        bool crosses = false;
        for (int i = 0; i < r.Rivers.Length && !crosses; i++)
        {
            int d = r.Ground.Downstream[i];
            if (r.Rivers[i] == 0 || d < 0) continue;
            crosses = i % wide / 64 != d % wide / 64 || i / wide / 64 != d / wide / 64;
        }
        Assert.True(crosses, "no river crosses a border");
        var a = r.Found(0, 0, "Upton", "May");
        Assert.Equal(r.TileRivers(0, 0), a.River);
    }

    [Fact]
    public void ARoadBridgesARiverAndTheRiverRunsOnUnderIt()
    {
        var w = World.Create(Fixture.Content, 42, 128, "Bridgeton");
        w.Funds = 10_000_000;
        // A straight stretch of river running north and south, dry land either side.
        (int X, int Y)? spot = null;
        for (int y = 2; y < 126 && spot == null; y++)
            for (int x = 3; x < 125 && spot == null; x++)
            {
                int i = w.Idx(x, y);
                if (!w.IsRiver(i)) continue;
                if (!w.IsWater(w.Idx(x - 2, y)) && !w.IsWater(w.Idx(x + 2, y)) && !w.IsWater(w.Idx(x - 1, y)) && !w.IsWater(w.Idx(x + 1, y))
                    && Math.Abs(w.Height[w.Idx(x - 1, y)] - (w.River[i] + 1)) <= 1 && Math.Abs(w.Height[w.Idx(x + 1, y)] - (w.River[i] + 1)) <= 1)
                    spot = (x, y);
            }
        Assert.NotNull(spot);
        var (sx, sy) = spot!.Value;
        int river = w.Idx(sx, sy);
        byte bed = w.Height[river];
        var a = new PlacePathAction(sx - 2, sy, sx + 2, sy, "road");
        Assert.Null(w.ValidatePath(a, out long cost, out _, out _));
        // The bridge cell costs five times a road cell.
        Assert.True(cost >= (4 + World.BridgeCostTimes) * Fixture.Content.Profiles[Fixture.Content.ProfileIndex("road")].CostPerCell);
        w.Enqueue(a);
        w.Step();
        Assert.NotEqual(0, w.Path[river]);
        Assert.Equal(bed, w.Height[river]);
        Assert.True(w.IsRiver(river));
        // The sea still can't be crossed.
        int sea = Enumerable.Range(0, w.N).First(i => w.Height[i] < w.WaterLevel && i % 128 > 0 && i % 128 < 127);
        Assert.NotNull(w.ValidatePath(new PlacePathAction(sea % 128 - 1, sea / 128, sea % 128 + 1, sea / 128, "road"), out _, out _, out _));
    }

    [Fact]
    public void AFloodCoversTheLowGroundBesideTheRiverAndDamagesWhatStandsThere()
    {
        var w = World.Create(Fixture.Content, 42, 128, "Floodham");
        w.Funds = 10_000_000;
        Assert.All(w.Flooded, c => Assert.Equal(0, c));
        var plain = w.FloodPlain(2);
        int cell = Enumerable.Range(0, w.N).FirstOrDefault(i => plain[i] != 0 && w.BuildingAt[i] == 0 && w.Path[i] == 0, -1);
        Assert.True(cell >= 0, "no ground beside a river is low enough to flood");
        // Floodwater lies only on dry ground lower than the raised river, within reach of it.
        for (int i = 0; i < w.N; i++)
            if (plain[i] != 0) Assert.True(!w.IsWater(i) && w.Height[i] < plain[i]);
        var house = Fixture.Content.Zoned(Zone.Residential, Level.Low, Level.Low);
        var b = w.AddBuilding(house, cell % 128, cell / 128, 0, 1, 1);
        b.Condition = 100;
        w.Enqueue(new FloodAction(2));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("in flood"));
        Assert.NotEqual(0, w.Flooded[cell]);
        w.Run(3);
        Assert.True(b.Condition < 100 - World.FloodDamagePerDay * 2, $"condition {b.Condition}");
        // A save keeps the stage; the water goes when it falls.
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(2, back.FloodStage);
        Assert.Equal(w.Flooded, back.Flooded);
        w.Enqueue(new FloodAction(0));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("back in its banks"));
        Assert.All(w.Flooded, c => Assert.Equal(0, c));
        // Only the region brings floods.
        Assert.NotNull(w.Forbidden(new FloodAction(3) { By = "player" }));
    }

    [Fact]
    public void RainUpstreamFloodsTheRiversOverTheYears()
    {
        using var r = Region.Create(Fixture.Content, "Wetlands", 7, 3, 3, 64);
        // Over a few years of fronts, some tile's rivers rise, and none stand up for long without rain.
        int flooded = 0, longest = 0;
        for (int k = 0; k < r.Tiles.Length; k++)
        {
            int run = 0;
            for (long t = 0; t < 3 * 360; t++)
            {
                int s = r.FloodStageAt(k, t);
                run = s > 0 ? run + 1 : 0;
                longest = Math.Max(longest, run);
                if (s > 0) flooded++;
            }
        }
        Assert.True(flooded > 0, "no river ever rose");
        Assert.True(longest < 90, $"a flood lasted {longest} days");
    }
}
