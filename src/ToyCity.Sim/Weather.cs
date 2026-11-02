namespace ToyCity.Sim;

public enum WeatherKind : byte { Clear = 0, Rain = 1, Snow = 2, Heat = 3, Wind = 4, Ice = 5 }

/// <summary>
/// The region tells a city today's weather over it, when it changes. Logged, so a city's log replays it alone.
/// Strength runs 1 (light) to 3 (severe).
/// </summary>
public sealed record WeatherAction(WeatherKind Kind, int Strength) : SimAction;

public sealed partial class World
{
    /// <summary>Today's weather over the city, from the region. Saved.</summary>
    public WeatherKind Weather;
    public int WeatherStrength;

    /// <summary>"Heavy rain", "Heatwave", or "" when clear.</summary>
    public static string WeatherName(WeatherKind k, int s) => k switch
    {
        WeatherKind.Rain => s >= 3 ? "Storms" : s == 2 ? "Heavy rain" : "Rain",
        WeatherKind.Snow => s >= 3 ? "Blizzard" : s == 2 ? "Heavy snow" : "Snow",
        WeatherKind.Heat => s >= 3 ? "Heatwave" : s == 2 ? "Hot spell" : "Warm",
        WeatherKind.Wind => s >= 3 ? "Gale" : s == 2 ? "Strong wind" : "Breezy",
        WeatherKind.Ice => s >= 3 ? "Ice storm" : s == 2 ? "Freezing rain" : "Freezing drizzle",
        _ => "",
    };

    public static string WeatherIcon(WeatherKind k) => k switch
    {
        WeatherKind.Rain => "🌧", WeatherKind.Snow => "🌨", WeatherKind.Heat => "🔥", WeatherKind.Wind => "💨", WeatherKind.Ice => "🧊", _ => "",
    };

    int W(WeatherKind k) => Weather == k ? WeatherStrength : 0;

    /// <summary>Percent on power use: air conditioning in heat, heating in snow.</summary>
    int WeatherPowerPct => 100 + 10 * W(WeatherKind.Heat) + 8 * W(WeatherKind.Snow) + 8 * W(WeatherKind.Ice);
    /// <summary>Percent on water use: gardens and pools in heat, less in rain.</summary>
    int WeatherWaterPct => 100 + 15 * W(WeatherKind.Heat) - 10 * W(WeatherKind.Rain);
    /// <summary>Percent on how readily fire spreads: heat and wind fan it, rain and snow damp it.</summary>
    int WeatherFirePct => Math.Max(10, 100 + 25 * W(WeatherKind.Heat) + 25 * W(WeatherKind.Wind) - 25 * W(WeatherKind.Rain) - 30 * W(WeatherKind.Snow) - 25 * W(WeatherKind.Ice));
    /// <summary>Percent on visitors: fine weather brings them, rain and snow keep them home.</summary>
    int WeatherTourismPct => 100 + 5 * W(WeatherKind.Heat) - 12 * W(WeatherKind.Rain) - 8 * W(WeatherKind.Snow) - 12 * W(WeatherKind.Ice);

    void ApplyWeather(WeatherAction a)
    {
        var (was, wasS) = (Weather, WeatherStrength);
        Weather = a.Kind;
        WeatherStrength = a.Kind == WeatherKind.Clear ? 0 : Math.Clamp(a.Strength, 1, 3);
        // News when something severe rolls in; freezing rain makes the news at any strength past a drizzle.
        if ((WeatherStrength >= 3 || Weather == WeatherKind.Ice && WeatherStrength >= 2) && (was != Weather || wasS < 3))
        {
            string text = Weather switch
            {
                WeatherKind.Heat => $"Heatwave over {Name}: power and water use are up, and fires catch more easily.",
                WeatherKind.Snow => $"Blizzard over {Name}: heating pushes power use up, visitors stay away, and the streets wait for the ploughs.",
                WeatherKind.Rain => $"Storms over {Name}: visitors stay away, dirt roads turn to mud, but the fire risk is low.",
                WeatherKind.Ice => $"{WeatherName(Weather, WeatherStrength)} over {Name}: the roads ice over and crashes are up. Road crews salt the highways first.",
                _ => $"A gale over {Name}: fires spread fast while it blows.",
            };
            Notices.Add(new SimNotice(text, "warning"));
            AddHeadline(text, "warning");
        }
    }
}

