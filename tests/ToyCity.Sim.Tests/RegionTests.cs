using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

public class RegionTests
{
    static Region Valley(ulong seed = 7)
    {
        var r = Region.Create(Fixture.Content, "Test Valley", seed, 3, 1, 128);
        r.Found(1, 0, "Middleton", "May", starter: true);
        return r;
    }

    [Fact]
    public void TilesAreCutFromOneMapSoEdgesMeet()
    {
        var r = Region.Create(Fixture.Content, "Edges", 11, 2, 2, 64);
        var all = r.Heights;
        int wide = 2 * 64;
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 2; col++)
        {
            var h = r.TileHeights(col, row);
            for (int y = 0; y < 64; y += 7)
            for (int x = 0; x < 64; x += 5)
                Assert.Equal(all[(row * 64 + y) * wide + col * 64 + x], h[y * 64 + x]);
        }
        // The ground runs on across the seam: no cliff between the last column of one tile and the first of the next.
        var west = r.TileHeights(0, 0);
        var east = r.TileHeights(1, 0);
        int jumps = 0;
        for (int y = 0; y < 64; y++) jumps += Math.Abs(west[y * 64 + 63] - east[y * 64]) > 2 ? 1 : 0;
        Assert.True(jumps < 4, $"{jumps} cliffs along the seam");
    }

    [Fact]
    public void ALoneCityHasTheSameGroundAsBefore()
    {
        // A lone city's ground is the region generator at the city's size, so cities from before regions look the same.
        var w = World.Create(Fixture.Content, 2026, 128, "Toyville");
        Assert.Equal(World.GenerateHeights(2026, 128, 128), w.Height);
    }

    [Fact]
    public void EveryCityKeepsTheRegionsDay()
    {
        using var r = Valley();
        r.Run(45);
        var late = r.Found(2, 0, "Eastfield", "Tess", starter: true);
        Assert.Equal(45, late.Tick);
        Assert.Equal(45, late.FoundedTick);
        Assert.Equal(r.Tiles[1].City!.Month, late.Month);
        r.Run(40);
        foreach (var c in r.Cities) Assert.Equal(r.Tick, c.Tick);
        Assert.Equal("May", r.TileAt(1, 0)!.Mayor);
        Assert.Equal("Tess", r.TileAt(2, 0)!.Mayor);
        Assert.True(late.Population > 0, "the late city grew");
        Assert.NotNull(r.CantFound(1, 0));
        Assert.NotNull(r.CantFound(5, 0));
    }

    [Fact]
    public void ARegionIsDeterministicWithCitiesSteppingSideBySide()
    {
        using var a = Valley(21);
        using var b = Valley(21);
        a.Found(0, 0, "Westbrook", "Sam", starter: true);
        b.Found(0, 0, "Westbrook", "Sam", starter: true);
        a.Run(200);
        b.Run(200);
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void ATileReplaysFromItsFounding()
    {
        using var r = Valley(5);
        r.Run(30);
        r.Found(0, 0, "Westbrook", "Sam", starter: true);
        r.Run(90);
        var t = r.TileAt(0, 0)!;
        using var replay = r.ReplayTile(t, r.Tick);
        Assert.Equal(t.City!.StateHash(), replay.StateHash());
    }

    [Fact]
    public void ARegionSavesAndCarriesOn()
    {
        using var r = Valley(9);
        r.Run(40);
        r.Found(2, 0, "Eastfield", "Tess");
        r.Run(20);
        var bytes = RegionMapping.Save(r);
        Assert.True(RegionMapping.IsRegion(bytes));
        Assert.False(RegionMapping.IsRegion(Mapping.Save(r.Tiles[1].City!)));
        using var back = RegionMapping.Load(Fixture.Content, bytes);
        Assert.Equal(r.StateHash(), back.StateHash());
        r.Run(60);
        back.Run(60);
        Assert.Equal(r.StateHash(), back.StateHash());
        Assert.False(back.TileAt(0, 0)!.Founded);
    }

    [Fact]
    public void TheRegionMapShowsEveryTile()
    {
        using var r = Valley(3);
        r.Run(60);
        var s = RegionMapping.State(r, 1, 0, 1, t => t.Founded ? new[] { "May" } : Array.Empty<string>());
        Assert.Equal(3, s.Tiles.Count);
        Assert.All(s.Tiles, t => Assert.Equal(RegionMapping.PictureSize * RegionMapping.PictureSize * 3, t.Picture.Length));
        Assert.True(s.Tiles[1].Founded);
        Assert.Equal("Middleton", s.Tiles[1].Name);
        Assert.True(s.Tiles[1].Population > 0);
        Assert.Equal("Open land", s.Tiles[0].Name);
    }
}
