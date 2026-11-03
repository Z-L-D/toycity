using ToyCity.Protocol;
using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.2, G4: a region that starts as one tile and grows a tile at a time.</summary>
public class GrowingRegionTests(ITestOutputHelper output)
{
    const int S = 64;

    /// <summary>The steps of the cells either side of a shared edge, and how much they differ on average.</summary>
    static double Seam(Region r, int col, int row, int side)
    {
        var a = World.StepsFromCorners(r.GrownGround(col, row)!.Corners, S, S);
        var (nc, nr) = side == 1 ? (col + 1, row) : (col, row + 1);
        var b = World.StepsFromCorners(r.GrownGround(nc, nr)!.Corners, S, S);
        double sum = 0;
        for (int k = 0; k < S; k++)
            sum += side == 1 ? Math.Abs(a[k * S + S - 1] - b[k * S]) : Math.Abs(a[(S - 1) * S + k] - b[k]);
        return sum / S;
    }

    /// <summary>How much neighbouring cells' steps differ inside a tile, on average, for comparison.</summary>
    static double Within(Region r, int col, int row)
    {
        var a = World.StepsFromCorners(r.GrownGround(col, row)!.Corners, S, S);
        double sum = 0; int n = 0;
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S - 1; x++) { sum += Math.Abs(a[y * S + x] - a[y * S + x + 1]); n++; }
        return sum / n;
    }

    [Fact]
    public void AGrowingRegionStartsAsOneTileOfOpenLand()
    {
        using var r = Region.CreateGrowing(Fixture.Content, "Seedling", 11, S);
        Assert.True(r.Grows);
        Assert.Equal(Region.GrowFrame, r.Cols);
        var start = r.TileAt(Region.GrowStart, Region.GrowStart)!;
        Assert.False(start.Wild);
        Assert.Single(r.Tiles, t => !t.Wild);
        Assert.Null(r.CantFound(start.Col, start.Row));
        Assert.Contains("isn't part of the region", r.CantFound(0, 0));
        var w = r.Found(start.Col, start.Row, "Firstville", "May");
        Assert.Equal(r.TileCorners(start.Col, start.Row), w.Corner);
        Assert.Equal(r.TileRivers(start.Col, start.Row), w.River);
        // Only beside it.
        Assert.Contains("on a side", r.CantGrow(0, 0));
        Assert.Contains("on a side", r.CantGrow(start.Col + 1, start.Row + 1));
        Assert.Contains("already", r.CantGrow(start.Col, start.Row));
        Assert.Null(r.CantGrow(start.Col + 1, start.Row));
        using var fixedRegion = Region.Create(Fixture.Content, "Fixed", 11, 2, 2, S);
        Assert.Contains("all at once", fixedRegion.CantGrow(0, 0));
    }

    [Fact]
    public void NewTilesMeetTheirNeighboursEdgesOnEverySide()
    {
        using var r = Region.CreateGrowing(Fixture.Content, "Patchwork", 5, S);
        int c = Region.GrowStart;
        var started = DateTime.UtcNow;
        // East, then south of that, then south of the first: the last meets two tiles at once, and a diagonal.
        r.Grow(c + 1, c);
        r.Grow(c + 1, c + 1);
        r.Grow(c, c + 1);
        output.WriteLine($"three tiles in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        foreach (var (col, row, side) in new[] { (c, c, 1), (c + 1, c, 2), (c, c, 2), (c, c + 1, 1) })
        {
            var a = r.GrownGround(col, row)!;
            var (nc, nr) = side == 1 ? (col + 1, row) : (col, row + 1);
            var b = r.GrownGround(nc, nr)!;
            for (int k = 0; k <= S; k++)
            {
                int ia = side == 1 ? k * (S + 1) + S : S * (S + 1) + k, ib = side == 1 ? k * (S + 1) : k;
                Assert.Equal(a.Corners[ia], b.Corners[ib]);
                Assert.Equal(a.Bedrock[ia], b.Bedrock[ib]);
            }
            double seam = Seam(r, col, row, side), within = Math.Max(Within(r, col, row), Within(r, nc, nr));
            output.WriteLine($"{col},{row} side {side}: across the seam {seam:0.00} steps, within the tiles {within:0.00}");
            Assert.True(seam <= within * 1.5 + 0.2, $"the ground jumps at the seam: {seam:0.00} steps against {within:0.00}");
        }
        // The region's own ground, over the box of its tiles, agrees with the tiles.
        Assert.Equal(r.TileCorners(c + 1, c + 1), r.GrownGround(c + 1, c + 1)!.Corners);
        Assert.Equal((c, c, 2, 2), (r.BoxCol, r.BoxRow, r.BoxCols, r.BoxRows));
        int wide = r.BoxCols * S;
        Assert.Equal(r.GrownGround(c + 1, c)!.Corners[0], r.Corners[(c - r.BoxRow) * S * (wide + 1) + (c + 1 - r.BoxCol) * S]);
    }

    /// <summary>A river's cells, followed down from one cell to where it ends.</summary>
    static List<int> Follow(TileGround g, int from)
    {
        var path = new List<int> { from };
        for (int c = from; g.Downstream[c] >= 0 && path.Count < S * S; c = g.Downstream[c]) path.Add(g.Downstream[c]);
        return path;
    }

    [Fact]
    public void ARiverRunsOnIntoATileAddedLater()
    {
        // Find a seed whose first tile has a river running off one of its sides.
        for (ulong seed = 1; seed < 40; seed++)
        {
            using var r = Region.CreateGrowing(Fixture.Content, "Rivers", seed, S);
            int c = Region.GrowStart;
            var g = r.GrownGround(c, c)!;
            int exit = -1, side = -1;
            for (int i = 0; i < S * S && exit < 0; i++)
            {
                if (g.Rivers[i] == 0 || g.Downstream[i] >= 0) continue;
                int x = i % S, y = i / S;
                int st = World.StepsFromCorners(g.Corners, S, S)[i];
                if (g.Rivers[i] <= st) continue;
                side = y == 0 ? 0 : x == S - 1 ? 1 : y == S - 1 ? 2 : x == 0 ? 3 : -1;
                if (side >= 0) exit = i;
            }
            if (exit < 0) continue;
            var (nc, nr) = side switch { 0 => (c, c - 1), 1 => (c + 1, c), 2 => (c, c + 1), _ => (c - 1, c) };
            r.Grow(nc, nr);
            var n = r.GrownGround(nc, nr)!;
            int ex = exit % S, ey = exit / S;
            var (ax, ay) = side switch { 0 => (ex, S - 1), 1 => (0, ey), 2 => (ex, 0), _ => (S - 1, ey) };
            int entry = ay * S + ax;
            output.WriteLine($"seed {seed}: a river leaves the first tile at {ex},{ey} on side {side}, surface {g.Rivers[exit]}; the new tile's cell {ax},{ay} has {n.Rivers[entry]}");
            Assert.NotEqual(0, n.Rivers[entry]);
            Assert.True(n.Rivers[entry] <= g.Rivers[exit], "the river climbs over the border");
            var path = Follow(n, entry);
            output.WriteLine($"it runs on {path.Count} cells in the new tile");
            // It runs on, unless it has reached the sea (water level 8) just over the border.
            Assert.True(path.Count >= 3 || n.Rivers[path[^1]] <= 8, "the river stops at the border");
            for (int k = 1; k < path.Count; k++) Assert.True(n.Rivers[path[k]] <= n.Rivers[path[k - 1]], "the river climbs");
            // In the region's ground (over the box of its tiles), the first tile's river flows on into the new one.
            int wide = r.BoxCols * S;
            int at = ((c - r.BoxRow) * S + ey) * wide + (c - r.BoxCol) * S + ex;
            int next = r.Ground.Downstream[at];
            Assert.Equal(((nr - r.BoxRow) * S + ay) * wide + (nc - r.BoxCol) * S + ax, next);
            return;
        }
        Assert.Fail("no seed had a river leaving its first tile");
    }

    [Fact]
    public void AFixedRegionsGroundIsUnchanged()
    {
        // The growing region's pieces are the fixed region's code with a place in the region: a fixed region still
        // makes the very same ground (the hash is the one main made before G4).
        var a = World.GenerateGround(42, 128, 128);
        ulong h = 1469598103934665603UL;
        foreach (var v in a.Corners) h = (h ^ v) * 1099511628211UL;
        foreach (var v in a.Rivers) h = (h ^ v) * 1099511628211UL;
        Assert.Equal(0xFC49FBAB92FABC7BUL, h);
    }

    [Fact]
    public void AGrownRegionSavesPlaysAndShowsItsWildLand()
    {
        using var r = Region.CreateGrowing(Fixture.Content, "Saved", 3, S);
        int c = Region.GrowStart;
        r.Found(c, c, "Middleton", "May", starter: true);
        r.Run(20);
        r.Grow(c - 1, c);
        r.Grow(c, c - 1);
        r.Found(c - 1, c, "Westbrook", "Tess");
        r.Run(40);
        Assert.Null(r.Unbalanced());
        // The map: the region's tiles and the ring of wild land around them, not the whole wide frame. Wild land has
        // no picture; the rest does.
        var s = RegionMapping.State(r, c, c, 1, _ => Array.Empty<string>());
        Assert.True(s.Grows);
        Assert.Equal(4 * 4, s.Tiles.Count);
        Assert.Equal(3, s.Tiles.Count(t => !t.Wild));
        Assert.All(s.Tiles.Where(t => t.Wild), t => Assert.True(t.Picture.IsEmpty));
        Assert.All(s.Tiles.Where(t => !t.Wild), t => Assert.Equal(RegionMapping.PictureSize * RegionMapping.PictureSize * 3, t.Picture.Length));
        Assert.Equal("Open land", s.Tiles.Single(t => t.Col == c && t.Row == c - 1).Name);
        // Saved and loaded, it carries on the same, with the same ground, and grows on the same.
        var bytes = RegionMapping.Save(r);
        using var back = RegionMapping.Load(Fixture.Content, bytes);
        Assert.True(back.Grows);
        Assert.Equal(r.StateHash(), back.StateHash());
        Assert.Equal(r.TileCorners(c, c - 1), back.TileCorners(c, c - 1));
        Assert.Equal(r.Ground.Downstream, back.Ground.Downstream);
        r.Run(30);
        back.Run(30);
        Assert.Equal(r.StateHash(), back.StateHash());
        r.Grow(c + 1, c);
        back.Grow(c + 1, c);
        Assert.Equal(r.TileCorners(c + 1, c), back.TileCorners(c + 1, c));
        // A city replays from its tile's ground.
        var t = r.TileAt(c - 1, c)!;
        using var replay = r.ReplayTile(t, r.Tick);
        Assert.Equal(t.City!.StateHash(), replay.StateHash());
    }
}
