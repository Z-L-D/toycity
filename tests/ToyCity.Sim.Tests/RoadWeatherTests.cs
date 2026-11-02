using ToyCity.Emergency;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.2, part 4: weather on the roads, from the showers to the crews.</summary>
public class RoadWeatherTests
{
    static int TierOf(World w, int i) => w.Content.Profiles[w.Path[i] - 1].WinterTier;

    static int FirstCell(World w, Func<int, bool> pick)
    {
        for (int i = 0; i < w.Path.Length; i++) if (w.Path[i] != 0 && pick(i)) return i;
        return -1;
    }

    static string SurfaceId(World w, int i) => w.PathSurface[i] > 0 ? w.Content.Surfaces[w.PathSurface[i] - 1].Id : "";

    /// <summary>Steps until <paramref name="done"/>, at most <paramref name="max"/> steps.</summary>
    static void RunUntil(World w, Func<bool> done, int max)
    {
        for (int k = 0; k < max && !done(); k++) w.Step();
    }

    [Fact]
    public void ADryCityPlaysAsItAlwaysDid()
    {
        var w = Fixture.Starter(42);
        w.Run(40);
        Assert.True(w.RoadsUsual);
        Assert.Equal("", w.RoadWeatherText());
        for (int i = 0; i < w.Path.Length; i++) if (w.Path[i] != 0) Assert.Equal(100, w.RoadSharePct(i));
        // The crews cost a little each month, and show on the budget.
        Assert.Contains(w.LastBudget, l => l.Label == "Road crews" && l.Amount < 0);
    }

    [Fact]
    public void ShowersFallOnSomeHoursOfTheClockMoreTheStrongerTheFront()
    {
        int Hours(int strength) => Enumerable.Range(0, 400).Count(h => World.ShowerHour(h, WeatherKind.Rain, strength));
        int light = Hours(1), heavy = Hours(3);
        Assert.InRange(light, 160, 280);
        Assert.InRange(heavy, 340, 400);
        // The same hours over every city of a region: nothing but the hour and the front decides.
        Assert.Equal(World.ShowerHour(77, WeatherKind.Snow, 2), World.ShowerHour(77, WeatherKind.Snow, 2));
    }

    [Fact]
    public void SnowBuildsUpWhereTheCrewsDontReachAndTheyClearTheBiggerRoadsFirst()
    {
        // Light snow: crews on full budget keep up; with none, it lies on every road.
        int Lying(int crews)
        {
            var w = Fixture.Starter(42);
            w.Run(20);
            w.Enqueue(new SetRoadCrewsAction(crews));
            w.Enqueue(new WeatherAction(WeatherKind.Snow, 1));
            for (int k = 0; k < 20; k++) w.Step();
            return w.RoadCover[1];  // the starter town's roads
        }
        int cleared = Lying(100), unploughed = Lying(0);
        Assert.True(unploughed > 300, $"{unploughed} of 1000 with no crews");
        Assert.True(cleared < unploughed / 3, $"{cleared} with crews against {unploughed} without");

        // A blizzard is more than the crews can clear: they hold the roads, and the streets fill.
        var (b, x, y) = Fixture.FlatSite();
        b.Enqueue(new PlacePathAction(x + 2, y + 2, x + 20, y + 2, "road"));
        b.Enqueue(new PlacePathAction(x + 2, y + 8, x + 20, y + 8, "street"));
        b.Step();
        Assert.Equal(1, TierOf(b, b.Idx(x + 5, y + 2)));
        Assert.Equal(2, TierOf(b, b.Idx(x + 5, y + 8)));
        b.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
        RunUntil(b, () => b.RoadCover[2] >= 900, 60);
        Assert.True(b.RoadCover[2] >= 900, $"streets {b.RoadCover[2]}");
        Assert.True(b.RoadCover[1] < b.RoadCover[2], $"roads {b.RoadCover[1]}, streets {b.RoadCover[2]}");
        Assert.True(b.RoadBanked[1] > 0, "ploughed snow on the roads' kerbs");
        Assert.Contains(b.Headlines, h => h.Text.Contains("snowed in"));
        Assert.Contains("snow on the streets", b.RoadWeatherText().ToLowerInvariant());
    }

