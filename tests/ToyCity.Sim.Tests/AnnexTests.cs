using ToyCity.Protocol;
using ToyCity.Contract;
using ToyCity.Emergency;
using ToyCity.Transport;
using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 4, part 6, stage 2: a city moved onto a bigger map when it annexes land, with everything it holds.</summary>
public class AnnexTests(ITestOutputHelper output)
{
    /// <summary>Ground for a tile of new land: a neighbouring tile of a small region made from the city's seed.</summary>
    static void Lay(World w, int x0, int y0, int size, ulong seed, (int X, int Y) old)
    {
        using var r = Region.Create(Fixture.Content, "ground", seed, 2, 2, size);
        var (bed, rock) = r.TileGeology(1, 1);
        // The city's own corners on the seam stay as the city made them.
        w.PlaceGround(x0, y0, size, r.TileCorners(1, 1), r.TileRivers(1, 1), bed, rock, (x, y) => x >= old.X && x <= old.X + 128 && y >= old.Y && y <= old.Y + 128);
    }

    [Theory]
    [InlineData(0, 0)]    // east
    [InlineData(128, 0)]  // west: everything moves 128 cells east
    [InlineData(0, 128)]  // north
    public void ABusTownMovesOntoABiggerMapWithItsLines(int dx, int dy)
    {
        var w = TransportTests.Town(81);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "", OperatorLevel.Autopilot));
        w.Run(150);
        var before = TransportTests.View(w);
        Assert.Single(before.Lines);
        int pop = w.Population, buildings = w.Buildings.Count(b => b != null), paths = w.Path.Count(p => p != 0);
        var firstHome = w.Buildings.First(b => b != null && b.Residents > 0)!;
        int hx = firstHome.X, hy = firstHome.Y, hid = firstHome.Id;
        int newW = 128 + (dy == 0 ? 128 : 0), newH = 128 + (dy > 0 ? 128 : 0);

        var big = w.Resized(newW, newH, dx, dy);
        Assert.Equal((newW, newH), (big.Width, big.HeightCells));
        Lay(big, dx == 0 && dy == 0 ? 128 : 0, 0, 128, 7, (dx, dy));
        big.Settle();

        Assert.Equal(pop, big.Population);
        Assert.Equal(buildings, big.Buildings.Count(b => b != null));
        Assert.Equal(paths, big.Path.Count(p => p != 0));
        Assert.Equal((hx + dx, hy + dy), (big.Buildings[hid]!.X, big.Buildings[hid]!.Y));
        Assert.Equal(hid + 1, big.BuildingAt[big.Idx(hx + dx, hy + dy)]);
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
            Assert.Equal(w.Path[w.Idx(x, y)], big.Path[big.Idx(x + dx, y + dy)]);
        // Saved and loaded, it's the same city.
        Assert.Equal(big.StateHash(), World.FromSnapshot(Fixture.Content, big.Snapshot()).StateHash());

        // It plays on: the line is routed again on the new map, and people ride it.
        big.Run(60);
        var after = TransportTests.View(big);
        var line = Assert.Single(after.Lines);
        output.WriteLine($"{dx},{dy}: population {pop} then {big.Population}; the line carries {line.Riders} riders ({line.Problem})");
        Assert.True(line.Riders > 0, "nobody rides after the move");
        Assert.True(big.Population >= pop * 9 / 10);
        // And it builds on the new land.
        int nx = dx == 0 && dy == 0 ? 170 : 40, ny = 40;
        big.Enqueue(new PlacePathAction(nx, ny, nx + 20, ny, "road"));
        big.ApplyPending();
        Assert.NotEqual(0, big.Path[big.Idx(nx + 10, ny)]);
        big.Dispose();
    }

    [Fact]
    public void TheEmergencyServicesMoveWithTheirUnits()
    {
        using var w = EmergencyTests.Town(701);
        w.Run(10);
        var units = EmergencyTests.View(w).Units.Select(u => (u.Id, u.Cell)).ToList();
        Assert.All(units, u => Assert.True(u.Cell >= 0));
        var big = w.Resized(256, 128, 128, 0);
        Lay(big, 0, 0, 128, 3, (128, 0));
        big.Settle();
        var moved = EmergencyTests.View(big).Units.Select(u => (u.Id, u.Cell)).ToList();
        Assert.Equal(units.Select(u => (u.Id, u.Cell / 128 * 256 + u.Cell % 128 + 128)), moved);
        big.Run(30);
        Assert.All(big.Leases.Where(l => l.GameId == EmergencyGame.Id), l => Assert.True(l.Jobs > 0));
        Assert.Equal(big.StateHash(), World.FromSnapshot(Fixture.Content, big.Snapshot()).StateHash());
        big.Dispose();
    }
}

