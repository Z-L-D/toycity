namespace ToyCity.Sim.Tests;

/// <summary>Beta 4, part 4: snow on the ground.</summary>
public class SnowTests
{
    [Fact]
    public void SnowLiesInTheShowersAndMeltsSlowerInWinter()
    {
        var w = Fixture.Starter(42);
        w.Run(10);
        Assert.Equal(0, w.GroundSnowMm);
        w.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
        for (int k = 0; k < 400 && w.GroundSnowMm < 200; k++) w.Step();
        Assert.InRange(w.GroundSnowMm, 200, World.MaxSnowMm);
        // Freezing rain glazes it over and leaves the depth as it is.
        w.Enqueue(new WeatherAction(WeatherKind.Ice, 2));
        w.Step();
        int glazed = w.GroundSnowMm;
        for (int k = 0; k < 20; k++) w.Step();
        Assert.True(w.GroundSnowMm == glazed, $"{glazed} mm went to {w.GroundSnowMm} under freezing rain");

        // The sky clears: it melts, faster in a warm month than in deep winter, fastest in rain.
        int Melted(int month, WeatherKind sky)
        {
            var m = Fixture.Starter(42);
            while (m.Month != month) m.Step();
            m.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
            for (int k = 0; k < 400 && m.GroundSnowMm < 300; k++) m.Step();
            m.Enqueue(new WeatherAction(sky, sky == WeatherKind.Clear ? 0 : 2));
            m.Step();
            int before = m.GroundSnowMm;
            for (int k = 0; k < 10; k++) m.Step();
            return before - m.GroundSnowMm;
        }
        int winter = Melted(1, WeatherKind.Clear), spring = Melted(5, WeatherKind.Clear), rain = Melted(1, WeatherKind.Rain);
        Assert.True(winter < spring, $"winter melts {winter} mm, spring {spring} mm");
        Assert.True(winter < rain, $"dry winter melts {winter} mm, rain {rain} mm");
    }

    [Fact]
    public void SnowSavesAndLoads()
    {
        var w = Fixture.Starter(7);
        w.Enqueue(new WeatherAction(WeatherKind.Snow, 2));
        for (int k = 0; k < 400 && w.GroundSnowMm < 50; k++) w.Step();
        Assert.True(w.GroundSnowMm >= 50);
        var back = World.FromSnapshot(w.Content, w.Snapshot());
        Assert.Equal(w.GroundSnowMm, back.GroundSnowMm);
        Assert.Equal(w.StateHash(), back.StateHash());
    }

    [Fact]
    public void SnowLiesDeeperHighUpAndFacingNorthAndNotOnCliffs()
    {
        float flat = SnowDepth.At(100, 0, 0, 0);
        Assert.Equal(100, flat, 3);
        Assert.True(SnowDepth.At(100, 100, 0, 0) > flat);
        Assert.True(SnowDepth.At(100, -50, 0, 0) < flat);
        Assert.True(SnowDepth.At(100, 0, 0.3f, 1) > SnowDepth.At(100, 0, 0.3f, -1));
        Assert.Equal(0, SnowDepth.At(100, 0, 1.2f, 0));
        Assert.Equal(0, SnowDepth.At(0, 300, 0, 1));
    }

    [Fact]
    public void ASummerThawsTheSnowEvenOnAFastCalendar()
    {
        var w = Fixture.Starter();
        for (int k = 0; k < 400 && w.Month != 6; k++) w.Step();
        w.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
        for (int k = 0; k < 300 && w.GroundSnowMm < 100; k++) w.Step();
        Assert.True(w.GroundSnowMm >= 100, $"{w.GroundSnowMm} mm");
        // A year a clock day: the day clock barely moves, but the calendar's days are summer days.
        w.Enqueue(new SetPaceAction(720));
        w.Enqueue(new WeatherAction(WeatherKind.Clear, 0));
        w.Run(10);
        Assert.Equal(0, w.GroundSnowMm);
    }

    [Fact]
    public void ASummerThawsTheRoadsEvenOnAFastCalendar()
    {
        // Beta 4.1, part 4: the snow on the roads and in the banks the crews pushed thaws with the calendar too.
        var w = Fixture.Starter();
        w.Enqueue(new SetRoadCrewsAction(0));
        for (int k = 0; k < 400 && w.Month != 6; k++) w.Step();
        w.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
        for (int k = 0; k < 300 && w.RoadCover.Max() < 800; k++) w.Step();
        Assert.True(w.RoadCover.Max() >= 800, $"cover {string.Join(",", w.RoadCover)}");
        w.Enqueue(new SetPaceAction(720));
        w.Enqueue(new WeatherAction(WeatherKind.Clear, 0));
        w.Run(6);
        Assert.All(w.RoadCover, c => Assert.Equal(0, c));
        Assert.All(w.RoadBanked, c => Assert.Equal(0, c));
        Assert.True(w.RoadsUsual || w.RoadWet > 0 || w.RoadMud > 0);
        // In midwinter it stays until the crews or the day clock's sun clear it.
        var winter = Fixture.Starter();
        winter.Enqueue(new SetRoadCrewsAction(0));
        winter.Enqueue(new WeatherAction(WeatherKind.Snow, 3));
        for (int k = 0; k < 300 && winter.RoadCover.Max() < 800; k++) winter.Step();
        winter.Enqueue(new SetPaceAction(720));
        winter.Enqueue(new WeatherAction(WeatherKind.Clear, 0));
        winter.Run(3);
        Assert.True(winter.RoadCover.Max() > 0);
    }
}
