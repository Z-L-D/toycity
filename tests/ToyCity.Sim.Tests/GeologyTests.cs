namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.2, G1 to G3: soil over rock, cuts that follow the material, made once and matched at borders.</summary>
public class GeologyTests
{
    [Fact]
    public void TheGroundHasDeepSoilOnTheFlatAndNearWaterAndThinSoilOnTheSteep()
    {
        var w = World.Create(Fixture.Content, 77, 128, "Layers");
        int cw = w.Width + 1;
        long flatSoil = 0, steepSoil = 0; int flatN = 0, steepN = 0;
        var kinds = new HashSet<byte>();
        for (int y = 1; y < w.HeightCells; y++)
        for (int x = 1; x < w.Width; x++)
        {
            int c = y * cw + x;
            Assert.True(w.Bedrock[c] <= w.Corner[c], $"rock above the ground at {x},{y}");
            int steep = Math.Max(Math.Abs(w.Corner[c] - w.Corner[c + 1]), Math.Abs(w.Corner[c] - w.Corner[c + cw]));
            if (w.Corner[c] < w.WaterLevel * World.StepDm) continue;
            if (steep <= 10) { flatSoil += w.SoilDm(c); flatN++; }
            else if (steep >= 80) { steepSoil += w.SoilDm(c); steepN++; }
            kinds.Add(w.Rock[w.Idx(x, y)]);
        }
        Assert.True(flatN > 100 && steepN > 20, $"{flatN} flat, {steepN} steep corners");
        Assert.True(flatSoil / flatN > 2 * (steepSoil / steepN), $"flat ground holds {flatSoil / flatN} dm of soil, steep {steepSoil / steepN}");
        Assert.DoesNotContain((byte)0, kinds);
        Assert.True(kinds.Count >= 3, $"only {kinds.Count} kinds of rock");
        // Made once, the same from the same seed.
        Assert.Equal(w.Bedrock, World.Create(Fixture.Content, 77, 128, "Layers").Bedrock);
    }

    [Fact]
    public void NeighbouringTilesShareTheirLayersAtTheBorder()
    {
        var r = Region.Create(Fixture.Content, "Strata", 11, 2, 2, 64);
        var (west, _) = r.TileGeology(0, 0);
        var (east, _) = r.TileGeology(1, 0);
        var (south, _) = r.TileGeology(0, 1);
        for (int k = 0; k <= 64; k++)
        {
            Assert.Equal(west[k * 65 + 64], east[k * 65]);
            Assert.Equal(west[64 * 65 + k], south[k]);
        }
        // A founded tile carries the region's layers, not its own.
        var city = r.Found(1, 0, "East", "Ana");
        Assert.Equal(east, city.Bedrock);
    }

    /// <summary>A hill on flat ground, all of one material: soil (bedrock at the bottom) or granite to the top.</summary>
    static (World W, int X, int Y) Hill(bool rock)
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Funds = 10_000_000;
        // A ridge 40 m high and 12 cells wide: the land either side of it dug down.
        for (int k = 0; k < 20; k++)
        {
            w.Enqueue(new TerraformAction(new CellRect(x, y, x + 9, y + 30), TerraformMode.Lower));
            w.Enqueue(new TerraformAction(new CellRect(x + 22, y, x + 31, y + 30), TerraformMode.Lower));
        }
        w.Step();
        for (int c = 0; c < w.Corner.Length; c++) w.Bedrock[c] = rock ? w.Corner[c] : (ushort)0;
        Array.Fill(w.Rock, rock ? (byte)4 : (byte)1);
        return (w, x, y);
    }

    [Fact]
    public void ACutInRockStandsSteepAndCostsMoreThanOneInSoil()
    {
        int cw = 0;
        var results = new List<(long Cost, int Moved, int Steepest)>();
        foreach (bool rock in new[] { false, true })
        {
            var (w, x, y) = Hill(rock);
            cw = w.Width + 1;
            // A road over the ridge, cut through it.
            var a = new PlacePathAction(x + 2, y + 15, x + 29, y + 15, "road");
            Assert.Null(w.ValidatePath(a, out long cost, out _, out _));
            var before = (ushort[])w.Corner.Clone();
            w.Enqueue(a);
            w.Step();
            int moved = 0, steepest = 0;
            for (int c = 0; c < w.Corner.Length; c++)
            {
                if (w.Corner[c] != before[c]) moved++;
                Assert.True(w.Bedrock[c] <= w.Corner[c]);
            }
            // The side of the cut, across the road.
            for (int xx = x + 13; xx <= x + 19; xx++)
            for (int yy = y + 10; yy <= y + 20; yy++)
                steepest = Math.Max(steepest, Math.Abs(w.Corner[yy * cw + xx] - w.Corner[(yy - 1) * cw + xx]));
            results.Add((cost, moved, steepest));
        }
        var (soil, granite) = (results[0], results[1]);
        Assert.True(soil.Steepest <= 101, $"a soil bank stands at {soil.Steepest} dm a cell");
        // The whole cut's side stands in one cell of rock, where soil slopes back across several.
        Assert.True(granite.Steepest > 3 * soil.Steepest / 2, $"a granite cut stands at {granite.Steepest} dm a cell, soil at {soil.Steepest}");
        Assert.True(granite.Moved < soil.Moved, $"granite moved {granite.Moved} corners, soil {soil.Moved}");
        Assert.True(granite.Cost > soil.Cost, $"granite cost {granite.Cost}, soil {soil.Cost}");
    }

    [Fact]
    public void TheLayersAreSavedAndDiggingBelowTheRockMakesTheRockTheGround()
    {
        var (w, x, y) = Fixture.FlatSite();
        int c = w.CornerIdx(x + 5, y + 5);
        w.Bedrock[c] = (ushort)(w.Corner[c] - 10);
        w.Enqueue(new TerraformAction(new CellRect(x + 4, y + 4, x + 5, y + 5), TerraformMode.Lower));
        w.Step();
        Assert.Equal(w.Corner[c], w.Bedrock[c]);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Bedrock, back.Bedrock);
        Assert.Equal(w.Rock, back.Rock);
    }
}
