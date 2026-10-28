using ToyCity.Protocol;
using ToyCity.Sim;

namespace ToyCity.Sim.Tests;

public class Fixture
{
    public static readonly ContentDb Content = ContentDb.LoadDefault();

    public static World Starter(ulong seed = 42, int size = 128)
    {
        var w = World.Create(Content, seed, size, "Testville");
        Assert.True(StarterTown.Apply(w), "no site for the starter town");
        return w;
    }

    /// <summary>A flat, dry block for hand-built test layouts.</summary>
    public static (World W, int X, int Y) FlatSite(ulong seed = 42)
    {
        var w = World.Create(Content, seed, 128, "Lab");
        var site = StarterTown.FindSite(w)!.Value;
        w.Enqueue(new TerraformAction(new CellRect(site.X, site.Y, site.X + 30, site.Y + 30), TerraformMode.Level));
        w.Step();
        return (w, site.X, site.Y);
    }
}

public class ContentTests
{
    [Fact]
    public void LoadsAllProfilesAndTemplates()
    {
        var c = Fixture.Content;
        Assert.Equal(18, c.Profiles.Count);
        Assert.Equal(27, c.Types.Count(t => !t.Placeable));
        Assert.Equal("Rustbucket Bungalow", c.Zoned(Zone.Residential, Level.Low, Level.Low).Name);
        Assert.True(c.ProfileIndex("highway-4") >= 0);
        Assert.False(c.Profiles[c.ProfileIndex("highway-4")].Frontage);
    }
}

public class DeterminismTests
{
    [Fact]
    public void SameSeedAndActionsGiveSameHash()
    {
        var a = Fixture.Starter();
        var b = Fixture.Starter();
        a.Run(720);
        b.Run(720);
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.True(a.Population > 0);
    }

    [Fact]
    public void DifferentSeedsGiveDifferentTerrain()
    {
        var a = World.Create(Fixture.Content, 1, 64);
        var b = World.Create(Fixture.Content, 2, 64);
        Assert.NotEqual(a.Height, b.Height);
    }

    [Fact]
    public void ReplayingTheLogReproducesTheCity()
    {
        var w = Fixture.Starter(7);
        w.Run(100);
        // A few mid-game actions on top of the starter town.
        w.Enqueue(new SetTaxAction(Zone.Residential, Level.Low, 70));
        w.Enqueue(new TakeLoanAction(10_000));
        w.Run(50);
        w.Enqueue(new SetFundingAction(w.Buildings.First(b => b != null && w.TypeOf(b).Placeable)!.Id, 150));
        w.Run(250);

        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }
}

public class SaveTests
{
    [Fact]
    public void SaveLoadAndContinueMatchesNeverSaving()
    {
        var w = Fixture.Starter(11);
        w.Run(200);
        var bytes = Mapping.Save(w);
        var loaded = Mapping.Load(Fixture.Content, bytes);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal(w.Log.Count, loaded.Log.Count);

        w.Run(200);
        loaded.Run(200);
        Assert.Equal(w.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void RejectsSaveFromDifferentContent()
    {
        var w = Fixture.Starter();
        var snap = w.Snapshot();
        var other = ContentDb.Parse(
            File.ReadAllText(Path.Combine(ContentDb.FindContentDir(), "profiles.json")),
            File.ReadAllText(Path.Combine(ContentDb.FindContentDir(), "buildings.json")).Replace("\"startingFunds\": 100000", "\"startingFunds\": 5"));
        Assert.Throws<InvalidDataException>(() => World.FromSnapshot(other, snap));
    }
}

public class GrowthTests
{
    [Fact]
    public void StarterTownGrowsWithinAYear()
    {
        var w = Fixture.Starter();
        w.Run(360);
        Assert.True(w.Population >= 500, $"population {w.Population}");
        Assert.True(w.Jobs > 0);
        Assert.True(w.Workers > 0);
    }

    [Fact]
    public void StarterTownLotsAllFaceARoad()
    {
        var w = Fixture.Starter();
        w.Step();
        int zoned = 0;
        for (int i = 0; i < w.N; i++)
        {
            if (w.ZoneCode[i] == 0) continue;
            zoned++;
            Assert.DoesNotContain(w.Why(i % w.Width, i / w.Width).Requirements, r => r.Key == "road" && !r.Met);
        }
        Assert.True(zoned > 300, $"only {zoned} zoned cells");
    }

    [Fact]
    public void LotsAwayFromTheRoadDoNotGrow()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x, y + 2, x + 10, y + 2, "road"));
        // Low density lots are at most two cells deep, so the third and fourth rows back can't reach the road.
        w.Enqueue(new PaintZoneAction(new CellRect(x, y + 3, x + 10, y + 6), Zone.Residential, Level.Low));
        w.Run(2);
        Assert.DoesNotContain(w.Why(x + 3, y + 3).Requirements, r => r.Key == "road" && !r.Met);
        Assert.DoesNotContain(w.Why(x + 3, y + 4).Requirements, r => r.Key == "road" && !r.Met);
        Assert.Contains(w.Why(x + 3, y + 5).Requirements, r => r.Key == "road" && !r.Met);
        Assert.Contains(w.Why(x + 3, y + 6).Requirements, r => r.Key == "road" && !r.Met);
    }

    [Fact]
    public void ZonesWithoutPowerDoNotGrowAndSayWhy()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x, y + 2, x + 10, y + 2, "road"));
        w.Enqueue(new PaintZoneAction(new CellRect(x, y + 3, x + 10, y + 4), Zone.Residential, Level.Low));
        w.Run(90);
        Assert.Equal(0, w.Buildings.Count(b => b != null));
        var why = w.Why(x + 3, y + 3);
        var power = Assert.Single(why.Requirements, r => r.Key == "power");
        Assert.False(power.Met);
        Assert.Contains("power", why.Summary);
    }

    [Fact]
    public void EveryEmptyZonedLotExplainsItself()
    {
        var w = Fixture.Starter(5);
        w.Run(720);
        int checkedLots = 0;
        for (int i = 0; i < w.N; i++)
        {
            if (w.ZoneCode[i] == 0 || w.BuildingAt[i] != 0) continue;
            var r = w.Why(i % w.Width, i / w.Width);
            checkedLots++;
            Assert.NotEmpty(r.Requirements);
            Assert.All(r.Requirements, q => Assert.False(string.IsNullOrWhiteSpace(q.Detail)));
            bool blocked = r.Requirements.Any(q => !q.Met);
            Assert.True(blocked || r.Summary.StartsWith("Ready to grow"), $"lot {i}: {r.Summary}");
        }
        Assert.True(checkedLots > 0);
    }

    [Fact]
    public void WhyAgreesWithGrowth()
    {
        // Lots the why-panel says are missing road or power never grow.
        var w = Fixture.Starter(3);
        w.Run(120);
        var blocked = new List<int>();
        for (int i = 0; i < w.N; i++)
            if (w.ZoneCode[i] != 0 && w.BuildingAt[i] == 0 && w.Why(i % w.Width, i / w.Width).Requirements.Any(q => q.Key is "road" or "power" && !q.Met)) blocked.Add(i);
        w.Run(30);
        Assert.All(blocked, i => Assert.Equal(0, w.BuildingAt[i]));
    }

    [Fact]
    public void AbandonedBuildingRecoversWhenFixed()
    {
        var w = Fixture.Starter(9);
        w.Run(360);
        var plant = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "coal-plant")!;
        w.Enqueue(new BulldozeAction(new CellRect(plant.X, plant.Y, plant.X, plant.Y)));
        w.Run(30 * 8);
        Assert.Contains(w.Buildings, b => b is { Abandoned: true });
        var home = w.Buildings.First(b => b is { Abandoned: true })!;
        var why = w.Why(home.X, home.Y);
        Assert.Contains(why.Requirements, r => r.Key == "power" && !r.Met);

        int peak = w.Buildings.Count(b => b is { Abandoned: true });
        w.Enqueue(new PlaceBuildingAction(plant.X, plant.Y, "coal-plant"));
        w.Run(30 * 6);
        var still = w.Buildings.Where(b => b is { Abandoned: true }).ToList();
        Assert.True(still.Count * 4 < peak, $"{still.Count} of {peak} still abandoned");
        // Whatever stays abandoned must say why: an unmet need, or how long until people move back.
        Assert.All(still, b =>
        {
            var why = w.Why(b!.X, b.Y);
            Assert.True(why.Requirements.Any(r => !r.Met) || why.Summary.Contains("move back in"), why.Summary);
        });
    }
}