/// <summary>Annexing on the region: a city of several tiles, its borders and neighbours, and the rules.</summary>
public class RegionAnnexTests(ITestOutputHelper output)
{
    const int S = 128;

    [Fact]
    public void ACityAnnexesTheLandBesideItAndKeepsItsNeighbours()
    {
        using var r = Region.Create(Fixture.Content, "Annexia", 12, 3, 2, S);
        var mid = r.Found(1, 0, "Middleton", "May", starter: true);
        r.Run(30);
        var at = r.TileAt(1, 0)!;
        long funds = mid.Funds;
        int pop = mid.Population;
        Assert.True(pop > 0);
        // East: a rectangle of two, at a price.
        Assert.Null(r.CantAnnex(at, 2, 0));
        Assert.Equal((1, 0, 2, 1), r.AnnexRect(at, 2, 0));
        long price = r.AnnexPrice(at, (1, 0, 2, 1));
        Assert.True(price > 0);
        var big = r.Annex(at, 2, 0);
        Assert.Equal((2 * S, S), (big.Width, big.HeightCells));
        Assert.Equal(funds - price, big.Funds);
        Assert.Equal(pop, big.Population);
        Assert.Same(big, r.TileAt(2, 0)!.City);
        Assert.Same(r.TileAt(1, 0), r.TileAt(2, 0)!.Anchor);
        Assert.Single(r.Cities);
        Assert.Equal((1, 0, 2, 1), r.Footprint(r.TileAt(2, 0)!));

        // A neighbour to the west, and one below: each side of Middleton borders one city.
        r.Found(0, 0, "Westbrook", "Tess", starter: true);
        r.Found(2, 1, "Southend", "Ann", starter: true);
        // A second city below Middleton would make two along its south side.
        Assert.Contains("only one other city", r.CantFound(1, 1));
        r.Run(40);
        Assert.Null(r.Unbalanced());
        Assert.Equal("Westbrook", big.NextDoor[3]?.Name);
        Assert.Equal("Southend", big.NextDoor[2]?.Name);
        Assert.Equal("Middleton", r.TileAt(2, 1)!.City!.NextDoor[0]?.Name);
        Assert.Equal(S, r.TileAt(2, 1)!.City!.NextDoor[0]!.Path.Length);
        // Middleton can't annex a city's land, nor grow into an L.
        Assert.Contains("by a deal", r.CantAnnex(at, 0, 0));
        Assert.Contains("Southend is there", r.CantAnnex(at, 2, 1)!);

        // The map: both of Middleton's tiles say so, with its pictures cut from its map.
        var s = RegionMapping.State(r, 1, 0, 1, _ => Array.Empty<string>());
        Assert.All(s.Tiles.Where(t => t.Name == "Middleton"), t => Assert.Equal((1, 0, 2, 1), (t.CityCol, t.CityRow, t.CityCols, t.CityRows)));
        Assert.Equal(2, s.Tiles.Count(t => t.Name == "Middleton"));
        Assert.NotEqual(s.Tiles.Single(t => t.Col == 1 && t.Row == 0).Picture, s.Tiles.Single(t => t.Col == 2 && t.Row == 0).Picture);

        // Saved and loaded, the same region.
        using var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r));
        Assert.Equal(r.StateHash(), back.StateHash());
        Assert.Same(back.TileAt(1, 0)!.City, back.TileAt(2, 0)!.City);
        r.Run(20);
        back.Run(20);
        Assert.Equal(r.StateHash(), back.StateHash());
        output.WriteLine($"Middleton {big.Population} people on {big.Width} × {big.HeightCells}");
    }

    [Fact]
    public void ACornerTileTakesTheTilesBetweenSoTheCityStaysARectangle()
    {
        using var r = Region.Create(Fixture.Content, "Squares", 5, 2, 2, S);
        var corner = r.Found(0, 0, "Corner", "May");
        var at = r.TileAt(0, 0)!;
        Assert.Equal((0, 0, 2, 2), r.AnnexRect(at, 1, 1));
        long price = r.AnnexPrice(at, (0, 0, 2, 2));
        Assert.True(price > r.AnnexPrice(at, (0, 0, 2, 1)) * 2, "a bigger city pays more a tile");
        Assert.Contains("needs $", r.CantAnnex(at, 1, 1));
        corner.Enqueue(new TakeLoanAction(price));
        corner.ApplyPending();
        var w = r.Annex(at, 1, 1);
        Assert.Equal((2 * S, 2 * S), (w.Width, w.HeightCells));
        Assert.All(r.Tiles, t => Assert.Same(w, t.City));
        // Its own region's charter caps it.
        r.Charter = new RegionCharter(CharterRule.Vote, CharterRule.Vote, 1);
        Assert.Equal(1, r.MaxCitySide);
    }
}
