namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.2, subways: tunnels laid under the city, stations on the surface, and commuters riding them.</summary>
public class SubwayTests
{
    /// <summary>A home and a factory 26 cells apart on a slow dirt road, optionally with a subway between them.</summary>
    static (World W, Building Home, int X, int Road) Lab(bool subway)
    {
        var (w, x, y) = Fixture.FlatSite();
        int road = y + 10;
        w.Funds = 1_000_000;
        w.Enqueue(new PlacePathAction(x, road, x + 30, road, "dirt-road"));
        w.Enqueue(new PlaceBuildingAction(x + 12, road - 4, "coal-plant"));
        if (subway)
        {
            // Straight under the road; the stations stand beside it, over nothing but its ring.
            w.Enqueue(new PlaceUtilityAction(x, road, x + 30, road, UtilityKind.Subway));
            w.Enqueue(new PlaceBuildingAction(x + 1, road + 1, "subway-station"));
            w.Enqueue(new PlaceBuildingAction(x + 27, road + 1, "subway-station"));
        }
        w.Step();
        var home = w.AddBuilding(Fixture.Content.Zoned(Zone.Residential, Level.Medium, Level.High), x + 1, road - 2, 0, 2, 2);
        w.AddBuilding(Fixture.Content.Zoned(Zone.Industrial, Level.Medium, Level.Medium), x + 27, road - 2, 0, 2, 2);
        w.Run(61);
        return (w, home, x, road);
    }

    [Fact]
    public void ATunnelCostsByTheCellAndGoesUnderAnything()
    {
        var w = Fixture.Starter(42);
        w.Run(30);
        // Right across the town, under its houses and streets.
        var town = w.Buildings.Where(b => b != null && b.Residents > 0).ToList();
        int cy = (int)town.Average(b => b!.Y), x0 = town.Min(b => b!.X), x1 = town.Max(b => b!.X);
        int under = Enumerable.Range(x0, x1 - x0 + 1).Count(x => w.BuildingAt[w.Idx(x, cy)] != 0 || w.Path[w.Idx(x, cy)] != 0);
        Assert.True(under > 0, "the tunnel should pass under something");
        int buildings = w.Buildings.Count(b => b != null);
        long funds = w.Funds;
        w.Enqueue(new PlaceUtilityAction(x0, cy, x1, cy, UtilityKind.Subway));
        w.ApplyPending();
        Assert.Equal(funds - (x1 - x0 + 1) * World.SubwayCost, w.Funds);
        Assert.All(Enumerable.Range(x0, x1 - x0 + 1), x => Assert.NotEqual(0, w.Subway[w.Idx(x, cy)]));
        Assert.Equal(buildings, w.Buildings.Count(b => b != null));
        Assert.True(w.Carries(TransitKind.Subway, w.Idx(x0, cy)));
        Assert.False(w.Carries(TransitKind.Rail, w.Idx(x0, cy)));

        // Saved, and dug up again only by the underground bulldozer.
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Subway, back.Subway);
        Assert.Equal(w.StateHash(), back.StateHash());
        w.Enqueue(new BulldozeAction(new CellRect(x0, cy, x1, cy)));
        w.ApplyPending();
        Assert.NotEqual(0, w.Subway[w.Idx(x0, cy)]);
        w.Enqueue(new BulldozeAction(new CellRect(x0, cy, x1, cy), Underground: true));
        w.ApplyPending();
        Assert.All(Enumerable.Range(x0, x1 - x0 + 1), x => Assert.Equal(0, w.Subway[w.Idx(x, cy)]));
    }

    [Fact]
    public void TheCitysSubwayWinsOverASlowRoad()
    {
        var (car, carHome, _, _) = Lab(subway: false);
        var (sub, subHome, x, road) = Lab(subway: true);
        Assert.All(carHome.Assignments, a => Assert.False(a.ByTransit));
        var rode = subHome.Assignments.Where(a => a.ByTransit).ToList();
        Assert.NotEmpty(rode);
        Assert.True(subHome.CommuteMinutes < carHome.CommuteMinutes, $"{subHome.CommuteMinutes} vs {carHome.CommuteMinutes} min");
        var station = sub.Buildings.First(b => b != null && sub.TypeOf(b).Id == "subway-station")!;
        Assert.True(station.Riders > 0);
        Assert.True(sub.LastBudget.Any(l => l.Label == "Subway tunnels" && l.Amount < 0), "the tunnels cost upkeep");

        // Dig up the middle of the tunnel and the trains stop.
        sub.Enqueue(new BulldozeAction(new CellRect(x + 10, road, x + 20, road), Underground: true));
        sub.Run(61);
        Assert.All(subHome.Assignments, a => Assert.False(a.ByTransit));
    }

    [Fact]
    public void AStationWorksOverItsTunnelOrBesideItButNotFarFromIt()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Funds = 1_000_000;
        int road = y + 10;
        w.Enqueue(new PlacePathAction(x, road, x + 30, road, "dirt-road"));
        w.Enqueue(new PlaceBuildingAction(x + 12, road - 4, "coal-plant"));
        w.Enqueue(new PlaceUtilityAction(x, road + 2, x + 30, road + 2, UtilityKind.Subway));
        w.Enqueue(new PlaceBuildingAction(x + 1, road + 1, "subway-station"));   // over the tunnel
        w.Enqueue(new PlaceBuildingAction(x + 27, road + 3, "subway-station"));  // beside it
        w.Enqueue(new PlaceBuildingAction(x + 14, road + 6, "subway-station"));  // two cells off it
        // Power to the stations off the street.
        w.Enqueue(new PlaceUtilityAction(x + 11, road - 4, x + 11, road + 5, UtilityKind.PowerLine));
        w.Enqueue(new PlaceUtilityAction(x + 11, road + 5, x + 30, road + 5, UtilityKind.PowerLine));
        w.Run(3);
        var stations = w.Buildings.Where(b => b != null && w.TypeOf(b).Id == "subway-station").ToList();
        Assert.Equal(3, stations.Count);
        Assert.All(stations, b => Assert.True(b!.Powered, w.Why(b.X, b.Y).Summary));
        var lonely = w.Alerts().Where(a => a.Key == "stop-no-line").ToList();
        Assert.Single(lonely);
        Assert.Contains("subway tunnel", lonely[0].Text);
        Assert.Contains("Nothing stops here", w.Why(x + 14, road + 6).Summary);
        Assert.DoesNotContain("Nothing stops here", w.Why(x + 1, road + 1).Summary);
        Assert.DoesNotContain("Nothing stops here", w.Why(x + 27, road + 3).Summary);
    }
}
