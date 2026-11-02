using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>An RGB colour, 0 to 1 each, in sRGB.</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public static Rgb Mix(Rgb a, Rgb b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    public Rgb Times(float k) => new(R * k, G * k, B * k);
    /// <summary>How warm the colour is: red over blue.</summary>
    public float Warmth => R - B;
}

/// <summary>
/// The light over the city at one moment (Beta 3.2, Part 6): where the sun is and the colour and strength of its
/// light, the sky and the shade, the haze, and how dark it is for the lights to come on.
/// </summary>
/// <param name="Elevation">The sun's height above the horizon, in degrees (negative below it).</param>
/// <param name="Azimuth">The sun's bearing, in degrees clockwise from north.</param>
/// <param name="Night">0 in daylight to 1 at full dark: how much the windows, street lights and headlights show.</param>
/// <param name="Overcast">0 clear to 1 under heavy rain or snow.</param>
/// <param name="Haze">Distance haze as a density per cell of distance.</param>
public readonly record struct Light(
    float Elevation, float Azimuth, Rgb Sun, float SunEnergy, Rgb Ambient, float AmbientEnergy,
    Rgb Sky, Rgb Horizon, float Haze, float Night, float Overcast)
{
    /// <summary>The direction the sunlight travels, in the map's frame: x east, y up, z south.</summary>
    public (float X, float Y, float Z) Direction
    {
        get
        {
            // The light comes from the sun, or from the moon opposite it at night, never from under the ground.
            float el = Elevation > -3 ? Math.Max(Elevation, 4f) : Math.Max(-Elevation, 20f);
            float az = Elevation > -3 ? Azimuth : Azimuth + 180;
            double e = el * Math.PI / 180, a = az * Math.PI / 180;
            float east = (float)(Math.Sin(a) * Math.Cos(e)), north = (float)(Math.Cos(a) * Math.Cos(e)), up = (float)Math.Sin(e);
            // From the sun towards the ground: the opposite of where it is.
            return (-east, -up, north);
        }
    }
}

/// <summary>
/// The light at an hour of the day clock in a month of the calendar and under the day's weather: the sun's path
/// for a city at 45° north, its colour through dawn, golden hour, dusk and night, and the sky dimmed and hazed by
/// rain and snow. Both clients light the city from it.
/// </summary>
public static class Daylight
{
    public const double Latitude = 45;

    /// <summary>
    /// The light a player can pin instead of following the day clock (6.1), as the sun's elevation and bearing, so a
    /// pinned golden hour is golden in any month.
    /// </summary>
    public static readonly (string Name, float Elevation, float Azimuth)[] Pins =
    {
        ("Follow the clock", 0, 0), ("Morning", 24, 115), ("Noon", 58, 180), ("Golden hour", 6, 255), ("Dusk", -3, 275), ("Night", -35, 0),
    };

    static readonly Rgb Noon = new(1.00f, 0.97f, 0.92f), Golden = new(1.00f, 0.66f, 0.38f), Red = new(0.95f, 0.42f, 0.28f),
        Moon = new(0.55f, 0.65f, 0.95f);
    static readonly Rgb SkyDay = new(0.32f, 0.54f, 0.86f), SkyDusk = new(0.36f, 0.38f, 0.58f), SkyNight = new(0.04f, 0.06f, 0.13f),
        HorizonDay = new(0.74f, 0.82f, 0.90f), HorizonGolden = new(0.96f, 0.74f, 0.52f), HorizonNight = new(0.08f, 0.10f, 0.18f),
        Grey = new(0.62f, 0.65f, 0.68f), GreyNight = new(0.07f, 0.08f, 0.10f);

    /// <summary>The sun's elevation and azimuth, in degrees, at an hour (0-24) of a month (1-12).</summary>
    public static (double Elevation, double Azimuth) Sun(double hour, int month)
    {
        // The middle of the month, as a day of the year, and the sun's declination then.
        int day = Math.Clamp(month, 1, 12) * 30 - 15;
        double decl = 23.44 * Math.Sin(2 * Math.PI * (day - 81) / 365.0) * Math.PI / 180;
        double lat = Latitude * Math.PI / 180, h = (hour - 12) * 15 * Math.PI / 180;
        double sinEl = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(h);
        double el = Math.Asin(Math.Clamp(sinEl, -1, 1));
        double cosAz = (Math.Sin(decl) - Math.Sin(el) * Math.Sin(lat)) / (Math.Cos(el) * Math.Cos(lat));
        double az = Math.Acos(Math.Clamp(cosAz, -1, 1)) * 180 / Math.PI;
        if (h > 0) az = 360 - az;
        return (el * 180 / Math.PI, az);
    }