    [Fact]
    public void EachSurfaceCarriesItsShareAndTrafficFeelsIt()
    {
        var w = Fixture.Starter(42);
        w.Run(40);
        // The starter town's roads are asphalt, crews' second tier.
        int street = FirstCell(w, i => TierOf(w, i) == 1 && SurfaceId(w, i) == "asphalt" && w.Trips[i] > 0);
        Assert.True(street >= 0, "a busy asphalt road");
        int dryCost = w.TravelCost[street], dryLevel = w.Traffic[street];
        w.Enqueue(new SetRoadCrewsAction(0));
        w.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
        RunUntil(w, () => w.RoadCover[1] >= 1000, 80);
        // The snow stops.
        w.Enqueue(new WeatherAction(WeatherKind.Clear, 0));
        w.Step();
        // An asphalt road carries 35% under full snow; a step of January sun has melted a little of it.
        Assert.InRange(w.RoadCover[1], 950, 1000);
        Assert.Equal(100 - 65 * w.RoadCover[1] / 1000, w.RoadSharePct(street));
        Assert.InRange(w.RoadSharePct(street), 35, 40);
        Assert.True(w.TravelCost[street] > dryCost, $"travel {w.TravelCost[street]} against {dryCost} dry");
        Assert.True(w.Traffic[street] > dryLevel, $"level {w.Traffic[street]} against {dryLevel} dry");
        Assert.Contains($"{w.RoadSharePct(street)}%", w.Why(street % w.Width, street / w.Width).Summary);
        // January's sun melts it, slowly.
        int was = w.RoadCover[1];
        w.Run(10);
        Assert.True(w.RoadCover[1] < was);
    }

    [Fact]
    public void RainLeavesDirtMuddyLongAfterThePavedRoadsDry()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x + 2, y + 2, x + 12, y + 2, "dirt-road"));
        w.Enqueue(new PlacePathAction(x + 2, y + 6, x + 12, y + 6, "street"));
        w.Step();
        int dirt = w.Idx(x + 6, y + 2), paved = w.Idx(x + 6, y + 6);
        Assert.Equal("dirt", SurfaceId(w, dirt));
        w.Enqueue(new WeatherAction(WeatherKind.Rain, 2));
        RunUntil(w, () => w.RoadMud >= 900, 60);
        w.Enqueue(new WeatherAction(WeatherKind.Clear, 0));
        // A few clock hours on (48 minutes a step at a month a clock day), asphalt is dry and the dirt still mud.
        w.Run(5);
        Assert.Equal(0, w.RoadWet);
        Assert.Equal(100, w.RoadSharePct(paved));
        Assert.True(w.RoadSharePct(dirt) < 60, $"dirt carries {w.RoadSharePct(dirt)}%");
        RunUntil(w, () => w.RoadMud == 0, 200);
        Assert.Equal(100, w.RoadSharePct(dirt));
    }

    [Fact]
    public void FreezingRainBringsMoreCrashCallsAndKeepsPeopleHome()
    {
        int Crashes(WeatherKind kind)
        {
            SideGameCatalog.Register(EmergencyGame.Id, () => new EmergencyGame());
            var w = Fixture.Starter(715);
            w.Run(60);
            w.Enqueue(new AttachSideGameAction(EmergencyGame.Id));
            w.Enqueue(new SetRoadCrewsAction(0));
            if (kind != WeatherKind.Clear) w.Enqueue(new WeatherAction(kind, 3));
            w.Step();
            var seen = new HashSet<long>(w.RecentIncidents.Select(i => i.Id));
            int crashes = 0;
            for (int k = 0; k < 40; k++)
            {
                w.Step();
                foreach (var i in w.RecentIncidents)
                    if (seen.Add(i.Id) && i.Source == "city" && i.Kind == "injury" && i.Cause.Contains("crash")) crashes++;
                if (kind == WeatherKind.Ice && k == 20) Assert.True(w.StayHomePct > 0);
            }
            return crashes;
        }
        int dry = Crashes(WeatherKind.Clear), icy = Crashes(WeatherKind.Ice);
        Console.WriteLine($"ROADS crashes in 40 turns: {dry} dry, {icy} in an ice storm");
        Assert.True(icy > dry, $"{icy} crashes on ice against {dry} dry");
    }

    [Fact]
    public void TheRoadsWeatherAndTheCrewsBudgetAreSaved()
    {
        var w = Fixture.Starter(42);
        w.Run(10);
        w.Enqueue(new SetRoadCrewsAction(40));
        w.Enqueue(new WeatherAction(WeatherKind.Ice, 2));
        RunUntil(w, () => w.RoadCover[2] > 0, 30);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(40, back.RoadCrewFunding);
        Assert.Equal(w.RoadCover, back.RoadCover);
        Assert.Equal(w.RoadIce, back.RoadIce);
        Assert.Equal(w.RoadWet, back.RoadWet);
        Assert.Equal(w.StateHash(), back.StateHash());
        // Out of range is refused, and it's the budget's to set.
        Assert.NotNull(w.OutOfBounds(new SetRoadCrewsAction(250)));
    }
}
