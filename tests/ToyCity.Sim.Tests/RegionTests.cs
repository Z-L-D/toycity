using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

public class RegionTests
{
    static Region Valley(ulong seed = 7)
    {
        var r = Region.Create(Fixture.Content, "Test Valley", seed, 3, 1, 128);
        r.Found(1, 0, "Middleton", "May", starter: true);
        return r;
    }

    [Fact]
    public void TilesAreCutFromOneMapSoEdgesMeet()
    {
        var r = Region.Create(Fixture.Content, "Edges", 11, 2, 2, 64);
        var all = r.Heights;
        int wide = 2 * 64;
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 2; col++)
        {
            var h = r.TileHeights(col, row);
            for (int y = 0; y < 64; y += 7)
            for (int x = 0; x < 64; x += 5)
                Assert.Equal(all[(row * 64 + y) * wide + col * 64 + x], h[y * 64 + x]);
        }
        // The ground runs on across the seam: no cliff between the last column of one tile and the first of the next.
        var west = r.TileHeights(0, 0);
        var east = r.TileHeights(1, 0);
        int jumps = 0;
        for (int y = 0; y < 64; y++) jumps += Math.Abs(west[y * 64 + 63] - east[y * 64]) > 2 ? 1 : 0;
        Assert.True(jumps < 4, $"{jumps} cliffs along the seam");
    }

    [Fact]
    public void ALoneCityHasTheSameGroundAsBefore()
    {
        // A lone city's ground is the region generator at the city's size, so cities from before regions look the same.
        var w = World.Create(Fixture.Content, 2026, 128, "Toyville");
        Assert.Equal(World.GenerateHeights(2026, 128, 128), w.Height);
    }

    [Fact]
    public void EveryCityKeepsTheRegionsDay()
    {
        using var r = Valley();
        r.Run(45);
        var late = r.Found(2, 0, "Eastfield", "Tess", starter: true);
        Assert.Equal(45, late.Tick);
        Assert.Equal(45, late.FoundedTick);
        Assert.Equal(r.Tiles[1].City!.Month, late.Month);
        r.Run(40);
        foreach (var c in r.Cities) Assert.Equal(r.Tick, c.Tick);
        Assert.Equal("May", r.TileAt(1, 0)!.Mayor);
        Assert.Equal("Tess", r.TileAt(2, 0)!.Mayor);
        Assert.True(late.Population > 0, "the late city grew");
        Assert.NotNull(r.CantFound(1, 0));
        Assert.NotNull(r.CantFound(5, 0));
    }

    [Fact]
    public void ARegionIsDeterministicWithCitiesSteppingSideBySide()
    {
        using var a = Valley(21);
        using var b = Valley(21);
        a.Found(0, 0, "Westbrook", "Sam", starter: true);
        b.Found(0, 0, "Westbrook", "Sam", starter: true);
        a.Run(200);
        b.Run(200);
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void ATileReplaysFromItsFounding()
    {
        using var r = Valley(5);
        r.Run(30);
        r.Found(0, 0, "Westbrook", "Sam", starter: true);
        r.Run(90);
        var t = r.TileAt(0, 0)!;
        using var replay = r.ReplayTile(t, r.Tick);
        Assert.Equal(t.City!.StateHash(), replay.StateHash());
    }

    [Fact]
    public void ARegionSavesAndCarriesOn()
    {
        using var r = Valley(9);
        r.Run(40);
        r.Found(2, 0, "Eastfield", "Tess");
        r.Run(20);
        var bytes = RegionMapping.Save(r);
        Assert.True(RegionMapping.IsRegion(bytes));
        Assert.False(RegionMapping.IsRegion(Mapping.Save(r.Tiles[1].City!)));
        using var back = RegionMapping.Load(Fixture.Content, bytes);
        Assert.Equal(r.StateHash(), back.StateHash());
        r.Run(60);
        back.Run(60);
        Assert.Equal(r.StateHash(), back.StateHash());
        Assert.False(back.TileAt(0, 0)!.Founded);
    }

    [Fact]
    public void TheRegionMapShowsEveryTile()
    {
        using var r = Valley(3);
        r.Run(60);
        var s = RegionMapping.State(r, 1, 0, 1, t => t.Founded ? new[] { "May" } : Array.Empty<string>());
        Assert.Equal(3, s.Tiles.Count);
        Assert.All(s.Tiles, t => Assert.Equal(RegionMapping.PictureSize * RegionMapping.PictureSize * 3, t.Picture.Length));
        Assert.True(s.Tiles[1].Founded);
        Assert.Equal("Middleton", s.Tiles[1].Name);
        Assert.True(s.Tiles[1].Population > 0);
        Assert.Equal("Open land", s.Tiles[0].Name);
    }

    /// <summary>A road from a city's town out to its east edge, along row y, levelled first so it never hits a slope.</summary>
    static void RoadEast(World w, int fromX, int y)
    {
        w.Enqueue(new TerraformAction(new CellRect(fromX, y - 1, w.Width - 1, y + 1), TerraformMode.Level));
        w.Enqueue(new PlacePathAction(fromX, y, w.Width - 1, y, "road"));
        w.ApplyPending();
    }

    static void RoadWest(World w, int toX, int y)
    {
        w.Enqueue(new TerraformAction(new CellRect(0, y - 1, toX, y + 1), TerraformMode.Level));
        w.Enqueue(new PlacePathAction(0, y, toX, y, "road"));
        w.ApplyPending();
    }

    [Fact]
    public void ARoadToTheBorderWorksOnlyWhereTheNeighboursRoadMeetsIt()
    {
        using var r = Region.Create(Fixture.Content, "Pair", 17, 2, 1, 128);
        var west = r.Found(0, 0, "Westbrook", "May", starter: true);
        r.Run(5);
        var townRoad = west.Buildings.Where(b => b != null).Select(b => b!).First(b => west.TypeOf(b).Id == "coal-plant");
        int y = townRoad.Y - 1;
        // The starter town's roads end short of the edge: run one out east, joined to the town.
        int x0 = Enumerable.Range(0, west.Width).Last(x => west.Path[west.Idx(x, y)] != 0 && x < west.Width - 2);
        RoadEast(west, x0, y);
        r.Run(31);
        // Open land to the east: the wider world takes the road, as before regions.
        Assert.True(west.SideCapacity[1] > 0);
        Assert.Null(west.NextDoor[1]);

        var east = r.Found(1, 0, "Eastfield", "Tess");
        r.Run(2);
        Assert.Equal("Eastfield", west.NextDoor[1]!.Name);
        Assert.Equal("Westbrook", east.NextDoor[3]!.Name);
        Assert.True(east.NextDoor[3]!.Path[y] != 0, "Eastfield sees Westbrook's road at its west edge");
        r.Run(31);
        Assert.Equal(0, west.SideCapacity[1]);
        Assert.Contains((1, y), west.UnmetCrossings());

        // Eastfield builds to meet it, and the next month's count has the connection.
        RoadWest(east, 20, y);
        r.Run(32);
        Assert.True(west.SideCapacity[1] > 0, "the roads meet");
        Assert.DoesNotContain((1, y), west.UnmetCrossings());

        // Each city's log has what it heard across the border, so it replays alone.
        foreach (var t in r.Tiles)
        {
            using var replay = r.ReplayTile(t, r.Tick);
            Assert.Equal(t.City!.StateHash(), replay.StateHash());
        }
    }

    [Fact]
    public void CommutersShoppersAndGoodsCrossAndBothLedgersAgree()
    {
        using var r = Region.Create(Fixture.Content, "Twins", 23, 2, 1, 128);
        var west = r.Found(0, 0, "Westbrook", "May", starter: true);
        var east = r.Found(1, 0, "Eastfield", "Tess", starter: true);
        r.Run(5);
        // One road straight across the border, joined to both towns' street grids.
        var wPlant = west.Buildings.First(b => b != null && west.TypeOf(b).Id == "coal-plant")!;
        int y = wPlant.Y - 1;
        int wx = Enumerable.Range(0, west.Width).Last(x => west.Path[west.Idx(x, y)] != 0 && x < west.Width - 2);
        RoadEast(west, wx, y);
        int ex = Enumerable.Range(0, east.Width).FirstOrDefault(x => x > 1 && east.Path[east.Idx(x, y)] != 0, -1);
        if (ex < 0)
        {
            // Eastfield's grid doesn't reach that row: a road down to it from its own streets.
            var any = Enumerable.Range(0, east.N).First(i => east.Path[i] != 0);
            int ax = any % east.Width, ay = any / east.Width;
            east.Enqueue(new TerraformAction(new CellRect(Math.Min(ax, 0), Math.Min(ay, y) - 1, ax + 1, Math.Max(ay, y) + 1), TerraformMode.Level));
            east.Enqueue(new PlacePathAction(ax, ay, ax, y, "road"));
            east.ApplyPending();
            ex = ax;
        }
        // Flatten the way at the town's own height (not logged: this test doesn't replay).
        byte h = east.Height[east.Idx(ex, y)];
        for (int x = 0; x <= ex; x++) for (int yy = y - 1; yy <= y + 1; yy++) if (east.BuildingAt[east.Idx(x, yy)] == 0) east.Height[east.Idx(x, yy)] = h;
        east.Enqueue(new PlacePathAction(0, y, ex, y, "road"));
        east.ApplyPending();
        r.Run(400);
        Assert.True(west.Flows[1].Capacity > 0, $"the border is open: w cap {west.SideCapacity[1]} e cap {east.SideCapacity[3]} wpath {west.Path[west.SideCell(1, y)]} epath {east.Path[east.SideCell(3, y)]} ex {ex} y {y} pubW {string.Join(",", west.Published)} statsE {string.Join(",", west.NextDoor[1]?.Stats ?? new int[0])} rej {string.Join(" / ", west.Notices.Concat(east.Notices).Select(n => n.Text).Where(t => t.Contains("steep") || t.Contains("Can")).Take(4))}");
        int crossings = 0;
        for (int m = 0; m < 12; m++)
        {
            var a = west.Flows[1];
            var b = east.Flows[3];
            Assert.Equal(a.Capacity, b.Capacity);
            Assert.Equal(a.CommuteOut, b.CommuteIn);
            Assert.Equal(a.CommuteIn, b.CommuteOut);
            Assert.Equal(a.ShoppersOut, b.ShoppersIn);
            Assert.Equal(a.ShoppersIn, b.ShoppersOut);
            Assert.Equal(a.TouristsOut, b.TouristsIn);
            Assert.Equal(a.FreightOut, b.FreightIn);
            Assert.Equal(a.FreightIn, b.FreightOut);
            crossings += a.Vehicles;
            r.Run(30);
        }
        Assert.True(crossings > 0, "something crossed in a year");
        Assert.True(west.Trips[west.SideCell(1, y)] > 0, "the crossing has traffic");
        var line = west.DemandReport()[1].Sources.First(l => l.Key == "regional").Detail;
        Assert.Contains("Eastfield", line + west.DemandReport()[0].Sources.First(l => l.Key == "regional").Detail);
    }

    [Fact]
    public void APowerDealFeedsTheNeighbourBothBooksAgreeAndItEndsWhenTheLineIsCut()
    {
        using var r = Region.Create(Fixture.Content, "Lines", 31, 2, 1, 128);
        var west = r.Found(0, 0, "Westbrook", "May", starter: true);
        var east = r.Found(1, 0, "Eastfield", "Tess");
        r.Run(3);
        var plant = west.Buildings.First(b => b != null && west.TypeOf(b).Id == "coal-plant")!;
        // A row with dry land from the plant to the border and on into Eastfield.
        int y = Enumerable.Range(plant.Y, plant.H).First(yy =>
            Enumerable.Range(plant.X + plant.W, west.Width - plant.X - plant.W).All(x => !west.IsWater(west.Idx(x, yy)))
            && Enumerable.Range(0, 12).All(x => !east.IsWater(east.Idx(x, yy))));
        west.Enqueue(new PlaceUtilityAction(plant.X + plant.W, y, west.Width - 1, y, UtilityKind.PowerLine));
        west.ApplyPending();
        var westTile = r.TileAt(0, 0)!;
        var eastTile = r.TileAt(1, 0)!;
        // Eastfield has no line yet: nothing to offer over.
        Assert.NotNull(r.CantOffer(westTile, 1, DealKind.Power, 500, 100));
        east.Enqueue(new PlaceUtilityAction(0, y, 11, y, UtilityKind.PowerLine));
        east.ApplyPending();
        r.Run(2);
        Assert.Null(r.CantOffer(westTile, 1, DealKind.Power, 500, 100));

        long eastPowerBefore = east.PowerCapacity, westPowerBefore = west.PowerCapacity;
        var d = r.Offer(westTile, 1, DealKind.Power, 500, 100, selling: true);
        Assert.NotNull(r.Answer(westTile, d.Id, true));   // the one who offered can't answer
        Assert.NotNull(r.CantOffer(eastTile, 3, DealKind.Power, 200, 50));   // one power deal a border
        // Eastfield counters at a lower price; Westbrook agrees to that.
        Assert.Null(r.Counter(eastTile, d.Id, 500, 80));
        Assert.NotNull(r.Answer(eastTile, d.Id, true));   // the first offer is gone
        var c = Assert.Single(r.Deals);
        Assert.Equal(80, c.Price);
        Assert.False(c.OfferedBySeller);
        Assert.Null(r.Answer(westTile, c.Id, true));
        Assert.Single(west.Deals);
        Assert.Single(east.Deals);
        r.Run(1);
        Assert.Equal(eastPowerBefore + 500, east.PowerCapacity);
        Assert.True(west.PowerCapacity < westPowerBefore, "Westbrook sends what it sells");
        Assert.Equal(1, east.Powered[east.Idx(5, y)]);

        long westPaid = 0, eastPaid = 0;
        for (int m = 0; m < 3; m++)
        {
            r.Run(30);
            westPaid += west.LastBudget.Where(l => l.Item1 == "Power sold to Eastfield").Sum(l => l.Item2);
            eastPaid += east.LastBudget.Where(l => l.Item1 == "Power bought from Westbrook").Sum(l => l.Item2);
        }
        Assert.True(westPaid > 0);
        Assert.Equal(westPaid, -eastPaid);

        // Eastfield cuts its line at the border: the region ends the deal in both cities the same day.
        east.Enqueue(new BulldozeAction(new CellRect(0, y, 0, y)));
        east.ApplyPending();
        r.Run(2);
        Assert.Empty(west.Deals);
        Assert.Empty(east.Deals);
        Assert.Empty(r.Deals);
        Assert.Contains(east.Notices, n => n.Text.Contains("no longer meet"));

        // Saved and loaded, and each city alone, it all comes out the same.
        using var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r));
        Assert.Equal(r.StateHash(), back.StateHash());
        foreach (var t in r.Tiles)
        {
            using var replay = r.ReplayTile(t, r.Tick);
            Assert.Equal(t.City!.StateHash(), replay.StateHash());
        }
    }

    [Fact]
    public void AnOfferWaitsAndADeclinedOrWithdrawnOneIsGone()
    {
        using var r = Region.Create(Fixture.Content, "Talks", 37, 2, 1, 128);
        r.Found(0, 0, "Westbrook", "May", starter: true);
        r.Found(1, 0, "Eastfield", "Tess", starter: true);
        var w = r.TileAt(0, 0)!;
        var e = r.TileAt(1, 0)!;
        // A shuttle needs a road that meets: not yet.
        Assert.Contains("road", r.CantOffer(w, 1, DealKind.Transit, 200, 50));
        Assert.NotNull(r.CantOffer(w, 0, DealKind.Power, 200, 50));   // open land north
        Assert.NotNull(r.EndDeal(e, 99));
    }

    [Fact]
    public void AComputerMayorGrowsItsCityAndReplaysAlone()
    {
        using var r = Region.Create(Fixture.Content, "Bots", 41, 2, 1, 128);
        var bot = r.Found(1, 0, "Botton", "", starter: true, computer: AutopilotStyle.Growth);
        Assert.True(bot.ComputerMayor);
        Assert.True(World.IsComputer(r.TileAt(1, 0)!.Mayor));
        int start = 0;
        r.Run(60);
        start = bot.Population;
        r.Run(720);
        Assert.True(bot.Population > start, $"grew from {start} to {bot.Population}");
        Assert.True(bot.Buildings.Count(b => b != null) > 10);
        using var replay = r.ReplayTile(r.TileAt(1, 0)!, r.Tick);
        Assert.Equal(bot.StateHash(), replay.StateHash());

        // A player takes the city over, and hands it back to a careful computer.
        Assert.NotNull(r.TakeOver(r.TileAt(0, 0)!, "Sam"));   // open land
        Assert.Null(r.TakeOver(r.TileAt(1, 0)!, "Sam"));
        Assert.Equal("Sam", bot.MayorSeat.Owner);
        Assert.False(bot.ComputerMayor);
        Assert.NotNull(r.TakeOver(r.TileAt(1, 0)!, "Tess"));  // Sam's now
        Assert.Null(r.HandToComputer(r.TileAt(1, 0)!, AutopilotStyle.Profit));
        Assert.True(bot.ComputerMayor);
        Assert.Equal(AutopilotStyle.Profit, bot.MayorSeat.Style);
    }

    [Fact]
    public void AComputerMayorCountersALowOfferAndTheDealRuns()
    {
        using var r = Region.Create(Fixture.Content, "Haggle", 43, 2, 1, 128);
        var west = r.Found(0, 0, "Westbrook", "May");
        var bot = r.Found(1, 0, "Botton", "", starter: true, computer: AutopilotStyle.Profit);
        r.Run(3);
        var plant = bot.Buildings.First(b => b != null && bot.TypeOf(b).Id == "coal-plant")!;
        int y = Enumerable.Range(plant.Y, plant.H).First(yy =>
            Enumerable.Range(0, plant.X).All(x => !bot.IsWater(bot.Idx(x, yy)))
            && Enumerable.Range(west.Width - 12, 12).All(x => !west.IsWater(west.Idx(x, yy))));
        bot.Enqueue(new PlaceUtilityAction(0, y, plant.X - 1, y, UtilityKind.PowerLine));
        west.Enqueue(new PlaceUtilityAction(west.Width - 12, y, west.Width - 1, y, UtilityKind.PowerLine));
        bot.ApplyPending();
        west.ApplyPending();
        r.Run(2);
        Assert.True(bot.Spare(DealKind.Power) > 200, $"spare {bot.Spare(DealKind.Power)}");

        // May offers to buy 100 kW for $5 a month; the careful computer wants 25% over the fair $20.
        var westTile = r.TileAt(0, 0)!;
        var offer = r.Offer(westTile, 1, DealKind.Power, 100, 5, selling: false);
        r.Run(1);
        var counter = Assert.Single(r.Deals);
        Assert.NotEqual(offer.Id, counter.Id);
        Assert.Equal(25, counter.Price);
        Assert.Contains(west.Notices, n => n.Text.Contains("asks $25"));
        Assert.Null(r.Answer(westTile, counter.Id, true));
        r.Run(1);
        Assert.Equal(100, west.PowerCapacity);

        // Countered back, a computer doesn't haggle twice: it says no.
        Assert.Null(r.EndDeal(westTile, counter.Id));
        var again = r.Offer(westTile, 1, DealKind.Power, 100, 5, selling: false);
        r.Run(1);
        var c2 = Assert.Single(r.Deals);
        Assert.Null(r.Counter(westTile, c2.Id, 100, 10));
        r.Run(1);
        Assert.Empty(r.Deals);
    }

    [Fact]
    public void WeatherFrontsCrossTheRegionAndChangeTheCities()
    {
        using var r = Region.Create(Fixture.Content, "Skies", 47, 3, 1, 64);
        var cities = new[] { r.Found(0, 0, "Westbrook", "May", starter: true), r.Found(1, 0, "Middleton", "Sam", starter: true), r.Found(2, 0, "Eastfield", "Tess", starter: true) };
        var seen = new HashSet<WeatherKind>();
        int differ = 0;
        for (int d = 0; d < 720; d++)
        {
            r.Step();
            foreach (var t in r.Tiles)
            {
                var (k, s) = r.WeatherAt(t, r.Tick - 1);
                Assert.Equal(k, t.City!.Weather);
                Assert.Equal(s, t.City.WeatherStrength);
                seen.Add(k);
            }
            if (cities[0].Weather != cities[2].Weather) differ++;
        }
        // Rain, wind, snow in winter and heat in summer all turn up in two years; a front reaches one end before the other.
        Assert.Contains(WeatherKind.Rain, seen);
        Assert.Contains(WeatherKind.Snow, seen);
        Assert.Contains(WeatherKind.Heat, seen);
        Assert.True(differ > 0, "fronts drift across, so the ends sometimes differ");
        foreach (var t in r.Tiles)
        {
            using var replay = r.ReplayTile(t, r.Tick);
            Assert.Equal(t.City!.StateHash(), replay.StateHash());
        }
        // A lone city has no region sky.
        using var lone = Region.Single(World.Create(Fixture.Content, 5, 64, "Alone"));
        lone.Run(400);
        Assert.Equal(WeatherKind.Clear, lone.Tiles[0].City!.Weather);
    }

    [Fact]
    public void AHeatwaveRaisesPowerAndWaterUse()
    {
        var w = World.Create(Fixture.Content, 2026, 128, "Hot");
        StarterTown.Apply(w);
        w.Run(120);
        var copy = World.FromSnapshot(Fixture.Content, w.Snapshot());
        copy.Enqueue(new WeatherAction(WeatherKind.Heat, 3));
        w.Step();
        copy.Step();
        Assert.True(copy.PowerDemand > w.PowerDemand * 12 / 10, $"{copy.PowerDemand} vs {w.PowerDemand}");
        Assert.True(copy.WaterDemand > w.WaterDemand * 13 / 10, $"{copy.WaterDemand} vs {w.WaterDemand}");
        Assert.Contains(copy.Notices, n => n.Text.Contains("Heatwave"));
        w.Dispose();
        copy.Dispose();
    }

    [Fact]
    public void TheColossusWalksOverTheBorderUnlessTheNeighbourForbidsIt()
    {
        using var r = Region.Create(Fixture.Content, "Stomp", 53, 2, 1, 64);
        var west = r.Found(0, 0, "Westbrook", "May", starter: true);
        var east = r.Found(1, 0, "Eastfield", "Tess", starter: true);
        r.Run(2);
        // The Colossus a few cells from the border, heading east with days to spare.
        west.Disasters.Add(new Disaster { Id = 900, Kind = DisasterKind.Robot, X = ((west.Width - 3) << 8) + 128, Y = (30 << 8) + 128, Dx = 256, DaysLeft = 40 });
        r.Run(6);
        var robot = Assert.Single(east.Disasters, d => d.Kind == DisasterKind.Robot);
        Assert.True(robot.CellX < 10 && robot.CellY == 30, $"it came in at {robot.CellX},{robot.CellY}");
        Assert.True(robot.DaysLeft < 40);
        Assert.DoesNotContain(west.Disasters, d => d.Kind == DisasterKind.Robot);
        Assert.Empty(west.Departures);
        using (var replay = r.ReplayTile(r.TileAt(1, 0)!, r.Tick))
            Assert.Equal(east.StateHash(), replay.StateHash());

        // Westbrook forbids absurd disasters: a second walk from Eastfield stops at its border.
        west.Enqueue(new SetCityRuleAction(Mechanic.AbsurdDisasters, true));
        east.Disasters.Clear();
        east.Disasters.Add(new Disaster { Id = 901, Kind = DisasterKind.Robot, X = (2 << 8) + 128, Y = (20 << 8) + 128, Dx = -256, DaysLeft = 40 });
        r.Run(6);
        Assert.DoesNotContain(west.Disasters, d => d.Kind == DisasterKind.Robot);
        Assert.Contains(west.Notices, n => n.Text.Contains("stopped at the border"));
    }

    [Fact]
    public void FireJumpsTheBorderBetweenBuildingsThatTouchIt()
    {
        using var r = Region.Create(Fixture.Content, "Sparks", 59, 2, 1, 64);
        var west = r.Found(0, 0, "Westbrook", "May");
        var east = r.Found(1, 0, "Eastfield", "Tess");
        int y = Enumerable.Range(4, 56).First(yy => !west.IsWater(west.Idx(west.Width - 1, yy)) && !east.IsWater(east.Idx(0, yy))
            && !west.IsWater(west.Idx(west.Width - 2, yy)) && !east.IsWater(east.Idx(1, yy)));
        west.Enqueue(new PlaceBuildingAction(west.Width - 1, y, "small-park"));
        east.Enqueue(new PlaceBuildingAction(0, y, "small-park"));
        west.ApplyPending();
        east.ApplyPending();
        var wPark = west.BuildingAtCell(west.Idx(west.Width - 1, y));
        var ePark = east.BuildingAtCell(east.Idx(0, y));
        Assert.NotNull(wPark);
        Assert.NotNull(ePark);
        bool caught = false;
        for (int d = 0; d < 60 && !caught; d++)
        {
            if (west.BuildingAtCell(west.Idx(west.Width - 1, y)) is { BurningDays: 0 } b) b.BurningDays = 1;
            r.Step();
            caught = east.Buildings.Any(b => b is { BurningDays: > 0 });
        }
        Assert.True(caught, "the fire crossed");
        Assert.Contains(east.Notices.Concat(east.Headlines.Select(h => new SimNotice(h.Text, "info"))), n => n.Text.Contains("across the border from Westbrook"));
    }
}
