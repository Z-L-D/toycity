using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Transport;
using ToyCity.Protocol;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>The transport beta's second half: freight, signals on the rails, and two companies bidding for districts.</summary>
public class FreightTests
{
    static SideGameAction Cmd(TW.Command c, string game = TransportGame.Id) => new(game, "operator", c.ToByteArray());

    /// <summary>
    /// A factory by one freight depot and shops by another, 22 cells apart on a road that runs on west to the map
    /// edge. The player runs the company and leases both depots.
    /// </summary>
    static (World W, int X, int Road) Lab()
    {
        SideGameCatalog.Register(TransportGame.Id, () => new TransportGame());
        var (w, x, y) = Fixture.FlatSite();
        int road = y + 10;
        w.Enqueue(new TerraformAction(new CellRect(0, road - 6, x + 30, road + 4), TerraformMode.Level));
        w.Enqueue(new PlacePathAction(0, road, x + 30, road, "road"));
        w.Enqueue(new PlaceBuildingAction(x + 12, road - 4, "coal-plant"));
        w.Enqueue(new PlaceBuildingAction(x + 2, road - 3, "freight-depot"));
        w.Enqueue(new PlaceBuildingAction(x + 24, road - 3, "freight-depot"));
        w.Enqueue(new AttachSideGameAction(TransportGame.Id));
        w.Step();
        w.AddBuilding(Fixture.Content.Zoned(Zone.Industrial, Level.Medium, Level.Medium), x + 2, road + 1, 0, 2, 2);
        w.AddBuilding(Fixture.Content.Zoned(Zone.Industrial, Level.Medium, Level.Medium), x + 5, road + 1, 0, 2, 2);
        w.AddBuilding(Fixture.Content.Zoned(Zone.Commercial, Level.Medium, Level.Medium), x + 24, road + 1, 0, 2, 2);
        w.AddBuilding(Fixture.Content.Zoned(Zone.Commercial, Level.Medium, Level.Medium), x + 27, road + 1, 0, 2, 2);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var l in w.Buildings.Where(b => b != null && w.LotKindOf(b) == "freight-depot").ToList()) w.Enqueue(new LeaseLotAction(TransportGame.Id, l!.Id));
        w.Run(2);
        return (w, x, road);
    }

    static int OpenFreight(World w, List<int> depots, bool toEdge, int trucks, string name = "Goods")
    {
        var save = new TW.SaveLine { Name = name, Mode = TW.Mode.Bus, Freight = true, ToEdge = toEdge };
        save.StopLeaseIds.AddRange(depots);
        w.Enqueue(Cmd(new TW.Command { SaveLine = save }));
        w.Step();
        int id = TransportTests.View(w).Lines.Single(l => l.Name == name).Id;
        w.Enqueue(Cmd(new TW.Command { Buses = new TW.SetBuses { LineId = id, Buses = trucks } }));
        w.Step();
        return id;
    }

    static List<int> Depots(World w) => w.Leases.Where(l => l.Kind == "freight-depot").OrderBy(l => w.Buildings[l.BuildingId]!.X).Select(l => l.Id).ToList();

    [Fact]
    public void TrucksCarryGoodsFromTheFactoriesToTheShops()
    {
        var (w, _, _) = Lab();
        var depots = Depots(w);
        Assert.Equal(2, depots.Count);
        var lots = TransportTests.View(w).Lots;
        Assert.True(lots.Single(l => l.LeaseId == depots[0]).TonsReady > 0, "no goods at the factory depot");
        Assert.True(lots.Single(l => l.LeaseId == depots[1]).TonsWanted > 0, "no shops want goods");

        OpenFreight(w, depots, toEdge: false, trucks: 2);
        w.Run(5);
        var v = TransportTests.View(w);
        var line = v.Lines.Single();
        Assert.True(line.Freight);
        Assert.Equal("", line.Problem);
        Assert.True(line.Tons > 0, "nothing carried");
        Assert.True(w.FreightDeliveredMonth > 0);
        Assert.Equal(0, w.FreightExportMonth);
        // A freight line isn't a passenger line: the city doesn't route commuters over it.
        Assert.True(v.ThisMonth.Fares > 0);
        // Drivers share out between the depots.
        Assert.Equal(2 * TransportGame.FreightSpecs[0].Crew + 2 * TransportGame.DepotStaff, w.Leases.Where(l => l.Kind == "freight-depot").Sum(l => l.Jobs));
        // Trucks can't call at a bus stop, and trams don't carry freight.
        w.Notices.Clear();
        var bad = new TW.SaveLine { Name = "Tram freight", Mode = TW.Mode.Tram, Freight = true };
        bad.StopLeaseIds.AddRange(depots);
        w.Enqueue(Cmd(new TW.Command { SaveLine = bad }));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("truck or by train"));
    }

    [Fact]
    public void GoodsCarriedToTheEdgeAreExportsAndRaiseIndustrysDemand()
    {
        var (w, _, _) = Lab();
        var depots = Depots(w);
        OpenFreight(w, new List<int> { depots[0] }, toEdge: true, trucks: 2);
        w.Run(3);
        var line = TransportTests.View(w).Lines.Single();
        Assert.Equal("", line.Problem);
        Assert.True(line.ToEdge);
        Assert.True(line.Tons > 0);
        Assert.True(w.FreightExportMonth > 0);
        // Next month industry hears of it.
        w.Run(World.DaysPerMonth - w.Day + 2);
        Assert.True(w.FreightExportDaily > 0);
        Assert.Contains(w.DemandReport()[2].Sources, s => s.Key == "external" && s.Detail.Contains("Company freight"));
    }

    [Fact]
    public void FreightOnAnotherProcessCarriesTheSameGoods()
    {
        static World Played()
        {
            var (w, _, _) = Lab();
            var depots = Depots(w);
            OpenFreight(w, depots, toEdge: true, trucks: 2);
            w.Run(6);
            return w;
        }
        using var local = Played();
        Assert.True(local.FreightExportMonth + local.FreightDeliveredMonth > 0);
        using var host = new RemoteSideGameTests.Host(id => id == TransportGame.Id ? new TransportGame() : null);
        using (host.Serve(TransportGame.Id, TimeSpan.FromSeconds(10)))
        {
            using var remote = Played();
            Assert.IsType<RemoteSideGame>(remote.SideGame(TransportGame.Id)!.Game);
            Assert.Equal(local.FreightExportMonth, remote.FreightExportMonth);
            Assert.Equal(TransportTests.View(local).ToByteArray(), TransportTests.View(remote).ToByteArray());
            Assert.Equal(local.StateHash(), remote.StateHash());
        }
    }

    [Fact]
    public void FreightSavesAndLoads()
    {
        var (w, _, _) = Lab();
        OpenFreight(w, Depots(w), toEdge: true, trucks: 2);
        w.Run(4);
        using var copy = ToyCity.Protocol.Mapping.Load(Fixture.Content, ToyCity.Protocol.Mapping.Save(w));
        Assert.Equal(w.StateHash(), copy.StateHash());
        w.Run(3); copy.Run(3);
        Assert.Equal(w.StateHash(), copy.StateHash());
        Assert.Equal(w.FreightExportMonth, copy.FreightExportMonth);
    }
}