/// <summary>
/// Region weather: fronts of rain, snow, heat and wind that drift across the region's tiles. A front is worked out
/// from the region's seed and the day alone, so weather needs no saving and every host computes the same sky.
/// </summary>
public sealed partial class Region
{
    /// <summary>A new front can start every this many days.</summary>
    public const int FrontPeriod = 12;
    /// <summary>The most days a front lasts.</summary>
    public const int FrontDays = 30;

    public readonly record struct Front(WeatherKind Kind, int Strength, long X, long Y, int Radius);

    static ulong Mix(ulong h)
    {
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
        return h;
    }

    /// <summary>The fronts over the region on a day, in region cells.</summary>
    public List<Front> Fronts(long tick)
    {
        var list = new List<Front>();
        long w = (long)Cols * TileSize, h = (long)Rows * TileSize;
        for (long slot = tick / FrontPeriod - FrontDays / FrontPeriod; slot <= tick / FrontPeriod; slot++)
        {
            if (slot < 0) continue;
            ulong r = Mix(Seed ^ ((ulong)slot * 0x9E3779B97F4A7C15UL) ^ 0xA5A5_5A5A_F00DUL);
            if (r % 100 >= 45) continue;  // most slots bring nothing
            long start = slot * FrontPeriod + (long)(r >> 8 & 7);
            int days = 8 + (int)(r >> 12 & 15) + (int)(r >> 16 & 7);
            if (tick < start || tick >= start + days) continue;
            int month = (int)(start / World.DaysPerMonth % 12);  // 0 = January
            bool winter = month is 11 or 0 or 1, summer = month is 5 or 6 or 7, shoulder = month is 2 or 10;
            int roll = (int)(r >> 20 & 0xFF) % 100;
            // Freezing rain comes in winter, and now and then as winter comes or goes.
            var kind = winter ? (roll < 45 ? WeatherKind.Snow : roll < 62 ? WeatherKind.Ice : roll < 82 ? WeatherKind.Wind : WeatherKind.Rain)
                : shoulder ? (roll < 15 ? WeatherKind.Snow : roll < 25 ? WeatherKind.Ice : roll < 75 ? WeatherKind.Rain : WeatherKind.Wind)
                : summer ? (roll < 50 ? WeatherKind.Heat : roll < 75 ? WeatherKind.Rain : WeatherKind.Wind)
                : roll < 60 ? WeatherKind.Rain : WeatherKind.Wind;
            int sroll = (int)(r >> 28 & 0xFF) % 100;
            int strength = sroll < 55 ? 1 : sroll < 88 ? 2 : 3;
            // It comes in from one edge of the region and drifts across: west to east, or north to south.
            bool eastward = (r >> 36 & 1) == 0;
            int radius = Math.Max(TileSize * 3 / 4, (int)(Math.Max(w, h) / 3));
            long along = (eastward ? w : h) + 2L * radius;
            long pos = -radius + along * (tick - start) / days;
            long across = (long)((r >> 40) % (ulong)(eastward ? h : w));
            list.Add(eastward ? new Front(kind, strength, pos, across, radius) : new Front(kind, strength, across, pos, radius));
        }
        return list;
    }

    /// <summary>
    /// A sky the host has pinned over the whole region in place of its fronts (an admin's weather control, for showing
    /// or testing weather), or null. Not saved: it lasts while the host runs.
    /// </summary>
    public (WeatherKind Kind, int Strength)? PinnedWeather { get; set; }

    /// <summary>The weather over a tile on a day: the strongest front over its middle, else clear.</summary>
    public (WeatherKind Kind, int Strength) WeatherAt(RegionTile t, long tick)
    {
        if (PinnedWeather is { } pin) return pin;
        long cx = (long)t.Col * TileSize + TileSize / 2, cy = (long)t.Row * TileSize + TileSize / 2;
        var best = (WeatherKind.Clear, 0);
        foreach (var f in Fronts(tick))
        {
            long dx = f.X - cx, dy = f.Y - cy;
            if (dx * dx + dy * dy > (long)f.Radius * f.Radius) continue;
            if (f.Strength > best.Item2) best = (f.Kind, f.Strength);
        }
        return best;
    }

    /// <summary>Tells each city today's weather when it changed. A lone city has no region sky.</summary>
    public void SendWeather()
    {
        // A lone city has no region sky of its own, only one its host pins; let go, it clears.
        bool lone = Tiles.Length < 2 && PinnedWeather == null;
        foreach (var t in Tiles)
        {
            if (t.City is not { } w) continue;
            var (k, s) = lone ? (WeatherKind.Clear, 0) : WeatherAt(t, Tick);
            if (k != w.Weather || s != w.WeatherStrength) w.Enqueue(new WeatherAction(k, s));
        }
    }
}
