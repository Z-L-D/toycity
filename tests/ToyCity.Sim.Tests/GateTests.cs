using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Transport;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>
/// The Alpha 3 gate: a player runs a bus company inside a city with visible effects in both directions, while the city
/// sits in stasis, caretaker or autopilot as chosen.
/// </summary>
public class GateTests
{
    [Theory]
    [InlineData(OperatorLevel.Stasis)]
    [InlineData(OperatorLevel.Caretaker)]
    [InlineData(OperatorLevel.Autopilot)]
    public void APlayerRunsABusCompanyWhileTheComputerHasTheCity(OperatorLevel level)
    {
        // The player runs the company: leases every stop and the depot, draws a line and buys buses.
        var w = TransportTests.Town(100 + (ulong)level);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "").ToList()) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        var save = new TW.SaveLine { Name = "Gate Line", FareCents = 150 };
        save.StopLeaseIds.AddRange(w.Leases.Where(l => l.Kind == "bus-stop").Select(l => l.Id));
        w.Enqueue(new SideGameAction(TransportGame.Id, "operator", new TW.Command { SaveLine = save }.ToByteArray()));
        w.Step();
        int lineId = TransportTests.View(w).Lines.Single().Id;
        w.Enqueue(new SideGameAction(TransportGame.Id, "operator", new TW.Command { Buses = new TW.SetBuses { LineId = lineId, Buses = 4 } }.ToByteArray()));

        // The mayor hands the city to the computer.
        w.Enqueue(new SetSeatAction(Seat.Mayor, "player", level));
        w.Step();
        Assert.Equal(level, w.MayorSeat.Level);
        long funds = w.Funds;
        int pop = w.Population, homes = w.Buildings.Count(b => b != null && b.Residents > 0);
        w.Run(95);

        // City to company: commuters ride the line and pay fares.
        var v = TransportTests.View(w);
        var line = v.Lines.Single();
        Assert.Equal("", line.Problem);
        Assert.True(line.Riders > 0, $"nobody rode with the city on {level}");
        Assert.True(v.LastMonth.Fares > 0);
        Assert.Equal("manual", v.Operator);

        // Company to city: drivers employed at the depot, riders through the city's stops, buses in the commute.
        var depot = w.Leases.Single(l => l.Kind == "bus-depot");
        Assert.True(w.Buildings[depot.BuildingId]!.Workers > 0);
        Assert.Contains(w.Leases, l => l.Kind == "bus-stop" && w.Buildings[l.BuildingId]!.Riders > 0);
        Assert.Contains(w.Buildings, b => b != null && b.Assignments.Any(a => a.LineLegs.Length > 0));

        // And the city did what its seat was set to.
        switch (level)
        {
            case OperatorLevel.Stasis:
                Assert.Equal(funds, w.Funds);
                Assert.Equal(pop, w.Population);
                Assert.Equal(0, v.LastMonth.Rent);
                break;
            case OperatorLevel.Caretaker:
                Assert.Contains(w.LastBudget, l => l.Label == "Leases: Toy Transit Co." && l.Amount > 0);
                Assert.True(v.LastMonth.Rent > 0);
                break;
            case OperatorLevel.Autopilot:
                Assert.Contains(w.LastBudget, l => l.Label == "Leases: Toy Transit Co." && l.Amount > 0);
                Assert.True(w.Buildings.Count(b => b != null && b.Residents > 0) > homes || w.Population > pop, "autopilot didn't grow the city");
                break;
        }

        // Back in hand, the report covers the time away.
        w.Enqueue(new SetSeatAction(Seat.Mayor, "player", OperatorLevel.Manual));
        w.Step();
        Assert.Equal(level, w.MayorSeat.Report!.Level);
    }
}
