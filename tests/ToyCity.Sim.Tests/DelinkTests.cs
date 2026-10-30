using ToyCity.Contract;
using ToyCity.Protocol;
using ToyCity.Transport;

namespace ToyCity.Sim.Tests;

/// <summary>De-linking: a player leaves with a copy of the city, the city keeps its own, and both carry on.</summary>
public class DelinkTests
{
    /// <summary>Zac is mayor and Olu runs the bus company, which leases one stop.</summary>
    static (World W, Lease Lease) Town(ulong seed)
    {
        var w = TransportTests.Town(seed);
        w.MakeCopies = true;
        w.Enqueue(new RenameOwnerAction("player", "zac"));
        w.Enqueue(new SetSeatAction(TransportGame.Id, "olu", OperatorLevel.Manual) { By = "olu" });
        var stop = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-stop")!;
        w.Enqueue(new LeaseLotAction(TransportGame.Id, stop.Id) { By = "zac" });
        w.Enqueue(new SetConsentAction("olu", Mechanic.Bribery, true) { By = "olu" });
        w.Step();
        return (w, w.Leases.Single());
    }

    static void Act(World w, SimAction a) { w.Enqueue(a); w.ApplyPending(); }

    [Fact]
    public void APlayerWhoLeavesTakesACopyAndTheCityKeepsItsOwn()
    {
        var (w, lease) = Town(901);
        w.Run(5);
        string home = w.Name;
        int stop = lease.BuildingId;
        Act(w, new DelinkAction("olu") { By = "olu" });

        // Home: the company is the computer's, its lease is over and Olu's settings are gone.
        var seat = w.SeatOf(TransportGame.Id)!;
        Assert.Equal("", seat.Owner);
        Assert.Equal(OperatorLevel.Caretaker, seat.Level);
        Assert.Empty(w.Leases);
        Assert.Null(w.ConsentOf("olu"));
        Assert.Equal(home, w.Name);
        Assert.Equal("zac", w.MayorSeat.Owner);
        var d = w.Delinks.Single();
        Assert.Equal(("olu", "zac", 1), (d.Player, d.Mayor, d.LeasesEnded));

        // The copy: Olu is its mayor and still runs the company on the same lot; Zac is gone from it.
        var (player, copy) = w.Copies.Single();
        using (copy)
        {
            Assert.Equal("olu", player);
            Assert.Equal("olu", copy.MayorSeat.Owner);
            Assert.Equal(OperatorLevel.Manual, copy.MayorSeat.Level);
            Assert.Equal("olu", copy.SeatOf(TransportGame.Id)!.Owner);
            Assert.Equal(stop, copy.Leases.Single().BuildingId);
            Assert.Equal($"{home} (olu)", copy.Name);
            Assert.Null(copy.ConsentOf("zac"));
            Assert.True(copy.ConsentOf("olu")!.Contains(Mechanic.Bribery));
            Assert.IsType<ForkAction>(copy.Log[^1].Action);

            // Both run on, save and load on their own.
            copy.Run(10);
            w.Run(10);
            using var copyLoaded = Mapping.Load(Fixture.Content, Mapping.Save(copy));
            Assert.Equal(copy.StateHash(), copyLoaded.StateHash());
            using var homeLoaded = Mapping.Load(Fixture.Content, Mapping.Save(w));
            Assert.Equal(w.StateHash(), homeLoaded.StateHash());
            copyLoaded.Run(3);
            homeLoaded.Run(3);
        }
    }

    [Fact]
    public void BothCopiesReplayFromTheirLogs()
    {
        var (w, _) = Town(902);
        w.Run(3);
        string home = w.Name;
        Act(w, new DelinkAction("olu") { By = "olu" });
        using var copy = w.Copies.Single().Copy;
        w.Run(4);
        copy.Run(4);
        using var homeReplay = World.Replay(Fixture.Content, w.Seed, w.Width, home, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), homeReplay.StateHash());
        using var copyReplay = World.Replay(Fixture.Content, copy.Seed, copy.Width, home, copy.Log, copy.Tick);
        Assert.Equal(copy.StateHash(), copyReplay.StateHash());
    }

    [Fact]
    public void TheMayorCutsAPlayerLooseOnlyOnAnEvictionsGrounds()
    {
        var (w, lease) = Town(903);
        w.Notices.Clear();
        Act(w, new DelinkAction("olu") { By = "zac" });
        Assert.Empty(w.Delinks);
        Assert.Contains(w.Notices, n => n.Text.Contains("good standing"));
        Assert.Contains(w.DisputeLadder(lease), s => s.Step == "De-link" && s.Rule.Contains("same grounds"));
        Act(w, new DelinkAction("olu") { By = "jo" });
        Assert.Empty(w.Delinks);

        // With hostile evictions on between them, the mayor may; what Olu owed is written off.
        Act(w, new SetConsentAction("zac", Mechanic.HostileEvictions, true) { By = "zac" });
        Act(w, new SetConsentAction("olu", Mechanic.HostileEvictions, true) { By = "olu" });
        Assert.Contains(w.DelinkOptions(), o => o.Player == "olu" && o.Why == null);
        lease.Arrears = 450;
        Act(w, new DelinkAction("olu") { By = "zac" });
        Assert.Equal(450, w.Delinks.Single().WrittenOff);
        Assert.Equal("zac", w.Delinks.Single().By);
        foreach (var (_, c) in w.Copies) c.Dispose();

        // The mayor can't de-link themselves.
        Act(w, new DelinkAction("zac") { By = "zac" });
        Assert.Single(w.Delinks);
    }

    [Fact]
    public void TheTwoCantShareACityUntilTheCooldownIsOver()
    {
        var (w, _) = Town(904);
        Act(w, new DelinkAction("olu") { By = "olu" });
        foreach (var (_, c) in w.Copies) c.Dispose();
        w.Notices.Clear();
        Act(w, new SetSeatAction(TransportGame.Id, "olu", OperatorLevel.Manual) { By = "olu" });
        Assert.Equal("", w.SeatOf(TransportGame.Id)!.Owner);
        Assert.Contains(w.Notices, n => n.Text.Contains("can share a city again"));
        Act(w, new SetDeputyAction("olu", Portfolio.Transit) { By = "zac" });
        Assert.Empty(w.Deputies);

        // Someone else may take the company meanwhile, and Olu may come back after the cooldown.
        w.Run(World.RelinkCooldownDays);
        Act(w, new SetSeatAction(TransportGame.Id, "olu", OperatorLevel.Manual) { By = "olu" });
        Assert.Equal("olu", w.SeatOf(TransportGame.Id)!.Owner);
    }
}
