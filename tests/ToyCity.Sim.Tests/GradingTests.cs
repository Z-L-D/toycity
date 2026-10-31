namespace ToyCity.Sim.Tests;

/// <summary>Part 10: roads grade the ground under them, and buildings level their own pads.</summary>
public class GradingTests
{
    [Fact]
    public void GradingClimbsAStepACellKeepsTheRoadsItMeetsAndRefusesOnlyWhatCantBeJoined()
    {
        var r = new Random(12);
        for (int k = 0; k < 2000; k++)
        {
            int n = r.Next(1, 30);
            var h = Enumerable.Range(0, n).Select(_ => r.Next(8, 20)).ToArray();
            var fix = Enumerable.Range(0, n).Select(_ => r.Next(6) == 0).ToArray();
            var g = World.Grade(h, fix, 8);
            bool joinable = true;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (fix[i] && fix[j] && Math.Abs(h[i] - h[j]) > j - i) joinable = false;
            Assert.Equal(joinable, g != null);
            if (g == null) continue;
            for (int i = 0; i < n; i++)
            {
                if (fix[i]) Assert.Equal(h[i], g[i]);
                Assert.True(g[i] >= 8);
                if (i > 0) Assert.InRange(g[i] - g[i - 1], -1, 1);
            }
            // Ground that's already gentle is left alone.
            if (h.Zip(h.Skip(1)).All(p => Math.Abs(p.First - p.Second) <= 1)) Assert.Equal(h, g);
        }
    }

    /// <summary>The flat site with a cliff three steps high from <c>x + 10</c> eastward.</summary>
    static (World W, int X, int Y, int Flat) Cliff()
    {
        var (w, x, y) = Fixture.FlatSite();
        for (int k = 0; k < 3; k++) w.Enqueue(new TerraformAction(new CellRect(x + 10, y, x + 30, y + 30), TerraformMode.Raise));
        w.Step();
        return (w, x, y, w.Height[w.Idx(x, y)]);
    }

    [Fact]
    public void ARoadUpACliffCutsAndFillsItsWayWithBanksBesideIt()
    {
        var (w, x, y, flat) = Cliff();
        long funds = w.Funds;
        var a = new PlacePathAction(x + 2, y + 5, x + 20, y + 5, "road");
        Assert.Null(w.ValidatePath(a, out long cost, out _, out _));
        long road = 19 * Fixture.Content.Profiles[Fixture.Content.ProfileIndex("road")].CostPerCell;
        Assert.True(cost > road, "grading costs something");
        w.Enqueue(a);
        w.Step();
        Assert.Equal(funds - cost, w.Funds);
        for (int xx = x + 3; xx <= x + 20; xx++)
        {
            int here = w.Height[w.Idx(xx, y + 5)];
            Assert.InRange(here - w.Height[w.Idx(xx - 1, y + 5)], -1, 1);
            // The banks either side are within a step of the road.
            Assert.InRange(w.Height[w.Idx(xx, y + 4)] - here, -1, 1);
            Assert.InRange(w.Height[w.Idx(xx, y + 6)] - here, -1, 1);
        }
        // The ends are where they were: flat ground at one, the top of the cliff at the other.
        Assert.Equal(flat, w.Height[w.Idx(x + 2, y + 5)]);
        Assert.Equal(flat + 3, w.Height[w.Idx(x + 20, y + 5)]);
        // And it replays.
        var replayed = World.Replay(World.Create(Fixture.Content, w.Seed, 128, w.Name), w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replayed.StateHash());
    }

    [Fact]
    public void RoadsTooCloseInHeightToJoinAreRefused()
    {
        var (w, x, y, _) = Cliff();
        // A street at the foot of the cliff and one on top, two cells apart but three steps. (The raised corners
        // along x + 10 make the cell west of it, x + 9, the cliff's face.)
        w.Enqueue(new PlacePathAction(x + 8, y + 2, x + 8, y + 12, "road"));
        w.Enqueue(new PlacePathAction(x + 10, y + 2, x + 10, y + 12, "road"));
        w.Step();
        var join = new PlacePathAction(x + 8, y + 7, x + 10, y + 7, "road");
        Assert.Contains("Too steep", w.ValidatePath(join, out _, out _, out _));
        // Three cells apart, a road can climb the three steps between them.
        w.Enqueue(new PlacePathAction(x + 7, y + 14, x + 7, y + 20, "road"));
        w.Step();
        Assert.Null(w.ValidatePath(new PlacePathAction(x + 7, y + 17, x + 10, y + 17, "road"), out _, out _, out _));
    }

