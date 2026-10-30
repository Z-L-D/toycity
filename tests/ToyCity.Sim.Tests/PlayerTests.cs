using ToyCity.Contract;
using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

/// <summary>Several players in one city: who may do what, refusals for the player alone, and seats while away.</summary>
public class PlayerTests
{
    /// <summary>A town with the probe side game, the city run by Zac.</summary>
    static World Town(ulong seed)
    {
        ProbeGame.Register();
        var w = Fixture.Starter(seed);
        w.Enqueue(new AttachSideGameAction(ProbeGame.Id));
        w.Run(20);
        w.Enqueue(new RenameOwnerAction("player", "zac")); w.ApplyPending();
        return w;
    }

    static readonly byte[] Bump = System.Text.Encoding.UTF8.GetBytes("bump");

    static List<SimNotice> Do(World w, SimAction a)
    {
        w.Notices.Clear();
        w.Enqueue(a);
        w.ApplyPending();
        return w.Notices.ToList();
    }

    static (int X, int Y) Road(World w)
    {
        for (int y = 0; y < w.HeightCells; y++)
            for (int x = 0; x < w.Width; x++)
                if (w.Path[w.Idx(x, y)] != 0) return (x, y);
        throw new InvalidOperationException("no road");
    }

    [Fact]
    public void OnlyTheMayorRunsTheCityAndTheRefusalGoesToWhoAsked()
    {
        var w = Town(81);
        var (x, y) = Road(w);
        int tax = w.TaxTenths[0];
        var refused = Do(w, new SetTaxAction(Zone.Residential, Level.Low, tax + 10) { By = "mo" });
        Assert.Equal(tax, w.TaxTenths[0]);
        var n = Assert.Single(refused);
        Assert.Equal("mo", n.To);
        Assert.Contains("Only the mayor (zac)", n.Text);
        Assert.NotEqual(0, w.Path[w.Idx(x, y)]);
        Do(w, new BulldozeAction(new CellRect(x, y, x, y)) { By = "mo" });
        Assert.NotEqual(0, w.Path[w.Idx(x, y)]);

        Do(w, new SetTaxAction(Zone.Residential, Level.Low, tax + 10) { By = "zac" });
        Assert.Equal(tax + 10, w.TaxTenths[0]);
        // An action from nobody in particular (a tool, a test, an old save) is allowed as before.
        Do(w, new SetTaxAction(Zone.Residential, Level.Low, tax) );
        Assert.Equal(tax, w.TaxTenths[0]);
    }

    [Fact]
    public void FreeSeatsAreClaimedForYourselfAndOwnedSeatsStayPut()
    {
        var w = Town(82);
        var seat = w.SeatOf(ProbeGame.Id)!;
        Assert.Contains(Do(w, new SetSeatAction(ProbeGame.Id, "zac", OperatorLevel.Manual) { By = "mo" }), n => n.Text.Contains("for yourself"));
        Assert.Equal("", seat.Owner);
        Do(w, new SetSeatAction(ProbeGame.Id, "mo", OperatorLevel.Manual) { By = "mo" });
        Assert.Equal("mo", seat.Owner);
        Assert.Contains(Do(w, new SetSeatAction(ProbeGame.Id, "zac", OperatorLevel.Manual) { By = "zac" }), n => n.Text.Contains("mo owns that seat"));
        Assert.Equal("mo", seat.Owner);
        Assert.Contains(Do(w, new SetSeatAction(Seat.Mayor, "mo", OperatorLevel.Manual) { By = "mo" }), n => n.Text.Contains("zac owns that seat"));
        Assert.Equal("zac", w.MayorSeat.Owner);

        // Only the operator commands the side game; only each player sets their own consent.
        var probe = (ProbeGame)w.SideGame(ProbeGame.Id)!.Game;
        int bumps = probe.Bumps;
        Assert.Contains(Do(w, new SideGameAction(ProbeGame.Id, ProbeGame.Id, Bump) { By = "zac" }), n => n.Text.Contains("mo runs Probe Co."));
        Assert.Equal(bumps, probe.Bumps);
        Do(w, new SideGameAction(ProbeGame.Id, ProbeGame.Id, Bump) { By = "mo" });
        Assert.Equal(bumps + 1, probe.Bumps);
        Assert.Contains(Do(w, new SetConsentAction("mo", Mechanic.Bribery, true) { By = "zac" }), n => n.Text.Contains("your own"));
        Assert.False(w.Allows("mo", Mechanic.Bribery));

        // The mayor can hand the city to another player, who then runs it.
        Do(w, new SetSeatAction(Seat.Mayor, "mo", OperatorLevel.Manual) { By = "zac" });
        Assert.Equal("mo", w.MayorSeat.Owner);
    }

