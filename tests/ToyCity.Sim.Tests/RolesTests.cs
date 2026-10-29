using ToyCity.Contract;
using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

/// <summary>Deputies with portfolios, and the council that votes on taxes, laws and loans.</summary>
public class RolesTests
{
    const int ResLow = 0;  // residential $ in TaxTenths

    /// <summary>Zac is mayor; Mo is around but holds nothing yet.</summary>
    static World Town(ulong seed)
    {
        var w = Fixture.Starter(seed);
        w.Enqueue(new RenameOwnerAction("player", "zac")); w.ApplyPending();
        return w;
    }

    static SetTaxAction Tax(int tenths, string by) => new(Zone.Residential, Level.Low, tenths) { By = by };

    [Fact]
    public void ADeputyActsOnlyInTheirPortfolio()
    {
        var w = Town(201);
        w.Notices.Clear();
        w.Enqueue(Tax(120, "mo"));
        w.ApplyPending();
        Assert.Equal(90, w.TaxTenths[ResLow]);
        Assert.Contains(w.Notices, n => n.To == "mo" && n.Text.Contains("deputy for the budget"));

        // Only the mayor names deputies.
        w.Enqueue(new SetDeputyAction("mo", Portfolio.Budget) { By = "mo" });
        w.ApplyPending();
        Assert.Empty(w.Deputies);
        w.Enqueue(new SetDeputyAction("mo", Portfolio.Budget) { By = "zac" });
        w.Enqueue(Tax(120, "mo"));
        w.ApplyPending();
        Assert.Equal(Portfolio.Budget, w.Deputies["mo"]);
        Assert.Equal(120, w.TaxTenths[ResLow]);

        // The budget isn't planning.
        w.Notices.Clear();
        w.Enqueue(new BulldozeAction(new CellRect(0, 0, 1, 1)) { By = "mo" });
        w.ApplyPending();
        Assert.Contains(w.Notices, n => n.To == "mo" && n.Text.Contains("deputy for planning"));

        // Stood down, they can't touch the budget either.
        w.Enqueue(new SetDeputyAction("mo", Portfolio.None) { By = "zac" });
        w.Enqueue(Tax(100, "mo"));
        w.ApplyPending();
        Assert.Empty(w.Deputies);
        Assert.Equal(120, w.TaxTenths[ResLow]);
    }

    [Fact]
    public void TheCouncilIsOffUntilTheMayorSeatsIt()
    {
        var w = Town(202);
        Assert.False(w.CouncilOn);
        w.Enqueue(new SetDeputyAction("mo", Portfolio.Budget) { By = "zac" });
        w.Enqueue(Tax(110, "zac"));
        w.ApplyPending();
        Assert.Equal(110, w.TaxTenths[ResLow]);
        Assert.Empty(w.Proposals);

        w.Enqueue(new SetCouncilAction(true) { By = "mo" });  // not the mayor
        w.ApplyPending();
        Assert.False(w.CouncilOn);
        w.Enqueue(new SetCouncilAction(true) { By = "zac" });
        w.ApplyPending();
        Assert.True(w.CouncilOn);
        Assert.Equal(new[] { "mo", "zac" }, w.CouncilMembers());
    }

    static World Sitting(ulong seed)
    {
        var w = Town(seed);
        w.Enqueue(new SetDeputyAction("mo", Portfolio.Budget) { By = "zac" });
        w.Enqueue(new SetCouncilAction(true) { By = "zac" });
        w.ApplyPending();
        return w;
    }

    [Fact]
    public void AProposalPassesWhenTheMembersAgree()
    {
        var w = Sitting(203);
        w.Enqueue(Tax(130, "mo"));
        w.ApplyPending();
        var p = Assert.Single(w.Proposals);
        Assert.Equal(ProposalStatus.Open, p.Status);
        Assert.Equal(90, w.TaxTenths[ResLow]);
        Assert.True(p.Votes["mo"]);

        // Spectators don't vote.
        w.Notices.Clear();
        w.Enqueue(new VoteAction(p.Id, false) { By = "jo" });
        w.ApplyPending();
        Assert.False(p.Votes.ContainsKey("jo"));

        w.Enqueue(new VoteAction(p.Id, true) { By = "zac" });
        w.ApplyPending();
        Assert.Equal(ProposalStatus.Passed, p.Status);
        Assert.Equal(130, w.TaxTenths[ResLow]);
        Assert.Contains(w.Notices, n => n.Text.Contains("council passed"));
    }

