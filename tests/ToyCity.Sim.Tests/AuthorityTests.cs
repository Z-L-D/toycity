using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Transport;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

public class AuthorityTests
{
    static SideGameAction Cmd(TW.Command c) => new(TransportGame.Id, "operator", c.ToByteArray());

    static SideGameAction Buy(string kind, int district) => Cmd(new TW.Command { Standing = new TW.BuyStanding { Kind = kind, District = district } });

    /// <summary>A town whose bus company the player runs, with every stop and the depot leased to it.</summary>
    static World PlayerCompany(ulong seed)
    {
        var w = TransportTests.Town(seed);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "").ToList()) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        return w;
    }

    static int DistrictOfLease(World w, Lease l) => w.DistrictOf(w.Buildings[l.BuildingId]!.X, w.Buildings[l.BuildingId]!.Y);

    [Fact]
    public void GoodServiceRaisesTheRating()
    {
        var w = TransportTests.Town(91);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "", OperatorLevel.Autopilot));
        w.Run(150);
        var stop = w.Leases.First(l => l.Kind == "bus-stop");
        int d = DistrictOfLease(w, stop);
        Assert.True(w.RatingOf(TransportGame.Id, d) > 0, $"rating {w.RatingOf(TransportGame.Id, d)}");
        Assert.Contains(TransportTests.View(w).Districts, x => x.Id == d && x.Rating == w.RatingOf(TransportGame.Id, d));
    }

    [Fact]
    public void PaidActionsGoThroughTheContractAndArePaidFor()
    {
        var w = PlayerCompany(92);
        var stop = w.Leases.First(l => l.Kind == "bus-stop");
        int d = DistrictOfLease(w, stop);
        long cash = TransportTests.View(w).Cash, funds = w.Funds;
        int before = w.RatingOf(TransportGame.Id, d);
        w.Enqueue(Buy("advertise", d));
        w.Enqueue(Buy("statue", d));
        w.Run(2);
        Assert.Equal(Math.Min(1000, before + World.AdvertiseLift), w.RatingOf(TransportGame.Id, d));
        Assert.True(w.FindStanding(TransportGame.Id, d)!.Statue);
        var v = TransportTests.View(w);
        Assert.True(v.Districts.Single(x => x.Id == d).Statue);
        Assert.True(cash - v.Cash >= 10_000, $"paid {cash - v.Cash}");
        Assert.Equal(10_000, v.ThisMonth.Standing + v.LastMonth.Standing);
        Assert.True(w.Funds - funds >= 8_000 - 500, "the statue is paid to the city");
        // A second statue in the same district is turned down, and costs nothing.
        w.Enqueue(Buy("statue", d));
        w.Run(2);
        Assert.Contains(TransportTests.View(w).Log, s => s.Contains("already has a statue"));
        Assert.Equal(10_000, TransportTests.View(w).ThisMonth.Standing + TransportTests.View(w).LastMonth.Standing);
    }

    [Fact]
    public void BriberyIsOffUntilEveryOwnerAllowsIt()
    {
        var w = PlayerCompany(93);
        int d = DistrictOfLease(w, w.Leases.First(l => l.Kind == "bus-stop"));
        Assert.False(w.Allowed(Mechanic.Bribery));
        w.Notices.Clear();
        w.Enqueue(Buy("bribe", d));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("Bribery is switched off") && n.Text.Contains("player"));

        w.Enqueue(new SetConsentAction("player", Mechanic.Bribery, true));
        w.Step();
        Assert.True(w.Allowed(Mechanic.Bribery));
        int before = w.RatingOf(TransportGame.Id, d);
        w.Enqueue(Buy("bribe", d));
        w.Run(2);
        var s = w.FindStanding(TransportGame.Id, d)!;
        Assert.True(s.Rating == Math.Min(1000, before + World.BribeLift) || (s.Rating == World.CaughtRating && s.BribeBanUntil > w.Tick), $"rating {s.Rating}");

        // A second player claims the bus company and doesn't allow bribes: nobody can bribe while they own a seat.
        w.Enqueue(new SetSeatAction(TransportGame.Id, "", OperatorLevel.Caretaker));
        w.Enqueue(new SetSeatAction(TransportGame.Id, "bob", OperatorLevel.Manual));
        w.Step();
        Assert.Equal(new[] { "bob", "player" }, w.Owners());
        Assert.False(w.Allowed(Mechanic.Bribery));
        Assert.Equal(new[] { "bob" }, w.Blockers(Mechanic.Bribery));
        var row = w.ConsentTable().Single(r => r.Mechanic == Mechanic.Bribery);
        Assert.Equal(new[] { false, true }, row.ByOwner);
        Assert.False(TransportTests.View(w).Offers.Single(o => o.Kind == "bribe").Allowed);
        // Loosening happens when the last blocker changes their mind.
        w.Enqueue(new SetConsentAction("bob", Mechanic.Bribery, true));
        w.Step();
        Assert.True(w.Allowed(Mechanic.Bribery));
    }

    [Fact]
    public void ExclusiveRightsTakeTheCityBusesOffUnleasedStops()
    {
        var w = TransportTests.Town(94);
        w.Run(20);
        var stops = w.Buildings.Where(b => b != null && w.LotKindOf(b) == "bus-stop").ToList();
        // A district with a stop to lease and another left to the city's own buses.
        var pair = stops.GroupBy(b => w.DistrictOf(b!.X, b.Y)).First(g => g.Count() >= 2).ToList();
        int d = w.DistrictOf(pair[0]!.X, pair[0]!.Y);
        var depot = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-depot")!;
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        w.Enqueue(new LeaseLotAction(TransportGame.Id, pair[0]!.Id));
        w.Enqueue(new LeaseLotAction(TransportGame.Id, depot.Id));
        w.Enqueue(new SetConsentAction("player", Mechanic.ExclusiveRights, true));
        w.Run(2);
        var cityStop = pair[1]!;
        Assert.Null(w.LeaseOn(cityStop.Id));
        w.Enqueue(Buy("exclusive-rights", d));
        w.Run(3);
        Assert.True(w.FindStanding(TransportGame.Id, d)!.ExclusiveUntil > w.Tick);
        Assert.Contains(w.Headlines, h => h.Text.Contains("exclusive rights"));
        w.Run(65);
        Assert.Equal(0, cityStop.Riders);
        Assert.InRange(TransportTests.View(w).Districts.Single(x => x.Id == d).ExclusiveDays, 250, 360);
        // Buying them again while they're held is turned down, and costs nothing.
        long standing = TransportTests.View(w).ThisMonth.Standing;
        w.Enqueue(Buy("exclusive-rights", d));
        w.Run(2);
        Assert.True(TransportTests.View(w).Log.Any(s => s.Contains("holds exclusive rights")) || w.Notices.Any(n => n.Text.Contains("costs $12,000")));
        Assert.Equal(standing, TransportTests.View(w).ThisMonth.Standing);
    }

    [Fact]
    public void HostileEvictionsNeedEveryonesConsentNoticeAndCompensation()
    {
        var w = PlayerCompany(95);
        var lease = w.Leases.First(l => l.Kind == "bus-stop");
        w.Notices.Clear();
        w.Enqueue(new EndLeaseAction(lease.Id, ByOperator: false));
        w.Step();
        Assert.NotNull(w.LeaseById(lease.Id));
        Assert.Contains(w.Notices, n => n.Text.Contains("hostile evictions are switched off"));

        w.Enqueue(new SetConsentAction("player", Mechanic.HostileEvictions, true));
        w.Enqueue(new EndLeaseAction(lease.Id, ByOperator: false));
        w.Step();
        Assert.Equal(lease.Terms.NoticeMonths, lease.EvictMonths);
        long comp = 6 * World.StandardTerms("bus-stop").RentPerMonth;
        w.Run(30 * (lease.Terms.NoticeMonths + 2));
        Assert.Null(w.LeaseById(lease.Id));
        Assert.Contains(w.Notices, n => n.Text.Contains($"paid ${comp:N0} compensation"));
        Assert.Contains(TransportTests.View(w).Log, s => s.Contains($"The city paid ${comp:N0} in compensation"));
    }

    [Fact]
    public void StandingAndConsentSaveAndReplay()
    {
        var w = PlayerCompany(96);
        int d = DistrictOfLease(w, w.Leases.First(l => l.Kind == "bus-stop"));
        w.Enqueue(new SetConsentAction("player", Mechanic.Bribery, true));
        w.Enqueue(new SetConsentAction("player", Mechanic.AbsurdDisasters, false));
        w.Enqueue(Buy("advertise", d));
        w.Run(40);
        w.Enqueue(Buy("bribe", d));
        w.Run(3);
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.True(loaded.Allowed(Mechanic.Bribery));
        Assert.False(loaded.Allowed(Mechanic.AbsurdDisasters));
        w.Run(40);
        loaded.Run(40);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }
}
