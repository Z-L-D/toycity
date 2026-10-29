using ToyCity.Contract;
using ToyCity.Protocol;
using ToyCity.Transport;

namespace ToyCity.Sim.Tests;

/// <summary>The dispute ladder over a lease: offers both ways, counters, the computer's answers, buyouts and moves.</summary>
public class DisputeTests
{
    /// <summary>Zac is mayor and Olu runs the bus company, which leases one stop; the other stops stand free.</summary>
    static (World W, Lease Lease) Town(ulong seed, bool oluPlays = true)
    {
        var w = TransportTests.Town(seed);
        w.Enqueue(new RenameOwnerAction("player", "zac"));
        if (oluPlays) w.Enqueue(new SetSeatAction(TransportGame.Id, "olu", OperatorLevel.Manual) { By = "olu" });
        var stop = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-stop")!;
        w.Enqueue(new LeaseLotAction(TransportGame.Id, stop.Id) { By = "zac" });
        w.Step();
        return (w, w.Leases.Single());
    }

    static LeaseTerms Rent(long rent) => new(LeaseMode.Business, rent, 100, 0, 0, 3);

    static OfferLeaseAction Offer(Lease l, bool fromOperator, string by, OfferKind kind = OfferKind.Terms, LeaseTerms? terms = null, long amount = 0, int to = -1, int counter = 0) =>
        new(l.Id, fromOperator, kind, terms, amount, to, counter) { By = by };

    static void Act(World w, SimAction a) { w.Enqueue(a); w.ApplyPending(); }

    [Fact]
    public void AgreedTermsStartAtOnceAndACounterReplacesTheOffer()
    {
        var (w, lease) = Town(801);
        Act(w, Offer(lease, false, "zac", terms: Rent(80)));
        var first = w.Offers.Single();
        Assert.Equal(OfferStatus.Open, first.Status);

        // Olu counters; the first offer is closed and only the counter is on the table.
        Act(w, Offer(lease, true, "olu", terms: Rent(20), counter: first.Id));
        Assert.Equal(OfferStatus.Countered, first.Status);
        var counter = w.Offers.Single(o => o.Status == OfferStatus.Open);
        Assert.True(counter.FromOperator);

        // Olu can't accept his own offer; Zac can.
        w.Notices.Clear();
        Act(w, new AnswerOfferAction(counter.Id, true) { By = "olu" });
        Assert.Equal(OfferStatus.Open, counter.Status);
        Act(w, new AnswerOfferAction(counter.Id, true) { By = "zac" });
        Assert.Equal(OfferStatus.Accepted, counter.Status);
        Assert.Equal(20, lease.Terms.RentPerMonth);
        Assert.Null(lease.PendingTerms);
        Assert.Contains(w.Notices, n => n.Text.Contains("accepted new terms"));
    }

    [Fact]
    public void OnlyTheTwoSidesDealAndOneOfferAtATime()
    {
        var (w, lease) = Town(802);
        w.Notices.Clear();
        Act(w, Offer(lease, false, "jo", terms: Rent(10)));
        Act(w, Offer(lease, true, "jo", terms: Rent(10)));
        Assert.Empty(w.Offers);
        Assert.Contains(w.Notices, n => n.To == "jo" && n.Text.Contains("mayor"));

        // A deputy for transit speaks for the city on a bus stop.
        Act(w, new SetDeputyAction("mo", Portfolio.Transit) { By = "zac" });
        Act(w, Offer(lease, false, "mo", terms: Rent(60)));
        Assert.Single(w.Offers);
        Act(w, Offer(lease, false, "zac", terms: Rent(70)));
        Assert.Single(w.Offers);
        Assert.Contains(w.Notices, n => n.Text.Contains("already an offer on the table"));

        // Its maker withdraws it.
        Act(w, new AnswerOfferAction(w.Offers[0].Id, false) { By = "mo" });
        Assert.Equal(OfferStatus.Withdrawn, w.Offers[0].Status);
    }