public class UtilityTests
{
    [Fact]
    public void BrownoutServesBuildingsInIdOrder()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlaceBuildingAction(x, y, "wind-turbine"));        // 300 kW
        w.Enqueue(new PlaceBuildingAction(x + 1, y, "hospital"));        // needs 300 kW
        w.Enqueue(new PlaceBuildingAction(x + 4, y, "police-station"));  // needs 40 kW more
        w.Run(2);
        Assert.True(w.PowerDemand > w.PowerCapacity, $"{w.PowerDemand} vs {w.PowerCapacity}");
        var hospital = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "hospital")!;
        var police = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "police-station")!;
        Assert.True(hospital.Powered);
        Assert.False(police.Powered);
        Assert.Equal(2, w.Powered[w.Idx(x + 4, y)]);
        Assert.Contains(w.Why(x + 4, y).Requirements, r => r.Key == "power" && !r.Met);
    }

    [Fact]
    public void PumpNeedsPowerAndPipe()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlaceBuildingAction(x, y, "water-pump"));
        w.Enqueue(new PlaceUtilityAction(x + 2, y, x + 10, y, UtilityKind.WaterPipe));
        w.Run(2);
        Assert.Equal(0, w.WaterCapacity);
        w.Enqueue(new PlaceBuildingAction(x, y + 2, "coal-plant"));
        w.Run(2);
        Assert.True(w.WaterCapacity > 0);
        Assert.Equal(1, w.Watered[w.Idx(x + 10, y + 5)]);
        Assert.Equal(0, w.Watered[w.Idx(x + 10, y + 10)]);
    }
}

public class ActionTests
{
    [Fact]
    public void PathOverWaterIsRejectedAndFree()
    {
        var w = World.Create(Fixture.Content, 42, 128);
        int water = Enumerable.Range(0, w.N).First(w.IsWater);
        int land = Enumerable.Range(0, w.N).First(i => !w.IsWater(i) && i / w.Width == water / w.Width);
        long funds = w.Funds;
        w.Enqueue(new PlacePathAction(land % w.Width, land / w.Width, water % w.Width, water / w.Width, "road"));
        w.Step();
        Assert.Equal(funds, w.Funds);
        Assert.Contains(w.Notices, n => n.Severity == "warning");
        Assert.All(Enumerable.Range(0, w.N), i => Assert.Equal(0, w.Path[i]));
    }

    [Fact]
    public void NotEnoughMoneyIsRejected()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Funds = 100;
        w.Enqueue(new PlaceBuildingAction(x, y, "coal-plant"));
        w.Step();
        Assert.Equal(100, w.Funds);
        Assert.DoesNotContain(w.Buildings, b => b != null);
    }

    [Fact]
    public void BulldozeClearsBuildingAndPath()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlaceBuildingAction(x, y, "police-station"));
        w.Enqueue(new PlacePathAction(x, y + 3, x + 5, y + 3, "street"));
        w.Step();
        Assert.NotEqual(0, w.BuildingAt[w.Idx(x + 1, y + 1)]);
        w.Enqueue(new BulldozeAction(new CellRect(x + 1, y + 1, x + 2, y + 3)));
        w.Step();
        Assert.Equal(0, w.BuildingAt[w.Idx(x, y)]);
        Assert.Equal(0, w.Path[w.Idx(x + 2, y + 3)]);
        Assert.NotEqual(0, w.Path[w.Idx(x + 4, y + 3)]);
    }

    [Fact]
    public void HighwaysGiveNoRoadAccess()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x, y + 2, x + 10, y + 2, "highway-4"));
        w.Enqueue(new PaintZoneAction(new CellRect(x, y + 3, x + 10, y + 3), Zone.Residential, Level.Low));
        w.Step();
        var r = w.Why(x + 4, y + 3);
        Assert.Contains(r.Requirements, q => q.Key == "road" && !q.Met);
    }

    [Fact]
    public void BudgetChargesUpkeepMonthly()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x, y, x + 20, y, "road"));
        w.Enqueue(new PlaceBuildingAction(x, y + 2, "police-station"));
        while (w.Day != World.DaysPerMonth) w.Step();
        long before = w.Funds;
        w.Run(2);
        Assert.Contains(w.LastBudget, l => l.Label == "Path upkeep" && l.Amount < 0);
        Assert.Contains(w.LastBudget, l => l.Label == "Police" && l.Amount == -250);
        Assert.Equal(before - w.LastExpenses + w.LastIncome, w.Funds);
    }
}

public class CommuteTests
{
    [Fact]
    public void CommutersFollowPathsToRealJobs()
    {
        var w = Fixture.Starter();
        w.Run(400);
        var homes = w.Buildings.Where(b => b is { Operating: true } && w.TypeOf(b).Residents > 0).ToList();
        Assert.NotEmpty(homes);
        Assert.Contains(homes, h => h!.Employed > 0);
        foreach (var h in homes)
            foreach (var a in h!.Assignments)
            {
                Assert.NotNull(w.Buildings[a.SiteId]);
                Assert.All(a.PathCells, c => Assert.NotEqual(0, w.Path[c]));
                Assert.InRange(a.Minutes, 1, 60);
            }
        int sum = homes.Sum(h => h!.Employed);
        Assert.Equal(w.Workers, sum);
    }
}

public class ProtocolTests
{
    [Fact]
    public void EveryActionRoundTrips()
    {
        var actions = new SimAction[]
        {
            new PlacePathAction(1, 2, 3, 4, "road"),
            new PlacePathAction(1, 2, 9, 9, "road", true, 9, 2),
            new ZoneBlockAction(5, 6, Zone.Residential, Level.Medium, Leftover.Parks),
            new PaintZoneAction(new CellRect(1, 2, 3, 4), Zone.Commercial, Level.High),
            new PlaceBuildingAction(5, 6, "clinic"),
            new BulldozeAction(new CellRect(1, 1, 2, 2), true),
            new TerraformAction(new CellRect(0, 0, 9, 9), TerraformMode.Lower),
            new SetTaxAction(Zone.Industrial, Level.Medium, 125),
            new SetFundingAction(3, 80),
            new TakeLoanAction(25_000),
            new PlaceUtilityAction(1, 1, 8, 9, UtilityKind.WaterPipe),
            new SetOrdinanceAction("recycling", true),
            new RespondPromptAction(4, true),
            new TriggerDisasterAction(DisasterKind.Robot, 10, 20),
            new SetDisasterSettingAction(DisasterSetting.Off),
        };
        foreach (var a in actions) Assert.Equal(a, Mapping.ToSim(Mapping.ToProto(a)));
    }

    [Fact]
    public void FullStateDescribesTheCity()
    {
        var w = Fixture.Starter();
        w.Run(60);
        var f = Mapping.FullState(w, 1);
        Assert.Equal(w.N, f.Heights.Length);
        Assert.Equal(w.N * 2, f.Paths.Length);
        Assert.Equal(w.Buildings.Count(b => b != null), f.Buildings.Count);
        Assert.Equal(9, f.Stats.Demand.Count);
        Assert.Equal(18, f.Catalog.Profiles.Count);
    }

