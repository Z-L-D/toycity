using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 3.2, Part 6: the light over the city through the day, the year and the weather.</summary>
public class DaylightTests
{
    [Fact]
    public void TheSunRisesInTheEastPeaksInTheSouthAndSetsInTheWest()
    {
        var (dawnEl, dawnAz) = Daylight.Sun(6.5, 6);
        var (noonEl, noonAz) = Daylight.Sun(12, 6);
        var (duskEl, duskAz) = Daylight.Sun(17.5, 6);
        var (nightEl, _) = Daylight.Sun(0, 6);
        Assert.InRange(dawnAz, 45, 135);
        Assert.InRange(noonAz, 175, 185);
        Assert.InRange(duskAz, 225, 315);
        // At 45° north: about 68° up at midsummer noon, 21° at midwinter noon; below the horizon at midnight.
        Assert.InRange(noonEl, 64, 70);
        Assert.InRange(Daylight.Sun(12, 12).Elevation, 19, 25);
        Assert.True(nightEl < -15);
        Assert.True(dawnEl > 0 && duskEl > 0);
    }

    [Fact]
    public void TheLightWarmsAtGoldenHourAndTheLightsComeOnAtNight()
    {
        var noon = Daylight.At(12.5, 6);
        var golden = Daylight.At(19.2, 6);
        var night = Daylight.At(23, 6);
        Assert.InRange(golden.Elevation, 0, 12);
        Assert.True(golden.Sun.Warmth > noon.Sun.Warmth + 0.2f, $"{golden.Sun} vs {noon.Sun}");
        Assert.True(golden.SunEnergy < noon.SunEnergy);
        Assert.Equal(0, noon.Night, 2);
        Assert.True(night.Night > 0.95f);
        Assert.True(night.SunEnergy < 0.2f && night.Sun.Warmth < 0, "moonlight is dim and blue");
        Assert.True(night.Sky.G < noon.Sky.G / 4);
        // The light always comes down onto the ground, from the sun by day and the moon by night.
        Assert.All(new[] { noon, golden, night, Daylight.At(5, 1) }, l => Assert.True(l.Direction.Y < -0.05f));
        // Noon light travels north, away from the sun in the south (z grows to the south).
        Assert.True(noon.Direction.Z < 0);
        // The haze thickens at dawn and dusk.
        Assert.True(golden.Haze > noon.Haze);
    }

    [Fact]
    public void RainAndSnowDimTheDayGreyTheSkyAndThickenTheHaze()
    {
        var clear = Daylight.At(12, 4);
        var storm = Daylight.At(12, 4, 1);
        Assert.True(storm.SunEnergy < clear.SunEnergy * 0.4f);
        Assert.True(storm.Haze > clear.Haze * 3);
        Assert.True(Math.Abs(storm.Sky.B - storm.Sky.R) < Math.Abs(clear.Sky.B - clear.Sky.R) / 2, "a grey sky");
        Assert.True(Daylight.At(15.8, 11, 1).Night > Daylight.At(15.8, 11).Night + 0.1f, "a stormy dusk turns the lights on early");

        var s = new CityStats { Weather = "🌧 Heavy rain", Month = 4, Roads = new RoadWeather { WeatherKind = "rain", Falling = true } };
        Assert.Equal(1f, Daylight.Overcast(s));
        s.Roads.Falling = false;
        s.Weather = "🌧 Rain";
        Assert.Equal(0.4f, Daylight.Overcast(s));
        Assert.Equal(0f, Daylight.Overcast(new CityStats { Weather = "🔥 Heat wave", Roads = new RoadWeather { WeatherKind = "heat" } }));
        Assert.Equal(0f, Daylight.Overcast(new CityStats()));
    }

    [Fact]
    public void APinnedLightWinsOverTheClockInAnyMonth()
    {
        long midnight = 3 * GameClock.MinutesPerDay;
        foreach (int month in new[] { 1, 6 })
        {
            var s = new CityStats { Month = month };
            Assert.True(Daylight.Now(s, midnight).Night > 0.9f);
            Assert.Equal(0, Daylight.Now(s, midnight, Daylight.Pin("Noon")).Night, 2);
            var golden = Daylight.Now(s, midnight, Daylight.Pin("Golden hour"));
            Assert.True(golden.Sun.Warmth > 0.4f && golden.Night < 0.1f, $"{golden}");
            Assert.True(Daylight.Now(s, midnight, Daylight.Pin("Night")).Night > 0.95f);
        }
    }

    [Fact]
    public void HomesLightMoreWindowsThanWorkplacesAndDarkBuildingsNone()
    {
        var home = new BuildingState { Residents = 120, Powered = true, Zone = ZoneKind.ZoneResidential };
        var office = new BuildingState { Jobs = 100, Workers = 50, Powered = true, Zone = ZoneKind.ZoneCommercial };
        var school = new BuildingState { Powered = true };
        Assert.True(BuildingLife.LitShare(home) > BuildingLife.LitShare(office));
        Assert.True(BuildingLife.LitShare(school) > 0);
        Assert.Equal(0, BuildingLife.LitShare(home.Clone().Also(b => b.Powered = false)));
        Assert.Equal(0, BuildingLife.LitShare(home.Clone().Also(b => b.Abandoned = true)));
        Assert.Equal(0, BuildingLife.LitShare(new BuildingState { Zone = ZoneKind.ZoneResidential, Powered = true }));
        // A standing building carries it to the shader.
        var life = new BuildingLife().Of(home);
        Assert.Equal(BuildingLife.LitShare(home), life.Lit);
    }
}

static class Also_
{
    public static T Also<T>(this T t, Action<T> f) { f(t); return t; }
}