public class SignalTests
{
    static SideGameAction Cmd(TW.Command c) => new(TransportGame.Id, "operator", c.ToByteArray());

    [Fact]
    public void WithoutSignalsOnlyOneTrainUsesTheLineAndAPathSignalLetsTwoShareIt()
    {
        var (w, _) = TrackTests.Lab(TransitMode.Rail);
        TrackTests.OpenLine(w, TransitMode.Rail, 2);
        w.Run(3);
        var line = TransportTests.View(w).Lines.Single();
        Assert.Equal("", line.Problem);
        Assert.True(line.SignalLimited);
        Assert.True(line.HeadwayMinutes > line.RoundTripMinutes / 2 + 2, $"every {line.HeadwayMinutes} of {line.RoundTripMinutes}");
        Assert.Contains(TransportTests.View(w).Reserved, c => c.Percent > 100);

        // A signal off the company's track is refused.
        w.Notices.Clear();
        w.Enqueue(Cmd(new TW.Command { Signal = new TW.PlaceSignal { Cell = 0, Kind = TW.Signal.Path } }));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("own rail lines"));

        // One halfway along splits the line in two: a train in each half.
        long cash = TransportTests.View(w).Cash;
        int mid = line.RouteCells[line.RouteCells.Count / 2];
        w.Enqueue(Cmd(new TW.Command { Signal = new TW.PlaceSignal { Cell = mid, Kind = TW.Signal.Path } }));
        w.Run(2);
        var v = TransportTests.View(w);
        line = v.Lines.Single();
        Assert.False(line.SignalLimited);
        Assert.Equal(Math.Max(TransportGame.MinHeadway, line.RoundTripMinutes / 2), line.HeadwayMinutes);
        Assert.Contains(v.Signals, s => s.Cell == mid && s.Kind == TW.Signal.Path);
        Assert.All(v.Reserved, c => Assert.True(c.Percent <= 100));
        Assert.Contains(v.Log, l => l.Contains("path signal"));
        Assert.True(v.Cash <= cash - TransportGame.SignalPrice + v.ThisMonth.Fares);