    [Fact]
    public void DeltaOnlyCarriesChanges()
    {
        var w = Fixture.Starter();
        w.Run(30);
        var t = new DeltaTracker();
        t.Reset(w);
        var d = t.Next(w, 1, Array.Empty<SimNotice>());
        Assert.Empty(d.Cells);
        Assert.Empty(d.Buildings);
        w.Enqueue(new PlacePathAction(0, 0, 3, 0, "dirt-road"));
        w.Step();
        d = t.Next(w, 1, Array.Empty<SimNotice>());
        Assert.True(d.Cells.Count > 0 || w.IsWater(0));
    }
}

public class TalkBackTests
{
    [Fact]
    public void AlertsAndPetitionsPointAtTheAreaAndTheBuildingThatHelps()
    {
        var w = Fixture.Starter(41);
        w.Run(120);
        Assert.Equal("coal-plant", w.AlertHelp(new Alert("no-power", "danger", "", 5, 5, 1, "power")).Fix);
        Assert.Equal("", w.AlertHelp(new Alert("traffic", "warning", "", 5, 5, 1, "traffic")).Fix);
        // A located alert highlights the building there, or the 8x8 neighbourhood its street name stands for.
        var home = w.Buildings.First(b => b is { Residents: > 0 })!;
        var onHome = w.AlertHelp(new Alert("at-risk", "warning", "", home.X, home.Y, 1, "decline")).Area;
        Assert.Equal(new CellRect(home.X, home.Y, home.X + home.W - 1, home.Y + home.H - 1), onHome);
        Assert.Equal(new CellRect(0, 8, 7, 15), w.NeighbourhoodOf(3, 9));
        Assert.Equal(World.NoArea, w.AlertHelp(new Alert("treasury", "danger", "", -1, -1, 1, "budget")).Area);
        // A petition asks for the building whose coverage its goal measures, none for the rest.
        Prompt PetitionFor(string id) => new() { Event = w.Content.Events.FindIndex(e => e.Id == id), Kind = EventKind.Petition, X = 20, Y = 20 };
        Assert.Equal("clinic", w.PromptHelp(PetitionFor("petition-clinic")).Fix);
        Assert.Equal("police-station", w.PromptHelp(PetitionFor("petition-police")).Fix);
        Assert.Equal("", w.PromptHelp(PetitionFor("petition-taxes")).Fix);
        Assert.Equal(new CellRect(16, 16, 23, 23), w.PromptHelp(PetitionFor("petition-park")).Area);
    }

    /// <summary>Runs a road from the starter town straight out to the nearest map edge it can reach.</summary>
    static bool ConnectToEdge(World w, (int X, int Y) site)
    {
        int ox = site.X, oy = site.Y;
        var tries = new List<PlacePathAction>
        {
            new(0, oy, ox, oy, "road"), new(ox, 0, ox, oy, "road"),
            new(ox + StarterTown.Size, oy, w.Width - 1, oy, "road"), new(ox, oy + 25, ox, w.HeightCells - 1, "road"),
        };
        foreach (var t in tries)
        {
            w.Enqueue(new TerraformAction(new CellRect(t.X0, t.Y0, t.X1, t.Y1), TerraformMode.Level));
            w.ApplyPending();
            if (w.ValidatePath(t, out _, out _, out _) == null) { w.Enqueue(t); w.ApplyPending(); return true; }
        }
        return false;
    }

    [Fact]
    public void DemandSourcesAddUpToRawDemand()
    {
        var w = Fixture.Starter();
        w.Run(720);
        foreach (var z in w.DemandReport())
            for (int k = 0; k < 3; k++)
            {
                int sum = z.Sources.Sum(s => s.Points[0]) + z.Adjustments.Sum(a => a.Points.Length == 1 ? a.Points[0] : a.Points[k]);
                Assert.Equal(Math.Clamp(sum, -100, 100), z.Raw[k]);
                Assert.True(z.Final[k] <= z.Raw[k]);
                Assert.False(string.IsNullOrEmpty(z.Binding));
            }
    }

    [Fact]
    public void NoPlantCapsDemandAndSaysSo()
    {
        var w = Fixture.Starter();
        w.Run(360);
        var plant = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "coal-plant")!;
        w.Enqueue(new BulldozeAction(new CellRect(plant.X, plant.Y, plant.X, plant.Y)));
        w.Run(2);
        Assert.All(w.Demand, d => Assert.True(d <= 0));
        Assert.Contains(w.DemandRaw, d => d > 0);
        Assert.All(w.DemandReport(), z => Assert.Equal("power", z.BindingKey));
        var alert = Assert.Single(w.Alerts(), a => a.Key == "no-power");
        Assert.True(w.InBounds(alert.X, alert.Y));
    }

    [Fact]
    public void RoadToTheMapEdgeRaisesRegionalDemand()
    {
        var w = World.Create(Fixture.Content, 42, 128, "Edge");
        var site = StarterTown.FindSite(w)!.Value;
        foreach (var a in StarterTown.Build(site.X, site.Y)) w.Enqueue(a);
        w.Run(60);
        Assert.Equal(0, w.EdgeCapacity);
        int before = w.DemandReport()[0].Sources.Single(s => s.Key == "regional").Points[0];
        Assert.True(ConnectToEdge(w, site), "no clear run to the edge");
        Assert.True(w.EdgeCapacity >= 1200);
        Assert.True(w.InBounds(w.EdgeCell % w.Width, w.EdgeCell / w.Width));
        w.Step();
        int after = w.DemandReport()[0].Sources.Single(s => s.Key == "regional").Points[0];
        Assert.True(after > before, $"regional {before} -> {after}");
        // The connection is saved with the city.
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.EdgeCapacity, loaded.EdgeCapacity);
    }

    [Fact]
    public void ForecastIsExactAndLeavesTheCityAlone()
    {
        var w = Fixture.Starter();
        w.Run(200);
        ulong hash = w.StateHash();
        var f = w.Forecast(4);
        Assert.Equal(hash, w.StateHash());
        Assert.Equal(4, f.Points.Count);
        Assert.NotEmpty(f.Summary);
        w.Run(4 * World.DaysPerMonth);
        Assert.Equal(w.Population, f.Points[^1].Population);
        Assert.Equal(w.Funds, f.Points[^1].Funds);
    }

    [Fact]
    public void ForecastWithAPlanShowsItsEffect()
    {
        var w = Fixture.Starter();
        w.Run(360);
        var plant = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "coal-plant")!;
        var f = w.Forecast(2, new SimAction[] { new BulldozeAction(new CellRect(plant.X, plant.Y, plant.X, plant.Y)) });
        Assert.Contains(f.Events, e => e.Text.Contains("no power plant", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(w.Buildings[plant.Id]);
    }

    [Fact]
    public void EveryTopicHasAnArticle()
    {
        var w = Fixture.Starter();
        w.Run(400);
        var plant = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "coal-plant")!;
        w.Enqueue(new BulldozeAction(new CellRect(plant.X, plant.Y, plant.X, plant.Y)));
        w.Run(40);
        var ids = w.Encyclopedia().Select(a => a.Id).ToHashSet();
        Assert.Equal(ids.Count, w.Encyclopedia().Count);
        foreach (var a in w.Encyclopedia())
        {
            Assert.NotEmpty(a.Paragraphs);
            Assert.All(a.SeeAlso, s => Assert.Contains(s, ids));
        }
        Assert.All(w.Alerts(), a => Assert.Contains(a.Topic, ids));
        Assert.All(w.DemandReport(), z => Assert.Contains(z.Topic, ids));
        foreach (var t in Fixture.Content.Types.Where(t => t.Placeable)) Assert.Contains(t.Id, ids);
        foreach (var p in Fixture.Content.Profiles) Assert.Contains(p.Id, ids);
        var proto = Mapping.Why(w.Why(plant.X + 6, plant.Y));
        Assert.All(proto.Requirements.Where(r => r.Topic != ""), r => Assert.Contains(r.Topic, ids));
    }

    [Fact]
    public void AlertsPointAtTheirSource()
    {
        var w = Fixture.Starter();
        w.Run(720);
        var plant = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "coal-plant")!;
        // Swap the coal plant for a wind turbine: the town browns out.
        w.Enqueue(new BulldozeAction(new CellRect(plant.X, plant.Y, plant.X, plant.Y)));
        w.Enqueue(new PlaceBuildingAction(plant.X, plant.Y, "wind-turbine"));
        w.Run(3);
        var brownout = Assert.Single(w.Alerts(), a => a.Key == "brownout");
        Assert.Equal(2, w.Powered[w.Idx(brownout.X, brownout.Y)]);
        Assert.Equal("danger", w.Alerts()[0].Severity);
    }
}