    /// <summary>
    /// How overcast the day is, 0 to 1, from the city's weather: showers falling now darken it most, a day of rain
    /// or snow between showers less, and heavy weather or storms more.
    /// </summary>
    public static float Overcast(CityStats s)
    {
        string kind = s.Roads?.WeatherKind ?? "";
        if (kind is not ("rain" or "snow" or "ice")) return 0;
        string text = s.Weather.ToLowerInvariant();
        bool heavy = text.Contains("heavy") || text.Contains("storm") || text.Contains("blizzard");
        bool falling = s.Roads?.Falling ?? false;
        return (falling ? 0.75f : 0.4f) + (heavy ? 0.25f : 0f);
    }

    /// <summary>The light at an hour (0-24) of a month, under weather this overcast.</summary>
    public static Light At(double hour, int month, float overcast = 0)
    {
        var (el, az) = Sun(hour, month);
        return FromSun((float)el, (float)az, overcast);
    }

    /// <summary>The light with the sun this high (degrees, negative below the horizon) on this bearing, under weather this overcast.</summary>
    public static Light FromSun(float el, float azD, float overcast = 0)
    {
        float oc = Math.Clamp(overcast, 0f, 1f);
        // Low sun is warm: red at the horizon, golden to about 12°, white above 30°.
        var sun = el < 2 ? Rgb.Mix(Red, Golden, (el + 4) / 6)
            : el < 14 ? Rgb.Mix(Golden, Noon, (el - 2) / 12f * 0.55f)
            : Rgb.Mix(Rgb.Mix(Golden, Noon, 0.55f), Noon, (el - 14) / 20);
        float day = Smooth(-6, 10, el), dark = 1 - Smooth(-10, 4, el);
        float energy = 0.12f + 0.9f * Smooth(-4, 12, el) + 0.18f * Smooth(12, 40, el);
        if (el < -3) { sun = Moon; energy = 0.12f; }
        var sky = el > 6 ? Rgb.Mix(SkyDusk, SkyDay, (el - 6) / 14) : Rgb.Mix(SkyNight, SkyDusk, (el + 10) / 16);
        var horizon = el > 14 ? Rgb.Mix(HorizonGolden, HorizonDay, (el - 14) / 12)
            : el > -2 ? HorizonGolden : Rgb.Mix(HorizonNight, HorizonGolden, (el + 10) / 8);
        // Shade lit by the sky: cool blue by day, dim and blue at night.
        var ambient = Rgb.Mix(new Rgb(0.20f, 0.24f, 0.40f), new Rgb(0.72f, 0.78f, 0.90f), day);
        float ambientEnergy = 0.18f + 0.40f * day;
        // Clouds: the sun dims and greys, the shade evens out, and the haze closes in.
        var grey = Rgb.Mix(GreyNight, Grey, day);
        sun = Rgb.Mix(sun, grey, oc * 0.7f);
        energy *= 1 - 0.7f * oc;
        ambient = Rgb.Mix(ambient, grey, oc * 0.6f);
        ambientEnergy *= 1 + 0.3f * oc;
        sky = Rgb.Mix(sky, grey, oc * 0.85f);
        horizon = Rgb.Mix(horizon, grey, oc * 0.9f);
        // Haze thickens towards dawn and dusk (morning mist), and in rain and snow.
        float haze = 0.0012f + 0.0015f * (1 - Smooth(0, 20, Math.Abs(el))) + 0.01f * oc;
        float night = Math.Clamp(dark + 0.35f * oc * (1 - Smooth(5, 25, el)), 0, 1);
        return new Light(el, azD, sun, energy, ambient, ambientEnergy, sky, horizon, haze, night, oc);
    }

    /// <summary>The light now: at the day clock's hour, or as one of <see cref="Pins"/> when <paramref name="pin"/> is above 0.</summary>
    public static Light Now(CityStats s, long clockMinute, int pin = 0)
    {
        if (pin > 0 && pin < Pins.Length) return FromSun(Pins[pin].Elevation, Pins[pin].Azimuth, Overcast(s));
        return At(clockMinute % GameClock.MinutesPerDay / 60.0, s.Month == 0 ? 6 : s.Month, Overcast(s));
    }

    /// <summary>The index in <see cref="Pins"/> of a pin by name.</summary>
    public static int Pin(string name) => Array.FindIndex(Pins, p => p.Name == name);

    static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
