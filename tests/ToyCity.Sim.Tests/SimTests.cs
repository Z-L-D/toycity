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

        w.Enqueue(new PlaceBuildingAction(plant.X, plant.Y, "coal-plant"));
        w.Run(30 * 6);
        Assert.True(w.Buildings.Count(b => b is { Abandoned: true }) < 5);
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