public class OrdinanceTests
{
    static OrdinanceView View(World w, string id) => w.OrdinanceViews().Single(v => v.Def.Id == id);

    [Fact]
    public void EveryBuiltInOrdinanceIsARuleGraph()
    {
        var ords = Fixture.Content.Ordinances;
        Assert.True(ords.Count >= 15);
        Assert.Contains(ords, o => o.Kind == OrdinanceAvailability.Always);
        Assert.Contains(ords, o => o.Kind == OrdinanceAvailability.Unlock);
        Assert.Contains(ords, o => o.Kind == OrdinanceAvailability.Random && o.DurationMonths > 0);
        // All behaviour lives in the graph: every ordinance acts through act.* nodes, and nothing else in the sim
        // refers to an ordinance by id.
        Assert.All(ords, o => Assert.Contains(o.Graph.Nodes, n => n.Op.StartsWith("act.")));
        var simDir = Path.Combine(Path.GetDirectoryName(ContentDb.FindContentDir())!, "src", "ToyCity.Sim");
        var code = string.Join("\n", Directory.GetFiles(simDir, "*.cs").Select(File.ReadAllText));
        Assert.All(ords, o => Assert.DoesNotContain($"\"{o.Id}\"", code));
    }

    [Fact]
    public void BadGraphsAreRejected()
    {
        static void Bad(string nodes) => Assert.Throws<InvalidDataException>(() =>
            RuleGraph.Compile(System.Text.Json.JsonDocument.Parse(nodes).RootElement, "test"));
        Bad("""[{ "op": "math.explode", "in": [1, 2] }]""");
        Bad("""[{ "op": "math.add", "in": ["later", 1] }, { "id": "later", "op": "const", "value": 1 }]""");
        Bad("""[{ "op": "act.modifier", "key": "happiness", "in": [5] }]""");
        Bad("""[{ "op": "measure.stat", "stat": "vibes" }]""");
        var ok = RuleGraph.Compile(System.Text.Json.JsonDocument.Parse("""[{ "id": "a", "op": "const", "value": 6 }, { "op": "math.mul", "in": ["a", { "op": "math.add", "in": [1, 2] }] }]""").RootElement, "test");
        Assert.Equal(18, Fixture.Starter().EvaluateRules(ok).Last.Number);
    }

    [Fact]
    public void LockedOrdinancesCannotBeEnacted()
    {
        var w = Fixture.Starter();
        w.Run(60);
        Assert.True(w.Population < 2000);
        Assert.Equal(OrdinanceStatus.Locked, View(w, "free-clinics").Status);
        w.Enqueue(new SetOrdinanceAction("free-clinics", true));
        w.Step();
        Assert.Equal(OrdinanceStatus.Locked, View(w, "free-clinics").Status);
        Assert.Contains(w.Notices, n => n.Text.Contains("isn't available"));
    }

    [Fact]
    public void PowerConservationCutsDemandAndCostsWhatThePreviewSaid()
    {
        var w = Fixture.Starter();
        w.Run(400);
        var before = View(w, "power-conservation");
        Assert.Equal(OrdinanceStatus.Available, before.Status);
        Assert.Contains("-15% power use", before.Effects);
        Assert.True(before.MonthlyNet < 0);
        long demand = w.PowerDemand;
        w.Enqueue(new SetOrdinanceAction("power-conservation", true));
        w.Step();
        Assert.Equal(-15, w.Mod(ModKey.PowerUse));
        Assert.True(w.PowerDemand < demand * 90 / 100, $"{w.PowerDemand} vs {demand}");
        // Run to the next budget: the cost line is there and matches the preview for the same city.
        while (w.Day != 1) w.Step();
        long preview = View(w, "power-conservation").MonthlyNet;
        w.Step();
        Assert.Contains(w.LastBudget, l => l.Label == "Power conservation" && l.Amount == preview);
        w.Enqueue(new SetOrdinanceAction("power-conservation", false));
        w.Step();
        Assert.Equal(0, w.Mod(ModKey.PowerUse));
    }

    [Fact]
    public void BuildingALargeParkUnlocksParkRangers()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Run(30);
        Assert.Equal(OrdinanceStatus.Locked, View(w, "park-rangers").Status);
        w.Enqueue(new PlaceBuildingAction(x + 2, y + 2, "large-park"));
        w.Run(31);
        Assert.Equal(OrdinanceStatus.Available, View(w, "park-rangers").Status);
        w.Enqueue(new SetOrdinanceAction("park-rangers", true));
        w.Step();
        Assert.Equal(5, w.Mod(ModKey.LandValue));
        Assert.Equal(2, w.Mod(ModKey.Attraction));
    }

    [Fact]
    public void RandomOffersAreTimeLimited()
    {
        var w = Fixture.Starter();
        int months = 0;
        while (View(w, "street-festival").Status != OrdinanceStatus.Offered && months++ < 240) w.Run(World.DaysPerMonth);
        Assert.Equal(OrdinanceStatus.Offered, View(w, "street-festival").Status);
        w.Enqueue(new SetOrdinanceAction("street-festival", true));
        w.Step();
        var v = View(w, "street-festival");
        Assert.Equal(OrdinanceStatus.Enacted, v.Status);
        Assert.Equal(3, v.MonthsLeft);
        Assert.Equal(5, w.Mod(ModKey.Attraction));
        w.Run(3 * World.DaysPerMonth + 1);
        Assert.NotEqual(OrdinanceStatus.Enacted, View(w, "street-festival").Status);
        Assert.Equal(0, w.Mod(ModKey.Attraction));
    }

    [Fact]
    public void EnactedOrdinancesSurviveSaveAndLoad()
    {
        var w = Fixture.Starter();
        w.Run(400);
        w.Enqueue(new SetOrdinanceAction("recycling", true));
        w.Enqueue(new SetOrdinanceAction("clean-air-act", true));
        w.Run(45);
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        w.Run(200);
        loaded.Run(200);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal(-25, loaded.Mod(ModKey.Garbage));
    }

    [Fact]
    public void CleanAirActCutsFactoryPollution()
    {
        var a = Fixture.Starter();
        a.Run(400);
        var b = World.FromSnapshot(Fixture.Content, a.Snapshot());
        b.Enqueue(new SetOrdinanceAction("clean-air-act", true));
        a.Run(31);
        b.Run(31);
        Assert.True(b.Pollution.Sum(p => p) < a.Pollution.Sum(p => p));
        Assert.Contains(b.DemandReport()[2].Adjustments, l => l.Key == "ordinances" && l.Points[0] == -20);
    }
}

public class DirectorTests
{
    /// <summary>Runs month by month until a prompt from the given event is open, or gives up after the limit.</summary>
    static Prompt? WaitFor(World w, string eventId, int months = 180)
    {
        for (int m = 0; m < months; m++)
        {
            var p = w.Prompts.FirstOrDefault(p => p.Status == PromptStatus.Open && w.Content.Events[p.Event].Id == eventId);
            if (p != null) return p;
            w.Run(World.DaysPerMonth);
        }
        return null;
    }

