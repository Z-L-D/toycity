using ToyCity.Contract;

namespace ToyCity.Sim.Tests;

public class LeaseTests
{
    static World WithProbe(ulong seed)
    {
        ProbeGame.Register();
        var w = Fixture.Starter(seed);
        w.Run(60);
        w.Enqueue(new AttachSideGameAction(ProbeGame.Id));
        w.Step();
        return w;
    }

    static ProbeGame Probe(World w) => (ProbeGame)w.SideGame(ProbeGame.Id)!.Game;

    /// <summary>Builds a bus stop on a free cell next to a road and returns its building id.</summary>
    static int PlaceStop(World w, int skip = 0)
    {
        for (int y = 1; y < w.HeightCells - 1; y++)
        for (int x = 1; x < w.Width - 1; x++)
        {
            if (w.ValidatePlacement(new PlaceBuildingAction(x, y, "bus-stop"), out _) != null) continue;
            if (w.Path[w.Idx(x + 1, y)] == 0 && w.Path[w.Idx(x - 1, y)] == 0 && w.Path[w.Idx(x, y + 1)] == 0 && w.Path[w.Idx(x, y - 1)] == 0) continue;
            if (skip-- > 0) continue;
            w.Enqueue(new PlaceBuildingAction(x, y, "bus-stop"));
            w.ApplyPending();
            return w.BuildingAt[w.Idx(x, y)] - 1;
        }
        throw new InvalidOperationException("no site for a stop");
    }

    static void ToNewMonth(World w)
    {
        do w.Step(); while (w.Day != 1);
        w.Step(); // the first of the month runs the budget, then the side games settle
    }