        // Taking it down puts the line back to one train.
        w.Enqueue(Cmd(new TW.Command { Signal = new TW.PlaceSignal { Cell = mid, Kind = TW.Signal.NoSignal } }));
        w.Run(2);
        Assert.True(TransportTests.View(w).Lines.Single().SignalLimited);
        Assert.Empty(TransportTests.View(w).Signals);
    }

    [Fact]
    public void SignalsSaveAndLoad()
    {
        var (w, _) = TrackTests.Lab(TransitMode.Rail);
        TrackTests.OpenLine(w, TransitMode.Rail, 2);
        w.Run(2);
        var cells = TransportTests.View(w).Lines.Single().RouteCells;
        w.Enqueue(Cmd(new TW.Command { Signal = new TW.PlaceSignal { Cell = cells[cells.Count / 3], Kind = TW.Signal.Block } }));
        w.Enqueue(Cmd(new TW.Command { Signal = new TW.PlaceSignal { Cell = cells[2 * cells.Count / 3], Kind = TW.Signal.Path } }));
        w.Run(5);
        using var loaded = ToyCity.Protocol.Mapping.Load(Fixture.Content, ToyCity.Protocol.Mapping.Save(w));
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal(TransportTests.View(w).ToByteArray(), TransportTests.View(loaded).ToByteArray());
        w.Run(5); loaded.Run(5);
        Assert.Equal(w.StateHash(), loaded.StateHash());
    }
}

public class FranchiseTests
{
    static SideGameAction Cmd(string game, TW.Command c) => new(game, "operator", c.ToByteArray());

    /// <summary>The starter town with a second transit company invited in, and the district of its busiest stop.</summary>
    static (World W, int District) TwoCompanies(ulong seed)
    {
        SideGameCatalog.Register(TransportGame.RivalId, () => new TransportGame(TransportGame.RivalId, TransportGame.RivalName));
        var w = TransportTests.Town(seed);
        w.Enqueue(new AttachSideGameAction(TransportGame.RivalId));
        w.Step();
        Assert.NotNull(w.SideGame(TransportGame.RivalId));
        var stop = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-stop")!;
        return (w, w.DistrictOf(stop.X, stop.Y));
    }