    [Fact]
    public void TaxPetitionResolvesWhenTaxesComeDown()
    {
        var w = Fixture.Starter();
        w.Run(90);
        for (int k = 0; k < 3; k++) w.Enqueue(new SetTaxAction(Zone.Residential, (Level)k, 120));
        var p = WaitFor(w, "petition-taxes");
        Assert.NotNull(p);
        Assert.False(string.IsNullOrEmpty(p!.Text));
        Assert.DoesNotContain("{", p.Text);
        Assert.True(w.InBounds(p.X, p.Y));
        // Fork the city: one copy answers the petition, the other doesn't. Answering leaves people happier.
        var ignored = World.FromSnapshot(Fixture.Content, w.Snapshot());
        for (int k = 0; k < 3; k++) w.Enqueue(new SetTaxAction(Zone.Residential, (Level)k, 90));
        w.Run(World.DaysPerMonth + 1);
        ignored.Run(World.DaysPerMonth + 1);
        Assert.Equal(PromptStatus.Fulfilled, p.Status);
        Assert.Contains(w.Headlines, h => h.Text == p.ResultText);
        Assert.True(w.Mood > ignored.Mood, $"{w.Mood} vs {ignored.Mood}");
    }

    [Fact]
    public void AcceptingADealUnlocksItsBuilding()
    {
        var w = Fixture.Starter(2026);
        Prompt? deal = null;
        for (int m = 0; m < 240 && deal == null; m++)
        {
            w.Run(World.DaysPerMonth);
            deal = w.Prompts.FirstOrDefault(p => p.Status == PromptStatus.Open && p.Kind == EventKind.Deal
                && w.Content.Events[p.Event].Yes!.Graph.Nodes.Any(n => n.Op == "act.unlock"));
        }
        Assert.NotNull(deal);
        var ev = w.Content.Events[deal!.Event];
        string typeId = ev.Yes!.Graph.Nodes.First(n => n.Op == "act.unlock").Params["type"];
        int t = w.Content.TypeIndex(typeId);
        Assert.False(w.TypeUnlocked[t]);
        Assert.NotNull(w.ValidatePlacement(new PlaceBuildingAction(0, 0, typeId), out _));
        long funds = w.Funds;
        w.Enqueue(new RespondPromptAction(deal.Id, true));
        w.ApplyPending();
        Assert.Equal(PromptStatus.Accepted, deal.Status);
        Assert.True(w.TypeUnlocked[t]);
        Assert.True(w.Funds >= funds);
    }

    [Fact]
    public void TheDirectorIsRateLimited()
    {
        var w = Fixture.Starter(2026);
        var created = new List<long>();
        for (int m = 0; m < 120; m++)
        {
            w.Run(World.DaysPerMonth);
            Assert.True(w.Prompts.Count(p => p.Status == PromptStatus.Open) <= w.Content.Director.MaxOpen);
            // Leave deals unanswered: they lapse as declined.
        }
        var times = w.Prompts.Select(p => p.Created).Order().ToList();
        Assert.NotEmpty(times);
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= w.Content.Director.MinMonthsBetween * World.DaysPerMonth);
        Assert.All(w.Prompts.Where(p => p.Kind == EventKind.Deal), p => Assert.NotEqual(PromptStatus.Accepted, p.Status));
    }

    [Fact]
    public void TheVoiceNeverChangesWhatHappens()
    {
        var dir = ContentDb.FindContentDir();
        string R(string f) => File.ReadAllText(Path.Combine(dir, f));
        var plain = ContentDb.Parse(R("profiles.json"), R("buildings.json"), R("ordinances.json"), R("events.json"),
            """{ "templates": { "petition-park": "x" }, "firstNames": ["A"], "lastNames": ["B"] }""");
        var a = World.Create(Fixture.Content, 2026, 128, "Same");
        var b = World.Create(plain, 2026, 128, "Same");
        StarterTown.Apply(a);
        StarterTown.Apply(b);
        for (int m = 0; m < 48; m++)
        {
            a.Run(World.DaysPerMonth);
            b.Run(World.DaysPerMonth);
            Assert.Equal(a.Population, b.Population);
            Assert.Equal(a.Funds, b.Funds);
            Assert.Equal(a.Approval, b.Approval);
        }
        Assert.Equal(a.Prompts.Select(p => (p.Event, p.X, p.Y, p.Status)), b.Prompts.Select(p => (p.Event, p.X, p.Y, p.Status)));
        Assert.Equal(a.Headlines.Count, b.Headlines.Count);
    }

    [Fact]
    public void LandmarksUnlockWithPopulationAndAreUnique()
    {
        var w = Fixture.Starter();
        var site = StarterTown.FindSite(World.Create(Fixture.Content, 42, 128, "x"))!.Value;
        var statue = new PlaceBuildingAction(site.X + 5, site.Y + StarterTown.Size, "toy-soldier-statue");
        Assert.Contains("isn't available", w.ValidatePlacement(statue, out _));
        w.Run(400);
        Assert.True(w.Population >= 400);
        Assert.True(w.TypeUnlocked[w.Content.TypeIndex("toy-soldier-statue")]);
        Assert.Contains(w.Headlines, h => h.Text.Contains("Toy Soldier Statue"));
        long funds = w.Funds;
        w.Enqueue(statue);
        w.ApplyPending();
        Assert.Contains(w.Buildings, b => b != null && w.TypeOf(b).Id == "toy-soldier-statue");
        Assert.Equal(funds, w.Funds); // landmarks are free
        Assert.Contains("already has", w.ValidatePlacement(statue with { X = statue.X + 2 }, out _));
    }

    [Fact]
    public void PromptsSurviveSaveAndLoad()
    {
        var w = Fixture.Starter(2026);
        while (w.Prompts.All(p => p.Status != PromptStatus.Open) && w.Tick < 3600) w.Run(World.DaysPerMonth);
        Assert.Contains(w.Prompts, p => p.Status == PromptStatus.Open);
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.Prompts.Select(p => p.Text), loaded.Prompts.Select(p => p.Text));
        w.Run(300);
        loaded.Run(300);
        Assert.Equal(w.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void AdvisorsAlwaysHaveSomethingToSay()
    {
        var w = Fixture.Starter();
        w.Run(500);
        var ids = w.Encyclopedia().Select(a => a.Id).ToHashSet();
        var advisors = w.Advisors();
        Assert.Equal(5, advisors.Count);
        foreach (var a in advisors)
        {
            Assert.NotEmpty(a.Messages);
            Assert.All(a.Messages, m => Assert.DoesNotContain("advisor.", m.Text));
            Assert.All(a.Messages, m => Assert.Contains(m.Topic, ids));
        }
    }
}

public class DisasterTests
{
    static World Fork(World w) => World.FromSnapshot(Fixture.Content, w.Snapshot());

    /// <summary>Buildings that existed in <paramref name="before"/> and are gone now.</summary>
    static int Lost(World before, World after) =>
        before.Buildings.Count(b => b != null && (b.Id >= after.Buildings.Count || after.Buildings[b.Id] == null));

    static List<Building> Homes(World w) => w.Buildings
        .Where(b => b != null && !w.TypeOf(b).Placeable && w.TypeOf(b).Zone == Zone.Residential && b.ConstructionLeft == 0)
        .Select(b => b!).ToList();

    [Fact]
    public void FireSpreadsWithoutCoverageAndFireStationsContainIt()
    {
        var w = Fixture.Starter(5);
        w.Run(365); // day 6: three weeks before coverage is recomputed
        var homes = Homes(w);
        var targets = new[] { homes[0], homes[homes.Count / 2], homes[^1] };
        var bare = Fork(w);
        var covered = Fork(w);
        Array.Fill(covered.Coverage[(int)Service.Fire], (byte)255);
        foreach (var t in targets)
        {
            bare.Enqueue(new TriggerDisasterAction(DisasterKind.Fire, t.X, t.Y));
            covered.Enqueue(new TriggerDisasterAction(DisasterKind.Fire, t.X, t.Y));
        }
        bare.Run(20);
        covered.Run(20);
        int lostBare = Lost(w, bare), lostCovered = Lost(w, covered);
        Assert.True(lostBare >= 3, $"only {lostBare} lost with no fire coverage");
        Assert.True(lostCovered < lostBare, $"{lostCovered} lost with full coverage vs {lostBare} without");
        Assert.DoesNotContain(covered.Buildings, b => b is { BurningDays: > 0 });
    }