    [Fact]
    public void TheComputerRunningTheCompanyTakesABuyoutWorthMoreThanStaying()
    {
        var (w, lease) = Town(803, oluPlays: false);
        long keep = World.Compensation(lease) + 3 * lease.LastRevenue;
        Act(w, Offer(lease, false, "zac", OfferKind.Buyout, amount: keep - 1));
        w.Step();
        Assert.Equal(OfferStatus.Open, w.Offers.Single().Status);  // made today, answered tomorrow
        w.Step();
        Assert.Equal(OfferStatus.Declined, w.Offers.Single().Status);
        Assert.Contains("wants at least", w.Offers.Single().Note);

        long funds = w.Funds;
        Act(w, Offer(lease, false, "zac", OfferKind.Buyout, amount: keep + 500));
        w.Run(2);
        Assert.Equal(OfferStatus.Accepted, w.Offers.Last().Status);
        Assert.Empty(w.Leases);
        Assert.True(w.Funds < funds);
    }

    [Fact]
    public void AMoveKeepsTheLeaseAndItsJobsAndTheBusesFollow()
    {
        var (w, lease) = Town(804, oluPlays: false);
        w.Run(5);
        var from = w.Buildings[lease.BuildingId]!;
        var to = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-stop" && w.LeaseOn(b.Id) == null)!;
        Assert.Contains(w.DisputeLadder(lease), s => s.Step == "Relocate" && s.Open);
        Act(w, Offer(lease, false, "zac", OfferKind.Relocate, amount: World.MovingCost, to: to.Id));
        w.Run(2);
        Assert.Equal(OfferStatus.Accepted, w.Offers.Single().Status);
        Assert.Same(lease, w.Leases.Single());
        Assert.Equal(to.Id, lease.BuildingId);
        Assert.Equal(TransportGame.Id, w.LeaseOn(to.Id)!.GameId);
        Assert.Null(w.LeaseOn(from.Id));

        // Moving to a lot of another kind, or one already leased, is refused.
        w.Notices.Clear();
        var depot = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-depot")!;
        Act(w, Offer(lease, false, "zac", OfferKind.Relocate, to: depot.Id));
        Assert.Contains(w.Notices, n => n.Text.Contains("A move has to be to another"));
    }

    [Fact]
    public void TheLadderSaysWhatEachRungNeedsAndOffersLapse()
    {
        var (w, lease) = Town(805);
        var ladder = w.DisputeLadder(lease);
        Assert.Equal(new[] { "Negotiate", "Buy out", "Relocate", "Impose terms", "Evict", "De-link" }, ladder.Select(s => s.Step));
        var evict = ladder.Single(s => s.Step == "Evict");
        Assert.False(evict.Open);
        Assert.Contains("bad standing", evict.Rule);

        // Olu plays the company, so nothing answers for him: the offer lapses after a month.
        Act(w, Offer(lease, false, "zac", terms: Rent(90)));
        w.Run(World.OfferDays + 1);
        Assert.Equal(OfferStatus.Expired, w.Offers.Single().Status);
        Assert.Equal(World.StandardTerms("bus-stop").RentPerMonth, lease.Terms.RentPerMonth);
    }

    [Fact]
    public void OffersSaveLoadAndReplay()
    {
        var (w, lease) = Town(806);
        Act(w, Offer(lease, false, "zac", terms: Rent(80)));
        Act(w, Offer(lease, true, "olu", terms: Rent(30), counter: w.Offers[0].Id));
        w.Run(3);
        using (var loaded = Mapping.Load(Fixture.Content, Mapping.Save(w)))
        {
            Assert.Equal(w.StateHash(), loaded.StateHash());
            Assert.Equal(2, loaded.Offers.Count);
        }
        Act(w, new AnswerOfferAction(w.Offers[1].Id, true) { By = "zac" });
        w.Run(2);
        using var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
        Assert.Equal(30, replay.Leases.Single().Terms.RentPerMonth);
    }
}