    [Fact]
    public void ATieGoesTheWayTheMayorVoted()
    {
        var w = Sitting(204);
        w.Enqueue(Tax(130, "mo"));
        w.ApplyPending();
        var p = w.Proposals.Single();
        w.Enqueue(new VoteAction(p.Id, false) { By = "zac" });
        w.ApplyPending();
        Assert.Equal(ProposalStatus.Failed, p.Status);
        Assert.Equal(90, w.TaxTenths[ResLow]);

        // The mayor's own proposal, opposed by the deputy: one each, and the mayor carries it.
        w.Enqueue(Tax(70, "zac"));
        w.ApplyPending();
        var q = w.Proposals.Single(x => x.Status == ProposalStatus.Open);
        w.Enqueue(new VoteAction(q.Id, false) { By = "mo" });
        w.ApplyPending();
        Assert.Equal(ProposalStatus.Passed, q.Status);
        Assert.Equal(70, w.TaxTenths[ResLow]);
    }

    [Fact]
    public void WhenTimeIsUpTheVotesCastDecide()
    {
        var w = Sitting(205);
        w.Enqueue(new TakeLoanAction(10_000) { By = "mo" });
        w.ApplyPending();
        var p = w.Proposals.Single();
        long funds = w.Funds;
        w.Run(World.VoteDays - 1);
        Assert.Equal(ProposalStatus.Open, p.Status);
        w.Run(2);
        Assert.Equal(ProposalStatus.Passed, p.Status);  // one for, none against
        Assert.True(w.Funds > funds + 5_000, $"funds {funds} → {w.Funds}");

        // Dissolving the council fails what is still open.
        w.Enqueue(Tax(150, "mo"));
        w.Enqueue(new SetCouncilAction(false) { By = "zac" });
        w.ApplyPending();
        Assert.All(w.Proposals, x => Assert.NotEqual(ProposalStatus.Open, x.Status));
        Assert.Equal(90, w.TaxTenths[ResLow]);
    }

    [Fact]
    public void RolesSaveAndReplay()
    {
        var w = Sitting(206);
        w.Enqueue(Tax(130, "mo"));
        w.Enqueue(new SetDeputyAction("jo", Portfolio.Planning | Portfolio.Transit) { By = "zac" });
        w.Run(3);
        var loaded = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal(Portfolio.Planning | Portfolio.Transit, loaded.Deputies["jo"]);
        Assert.True(loaded.CouncilOn);
        Assert.Equal(ProposalStatus.Open, loaded.Proposals.Single().Status);
        w.Run(10);
        loaded.Run(10);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal(130, loaded.TaxTenths[ResLow]);
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());

        var civic = Mapping.Civic(loaded);
        Assert.True(civic.CouncilOn);
        Assert.Contains(civic.Deputies, d => d.Player == "jo" && d.Portfolios == 9);
        Assert.Equal("passed", civic.Proposals.Single().Status);
    }
    [Fact]
    public void WhoeverRunsACompanyLeasesFreeLotsForItOnTheStandardTermsOnly()
    {
        SideGameCatalog.Register(Transport.TransportGame.Id, () => new Transport.TransportGame());
        var w = Town(205);
        w.Run(60);
        w.Enqueue(new AttachSideGameAction(Transport.TransportGame.Id) { By = "zac" });
        w.Step();
        var stops = TransportTests.PlaceAlongRoads(w, "bus-stop", 2, spacing: 6);
        w.Enqueue(new SetSeatAction(Transport.TransportGame.Id, "mo", OperatorLevel.Manual) { By = "mo" });
        w.ApplyPending();

        // Mo may take a free stop for the company, but not name the terms, which are the city's to set.
        w.Notices.Clear();
        w.Enqueue(new LeaseLotAction(Transport.TransportGame.Id, stops[0], new LeaseTerms(LeaseMode.Business, 0, 0, 0, 0, 1)) { By = "mo" });
        w.ApplyPending();
        Assert.Empty(w.Leases);
        Assert.Contains(w.Notices, n => n.To == "mo" && n.Text.Contains("deputy for transit"));
        w.Enqueue(new LeaseLotAction(Transport.TransportGame.Id, stops[0]) { By = "mo" });
        w.ApplyPending();
        var lease = Assert.Single(w.Leases);
        Assert.Equal(World.StandardTerms("bus-stop"), lease.Terms);

        // Someone who doesn't run it can't lease for it.
        w.Enqueue(new LeaseLotAction(Transport.TransportGame.Id, stops[1]) { By = "jo" });
        w.ApplyPending();
        Assert.Single(w.Leases);
    }
}