    [Fact]
    public void TheColossusFlattensAStraightPathAndWindsDown()
    {
        var w = Fixture.Starter(3);
        w.Run(200);
        var target = Homes(w)[0];
        var calm = Fork(w);
        var after = Fork(w);
        after.Enqueue(new TriggerDisasterAction(DisasterKind.Robot, target.X, target.Y));
        after.Step();
        var robot = Assert.Single(after.Disasters);
        bool alongX = robot.Dx != 0;
        int lane = alongX ? robot.CellY : robot.CellX;
        after.Run(World.RobotDays + 1);
        calm.Run(World.RobotDays + 2);
        Assert.Empty(after.Disasters);
        Assert.True(Lost(w, after) > Lost(w, calm));
        // Everything it flattened touched its 2-cell-wide lane; growth replaces a few lots either way.
        bool Gone(World x, Building b) => b.Id >= x.Buildings.Count || x.Buildings[b.Id] == null;
        foreach (var b in w.Buildings.Where(b => b != null && Gone(after, b) && !Gone(calm, b)))
        {
            int lo = alongX ? b!.Y : b!.X, hi = lo + (alongX ? b.H : b.W) - 1;
            Assert.True(hi >= lane && lo <= lane + 1, $"building #{b.Id} at {b.X},{b.Y} is off the lane {lane}");
        }
        Assert.Contains(after.Headlines, h => h.Text.Contains("spring") || h.Text.Contains("run down"));
    }

    [Fact]
    public void AQuakeOnlyReachesItsRadius()
    {
        var w = Fixture.Starter(8);
        w.Run(100);
        var centre = Homes(w)[0];
        var shaken = Fork(w);
        var calm = Fork(w);
        shaken.Enqueue(new TriggerDisasterAction(DisasterKind.Earthquake, centre.X, centre.Y));
        shaken.Step();
        calm.Step();
        int hit = 0;
        foreach (var b in calm.Buildings)
        {
            if (b == null || b.Id >= shaken.Buildings.Count) continue;
            int dx = Math.Max(0, Math.Max(b.X - centre.X, centre.X - (b.X + b.W - 1)));
            int dy = Math.Max(0, Math.Max(b.Y - centre.Y, centre.Y - (b.Y + b.H - 1)));
            int dist = (int)Math.Sqrt(dx * dx + dy * dy);
            var s = shaken.Buildings[b.Id];
            if (dist > World.QuakeRadius) Assert.True(s != null && s.Condition == b.Condition, $"#{b.Id} at distance {dist} was hit");
            else if (s == null || s.Condition < b.Condition) hit++;
        }
        Assert.True(hit > 0);
    }

    [Fact]
    public void ActiveDisastersSurviveSaveAndReplay()
    {
        var w = Fixture.Starter(12);
        w.Run(120);
        var home = Homes(w)[^1];
        w.Enqueue(new TriggerDisasterAction(DisasterKind.Tornado, home.X, home.Y));
        w.Enqueue(new TriggerDisasterAction(DisasterKind.Fire, home.X + 3, home.Y));
        w.Run(4);
        Assert.NotEmpty(w.Disasters);
        var loaded = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), loaded.StateHash());
        w.Run(40);
        loaded.Run(40);
        Assert.Equal(w.StateHash(), loaded.StateHash());
        var replayed = World.Replay(Fixture.Content, 12, 128, "Testville", w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replayed.StateHash());
    }

    [Fact]
    public void TheSandboxWorksWithDisastersOff()
    {
        var w = Fixture.Starter();
        w.Run(30);
        w.Enqueue(new SetDisasterSettingAction(DisasterSetting.Off));
        var home = Homes(w)[0];
        w.Enqueue(new TriggerDisasterAction(DisasterKind.Tornado, home.X, home.Y));
        w.Step();
        Assert.Equal(DisasterSetting.Off, w.DisasterSetting);
        Assert.Contains(w.Disasters, d => d.Kind == DisasterKind.Tornado);
        Assert.Contains(w.Alerts(), a => a.Topic == "disasters");
    }
}

public class WearTests
{
    [Fact]
    public void GraffitiGrowsWithoutPoliceAndTheRemovalOrdinanceCleansIt()
    {
        var w = Fixture.Starter(21);
        w.Run(360 * 2);
        int before = w.Buildings.Sum(b => b is { Abandoned: false } ? b.Graffiti : 0);
        Assert.True(before > 0, "no graffiti in a town without police");
        var cleaned = World.FromSnapshot(Fixture.Content, w.Snapshot());
        cleaned.Enqueue(new SetOrdinanceAction("graffiti-removal", true));
        w.Run(360);
        cleaned.Run(360);
        int left = cleaned.Buildings.Sum(b => b is { Abandoned: false } ? b.Graffiti : 0);
        int untouched = w.Buildings.Sum(b => b is { Abandoned: false } ? b.Graffiti : 0);
        Assert.True(left * 2 < untouched, $"{left} graffiti with the crew vs {untouched} without");
    }

    [Fact]
    public void ConditionHeadsForWhatOwnersCanAffordAndWhySaysSo()
    {
        var w = Fixture.Starter(4);
        w.Run(360 * 2);
        var homes = w.Buildings.Where(b => b is { Abandoned: false, ConstructionLeft: 0 } && !w.TypeOf(b).Placeable).Select(b => b!).ToList();
        // Richer land is kept up better.
        var rich = homes.OrderByDescending(b => w.LandValue[w.Idx(b.X, b.Y)]).Take(10).Average(b => w.UpkeepTarget(b, null));
        var poor = homes.OrderBy(b => w.LandValue[w.Idx(b.X, b.Y)]).Take(10).Average(b => w.UpkeepTarget(b, null));
        Assert.True(rich > poor, $"{rich} vs {poor}");
        var why = w.Why(homes[0].X, homes[0].Y);
        var cond = Assert.Single(why.Requirements, r => r.Key == "condition");
        Assert.Contains("Owners keep it at", cond.Detail);
        Assert.Contains(w.Encyclopedia(), a => a.Id == "wear");
    }

    [Fact]
    public void RunDownBuildingsBlightTheirNeighbours()
    {
        var w = Fixture.Starter(6);
        w.Run(60);
        var home = w.Buildings.First(b => b is { ConstructionLeft: 0 } && !w.TypeOf(b).Placeable && w.TypeOf(b).Zone == Zone.Residential)!;
        int i = w.Idx(home.X, home.Y);
        int lvBefore = w.LandValueOf(i, null);
        home.Condition = 0;
        home.Abandoned = true;
        home.UnmetMonths = World.MonthsToAbandon + World.DerelictAfter;
        Assert.Equal(BuildingLook.Derelict, w.LookOf(home));
        w.Run(31);
        Assert.True(w.Blight[i] > 0);
        var f = new List<Factor>();
        w.LandValueOf(i, f);
        Assert.Contains(f, x => x.Label == "Run-down area" && x.Value < 0);
    }
}

public class TransitTests
{
    /// <summary>
    /// A hand-built lab: a home and a factory 26 cells apart on a slow dirt road, a coal plant between them, and
    /// optionally a rail line with a station at each end.
    /// </summary>
    static (World W, Building Home, Building Work, int RoadY) Lab(bool rail)
    {
        var (w, x, y) = Fixture.FlatSite();
        int road = y + 10;
        w.Enqueue(new PlacePathAction(x, road, x + 30, road, "dirt-road"));
        w.Enqueue(new PlaceBuildingAction(x + 12, road - 4, "coal-plant"));
        if (rail)
        {
            w.Enqueue(new PlacePathAction(x, road + 3, x + 30, road + 3, "rail"));
            w.Enqueue(new PlaceBuildingAction(x + 1, road + 1, "rail-station"));
            w.Enqueue(new PlaceBuildingAction(x + 27, road + 1, "rail-station"));
        }
        w.Step();
        var home = w.AddBuilding(Fixture.Content.Zoned(Zone.Residential, Level.Medium, Level.High), x + 1, road - 2, 0, 2, 2);
        var work = w.AddBuilding(Fixture.Content.Zoned(Zone.Industrial, Level.Medium, Level.Medium), x + 27, road - 2, 0, 2, 2);
        // Long enough for the home to pick its jobs and for the month's traffic count to see them.
        w.Run(61);
        return (w, home, work, road);
    }

