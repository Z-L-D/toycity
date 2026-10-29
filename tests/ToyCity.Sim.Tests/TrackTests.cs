using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Emergency;
using ToyCity.Protocol;
using ToyCity.Transport;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>The transport beta's first half: trams and trains run by the company, timetables, and vehicles that wear.</summary>
public class TrackTests
{
    static SideGameAction Cmd(TW.Command c) => new(TransportGame.Id, "operator", c.ToByteArray());

    /// <summary>
    /// A home and a factory 26 cells apart on a slow dirt road, with a track between them (a rail line or a tram
    /// avenue), a stop at each end and a depot for the company. The player runs the company and leases it all.
    /// </summary>
    static (World W, Building Home) Lab(TransitMode mode)
    {
        SideGameCatalog.Register(TransportGame.Id, () => new TransportGame());
        var (w, x, y) = Fixture.FlatSite();
        int road = y + 10;
        w.Enqueue(new PlacePathAction(x, road, x + 30, road, "dirt-road"));
        w.Enqueue(new PlaceBuildingAction(x + 12, road - 4, "coal-plant"));
        if (mode == TransitMode.Rail)
        {
            w.Enqueue(new PlacePathAction(x, road + 3, x + 30, road + 3, "rail"));
            w.Enqueue(new PlaceBuildingAction(x + 1, road + 1, "rail-station"));
            w.Enqueue(new PlaceBuildingAction(x + 27, road + 1, "rail-station"));
            w.Enqueue(new PlaceBuildingAction(x + 20, road - 3, "rail-depot"));
        }
        else
        {
            w.Enqueue(new PlacePathAction(x, road + 3, x + 30, road + 3, "tram-avenue"));
            w.Enqueue(new PlaceBuildingAction(x + 1, road + 2, "tram-stop"));
            w.Enqueue(new PlaceBuildingAction(x + 28, road + 2, "tram-stop"));
            w.Enqueue(new PlaceBuildingAction(x + 20, road - 3, "tram-depot"));
        }
        w.Enqueue(new AttachSideGameAction(TransportGame.Id));
        w.Step();
        // Working people of modest means, so plenty have no car.
        var home = w.AddBuilding(Fixture.Content.Zoned(Zone.Residential, Level.Medium, Level.Low), x + 1, road - 2, 0, 2, 2);
        w.AddBuilding(Fixture.Content.Zoned(Zone.Industrial, Level.Medium, Level.Medium), x + 27, road - 2, 0, 2, 2);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b).Length > 0 && w.LotKindOf(b) != "")) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        return (w, home);
    }

    static int OpenLine(World w, TransitMode mode, int vehicles, string name = "Line")
    {
        var save = new TW.SaveLine { Name = name, FareCents = 100, Mode = (TW.Mode)(int)mode };
        save.StopLeaseIds.AddRange(w.Leases.Where(l => l.Kind == TransitModes.StopKind(mode)).Select(l => l.Id));
        w.Enqueue(Cmd(new TW.Command { SaveLine = save }));
        w.Step();
        int id = TransportTests.View(w).Lines.Single(l => l.Name == name).Id;
        w.Enqueue(Cmd(new TW.Command { Buses = new TW.SetBuses { LineId = id, Buses = vehicles } }));
        w.Step();
        return id;
    }

    [Theory]
    [InlineData(TransitMode.Rail)]
    [InlineData(TransitMode.Tram)]
    public void TheCompanysTrainsAndTramsCarryTheCommuters(TransitMode mode)
    {
        var (w, home) = Lab(mode);
        int vehicles = mode == TransitMode.Rail ? 1 : 2;
        OpenLine(w, mode, vehicles);
        w.Run(61);
        var line = TransportTests.View(w).Lines.Single();
        Assert.Equal("", line.Problem);
        Assert.Equal((TW.Mode)(int)mode, line.Mode);
        Assert.True(line.HeadwayMinutes > 0 && line.RouteCells.Count > 2);
        Assert.True(line.Riders > 0, "nobody rode");
        Assert.Contains(home.Assignments, a => a.ByTransit && a.LineLegs.Length > 0);
        // Their crews work at the depot.
        var depot = w.Leases.Single(l => l.Kind == TransitModes.DepotKind(mode));
        Assert.Equal(vehicles * TransportGame.SpecOf(mode).Crew + TransportGame.DepotStaff, depot.Jobs);
    }

    [Fact]
    public void ALineRunsOnlyOnItsOwnKindOfStop()
    {
        var (w, _) = Lab(TransitMode.Rail);
        var save = new TW.SaveLine { Name = "Wrong", Mode = TW.Mode.Bus };
        save.StopLeaseIds.AddRange(w.Leases.Where(l => l.Kind == "rail-station").Select(l => l.Id));
        w.Notices.Clear();
        w.Enqueue(Cmd(new TW.Command { SaveLine = save }));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("must be a bus stop"));
        Assert.Empty(TransportTests.View(w).Lines);
    }

    [Fact]
    public void ATimetableRunsWhatItNeedsAndKeepsTheRestAsSpares()
    {
        var w = TransportTests.Town(1001);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "")) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        int id = OpenLine(w, TransitMode.Bus, 6);
        w.Run(3);
        var line = TransportTests.View(w).Lines.Single();
        int every = (line.RoundTripMinutes + 2) / 3;
        w.Enqueue(Cmd(new TW.Command { Timetable = new TW.SetTimetable { LineId = id, HeadwayMinutes = every } }));
        w.Run(3);
        line = TransportTests.View(w).Lines.Single();
        Assert.Equal(3, line.Needed);
        Assert.Equal(3, line.Running);
        Assert.True(line.HeadwayMinutes >= every);
        Assert.True(line.Vehicles.Count(v => v.State == "spare") >= 2);

        // A bad timetable is refused.
        w.Notices.Clear();
        w.Enqueue(Cmd(new TW.Command { Timetable = new TW.SetTimetable { LineId = id, HeadwayMinutes = 1 } }));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("timetable runs from"));
        w.Run(30);
        Assert.Equal(100, TransportTests.View(w).Lines.Single().OnTimePercent);
    }

    [Fact]
    public void VehiclesWearWithoutServicingAndBreakDown()
    {
        World Run(int serviceDays)
        {
            var w = TransportTests.Town(1002);
            w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
            foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "")) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
            w.Run(2);
            int id = OpenLine(w, TransitMode.Bus, 4);
            w.Enqueue(Cmd(new TW.Command { Service = new TW.SetService { LineId = id, EveryDays = serviceDays } }));
            w.Run(360);
            return w;
        }
        var kept = Run(30);
        var neglected = Run(0);
        var good = TransportTests.View(kept).Lines.Single();
        var bad = TransportTests.View(neglected).Lines.Single();
        Assert.True(good.Condition >= 70, $"{good.Condition}% kept");
        Assert.True(bad.Condition <= 20, $"{bad.Condition}% neglected");
        Assert.True(bad.LastMonth.Upkeep < good.LastMonth.Upkeep || bad.Vehicles.Any(v => v.State == "broken") || neglected.RecentIncidents.Any(i => i.Kind == "breakdown"));
        Assert.Contains(TransportTests.View(neglected).Log, s => s.Contains("broke down"));
        Assert.True(good.ServiceDays == 30 && bad.ServiceDays == 0);
    }

    [Fact]
    public void AWornOutVehicleCanBeReplacedAndAgeCapsServicing()
    {
        var w = TransportTests.Town(1003);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "")) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        int id = OpenLine(w, TransitMode.Bus, 2);
        w.Run(30);
        var before = TransportTests.View(w).Lines.Single().Vehicles.OrderByDescending(v => v.AgeDays).First();
        Assert.True(before.AgeDays >= 30);
        w.Enqueue(Cmd(new TW.Command { Renew = new TW.Renew { LineId = id } }));
        w.Step();
        var after = TransportTests.View(w).Lines.Single().Vehicles;
        Assert.DoesNotContain(after, v => v.Id == before.Id);
        Assert.Contains(after, v => v.AgeDays <= 1);
    }

    [Fact]
    public void AnInjuriousBreakdownIsACallForTheAmbulanceCrews()
    {
        var w = EmergencyTests.Town(1004);
        var clinic = w.Leases.Single(l => l.Kind == "clinic");
        var b = w.Buildings[clinic.BuildingId]!;
        w.RaiseIncident("breakdown", b.X + 2, b.Y, TransportGame.Id, "tram breakdown on the Riverside line", 2, "A tram broke down and passengers were hurt.");
        w.RaiseIncident("breakdown", b.X + 3, b.Y, TransportGame.Id, "bus breakdown on Route 1", 1, "A bus broke down.");
        w.Step();
        var calls = EmergencyTests.View(w).Calls;
        Assert.Contains(calls, c => c.Kind == "injury" && c.Text.Contains("Passengers hurt"));
        Assert.DoesNotContain(calls, c => c.Text.Contains("Route 1"));
    }

    [Fact]
    public void TramsAndTheirFleetsSaveAndLoad()
    {
        var (w, _) = Lab(TransitMode.Tram);
        int id = OpenLine(w, TransitMode.Tram, 2);
        w.Enqueue(Cmd(new TW.Command { Timetable = new TW.SetTimetable { LineId = id, HeadwayMinutes = 20 } }));
        w.Run(40);
        using var loaded = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), loaded.StateHash());
        w.Run(20);
        loaded.Run(20);
        Assert.Equal(w.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void FleetsTimetablesAndServicingReplay()
    {
        var w = TransportTests.Town(1005);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "")) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        int id = OpenLine(w, TransitMode.Bus, 5);
        w.Enqueue(Cmd(new TW.Command { Timetable = new TW.SetTimetable { LineId = id, HeadwayMinutes = 12 } }));
        w.Enqueue(Cmd(new TW.Command { Service = new TW.SetService { LineId = id, EveryDays = 14 } }));
        w.Run(80);
        w.Enqueue(Cmd(new TW.Command { Renew = new TW.Renew { LineId = id } }));
        w.Run(20);
        using var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }

    [Fact]
    public void ATramLineOnAnotherProcessRoutesAlongItsTrack()
    {
        static World Played()
        {
            var (w, _) = Lab(TransitMode.Tram);
            OpenLine(w, TransitMode.Tram, 2);
            w.Run(20);
            return w;
        }
        using var local = Played();
        Assert.True(TransportTests.View(local).Lines.Single().RoundTripMinutes > 0);
        using var host = new RemoteSideGameTests.Host(id => id == TransportGame.Id ? new TransportGame() : null);
        using (host.Serve(TransportGame.Id, TimeSpan.FromSeconds(10)))
        {
            using var remote = Played();
            Assert.IsType<RemoteSideGame>(remote.SideGame(TransportGame.Id)!.Game);
            Assert.Equal(local.StateHash(), remote.StateHash());
            Assert.Equal(TransportTests.View(local).ToByteArray(), TransportTests.View(remote).ToByteArray());
        }
    }
}