    [Fact]
    public void ABuildingLevelsItsPadUpToThreeStepsOfRise()
    {
        var (w, x, y, flat) = Cliff();
        var t = Fixture.Content.Types.Where(t => t.Placeable && !t.Roadside && t.W >= 2 && t.H >= 2 && w.TypeUnlocked[t.Index] && !t.Unique).MinBy(t => t.Cost)!;
        // Across the foot of the cliff: three steps of rise, which a pad levels.
        int bx = x + 10 - t.W / 2;
        long padCost = w.PadCost(bx, y + 15, t.W, t.H);
        Assert.True(padCost > 0);
        long funds = w.Funds;
        w.Enqueue(new PlaceBuildingAction(bx, y + 15, t.Id));
        w.Step();
        var b = Assert.Single(w.Buildings, b => b != null && b.Type == t.Index);
        Assert.Equal(funds - t.Cost - padCost, w.Funds);
        var heights = Enumerable.Range(0, t.W * t.H).Select(k => w.Height[w.Idx(b.X + k % t.W, b.Y + k / t.W)]).Distinct().ToList();
        Assert.Single(heights);
        Assert.InRange(heights[0], flat, flat + 3);

        // Across the foot of a cliff raised eight more, where its face climbs four steps in a cell, it's refused.
        for (int k = 0; k < 8; k++) w.Enqueue(new TerraformAction(new CellRect(x + 20, y + 18, x + 24, y + 26), TerraformMode.Raise));
        w.Step();
        Assert.Contains("too uneven", w.ValidatePlacement(new PlaceBuildingAction(x + 19 - t.W / 2, y + 22, t.Id), out _) ?? "");
    }

    [Fact]
    public void TheTownGrowsOffItsPlatformOnLevelledPads()
    {
        // A town whose platform has hills to the west, with a road and zoning run out onto them.
        foreach (ulong seed in Enumerable.Range(1, 40).Select(s => (ulong)s))
        {
            var w = World.Create(Fixture.Content, seed, 128, "Hilltop");
            var site = StarterTown.FindSite(w);
            if (site is not { } s || s.X < 26) continue;
            int ox = s.X, oy = s.Y;
            var strip = Enumerable.Range(ox - 22, 22).SelectMany(xx => Enumerable.Range(oy, 5).Select(yy => w.Idx(xx, yy))).ToList();
            if (strip.Any(w.IsWater) || strip.Max(i => w.Height[i]) - strip.Min(i => w.Height[i]) < 2) continue;
            Assert.True(StarterTown.Apply(w));
            w.Enqueue(new PlacePathAction(ox, oy, ox - 22, oy, "road"));
            w.Enqueue(new PaintZoneAction(new CellRect(ox - 21, oy + 1, ox - 1, oy + 3), Zone.Residential, Level.Low));
            w.Run(2);
            if (w.Path[w.Idx(ox - 22, oy)] == 0) continue;  // the hills were too much even graded: try another
            w.Run(720);
            var grown = w.Buildings.Where(b => b != null && b.X < ox && !w.TypeOf(b).Placeable).ToList();
            Assert.NotEmpty(grown);
            foreach (var b in w.Buildings)
            {
                if (b == null || w.TypeOf(b).Roadside) continue;
                var hs = Enumerable.Range(0, b.W * b.H).Select(k => w.Height[w.Idx(b.X + k % b.W, b.Y + k / b.W)]).Distinct().Count();
                Assert.True(hs == 1, $"{w.TypeOf(b).Id} at {b.X},{b.Y} stands on {hs} heights");
            }
            return;
        }
        Assert.Fail("no seed had hills beside the town");
    }
}
