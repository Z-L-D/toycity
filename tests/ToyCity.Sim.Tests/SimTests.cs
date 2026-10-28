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
        Assert.Equal(15, c.Profiles.Count);
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
            new PaintZoneAction(new CellRect(1, 2, 3, 4), Zone.Commercial, Level.High),
            new PlaceBuildingAction(5, 6, "clinic"),
            new BulldozeAction(new CellRect(1, 1, 2, 2), true),
            new TerraformAction(new CellRect(0, 0, 9, 9), TerraformMode.Lower),
            new SetTaxAction(Zone.Industrial, Level.Medium, 125),
            new SetFundingAction(3, 80),
            new TakeLoanAction(25_000),
            new PlaceUtilityAction(1, 1, 8, 9, UtilityKind.WaterPipe),
            new SetOrdinanceAction("recycling", true),
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
        Assert.Equal(15, f.Catalog.Profiles.Count);
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
