using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Protocol;
using ToyCity.Transport;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>Consent in full: everyone or just the two involved, pacts, and the host's and the city's minimums.</summary>
public class ConsentTests
{
    static SideGameAction Buy(string kind, int district, string by) =>
        new(TransportGame.Id, "operator", new TW.Command { Standing = new TW.BuyStanding { Kind = kind, District = district } }.ToByteArray()) { By = by };

    /// <summary>Zac is mayor, Mo runs the bus company on every lot, and Jo runs the probe: a third player who owns a seat.</summary>
    static (World W, int District) Three(ulong seed)
    {
        ProbeGame.Register();
        var w = TransportTests.Town(seed);
        w.Enqueue(new RenameOwnerAction("player", "zac")); w.ApplyPending();
        w.Enqueue(new AttachSideGameAction(ProbeGame.Id) { By = "zac" });
        w.Enqueue(new SetSeatAction(TransportGame.Id, "mo", OperatorLevel.Manual) { By = "mo" });
        w.Enqueue(new SetSeatAction(ProbeGame.Id, "jo", OperatorLevel.Manual) { By = "jo" });
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "").ToList()) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id) { By = "zac" });
        w.Run(2);
        var lot = w.Buildings[w.Leases.First(l => l.Kind == "bus-stop").BuildingId]!;
        return (w, w.DistrictOf(lot.X, lot.Y));
    }

    static void Allow(World w, string who, Mechanic m, bool on = true) { w.Enqueue(new SetConsentAction(who, m, on) { By = who }); w.ApplyPending(); }

    [Fact]
    public void ABribeNeedsOnlyTheOperatorAndTheMayor()
    {
        var (w, d) = Three(101);
        Allow(w, "mo", Mechanic.Bribery);
        Assert.False(w.AllowedBetween(Mechanic.Bribery, "mo", "zac"));
        Allow(w, "zac", Mechanic.Bribery);
        // Jo still says no, but a bribe stays between Mo and the mayor.
        Assert.False(w.Allows("jo", Mechanic.Bribery));
        Assert.True(w.AllowedBetween(Mechanic.Bribery, "mo", "zac"));
        Assert.False(w.Allowed(Mechanic.Bribery));
        w.Step();  // the company hears tomorrow's offers, bribes now among them
        Assert.True(TransportTests.View(w).Offers.Single(o => o.Kind == "bribe").Allowed);
        int before = w.RatingOf(TransportGame.Id, d);
        w.Enqueue(Buy("bribe", d, "mo"));
        w.Run(2);
        var st = w.FindStanding(TransportGame.Id, d)!;
        Assert.True(st.Rating == Math.Min(1000, before + World.BribeLift) || (st.Rating == World.CaughtRating && st.BribeBanUntil > w.Tick), $"rating {st.Rating}");
    }

    [Fact]
    public void AMechanicThatReachesEveryoneNeedsEveryone()
    {
        var (w, _) = Three(102);
        foreach (var p in new[] { "zac", "mo" }) Allow(w, p, Mechanic.ExclusiveRights);
        Assert.False(w.AllowedBetween(Mechanic.ExclusiveRights, "mo", "zac"));
        Assert.Contains("jo won't allow it", w.WhyOff(Mechanic.ExclusiveRights, "mo", "zac"));
        Allow(w, "jo", Mechanic.ExclusiveRights);
        Assert.True(w.Allowed(Mechanic.ExclusiveRights));
        // And a pact can't be made on one.
        w.Notices.Clear();
        w.Enqueue(new OfferPactAction("zac", Mechanic.AbsurdDisasters) { By = "mo" });
        w.ApplyPending();
        Assert.Contains(w.Notices, n => n.Text.Contains("reaches everyone") && n.To == "mo");
    }

    [Fact]
    public void APactAllowsItBetweenTwoWhateverTheyAllowInGeneral()
    {
        var (w, _) = Three(103);
        Assert.False(w.AllowedBetween(Mechanic.HostileEvictions, "zac", "mo"));
        w.Enqueue(new OfferPactAction("mo", Mechanic.HostileEvictions) { By = "zac" });
        w.ApplyPending();
        Assert.Contains(w.PactOffers, o => o is { From: "zac", To: "mo" });
        Assert.False(w.AllowedBetween(Mechanic.HostileEvictions, "zac", "mo"));
        // Only the one offered can accept.
        w.Enqueue(new AcceptPactAction("zac", Mechanic.HostileEvictions) { By = "jo" });
        w.Enqueue(new AcceptPactAction("zac", Mechanic.HostileEvictions) { By = "mo" });
        w.ApplyPending();
        Assert.True(w.AllowedBetween(Mechanic.HostileEvictions, "mo", "zac"));
        Assert.False(w.AllowedBetween(Mechanic.HostileEvictions, "jo", "zac"));
        Assert.Empty(w.PactOffers);

        // The eviction itself now goes ahead with notice.
        var lease = w.Leases.First(l => l.Kind == "bus-stop");
        w.Enqueue(new EndLeaseAction(lease.Id, ByOperator: false) { By = "zac" });
        w.ApplyPending();
        Assert.True(w.LeaseById(lease.Id)!.EvictMonths > 0);

        var file = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), file.StateHash());
        Assert.Single(file.Pacts);

        w.Enqueue(new EndPactAction("zac", Mechanic.HostileEvictions) { By = "mo" });
        w.ApplyPending();
        Assert.Empty(w.Pacts);
        Assert.False(w.AllowedBetween(Mechanic.HostileEvictions, "mo", "zac"));
    }

    [Fact]
    public void TheHostAndTheCityCanForbidAnything()
    {
        var (w, _) = Three(104);
        foreach (var p in new[] { "zac", "mo", "jo" }) Allow(w, p, Mechanic.Bribery);
        Assert.True(w.Allowed(Mechanic.Bribery));

        w.Enqueue(new SetCityRuleAction(Mechanic.Bribery, true) { By = "mo" });  // not the mayor
        w.ApplyPending();
        Assert.False(w.CityForbids(Mechanic.Bribery));
        w.Enqueue(new SetCityRuleAction(Mechanic.Bribery, true) { By = "zac" });
        w.ApplyPending();
        Assert.False(w.AllowedBetween(Mechanic.Bribery, "mo", "zac"));
        Assert.Contains("forbidden in this city", w.WhyOff(Mechanic.Bribery, "mo", "zac"));
        w.Enqueue(new SetCityRuleAction(Mechanic.Bribery, false) { By = "zac" });
        w.ApplyPending();
        Assert.True(w.Allowed(Mechanic.Bribery));

        // The host's rules come from the server alone, and beat a pact.
        w.Enqueue(new SetHostRulesAction(new[] { Mechanic.Bribery }) { By = "zac" });
        w.ApplyPending();
        Assert.False(w.HostForbids(Mechanic.Bribery));
        w.Enqueue(new SetHostRulesAction(new[] { Mechanic.Bribery, Mechanic.HostileEvictions }));
        w.Enqueue(new OfferPactAction("mo", Mechanic.HostileEvictions) { By = "zac" });
        w.Enqueue(new AcceptPactAction("zac", Mechanic.HostileEvictions) { By = "mo" });
        w.ApplyPending();
        Assert.False(w.AllowedBetween(Mechanic.Bribery, "mo", "zac"));
        Assert.False(w.AllowedBetween(Mechanic.HostileEvictions, "mo", "zac"));
        Assert.Contains("by its host", w.WhyOff(Mechanic.HostileEvictions, "mo", "zac"));
        var row = w.ConsentTable().Single(r => r.Mechanic == Mechanic.HostileEvictions);
        Assert.True(row.HostForbids);
        Assert.Equal(ConsentScope.Pair, row.Scope);

        var file = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Contains(file.Log, l => l.Action is SetHostRulesAction h && h.Forbidden.Contains(Mechanic.HostileEvictions));
        Assert.True(file.HostForbids(Mechanic.HostileEvictions));
    }
}
