namespace ToyCity.Sim.Tests;

/// <summary>Part 10: roads grade the ground under them, and buildings level their own pads.</summary>
public class GradingTests
{
    [Fact]
    public void GradingHoldsTheStepItIsGivenKeepsThePathsItMeetsAndRefusesOnlyWhatCantBeJoined()
    {
        var r = new Random(12);
        for (int k = 0; k < 2000; k++)
        {
            int n = r.Next(1, 30), step = r.Next(1, 60);
            var h = Enumerable.Range(0, n).Select(_ => r.Next(160, 400)).ToArray();
            var fix = Enumerable.Range(0, n).Select(_ => r.Next(6) == 0).ToArray();
            var g = World.Grade(h, fix, 160, step);
            bool joinable = true;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (fix[i] && fix[j] && Math.Abs(h[i] - h[j]) > step * (j - i)) joinable = false;
            Assert.Equal(joinable, g != null);
            if (g == null)
            {
                Assert.True(PathGrade.Needed(h, fix).Pct > PathGrade.Pct(step, 1) - 1);
                continue;
            }
            for (int i = 0; i < n; i++)
            {
                if (fix[i]) Assert.Equal(h[i], g[i]);
                Assert.True(g[i] >= 160);
                if (i > 0) Assert.InRange(g[i] - g[i - 1], -step, step);
            }
            // Ground that's already gentle enough is left alone.
            if (h.Zip(h.Skip(1)).All(p => Math.Abs(p.First - p.Second) <= step)) Assert.Equal(h, g);
        }
    }

    [Fact]
    public void EachPathTypeAndSurfaceHasItsOwnGrade()
    {
        var c = Fixture.Content;
        int P(string id) => c.ProfileIndex(id);
        int S(string id) => c.SurfaceIndex(id) + 1;
        // Zac's order: footpath stairs, dirt, gravel and limestone, brick, asphalt, concrete, roads, highways.
        Assert.Equal(120, c.GradePct(P("walking-trail"), S("dirt")));
        Assert.Equal(25, c.StairsAbovePct(P("walking-trail"), S("dirt")));
        Assert.Equal(25, c.GradePct(P("dirt-road"), S("dirt")));
        Assert.Equal(20, c.GradePct(P("street"), S("gravel")));
        Assert.Equal(20, c.GradePct(P("street"), S("limestone")));
        Assert.Equal(17, c.GradePct(P("street"), S("brick")));
        Assert.Equal(15, c.GradePct(P("street"), c.SurfaceCode(P("street"), null)));
        Assert.Equal(12, c.GradePct(P("street"), S("concrete")));
        Assert.Equal(10, c.GradePct(P("road"), S("asphalt")));
        Assert.Equal(6, c.GradePct(P("highway-4"), S("concrete")));
        Assert.Equal(3, c.GradePct(P("rail"), 0));
        Assert.Equal(-1, c.SurfaceCode(P("highway-4"), "dirt"));
        Assert.Equal(0, c.SurfaceCode(P("rail"), null));
    }

    /// <summary>The flat site with a cliff three steps (6 m) high from <c>x + 10</c> eastward.</summary>
    static (World W, int X, int Y, int Flat) Cliff()
    {
        var (w, x, y) = Fixture.FlatSite();
        for (int k = 0; k < 3; k++) w.Enqueue(new TerraformAction(new CellRect(x + 10, y, x + 30, y + 30), TerraformMode.Raise));
        w.Step();
        return (w, x, y, w.Height[w.Idx(x, y)]);
    }

    static int CellDm(World w, int x, int y) => PathGrade.CellDmOf(w.Corner, w.Width, w.Idx(x, y));

