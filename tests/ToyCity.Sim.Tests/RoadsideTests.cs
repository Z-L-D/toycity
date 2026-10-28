using ToyCity.Contract;

namespace ToyCity.Sim.Tests;

/// <summary>Bus stops on the kerb of a road, and civic buildings upgrading in place.</summary>
public class RoadsideTests
{
    static readonly int[] Dx = { 0, 1, 0, -1 }, Dy = { -1, 0, 1, 0 };

    /// <summary>A road cell buses can use with land beyond its kerb on side k: the first one from the top left.</summary>
    static (int X, int Y, int Side) Kerb(World w, int skip = 0)
    {
        for (int y = 1; y < w.HeightCells - 1; y++)
        for (int x = 1; x < w.Width - 1; x++)
        for (int k = 0; k < 4; k++)
        {
            if (w.Path[w.Idx(x, y)] == 0 || w.ValidatePlacement(new PlaceBuildingAction(x, y, "bus-stop", k), out _) != null) continue;
            if (skip-- > 0) continue;
            return (x, y, k);
        }
        throw new InvalidOperationException("no kerb for a stop");
    }

    /// <summary>A starter town grown until transit unlocks.</summary>
    static World Grown(ulong seed)
    {
        var w = Fixture.Starter(seed);
        w.Run(60);
        return w;
    }

    static Building Place(World w, int x, int y, int side, string type = "bus-stop")
    {
        int before = w.Buildings.Count;
        w.Enqueue(new PlaceBuildingAction(x, y, type, side));
        w.ApplyPending();
        Assert.Equal(before + 1, w.Buildings.Count);
        return w.Buildings[^1]!;
    }

    [Fact]
    public void AStopGoesOnTheKerbAndTakesNoLand()
    {
        var w = Grown(301);
        var (x, y, side) = Kerb(w);
        int i = w.Idx(x, y);
        ushort path = w.Path[i];
        var stop = Place(w, x, y, side);
        Assert.Equal((x, y, side), (stop.X, stop.Y, stop.Side));
        Assert.Equal(path, w.Path[i]);
        Assert.Equal(0, w.BuildingAt[i]);
        Assert.Same(stop, w.BuildingOrStopAt(i));
        Assert.Equal("Bus Stop", w.Why(x, y).Subject);

        // One stop a side.
        Assert.NotNull(w.ValidatePlacement(new PlaceBuildingAction(x, y, "bus-stop", side), out _));
        // A side where another road joins has no kerb.
        for (int k = 0; k < 4; k++)
            if (w.InBounds(x + Dx[k], y + Dy[k]) && w.Path[w.Idx(x + Dx[k], y + Dy[k])] != 0)
                Assert.NotNull(w.ValidatePlacement(new PlaceBuildingAction(x, y, "bus-stop", k), out _));
    }

    [Fact]
    public void AClickOnLandBesideARoadPutsTheStopOnTheKerbFacingIt()
    {
        var w = Grown(302);
        var (x, y, side) = Kerb(w);
        int lx = x + Dx[side], ly = y + Dy[side];
        Assert.Equal(0, w.Path[w.Idx(lx, ly)]);
        var stop = Place(w, lx, ly, side);
        Assert.Equal((x, y, side), (stop.X, stop.Y, stop.Side));
    }

    [Fact]
    public void AStopGoesWithItsRoad()
    {
        var w = Grown(303);
        var (x, y, side) = Kerb(w);
        var stop = Place(w, x, y, side);
        w.Enqueue(new BulldozeAction(new CellRect(x, y, x, y)));
        w.Run(5);
        Assert.Equal(0, w.Path[w.Idx(x, y)]);
        Assert.Null(w.Buildings[stop.Id]);
    }

    [Fact]
    public void UpgradesWaitUntilEarnedCostLessThanNewAndBranch()
    {
        var w = Grown(304);
        var (x, y, side) = Kerb(w);
        var stop = Place(w, x, y, side);
        w.Run(10);
        Assert.True(stop.Operating);

        // Not yet: a new stop has no riders.
        var opt = Assert.Single(w.UpgradeOptions(stop));
        Assert.Equal("bus-shelter", opt.To.Id);
        Assert.False(opt.Available);
        Assert.Contains("riders", opt.WhyNot);
        w.Enqueue(new UpgradeBuildingAction(stop.Id, "bus-shelter"));
        w.ApplyPending();
        Assert.Equal("bus-stop", w.TypeOf(stop).Id);

        // Earned: cheaper than building a shelter new, and more upkeep after.
        stop.Riders = 200;
        Assert.True(w.UpgradeOptions(stop).Single().Available);
        Assert.True(opt.Def.Cost < opt.To.Cost);
        long funds = w.Funds;
        w.Enqueue(new UpgradeBuildingAction(stop.Id, "bus-shelter"));
        w.ApplyPending();
        Assert.Equal("bus-shelter", w.TypeOf(stop).Id);
        Assert.Equal(funds - opt.Def.Cost, w.Funds);
        Assert.Equal((x, y, side), (stop.X, stop.Y, stop.Side));
        Assert.True(w.TypeOf(stop).Maintenance > w.Content.Types[w.Content.TypeIndex("bus-stop")].Maintenance);

        // The shelter branches; taking one closes the other.
        Assert.Equal(new[] { "bus-bay", "kiosk-shelter" }, w.UpgradeOptions(stop).Select(o => o.To.Id).OrderBy(s => s));
        stop.Riders = 600;
        w.Enqueue(new UpgradeBuildingAction(stop.Id, "bus-bay"));
        w.ApplyPending();
        Assert.Equal("bus-bay", w.TypeOf(stop).Id);
        Assert.Empty(w.UpgradeOptions(stop));
        w.Enqueue(new UpgradeBuildingAction(stop.Id, "kiosk-shelter"));
        w.ApplyPending();
        Assert.Equal("bus-bay", w.TypeOf(stop).Id);

        // It survives a save.
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        var b = loaded.Buildings[stop.Id]!;
        Assert.Equal(("bus-bay", side), (loaded.TypeOf(b).Id, b.Side));
    }

    [Fact]
    public void ABiggerLeasedStopRaisesTheRentAfterNotice()
    {
        var w = LeaseTests.WithProbe(305);
        var (x, y, side) = Kerb(w);
        var stop = Place(w, x, y, side);
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop.Id));
        w.Run(10);
        var lease = w.LeaseOn(stop.Id)!;
        Assert.Equal(40, lease.Terms.RentPerMonth);
        stop.Riders = 200;
        w.Enqueue(new UpgradeBuildingAction(stop.Id, "bus-shelter"));
        w.ApplyPending();
        Assert.Equal(40, lease.Terms.RentPerMonth);
        Assert.Equal(80, lease.PendingTerms!.RentPerMonth);
        Assert.Equal("bus-stop", lease.Kind);
        Assert.Contains(w.Notices, n => n.Text.Contains("rent there goes up to $80"));
    }

    [Fact]
    public void RoadsideStopsReplay()
    {
        var w = Grown(306);
        var (x, y, side) = Kerb(w);
        Place(w, x, y, side);
        var (x2, y2, side2) = Kerb(w, skip: 12);
        Place(w, x2, y2, side2);
        w.Run(40);
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
        Assert.Contains(replay.Buildings, b => b != null && b.X == x2 && b.Y == y2 && b.Side == side2);
    }
}