    [Fact]
    public void ABusinessLeasePaysRentAndFareTaxAndHires()
    {
        var w = WithProbe(71);
        int stop = PlaceStop(w);
        Probe(w).FaresPerLot = 20;
        Probe(w).JobsPerLot = 6;
        int baseJobs = w.Buildings[stop]!.Jobs;
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop));
        w.Step();
        var lease = w.LeaseOn(stop)!;
        Assert.Equal(World.StandardTerms("bus-stop"), lease.Terms);
        Assert.Equal(baseJobs + 6, w.Buildings[stop]!.Jobs);
        Assert.Single(Probe(w).Lots);
        ToNewMonth(w);
        // Rent, and 10% of the fares taken since the lease began.
        var bills = Probe(w).LastBills;
        Assert.Contains(bills, b => b.Label == "Rent" && b.Amount == 40);
        Assert.Contains(bills, b => b.Label == "Fare tax" && b.Amount > 0);
        long expected = bills.Sum(b => b.Amount);
        Assert.Contains(w.LastBudget, l => l.Label == "Leases: Probe Co." && l.Amount == expected);
        Assert.Equal(0, lease.Arrears);
        Assert.False(lease.BadStanding);
        // The stop can't be bulldozed while the lease stands.
        var b0 = w.Buildings[stop]!;
        w.Enqueue(new BulldozeAction(new CellRect(b0.X, b0.Y, b0.X, b0.Y)));
        w.Step();
        Assert.NotNull(w.Buildings[stop]);
        Assert.Contains(w.Notices, n => n.Text.Contains("leased to Probe Co."));
    }

    [Fact]
    public void UnpaidBillsBringBadStandingACurePeriodAndThenTheEnd()
    {
        var w = WithProbe(72);
        int stop = PlaceStop(w);
        Probe(w).PaysBills = false;
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop));
        ToNewMonth(w);
        var lease = w.LeaseOn(stop)!;
        Assert.True(lease.BadStanding);
        Assert.Equal(World.CureMonths, lease.CureMonthsLeft);
        Assert.Contains(w.LeaseTargets(lease), t => t.Contains("Pay the $40 owed"));
        w.Enqueue(new EndLeaseAction(lease.Id, ByOperator: false));
        w.Step();
        Assert.NotNull(w.LeaseOn(stop));
        // Catching up cures it.
        Probe(w).PaysBills = true;
        ToNewMonth(w);
        Assert.False(lease.BadStanding);
        Assert.Equal(0, lease.Arrears);
        // Falling behind again and staying behind: after the cure period the mayor may end it.
        Probe(w).PaysBills = false;
        for (int m = 0; m <= World.CureMonths; m++) ToNewMonth(w);
        Assert.True(lease.Terminable);
        Assert.Contains(w.Notices, n => n.Text.Contains("may now end the lease"));
        w.Enqueue(new EndLeaseAction(lease.Id, ByOperator: false));
        w.Step();
        Assert.Null(w.LeaseOn(stop));
        Assert.NotNull(w.Buildings[stop]);
    }

    [Fact]
    public void ChangingToPublicServiceWaitsOutTheNotice()
    {
        var w = WithProbe(73);
        int stop = PlaceStop(w);
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop));
        w.Step();
        var lease = w.LeaseOn(stop)!;
        var pub = new LeaseTerms(LeaseMode.PublicService, 0, 0, 120, 100, 3);
        w.Enqueue(new SetLeaseTermsAction(lease.Id, pub));
        w.Step();
        Assert.Equal(LeaseMode.Business, lease.Terms.Mode);
        Assert.Equal(3, lease.PendingMonths);
        ToNewMonth(w);
        ToNewMonth(w);
        Assert.Equal(LeaseMode.Business, lease.Terms.Mode);
        Assert.Equal(pub, Probe(w).Lots.Single().PendingTerms);
        ToNewMonth(w);
        Assert.Equal(pub, lease.Terms);
        ToNewMonth(w);
        Assert.Contains(Probe(w).LastBills, b => b.Label == "Subsidy" && b.Amount == -120);
        Assert.Contains(w.LastBudget, l => l.Label == "Subsidies: Probe Co." && l.Amount == -120);
    }

    [Fact]
    public void TheComputerOnlyGrantsStandardTermsAndSideGamesCanAskAndLeave()
    {
        var w = WithProbe(74);
        int a = PlaceStop(w), b = PlaceStop(w, skip: 3);
        w.Enqueue(new SetSeatAction(Seat.Mayor, "player", OperatorLevel.Autopilot));
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, a, new LeaseTerms(LeaseMode.Business, 1, 0, 0, 0, 3)));
        w.Step();
        Assert.Null(w.LeaseOn(a));
        Assert.Contains(w.Notices, n => n.Text.Contains("only grants leases on its standard terms"));
        // The side game asks for a lot itself, and gets the standard terms.
        Probe(w).Requests.Add(b);
        w.Step();
        w.Step();
        var lease = w.LeaseOn(b)!;
        Assert.Equal(World.StandardTerms("bus-stop"), lease.Terms);
        Probe(w).Releases.Add(lease.Id);
        w.Step();
        w.Step();
        Assert.Null(w.LeaseOn(b));
    }

    [Fact]
    public void ALeaseEndsWhenItsLotIsDestroyed()
    {
        var w = WithProbe(75);
        int stop = PlaceStop(w);
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop));
        w.Step();
        var lease = w.LeaseOn(stop)!;
        w.Enqueue(new EndLeaseAction(lease.Id, ByOperator: true));
        w.Step();
        Assert.Null(w.LeaseOn(stop));
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop));
        w.Step();
        // A tornado or a fire can take a leased lot; the lease goes with it.
        var b = w.Buildings[stop]!;
        w.Enqueue(new BulldozeAction(new CellRect(b.X, b.Y, b.X, b.Y)));
        w.Step();
        Assert.NotNull(w.LeaseOn(stop));
        w.RemoveBuilding(w.Buildings[stop]!);
        w.Step();
        Assert.Null(w.LeaseOn(stop));
        Assert.Empty(Probe(w).Lots);
    }

    [Fact]
    public void LeasesSaveAndReplay()
    {
        var w = WithProbe(76);
        int stop = PlaceStop(w);
        w.Enqueue(new LeaseLotAction(ProbeGame.Id, stop));
        w.Run(40);
        var lease = w.LeaseOn(stop)!;
        w.Enqueue(new SetLeaseTermsAction(lease.Id, new LeaseTerms(LeaseMode.PublicService, 0, 0, 50, 0, 2)));
        w.Run(20);
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        w.Run(70);
        loaded.Run(70);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
        Assert.Equal(LeaseMode.PublicService, replay.LeaseOn(stop)!.Terms.Mode);
    }
}