    [Fact]
    public void TrainsWinOverASlowRoadAndTakeCarsOffIt()
    {
        var (car, carHome, _, road) = Lab(rail: false);
        var (train, trainHome, _, _) = Lab(rail: true);
        Assert.NotEmpty(carHome.Assignments);
        Assert.All(carHome.Assignments, a => Assert.False(a.ByTransit));
        var rode = trainHome.Assignments.Where(a => a.ByTransit).ToList();
        Assert.NotEmpty(rode);
        Assert.True(rode.Sum(a => a.Count) * 2 > trainHome.Employed, "most should take the train");
        Assert.All(rode, a => Assert.Equal(2, a.Stops.Length));
        Assert.True(trainHome.CommuteMinutes < carHome.CommuteMinutes, $"{trainHome.CommuteMinutes} vs {carHome.CommuteMinutes} min");
        // The road between them carries fewer trips.
        int roadTrips(World w) => Enumerable.Range(0, w.Width).Select(x => w.Trips[w.Idx(x, road)]).Max();
        Assert.True(roadTrips(train) < roadTrips(car), $"{roadTrips(train)} vs {roadTrips(car)} trips");
        Assert.True(train.TransitBoardings > 0);
        var station = train.Buildings.First(b => b != null && train.TypeOf(b).Id == "rail-station")!;
        Assert.True(station.Riders > 0);
        Assert.Contains("people get on or off", train.Why(station.X, station.Y).Summary);
    }

    [Fact]
    public void BusesCarryPeopleWithoutCarsAndEarnFares()
    {
        var w = Fixture.Starter(42);
        w.Run(360);
        var spots = new List<(int X, int Y)>();
        for (int y = 0; y < w.HeightCells; y++)
        for (int x = 0; x < w.Width; x++)
        {
            int i = w.Idx(x, y);
            if (w.BuildingAt[i] != 0 || w.Path[i] != 0 || w.IsWater(i)) continue;
            bool road = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => w.InBounds(x + d.Item1, y + d.Item2) && w.Carries(TransitKind.Bus, w.Idx(x + d.Item1, y + d.Item2)));
            if (!road || spots.Any(s => Math.Abs(s.X - x) + Math.Abs(s.Y - y) < 7)) continue;
            spots.Add((x, y));
            w.Enqueue(new PlaceBuildingAction(x, y, "bus-stop"));
        }
        w.Run(60);
        int riders = w.Buildings.Sum(b => b == null ? 0 : b.Assignments.Where(a => a.ByTransit).Sum(a => a.Count));
        Assert.True(riders > 0, "nobody took the bus");
        Assert.True(w.TransitBoardings > 0);
        Assert.Contains(w.LastBudget, l => l.Label == "Transit fares" && l.Amount > 0);
        // Low-wealth homes lean on the bus more than rich ones.
        double Share(Level wealth)
        {
            var homes = w.Buildings.Where(b => b is { Residents: > 0 } && w.TypeOf(b).Wealth == wealth).ToList();
            int all = homes.Sum(h => h!.Employed), bus = homes.Sum(h => h!.Assignments.Where(a => a.ByTransit).Sum(a => a.Count));
            return all == 0 ? 0 : bus / (double)all;
        }
        Assert.True(Share(Level.Low) > 0);
        Assert.Contains(w.Encyclopedia(), a => a.Id == "transit");
    }

    [Fact]
    public void TransitCommutesSurviveSaveAndReplay()
    {
        var (w, _, _, _) = Lab(rail: true);
        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        w.Run(45);
        loaded.Run(45);
        Assert.Equal(w.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void AStopAwayFromItsLineSaysSo()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlaceBuildingAction(x + 5, y + 5, "tram-stop"));
        w.Step();
        var stop = w.Buildings.First(b => b != null && w.TypeOf(b).Id == "tram-stop")!;
        Assert.Contains(w.Alerts(), a => a.Key == "stop-no-line" && a.Topic == "transit");
        Assert.Contains("must touch a tram line", w.Why(stop.X, stop.Y).Summary);
    }
}

public class BlockTests
{
    [Theory]
    [InlineData(0, 0, 10, 0, 10, 10)]
    [InlineData(3, 20, 3, 3, 20, 3)]
    [InlineData(5, 5, 9, 6, 14, 5)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(10, 0, 0, 30, 20, 2)]
    public void CurvesAreEdgeConnectedAndEndWhereAsked(int x0, int y0, int cx, int cy, int x1, int y1)
    {
        var cells = World.CurveCells(x0, y0, cx, cy, x1, y1);
        Assert.Equal((x0, y0), cells[0]);
        Assert.Equal((x1, y1), cells[^1]);
        Assert.Equal(cells.Count, cells.Distinct().Count());
        // Each cell joins one before it edge to edge (a curve that doubles back reuses its cells), so the run is connected.
        for (int k = 1; k < cells.Count; k++)
            Assert.Contains(cells.Take(k), c => Math.Abs(c.X - cells[k].X) + Math.Abs(c.Y - cells[k].Y) == 1);
        // A curve stays inside the triangle's bounding box.
        Assert.All(cells, c => Assert.InRange(c.X, Math.Min(x0, Math.Min(cx, x1)), Math.Max(x0, Math.Max(cx, x1))));
    }

    [Fact]
    public void ACurvedRoadIsBuiltAndPaidForCellByCell()
    {
        var (w, x, y) = Fixture.FlatSite();
        long before = w.Funds;
        var a = new PlacePathAction(x, y + 12, x + 12, y, "road", true, x, y);
        var cells = World.PathCellsOf(a);
        w.Enqueue(a);
        w.Step();
        Assert.All(cells, c => Assert.Equal(Fixture.Content.ProfileIndex("road") + 1, w.Path[w.Idx(c.X, c.Y)]));
        Assert.Equal(before - cells.Count * Fixture.Content.Profiles[Fixture.Content.ProfileIndex("road")].CostPerCell, w.Funds);
    }

    static (World W, int X, int Y) Ring()
    {
        var (w, x, y) = Fixture.FlatSite();
        // A 16 x 16 ring of road around a 14 x 14 block.
        w.Enqueue(new PlacePathAction(x, y, x + 15, y, "road"));
        w.Enqueue(new PlacePathAction(x, y + 15, x + 15, y + 15, "road"));
        w.Enqueue(new PlacePathAction(x, y, x, y + 15, "road"));
        w.Enqueue(new PlacePathAction(x + 15, y, x + 15, y + 15, "road"));
        w.Step();
        return (w, x, y);
    }

    [Fact]
    public void BlockZoningFillsLotDeepAndLeavesTheMiddle()
    {
        var (w, x, y) = Ring();
        w.Enqueue(new ZoneBlockAction(x + 7, y + 7, Zone.Residential, Level.Medium, Leftover.Leave));
        w.Step();
        int reach = w.LotDepth(Level.Medium);
        for (int yy = y + 1; yy <= y + 14; yy++)
        for (int xx = x + 1; xx <= x + 14; xx++)
        {
            int depth = Math.Min(Math.Min(xx - x, x + 15 - xx), Math.Min(yy - y, y + 15 - yy));
            byte code = w.ZoneCode[w.Idx(xx, yy)];
            if (depth <= reach) Assert.Equal(World.MakeZoneCode(Zone.Residential, Level.Medium), code);
            else Assert.Equal(0, code);
        }
        // Nothing zoned is stranded.
        Assert.DoesNotContain(w.Alerts(), a => a.Key == "stranded");
    }