    [Fact]
    public void SeatsGoToTheComputerWhileTheirOwnerIsAwayAndComeBack()
    {
        var w = Town(83);
        Do(w, new SetSeatAction(ProbeGame.Id, "mo", OperatorLevel.Manual) { By = "mo" });
        Do(w, new SetAwayLevelAction(Seat.Mayor, OperatorLevel.Stasis) { By = "zac" });
        Assert.Contains(Do(w, new SetAwayLevelAction(ProbeGame.Id, OperatorLevel.Stasis) { By = "mo" }), n => n.Text.Contains("Stasis is for the city"));
        Assert.Contains(Do(w, new SetAwayLevelAction(Seat.Mayor, OperatorLevel.Autopilot) { By = "mo" }), n => n.Text.Contains("seat's owner"));

        Do(w, new SeatAwayAction(Seat.Mayor, true) { By = "zac" });
        Do(w, new SeatAwayAction(ProbeGame.Id, true) { By = "mo" });
        Assert.True(w.InStasis);
        Assert.True(w.MayorSeat.AutoAway);
        Assert.Equal(OperatorLevel.Caretaker, w.SeatOf(ProbeGame.Id)!.Level);
        w.Run(40);

        Do(w, new SeatAwayAction(Seat.Mayor, false) { By = "zac" });
        Assert.Equal(OperatorLevel.Manual, w.MayorSeat.Level);
        Assert.False(w.MayorSeat.AutoAway);
        Assert.NotNull(w.MayorSeat.Report);
        // A seat its owner handed over on purpose isn't taken back when they return.
        Do(w, new SetSeatAction(ProbeGame.Id, "mo", OperatorLevel.Autopilot) { By = "mo" });
        Do(w, new SeatAwayAction(ProbeGame.Id, false) { By = "mo" });
        Assert.Equal(OperatorLevel.Autopilot, w.SeatOf(ProbeGame.Id)!.Level);
    }

    [Fact]
    public void WhoDidWhatSavesAndReplays()
    {
        var w = Town(84);
        Do(w, new SetSeatAction(ProbeGame.Id, "mo", OperatorLevel.Manual) { By = "mo" });
        Do(w, new SetTaxAction(Zone.Commercial, Level.Low, 120) { By = "mo" });   // refused
        Do(w, new SetTaxAction(Zone.Industrial, Level.Low, 50) { By = "zac" });
        Do(w, new SetAwayLevelAction(ProbeGame.Id, OperatorLevel.Autopilot) { By = "mo" });
        Do(w, new SeatAwayAction(ProbeGame.Id, true) { By = "mo" });
        w.Run(30);

        var file = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), file.StateHash());
        Assert.Contains(file.Log, l => l.Action is SetTaxAction { By: "mo" });
        Assert.Contains(file.Log, l => l.Action is SeatAwayAction { By: "mo", Away: true });
        Assert.Equal(OperatorLevel.Autopilot, file.SeatOf(ProbeGame.Id)!.AwayLevel);
        Assert.True(file.SeatOf(ProbeGame.Id)!.AutoAway);

        // The server's rename is logged too, so a replay arrives at the same city.
        Assert.Contains(file.Log, l => l.Action is RenameOwnerAction { From: "player", To: "zac" });
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, file.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }
}