    [Fact]
    public void ARoadUpACliffIsGradedByTheDecimetreWithSoilBanksBesideIt()
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
        // A road climbs at most 10%: 16 dm across a 16 m cell (a little more where corners round).
        for (int xx = x + 3; xx <= x + 20; xx++)
            Assert.InRange(CellDm(w, xx, y + 5) - CellDm(w, xx - 1, y + 5), -17, 17);
        // The ground beside it stands no steeper than soil: a bank of at most 100 dm a cell.
        int cw = w.Width + 1;
        for (int xx = x + 2; xx <= x + 21; xx++)
        for (int yy = y + 1; yy <= y + 9; yy++)
        {
            int c = yy * cw + xx;
            Assert.InRange(w.Corner[c] - w.Corner[c - cw], -101, 101);
        }
        // The ends are where they were: flat ground at one, the top of the cliff at the other.
        Assert.Equal(flat, w.Height[w.Idx(x + 2, y + 5)]);
        Assert.Equal(flat + 3, w.Height[w.Idx(x + 20, y + 5)]);
        // And it replays.
        var replayed = World.Replay(World.Create(Fixture.Content, w.Seed, 128, w.Name), w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replayed.StateHash());
    }

    [Fact]
    public void AGentleTypeIsRefusedWhereASteeperOneOrStairsWouldClimb()
    {
        var (w, x, y, _) = Cliff();
        // A street at the foot of the cliff and one on top, three cells apart and 6 m: a 12.5% climb.
        w.Enqueue(new PlacePathAction(x + 7, y + 2, x + 7, y + 12, "street"));
        w.Enqueue(new PlacePathAction(x + 10, y + 2, x + 10, y + 12, "street"));
        w.Step();
        // A road climbs at most 10%, so it's refused, and told what would make it.
        string? why = w.ValidatePath(new PlacePathAction(x + 7, y + 7, x + 10, y + 7, "road"), out _, out _, out _);
        Assert.Contains("Too steep", why);
        Assert.Contains("Try a", why);
        // A concrete street holds 12%; in asphalt it holds 15%, which climbs it.
        Assert.Contains("Try it in", w.ValidatePath(new PlacePathAction(x + 7, y + 7, x + 10, y + 7, "street", Surface: "concrete"), out _, out _, out _));
        Assert.Null(w.ValidatePath(new PlacePathAction(x + 7, y + 7, x + 10, y + 7, "street"), out _, out _, out _));
        // A footpath climbs it as stairs.
        Assert.Null(w.ValidatePath(new PlacePathAction(x + 7, y + 4, x + 10, y + 4, "walking-trail"), out _, out _, out _));
        // A surface the type can't take is refused.
        Assert.Contains("can't be built in", w.ValidatePath(new PlacePathAction(x + 2, y + 20, x + 5, y + 20, "highway-4", Surface: "dirt"), out _, out _, out _));
    }

    [Fact]
    public void ASurfaceScalesCostAndSpeedAndIsSaved()
    {
        var (w, x, y, _) = Cliff();
        var c = Fixture.Content;
        int street = c.ProfileIndex("street");
        Assert.Null(w.ValidatePath(new PlacePathAction(x, y + 20, x + 5, y + 20, "street", Surface: "dirt"), out long dirt, out _, out _));
        Assert.Null(w.ValidatePath(new PlacePathAction(x, y + 20, x + 5, y + 20, "street", Surface: "brick"), out long brick, out _, out _));
        Assert.True(brick > dirt);
        w.Enqueue(new PlacePathAction(x, y + 20, x + 5, y + 20, "street", Surface: "dirt"));
        w.Step();
        int i = w.Idx(x + 2, y + 20);
        Assert.Equal(c.SurfaceIndex("dirt") + 1, w.PathSurface[i]);
        Assert.True(w.PathCellTime(i) > c.Profiles[street].CellTime);
        var back = World.FromSnapshot(c, w.Snapshot());
        Assert.Equal(w.PathSurface, back.PathSurface);
    }

    [Fact]
    public void ARoadCanTunnelUnderAHillInsteadOfCuttingThroughIt()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Funds = 10_000_000;
        // A ridge 40 m high and 12 cells wide across the way: the land either side of it dug down.
        for (int k = 0; k < 20; k++)
        {
            w.Enqueue(new TerraformAction(new CellRect(x, y, x + 9, y + 30), TerraformMode.Lower));
            w.Enqueue(new TerraformAction(new CellRect(x + 22, y, x + 31, y + 30), TerraformMode.Lower));
        }
        w.Step();
        var cut = new PlacePathAction(x + 2, y + 15, x + 29, y + 15, "road");
        var bore = cut with { Tunnel = true };
        Assert.Null(w.ValidatePath(cut, out long cutCost, out _, out _));
        Assert.Null(w.ValidatePath(bore, out long boreCost, out _, out _));
        Assert.NotEqual(cutCost, boreCost);
        var top = w.Corner[(y + 15) * (w.Width + 1) + x + 16];
        w.Enqueue(bore);
        w.Step();
        Assert.DoesNotContain(w.Notices, n => n.Severity == "warning");
        // Under the middle of the ridge the road has its own floor, well under the ground, which is left alone.
        int mid = w.Idx(x + 16, y + 15);
        Assert.True(w.PathDeck[mid] > 0);
        Assert.True(CellDm(w, x + 16, y + 15) - w.PathDeck[mid] >= PathGrade.TunnelMinDm);
        Assert.Equal(top, w.Corner[(y + 15) * (w.Width + 1) + x + 16]);
        // Out on the flat it's on the ground.
        Assert.Equal(0, w.PathDeck[w.Idx(x + 3, y + 15)]);
        // It saves and bulldozes. (Funds were set by hand, so this one doesn't replay.)
        Assert.Equal(w.PathDeck, World.FromSnapshot(Fixture.Content, w.Snapshot()).PathDeck);
        w.Enqueue(new BulldozeAction(new CellRect(x + 16, y + 15, x + 16, y + 15)));
        w.Step();
        Assert.Equal(0, w.PathDeck[mid]);
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
