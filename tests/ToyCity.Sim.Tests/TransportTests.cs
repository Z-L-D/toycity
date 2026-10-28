using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Transport;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

public class TransportTests
{
    /// <summary>A starter town with the bus company attached, six bus stops spread along its roads and a depot.</summary>
    internal static World Town(ulong seed, int days = 90)
    {
        SideGameCatalog.Register(TransportGame.Id, () => new TransportGame());
        var w = Fixture.Starter(seed);
        w.Run(days);
        w.Enqueue(new AttachSideGameAction(TransportGame.Id));
        w.Step();
        PlaceAlongRoads(w, "bus-stop", 6, spacing: 7);
        PlaceAlongRoads(w, "bus-depot", 1, spacing: 0);
        return w;
    }

    /// <summary>Builds civic buildings on free land touching a road, at least <paramref name="spacing"/> cells apart.</summary>
    internal static List<int> PlaceAlongRoads(World w, string type, int count, int spacing)
    {
        var placed = new List<int>();
        var t = w.Content.Types[w.Content.TypeIndex(type)];
        var town = w.Buildings.Where(b => b != null && b.Residents > 0).ToList();
        int cx = (int)town.Average(b => b!.X), cy = (int)town.Average(b => b!.Y);
        var spots = new List<(int X, int Y)>();
        for (int y = 1; y + t.H < w.HeightCells - 1; y++)
        for (int x = 1; x + t.W < w.Width - 1; x++)
        {
            if (Math.Abs(x - cx) > 24 || Math.Abs(y - cy) > 24) continue;
            if (w.ValidatePlacement(new PlaceBuildingAction(x, y, type), out _) != null) continue;
            bool road = false;
            for (int k = 0; k < t.W && !road; k++) road = w.Path[w.Idx(x + k, y - 1)] != 0 || w.Path[w.Idx(x + k, y + t.H)] != 0;
            if (!road) continue;
            if (spots.Any(s => Math.Abs(s.X - x) + Math.Abs(s.Y - y) < Math.Max(spacing, t.W + t.H))) continue;
            spots.Add((x, y));
        }
        // Spread out: take every so many of the candidates, from the town's middle outwards.
        spots = spots.OrderBy(s => Math.Abs(s.X - cx) + Math.Abs(s.Y - cy)).ThenBy(s => s.Y).ThenBy(s => s.X).ToList();
        foreach (var (x, y) in spots.Take(count))
        {
            w.Enqueue(new PlaceBuildingAction(x, y, type));
            w.ApplyPending();
            placed.Add(w.Buildings.Count - 1);
        }
        Assert.Equal(count, placed.Count);
        return placed;
    }

    internal static TW.View View(World w) => TW.View.Parser.ParseFrom(w.SideGame(TransportGame.Id)!.Game.View("operator"));

    static SideGameAction Cmd(TW.Command c) => new(TransportGame.Id, "operator", c.ToByteArray());

