namespace ToyCity.Sim.Tests;

/// <summary>The ground as a height at every grid corner (SimCity 4's model), with each cell's step derived from it.</summary>
public class CornerTests
{
    [Fact]
    public void EveryCellsStepIsItsCornersAveraged()
    {
        var w = World.Create(Fixture.Content, 7, 64, "Corners");
        Assert.Equal(65 * 65, w.Corner.Length);
        for (int y = 0; y < 64; y += 3)
        for (int x = 0; x < 64; x += 3)
        {
            int sum = w.Corner[w.CornerIdx(x, y)] + w.Corner[w.CornerIdx(x + 1, y)] + w.Corner[w.CornerIdx(x + 1, y + 1)] + w.Corner[w.CornerIdx(x, y + 1)];
            Assert.Equal((sum + 2 * World.StepDm) / (4 * World.StepDm), w.Height[w.Idx(x, y)]);
        }
    }

    [Fact]
    public void NeighbouringTilesShareTheirEdgeCorners()
    {
        var r = Region.Create(Fixture.Content, "Seams", 11, 2, 2, 64);
        var west = r.TileCorners(0, 0);
        var east = r.TileCorners(1, 0);
        var south = r.TileCorners(0, 1);
        for (int k = 0; k <= 64; k++)
        {
            Assert.Equal(west[k * 65 + 64], east[k * 65]);
            Assert.Equal(west[64 * 65 + k], south[k]);
        }
    }

    [Fact]
    public void RaisingMovesCornersAndTheGroundBesideSlopesUpToIt()
    {
        var (w, x, y) = Fixture.FlatSite();
        int flat = w.Height[w.Idx(x + 5, y + 5)];
        int before = w.Corner[w.CornerIdx(x + 10, y + 10)];
        w.Enqueue(new TerraformAction(new CellRect(x + 10, y + 10, x + 14, y + 14), TerraformMode.Raise));
        w.Step();
        Assert.Equal(before + World.StepDm, w.Corner[w.CornerIdx(x + 10, y + 10)]);
        Assert.Equal(flat + 1, w.Height[w.Idx(x + 12, y + 12)]);
        // The cell just outside shares the raised corners on one side: half a step up, so a slope, not a wall.
        int beside = w.Corner[w.CornerIdx(x + 9, y + 12)] + w.Corner[w.CornerIdx(x + 10, y + 12)];
        Assert.Equal(2 * before + World.StepDm, beside);
    }

    [Fact]
    public void CornersAreSavedAndReplayed()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new TerraformAction(new CellRect(x + 3, y + 3, x + 8, y + 8), TerraformMode.Raise));
        w.Enqueue(new TerraformAction(new CellRect(x + 2, y + 2, x + 9, y + 9), TerraformMode.Smooth));
        w.Step();
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Corner, loaded.Corner);
        Assert.Equal(w.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void LevelledGroundMeetsTheLandOnAnEmbankmentNotACliff()
    {
        var w = World.Create(Fixture.Content, 42, 128, "Banks");
        w.Funds = 10_000_000;
        // The hilliest dry 10 × 10 square, levelled to its corner's height.
        var (bx, by) = (from y in Enumerable.Range(14, 100) from x in Enumerable.Range(14, 100)
                        where Enumerable.Range(0, 100).All(k => !w.IsWater(w.Idx(x + k % 10, y + k / 10)))
                        orderby Enumerable.Range(0, 100).Max(k => w.Height[w.Idx(x + k % 10, y + k / 10)]) - Enumerable.Range(0, 100).Min(k => w.Height[w.Idx(x + k % 10, y + k / 10)]) descending
                        select (x, y)).First();
        long funds = w.Funds;
        var wet = Enumerable.Range(0, w.N).Select(w.IsWater).ToArray();
        w.Enqueue(new TerraformAction(new CellRect(bx, by, bx + 9, by + 9), TerraformMode.Level));
        w.Step();
        int target = w.Corner[by * 129 + bx];
        // Out to the embankment's reach, no corner stands farther from the level than its distance allows.
        for (int cy = by - 5; cy <= by + 15; cy++)
            for (int cx = bx - 5; cx <= bx + 15; cx++)
            {
                int d = Math.Max(Math.Max(bx - cx, cx - (bx + 10)), Math.Max(by - cy, cy - (by + 10)));
                if (d <= 0) { Assert.Equal(target, w.Corner[cy * 129 + cx]); continue; }
                // Ground that was by the water is left alone.
                bool byWater = Enumerable.Range(0, 4).Any(k => w.InBounds(cx - k % 2, cy - k / 2) && wet[w.Idx(cx - k % 2, cy - k / 2)]);
                if (!byWater) Assert.True(Math.Abs(w.Corner[cy * 129 + cx] - target) <= d * World.EmbankDm, $"corner {cx},{cy} at {w.Corner[cy * 129 + cx]}, {d} out from {target}; cells {string.Join(",", Enumerable.Range(0, 4).Select(k => w.Height[w.Idx(cx - k % 2, cy - k / 2)] + "/" + w.River[w.Idx(cx - k % 2, cy - k / 2)] + "/" + w.Path[w.Idx(cx - k % 2, cy - k / 2)]))}");
            }
        // The embankment comes with the levelling: only the brush's cells and the ring beside them are paid for.
        Assert.True(funds - w.Funds <= 12 * 12 * Fixture.Content.TerraformCostPerCell + 5000, $"{funds - w.Funds}");
    }

}

/// <summary>The generator: lowland, coast and lakes, and mountain ranges worn by rain.</summary>
public class GeneratorTests
{
    [Fact]
    public void SomeMapsHaveMountainsAndTheSameSeedMakesTheSameGround()
    {
        var tall = Enumerable.Range(1, 6).Select(s => World.GenerateCorners((ulong)s, 256, 256)).Select(c => c.Max() / 10.0).ToList();
        Assert.Contains(tall, m => m > 150);
        Assert.Contains(tall, m => m < 150);
        Assert.Equal(World.GenerateCorners(4, 128, 128), World.GenerateCorners(4, 128, 128));
    }

    [Fact]
    public void AboutAFifthIsWaterAndTheShoreIsJustAboveIt()
    {
        var c = World.GenerateCorners(9, 256, 256);
        double water = c.Count(v => v < 8 * World.StepDm) / (double)c.Length;
        Assert.InRange(water, 0.15, 0.21);
    }
}