    [Fact]
    public void TwoCompaniesBidAndTheHighestWinsTheFranchise()
    {
        var (w, d) = TwoCompanies(1101);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "ann", OperatorLevel.Manual));
        w.Enqueue(new SetSeatAction(TransportGame.RivalId, "bob", OperatorLevel.Manual));
        w.Enqueue(new OpenTenderAction(d, 12));
        w.Run(2);
        Assert.NotNull(w.OpenTenderIn(d));
        var tender = TransportTests.View(w).Tenders.Single();
        Assert.Equal(d, tender.District);
        Assert.Equal(12 * World.MinBidPerMonth, tender.MinBid);

        // Too low a bid is refused by the company; then each bids.
        w.Notices.Clear();
        w.Enqueue(Cmd(TransportGame.Id, new TW.Command { Bid = new TW.Bid { District = d, Amount = 100 } }));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("at least"));
        w.Enqueue(Cmd(TransportGame.Id, new TW.Command { Bid = new TW.Bid { District = d, Amount = 5_000 } }));
        w.Enqueue(Cmd(TransportGame.RivalId, new TW.Command { Bid = new TW.Bid { District = d, Amount = 3_000 } }));
        w.Run(2);
        Assert.Equal(2, w.OpenTenderIn(d)!.Bids.Count);
        Assert.Equal(5_000, TransportTests.View(w).Tenders.Single().YourBid);
        w.Run(World.TenderDays);

        var f = w.FranchiseIn(d);
        Assert.NotNull(f);
        Assert.Equal(TransportGame.Id, f!.Winner);
        Assert.Equal(5_000, f.Amount);
        Assert.Equal(w.Allowed(Mechanic.ExclusiveRights), f.Exclusive);
        var ann = TransportTests.View(w);
        Assert.Contains(ann.Franchises, x => x.District == d && x.Mine);
        Assert.Contains(ann.Log, l => l.Contains("won the 12-month transit franchise"));
        Assert.True(ann.ThisMonth.Standing + ann.LastMonth.Standing >= 5_000);
        var bob = TW.View.Parser.ParseFrom(w.SideGame(TransportGame.RivalId)!.Game.View("operator"));
        Assert.Contains(bob.Franchises, x => x.District == d && !x.Mine && x.Holder == TransportGame.Id);
        Assert.Contains(bob.Log, l => l.Contains("wasn't enough"));

        // The loser can't lease transit lots there now, and the mayor can't tender it again while it runs.
        var free = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-stop" && w.DistrictOf(b.X, b.Y) == d && w.Leases.All(l => l.BuildingId != b.Id))!;
        w.Notices.Clear();
        w.Enqueue(new LeaseLotAction(TransportGame.RivalId, free.Id));
        w.Enqueue(new OpenTenderAction(d, 6));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("holds the transit franchise"));
        Assert.Contains(w.Notices, n => n.Text.Contains("until it runs out"));

        using var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }

    [Fact]
    public void OnAutopilotACompanyBidsWhereItHasLots()
    {
        var (w, d) = TwoCompanies(1102);
        w.Enqueue(new SetSeatAction(TransportGame.RivalId, "", OperatorLevel.Autopilot));
        w.Enqueue(new SetSeatAction(TransportGame.Id, "ann", OperatorLevel.Manual));
        var stop = w.Buildings.First(b => b != null && w.LotKindOf(b) == "bus-stop" && w.DistrictOf(b.X, b.Y) == d)!;
        w.Enqueue(new LeaseLotAction(TransportGame.RivalId, stop.Id));
        w.Run(2);
        w.Enqueue(new OpenTenderAction(d, 6));
        w.Run(World.TenderDays + 3);
        Assert.Equal(TransportGame.RivalId, w.FranchiseIn(d)?.Winner);
        // The city lists the franchise for the mayor.
        var civic = ToyCity.Protocol.Mapping.Civic(w);
        var row = civic.Franchises.Single(x => x.District == d);
        Assert.Equal("held", row.Status);
        Assert.Equal(TransportGame.RivalName, row.HolderName);
    }

    [Fact]
    public void ARivalCanBeInvitedOnlyOnceAndIsListedUntilThen()
    {
        SideGameCatalog.Register(TransportGame.RivalId, () => new TransportGame(TransportGame.RivalId, TransportGame.RivalName));
        SideGameCatalog.Offer(TransportGame.RivalId, TransportGame.RivalName, "A rival.");
        var w = TransportTests.Town(1103);
        Assert.Contains(ToyCity.Protocol.Mapping.Civic(w).Invitable, i => i.Id == TransportGame.RivalId);
        w.Enqueue(new AttachSideGameAction(TransportGame.RivalId));
        w.Step();
        Assert.DoesNotContain(ToyCity.Protocol.Mapping.Civic(w).Invitable, i => i.Id == TransportGame.RivalId);
        Assert.Equal(2, w.SideGames.Count(g => g.Id.StartsWith("transport")));
        Assert.NotEqual(w.SideGame(TransportGame.Id)!.Game.Manifest.Name, w.SideGame(TransportGame.RivalId)!.Game.Manifest.Name);
    }
}