    [Fact]
    public void OnAutopilotTheCompanyLeasesLotsRunsALineAndFillsItsBuses()
    {
        var w = Town(81);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "", OperatorLevel.Autopilot));
        w.Run(150);
        var v = View(w);
        Assert.Contains(v.Lots, l => l.Kind == "bus-depot");
        Assert.True(v.Lots.Count(l => l.Kind == "bus-stop") >= 2);
        var line = Assert.Single(v.Lines);
        Assert.True(line.Buses > 0 && line.HeadwayMinutes > 0, line.Problem);
        Assert.True(line.Riders > 0, "nobody rode");
        Assert.True(v.LastMonth.Fares > 0);
        // The city sees it: rent on the budget, drivers employed at the depot, riders using the stops.
        Assert.Contains(w.LastBudget, l => l.Label.StartsWith("Leases:") && l.Amount > 0);
        var depot = w.Leases.Single(l => l.Kind == "bus-depot");
        Assert.True(w.Buildings[depot.BuildingId]!.Workers > 0);
        Assert.Contains(w.Buildings, b => b != null && w.LeaseOn(b.Id) is { Kind: "bus-stop" } && b.Riders > 0);
    }

    [Fact]
    public void APlayerRunsALineAndTheFareMovesRidership()
    {
        var w = Town(82);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        var lots = w.Buildings.Where(b => b != null && w.LotKindOf(b) != "").Select(b => b!.Id).ToList();
        foreach (var id in lots) w.Enqueue(new LeaseLotAction(TransportGame.Id, id));
        w.Step();
        w.Step();
        var stops = w.Leases.Where(l => l.Kind == "bus-stop").Select(l => l.Id).ToList();
        Assert.Equal(6, stops.Count);
        var save = new TW.SaveLine { Name = "Crosstown", FareCents = 100 };
        save.StopLeaseIds.AddRange(stops);
        w.Enqueue(Cmd(new TW.Command { SaveLine = save }));
        w.Step();
        int lineId = View(w).Lines.Single().Id;
        w.Enqueue(Cmd(new TW.Command { Buses = new TW.SetBuses { LineId = lineId, Buses = 4 } }));
        w.Run(65);
        var cheap = View(w).Lines.Single();
        Assert.Equal("", cheap.Problem);
        Assert.True(cheap.Riders > 0, "nobody rode at $1");
        Assert.True(cheap.LastMonth.Fares > 0);
        Assert.Equal(4 * TransportGame.WagePerBus, cheap.LastMonth.Wages);

        // A $9 fare drives riders back to their cars, or onto their feet.
        var dear = save.Clone();
        dear.LineId = lineId;
        dear.FareCents = 900;
        w.Enqueue(Cmd(new TW.Command { SaveLine = dear }));
        w.Run(45);
        Assert.True(View(w).Lines.Single().Riders < cheap.Riders, $"{View(w).Lines.Single().Riders} riders at $9 vs {cheap.Riders} at $1");
    }

    [Fact]
    public void ALineThroughJustTwoStopsFindsRiders()
    {
        // A depot and two stops: the smallest company a player can start. Their walking reach has to cover enough
        // homes and jobs for anyone to ride.
        var w = Town(82);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        var stops = w.Buildings.Where(b => b != null && w.LotKindOf(b) == "bus-stop").Take(2).ToList();
        foreach (var b in stops.Append(w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-depot"))) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        var save = new TW.SaveLine { Name = "Shuttle", FareCents = 150 };
        save.StopLeaseIds.AddRange(w.Leases.Where(l => l.Kind == "bus-stop").Select(l => l.Id));
        w.Enqueue(Cmd(new TW.Command { SaveLine = save }));
        w.Step();
        w.Enqueue(Cmd(new TW.Command { Buses = new TW.SetBuses { LineId = View(w).Lines.Single().Id, Buses = 2 } }));
        w.Run(65);
        Assert.True(View(w).Lines.Single().Riders > 0, "nobody rode a two-stop line");
    }

    [Fact]
    public void BadCommandsAreTurnedDown()
    {
        var w = Town(83);
        w.Enqueue(Cmd(new TW.Command { SaveLine = new TW.SaveLine { Name = "Nowhere", StopLeaseIds = { 99, 98 } } }));
        w.Enqueue(new SideGameAction(TransportGame.Id, "operator", new byte[] { 0xff, 0xff }));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("bus stop the company leases"));
        Assert.Contains(w.Notices, n => n.Text.Contains("didn't understand"));
        Assert.Empty(View(w).Lines);
    }

    [Fact]
    public void AFireNearAStopClosesItForAFewDays()
    {
        var w = Town(84);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "", OperatorLevel.Autopilot));
        w.Run(60);
        var lot = View(w).Lots.First(l => l.Kind == "bus-stop");
        var near = w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => Math.Abs(b!.X - lot.X) + Math.Abs(b.Y - lot.Y)).First()!;
        Assert.True(Math.Max(Math.Abs(near.X - lot.X), Math.Abs(near.Y - lot.Y)) <= 3, "no home near the stop");
        w.Enqueue(new TriggerDisasterAction(DisasterKind.Fire, near.X, near.Y));
        w.Run(2);
        Assert.True(View(w).Lots.First(l => l.LeaseId == lot.LeaseId).ClosedDays > 0);
        Assert.Contains(View(w).Log, s => s.Contains("Closed the stop"));
    }

    [Fact]
    public void TheBusCompanySavesAndReplaysWithTheCity()
    {
        var w = Town(85);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "", OperatorLevel.Autopilot));
        w.Run(100);
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        w.Run(70);
        loaded.Run(70);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }
}
