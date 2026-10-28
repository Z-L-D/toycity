using ToyCity.Contract;
using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

public class SeatTests
{
    static World Town(ulong seed, int days = 120)
    {
        var w = Fixture.Starter(seed);
        w.Run(days);
        return w;
    }

    static void Hand(World w, OperatorLevel level, AutopilotStyle style = AutopilotStyle.Growth, long cap = Seat.DefaultSpendCap)
    {
        w.Enqueue(new SetSeatAction(Seat.Mayor, "player", level, style, cap));
        w.Step();
    }

    [Fact]
    public void StasisHoldsTheCityWhilePeopleKeepMoving()
    {
        var w = Town(61);
        Hand(w, OperatorLevel.Stasis);
        Assert.True(w.InStasis);
        int pop = w.Population, buildings = w.Buildings.Count(b => b != null);
        long funds = w.Funds, debt = w.Debt;
        int approval = w.Approval;
        var deadlines = w.Prompts.Where(p => p.Status == PromptStatus.Open).ToDictionary(p => p.Id, p => p.Deadline);
        w.Run(150);
        Assert.Equal(pop, w.Population);
        Assert.Equal(buildings, w.Buildings.Count(b => b != null));
        Assert.Equal(funds, w.Funds);
        Assert.Equal(debt, w.Debt);
        Assert.Equal(approval, w.Approval);
        foreach (var p in w.Prompts.Where(p => deadlines.ContainsKey(p.Id)))
            Assert.Equal(deadlines[p.Id] + 150, p.Deadline);

        Hand(w, OperatorLevel.Manual);
        var report = w.MayorSeat.Report!;
        Assert.Equal(OperatorLevel.Stasis, report.Level);
        Assert.Contains(report.Changes, c => c.Contains("5 months") && c.Contains("stasis"));
        Assert.Contains(report.Changes, c => c.Contains("didn't need to do anything"));
        // Back in hand, the city moves again.
        w.Run(90);
        Assert.NotEqual(funds, w.Funds);
    }

    [Fact]
    public void CaretakerPaysTheBillsRebuildsAndTurnsDownDeals()
    {
        var w = Town(62);
        Hand(w, OperatorLevel.Caretaker);
        var baseline = (int[])w.TaxTenths.Clone();
        var pump = w.Buildings.First(b => b != null && w.TypeOf(b).Category == Category.Water)!;
        int px = pump.X, py = pump.Y;
        w.Enqueue(new BulldozeAction(new CellRect(px, py, px + 1, py + 1)));
        w.Step();
        Assert.Null(w.BuildingAtCell(w.Idx(px, py)));
        w.Funds = -3_000;
        w.Run(31);
        Assert.True(w.Funds >= 0, $"funds {w.Funds}");
        Assert.True(w.Debt > 0);
        Assert.NotNull(w.BuildingAtCell(w.Idx(px, py)));
        Assert.Equal(Category.Water, w.TypeOf(w.BuildingAtCell(w.Idx(px, py))!).Category);
        w.Run(360);
        for (int k = 0; k < 9; k++) Assert.InRange(w.TaxTenths[k], baseline[k] - 10, baseline[k] + 10);
        Assert.DoesNotContain(w.Prompts, p => p.Kind == EventKind.Deal && p.Status == PromptStatus.Accepted);
        // Caretaker never zones or lays roads.
        Assert.DoesNotContain(w.MayorSeat.Away!.Notes, n => n.Text.StartsWith("Zoned") || n.Text.Contains("streets"));

        Hand(w, OperatorLevel.Manual);
        var report = w.MayorSeat.Report!;
        Assert.Contains(report.Notes, n => n.Text.Contains("loan"));
        Assert.Contains(report.Notes, n => n.Text.Contains("Rebuilt"));
    }

    [Fact]
    public void AutopilotGrowsTheCityWithinItsCap()
    {
        var w = Town(63, 60);
        Hand(w, OperatorLevel.Autopilot, AutopilotStyle.Growth, cap: 20_000);
        int pop = w.Population;
        w.Run(36 * 30);
        Assert.True(w.Population > pop, $"population {pop} → {w.Population}");
        Assert.InRange(w.TaxTenths.Min(), 70, 100);
        Assert.InRange(w.TaxTenths.Max(), 70, 100);
        var notes = w.MayorSeat.Away!.Notes;
        Assert.NotEmpty(notes);
        Assert.Contains(notes, n => n.Text.StartsWith("Zoned") || n.Text.Contains("streets") || n.Text.StartsWith("Built"));
    }

    [Fact]
    public void AutopilotNeverSpendsMoreThanTheCapInAMonth()
    {
        var w = Town(64, 60);
        w.Funds = 200_000;
        Hand(w, OperatorLevel.Autopilot, AutopilotStyle.Growth, cap: 3_000);
        for (int m = 0; m < 24; m++)
        {
            while (w.Day != 1) w.Step();
            long before = w.Funds;
            w.Step(); // the month's budget and automation run on day 1
            long net = w.LastIncome - w.LastExpenses;
            long automation = before + net - w.Funds;
            Assert.True(automation <= 3_000, $"month {m}: automation spent {automation}");
        }
    }

    [Fact]
    public void SeatsSaveAndReplay()
    {
        ProbeGame.Register();
        var w = Town(65, 60);
        w.Enqueue(new AttachSideGameAction(ProbeGame.Id));
        Hand(w, OperatorLevel.Autopilot, AutopilotStyle.Profit);
        Assert.Equal(OperatorLevel.Caretaker, w.SeatOf(ProbeGame.Id)!.Level);
        w.Enqueue(new SetSeatAction(ProbeGame.Id, "dana", OperatorLevel.Manual));
        w.Run(100);
        Assert.Equal(OperatorLevel.Manual, ((ProbeGame)w.SideGame(ProbeGame.Id)!.Game).LastOperator);

        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal("dana", loaded.SeatOf(ProbeGame.Id)!.Owner);
        w.Run(60);
        loaded.Run(60);
        Assert.Equal(w.StateHash(), loaded.StateHash());

        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
        var file = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), file.StateHash());
    }

    [Fact]
    public void SeatRulesAreEnforced()
    {
        var w = Town(66, 10);
        w.Enqueue(new SetSeatAction(Seat.Mayor, "", OperatorLevel.Autopilot));
        w.Enqueue(new SetSeatAction("nope", "player", OperatorLevel.Manual));
        w.Step();
        Assert.Equal(OperatorLevel.Manual, w.MayorSeat.Level);
        Assert.Contains(w.Notices, n => n.Text.Contains("always has a mayor"));
        Assert.Contains(w.Notices, n => n.Text.Contains("no seat called"));
    }
}