    [Fact]
    public void LeftoverPoliciesParkOrZoneTheMiddle()
    {
        var (parks, x, y) = Ring();
        var zoned = World.FromSnapshot(Fixture.Content, parks.Snapshot());
        parks.Enqueue(new ZoneBlockAction(x + 7, y + 7, Zone.Commercial, Level.Low, Leftover.Parks));
        zoned.Enqueue(new ZoneBlockAction(x + 7, y + 7, Zone.Commercial, Level.Low, Leftover.Zone));
        parks.Step();
        zoned.Step();
        int middle = 14 - 2 * parks.LotDepth(Level.Low);
        Assert.Equal(middle * middle, parks.Buildings.Count(b => b != null && parks.TypeOf(b).Id == "pocket-park"));
        Assert.Equal(14 * 14, Enumerable.Range(0, zoned.N).Count(i => zoned.ZoneCode[i] != 0));
    }

    [Fact]
    public void OpenLandIsNotABlock()
    {
        var (w, x, y) = Fixture.FlatSite();
        long funds = w.Funds;
        w.Enqueue(new ZoneBlockAction(x + 5, y + 5, Zone.Industrial, Level.Low, Leftover.Zone));
        w.Step();
        Assert.Equal(funds, w.Funds);
        Assert.Equal(0, Enumerable.Range(0, w.N).Count(i => w.ZoneCode[i] != 0));
        Assert.Null(w.BlockAt(x + 5, y + 5));
    }

    [Fact]
    public void ACurvedBlockIsZonedRightUpToItsRoad()
    {
        var (w, x, y) = Fixture.FlatSite();
        // A quarter-round block: straight roads on two sides and an arc closing it.
        w.Enqueue(new PlacePathAction(x, y, x, y + 14, "road"));
        w.Enqueue(new PlacePathAction(x, y + 14, x + 14, y + 14, "road"));
        w.Enqueue(new PlacePathAction(x, y, x + 14, y + 14, "street", true, x + 14, y));
        w.Step();
        var block = w.BlockAt(x + 3, y + 11);
        Assert.NotNull(block);
        w.Enqueue(new ZoneBlockAction(x + 3, y + 11, Zone.Residential, Level.Low, Leftover.Leave));
        w.Step();
        foreach (int c in block!)
        {
            int cx = c % w.Width, cy = c / w.Width;
            bool touchesRoad = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => w.Path[w.Idx(cx + d.Item1, cy + d.Item2)] != 0);
            if (touchesRoad) Assert.NotEqual(0, w.ZoneCode[c]);
        }
    }
}

public class AccountingTests
{
    [Fact]
    public void TheBooksBalanceEveryMonthThroughGrowthTransitAndDisasters()
    {
        var w = Fixture.Starter(31);
        for (int m = 0; m < 30; m++)
        {
            if (m == 10)
            {
                // Bus stops beside a few roads, and a fire and a quake for good measure.
                int placed = 0;
                for (int i = 0; i < w.N && placed < 12; i += 37)
                {
                    int x = i % w.Width, y = i / w.Width;
                    if (w.BuildingAt[i] != 0 || w.Path[i] != 0 || w.IsWater(i) || !w.InBounds(x + 1, y)) continue;
                    if (!w.Carries(TransitKind.Bus, w.Idx(x + 1, y))) continue;
                    w.Enqueue(new PlaceBuildingAction(x, y, "bus-stop"));
                    placed++;
                }
            }
            if (m == 14)
            {
                var home = w.Buildings.First(b => b is { Residents: > 0 })!;
                w.Enqueue(new TriggerDisasterAction(DisasterKind.Fire, home.X, home.Y));
                w.Enqueue(new TriggerDisasterAction(DisasterKind.Earthquake, home.X + 10, home.Y));
            }
            // Check mid-month too, not only right after the monthly recount.
            w.Run(17);
            Assert.Empty(w.Accounts());
            w.Run(13);
            Assert.Empty(w.Accounts());
        }
        Assert.True(w.Population > 0);
    }

    [Fact]
    public void FundsMoveByExactlyTheBudget()
    {
        var w = Fixture.Starter(32);
        int budgets = 0;
        for (int d = 0; d < 400; d++)
        {
            if (d == 100) w.Enqueue(new TakeLoanAction(5_000));
            if (d == 130) w.Enqueue(new PlacePathAction(0, 0, 5, 0, "street"));
            long before = w.Funds;
            w.Step();
            // Every dollar is either a one-off (building, a loan, a grant) or a budget line.
            long budget = w.BudgetRan ? w.LastIncome - w.LastExpenses : 0;
            Assert.Equal(before + w.OneOffs + budget, w.Funds);
            if (w.BudgetRan) budgets++;
        }
        Assert.Equal(13, budgets);
    }

    [Fact]
    public void CitizensAddUpAndLifeGoesOn()
    {
        var w = Fixture.Starter(33);
        w.Run(360);
        var home = w.Buildings.First(b => b is { Residents: > 0, Operating: true } && b.Assignments.Count > 0)!;
        var people = w.CitizensOf(home);
        Assert.Equal(home.Residents, people.Count);
        Assert.Equal(home.Employed, people.Count(c => c.Role == CitizenRole.Worker));
        Assert.All(people.Where(c => c.Role == CitizenRole.Worker), c => Assert.InRange(c.Age, 18, 64));
        Assert.All(people, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        // Over ten years some of them move on and new people arrive.
        var ids = w.Buildings.Where(b => b is { Residents: > 0, Operating: true }).SelectMany(b => w.CitizensOf(b!)).Select(c => c.Id).ToHashSet();
        w.Run(360 * 3);
        var later = w.Buildings.Where(b => b is { Residents: > 0, Operating: true }).SelectMany(b => w.CitizensOf(b!)).Select(c => c.Id).ToHashSet();
        Assert.True(later.Except(ids).Any(), "nobody new moved in");
        Assert.True(later.Intersect(ids).Any(), "everyone moved away");
    }

    [Fact]
    public void AgentTiersDoNotChangeTheCity()
    {
        var a = Fixture.Starter(34);
        var b = Fixture.Starter(34);
        var full = new AgentView(a, new AgentTiers(100_000, 0));
        var none = new AgentView(b, new AgentTiers(0, 0));
        for (int d = 0; d < 400; d++)
        {
            a.Step(); full.Daily();
            b.Step(); none.Daily();
        }
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.True(full.Trips.Count > 0);
        Assert.Empty(none.Trips);
        var t = full.Trips[0];
        Assert.Equal(AgentTier.Full, full.TierOf(t.Id));
        Assert.True(full.StatsOf(t.Id)!.Days > 0);
        Assert.NotEmpty(t.Cells);
    }

    [Fact]
    public void AnAgentsCommuteAgreesWithItsHousehold()
    {
        var w = Fixture.Starter(36);
        w.Run(360);
        var view = new AgentView(w, new AgentTiers(100_000, 0));
        view.Select();
        var minutes = w.Buildings.Where(b => b is { Residents: > 0, Operating: true })
            .SelectMany(h => w.CitizensOf(h!)).ToDictionary(c => c.Id, c => c.CommuteMinutes);
        Assert.NotEmpty(view.Trips);
        int same = view.Trips.Count(t => view.MinutesOf(t) == minutes[t.Id]);
        Assert.True(same * 100 >= view.Trips.Count * 95, $"{same} of {view.Trips.Count} agree");
    }

    [Fact]
    public void TiersFollowTheLimits()
    {
        var w = Fixture.Starter(35);
        w.Run(360);
        var view = new AgentView(w, new AgentTiers(50, 200));
        view.Select();
        Assert.Equal(50, view.Trips.Count);
        Assert.Equal(200, view.LightCount);
        Assert.Equal(w.Population, view.Citizens);
        int full = 0, light = 0;
        foreach (var home in w.Buildings.Where(b => b is { Residents: > 0, Operating: true }))
            foreach (var c in w.CitizensOf(home!))
                switch (view.TierOf(c.Id)) { case AgentTier.Full: full++; break; case AgentTier.Light: light++; break; }
        Assert.Equal(50, full);
        Assert.Equal(200, light);
    }
}
