using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 4.1, part 3: storm cells, the weather station and storms a player sets going.</summary>
public class StormTests(ITestOutputHelper output)
{
    static World City()
    {
        var w = World.Create(Fixture.Content, 42);
        w.Funds = 5_000_000;
        return w;
    }

    [Fact]
    public void TheRadarAgreesWithTheShowers()
    {
        // Over two days of rain at each strength: a cell over the middle of the city in every shower hour, none in the others.
        foreach (int strength in new[] { 1, 2, 3 })
        {
            int agree = 0, hours = 0;
            for (long hour = 24 * 100; hour < 24 * 102; hour++)
            {
                long minute = hour * 60 + 30;
                var cells = World.StormCells(minute, WeatherKind.Rain, strength, Array.Empty<World.PlacedStorm>(), 6);
                bool wet = World.DbzAt(cells, 0, 0) >= 15;
                bool shower = World.ShowerHour(hour, WeatherKind.Rain, strength);
                hours++;
                if (wet == shower) agree++;
                // Through the whole hour, not only in its middle.
                if (shower)
                    foreach (int m in new[] { 2, 58 })
                        Assert.True(World.DbzAt(World.StormCells(hour * 60 + m, WeatherKind.Rain, strength, Array.Empty<World.PlacedStorm>(), 6), 0, 0) >= 15, $"hour {hour} minute {m}");
            }
            output.WriteLine($"strength {strength}: radar and showers agree {agree} of {hours} hours");
            Assert.Equal(hours, agree);
        }
        // And there's more weather about than over the city alone.
        var some = World.StormCells(24 * 100 * 60, WeatherKind.Rain, 2, Array.Empty<World.PlacedStorm>(), 6);
        Assert.True(some.Count >= 4);
        // A clear day has none; a heatwave's afternoon can have thunderstorms, but never over the city.
        Assert.Empty(World.StormCells(24 * 100 * 60, WeatherKind.Clear, 1, Array.Empty<World.PlacedStorm>(), 6));
        int thunder = 0;
        for (long hour = 24 * 100; hour < 24 * 110; hour++)
        {
            var cells = World.StormCells(hour * 60, WeatherKind.Heat, 2, Array.Empty<World.PlacedStorm>(), 7);
            thunder += cells.Count(c => c.Kind == StormKind.Thunder);
            Assert.True(World.DbzAt(cells, 0, 0) < 15);
        }
        Assert.True(thunder > 10);
    }

    [Fact]
    public void TheStationReadsTheWeather()
    {
        var w = City();
        var calm = w.Station();
        output.WriteLine($"clear January: {calm}");
        Assert.InRange(calm.TempC, -15, 8);
        Assert.InRange(calm.Humidity, 5, 100);
        Assert.Equal(0, calm.RateMmH);
        w.Enqueue(new WeatherAction(WeatherKind.Rain, 3));
        w.Step();
        long hour = Enumerable.Range(0, 48).Select(h => w.ClockMinute / 60 + h).First(h => World.ShowerHour(h, WeatherKind.Rain, 3));
        var wet = w.Station(hour * 60 + 30);
        output.WriteLine($"heavy rain: {wet}");
        Assert.True(wet.RateMmH > 1);
        Assert.True(wet.Humidity >= 85);
        Assert.True(wet.PressureHpa < calm.PressureHpa);
        Assert.InRange(wet.WindFromDeg, 0, 359);
        Assert.True(w.Station(hour * 60 + 59).TodayMm >= w.Station(hour * 60 + 10).TodayMm);
    }

    [Fact]
    public void APlacedStormMovesAcrossAndRainsWhereItPasses()
    {
        var w = City();
        // Midsummer and a clear sky, so what falls is the storm's rain; set going 30 km west of the middle, heading east at 40 km/h.
        while (w.Month != 7) w.Step();
        w.Enqueue(new WeatherAction(WeatherKind.Clear, 1));
        w.Step();
        Assert.False(w.Falling);
        int x = w.Width / 2 - (int)(30 / World.CellKm);
        w.Enqueue(new PlaceStormAction(x, w.HeightCells / 2, 0, 40, 2));
        w.Step();
        Assert.Single(w.PlacedStorms);
        var start = w.StormCells(w.PlacedStorms[0].StartMinute).Single();
        bool rained = w.Falling && w.FallingKind == WeatherKind.Rain;
        long until = w.ClockMinute + 4 * 60;
        while (w.ClockMinute < until && !rained) { w.Step(); rained |= w.Falling && w.FallingKind == WeatherKind.Rain; }
        Assert.True(rained);
        Assert.True(w.RoadWet > 0);
        var later = w.StormCells(w.ClockMinute).Single();
        Assert.True(later.X > start.X);
        // Saved with the city, and gone once it has passed.
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.PlacedStorms, back.PlacedStorms);
        Assert.Equal(w.StateHash(), back.StateHash());
        while (w.PlacedStorms.Count > 0) w.Step();
        Assert.Empty(w.StormCells(w.ClockMinute));
    }

    [Fact]
    public void AStormIsTheMayorsToSetGoing()
    {
        var w = City();
        w.Enqueue(new PlaceStormAction(10, 10, 90, 300, 2));
        w.Step();
        Assert.Empty(w.PlacedStorms);
        Assert.Contains(w.Notices, n => n.Text.Contains("120 km/h"));
    }
}
