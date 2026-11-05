namespace ToyCity.Sim;

/// <summary>
/// The mayor (or an admin) sets a storm going over the radar's ground (Beta 4.1, part 3): it starts at cell
/// (<c>X</c>, <c>Y</c>) of the map, or off it, heads toward <c>HeadingDeg</c> (0 east, 90 south) at <c>SpeedKmh</c>
/// and is light, heavy or severe by <c>Strength</c> 1 to 3. Where it passes over the city it rains, or snows below
/// freezing.
/// </summary>
public sealed record PlaceStormAction(int X, int Y, int HeadingDeg, int SpeedKmh, int Strength) : SimAction;

/// <summary>What a storm cell brings: rain, snow, freezing rain or a summer thunderstorm with hail in its core.</summary>
public enum StormKind : byte { Rain = 0, Snow = 1, Ice = 2, Thunder = 3 }

/// <summary>
/// One storm cell as the radar sees it now, in kilometres from the middle of the city (x east, y south): moving at
/// (<c>Vx</c>, <c>Vy</c>) km/h, an ellipse <c>Along</c> km long down its track and <c>Across</c> km wide, its core at
/// <c>Peak</c> dBZ falling to 18 dBZ less at its edge, its tops at <c>TopKm</c>. <c>Seed</c> gives its texture.
/// </summary>
public readonly record struct StormCell(double X, double Y, double Vx, double Vy, double Along, double Across, double Peak, double TopKm, StormKind Kind, uint Seed)
{
    /// <summary>The reflectivity (dBZ) this cell gives at a point, or below 0 where it doesn't reach.</summary>
    public double DbzAt(double px, double py)
    {
        double sp = Math.Sqrt(Vx * Vx + Vy * Vy);
        double ux = sp > 0.01 ? Vx / sp : 1, uy = sp > 0.01 ? Vy / sp : 0;
        double dx = px - X, dy = py - Y;
        double a = (dx * ux + dy * uy) / Along, b = (-dx * uy + dy * ux) / Across;
        double d2 = a * a + b * b;
        return d2 > 1 ? -1 : Peak - 18 * d2;
    }
}

/// <summary>
/// The weather station's readings (Beta 4.1, part 3): temperature and dew point (°C), relative humidity (%), wind
/// from <c>WindFromDeg</c> (meteorological: 270 is a west wind) at <c>WindKmh</c>, pressure (hPa), the rain falling on
/// the city now (mm an hour, as water) and what has fallen since midnight (mm).
/// </summary>
public readonly record struct StationReading(double TempC, double DewC, int Humidity, int WindFromDeg, int WindKmh, double PressureHpa, double RateMmH, double TodayMm);

/// <summary>
/// Storm cells and the weather station (Beta 4.1, part 3). The fronts set the day's weather and the day clock's
/// showers (<see cref="ShowerHour"/>) say in which hours it rains or snows on the city; the storm cells are drawn to
/// match: every shower hour brings a cell down its track over the middle of the city, and others pass by, nearer or
/// further, so the radar always agrees with what's falling. A summer heatwave brings afternoon thunderstorms in the
/// country round about. All of it is worked out from the day, the hour and the weather, so none of it is saved but the
/// storms a player sets going (format 21).
/// </summary>
public sealed partial class World
{
    /// <summary>A storm a player set going: where it was when it started and when, which way and how fast, how strong.</summary>
    public readonly record struct PlacedStorm(long StartMinute, double X, double Y, int HeadingDeg, int SpeedKmh, int Strength);

    /// <summary>Storms set going by players, until they've passed. Saved.</summary>
    public readonly List<PlacedStorm> PlacedStorms = new();

    /// <summary>How long a placed storm lasts, in clock hours.</summary>
    public const int PlacedStormHours = 6;

    /// <summary>How far the radar sees from the middle of the city, in km: where a storm can be set going.</summary>
    public const double RadarRangeKm = 40;

    /// <summary>Kilometres across a cell of the map.</summary>
    public const double CellKm = 0.016;

    static ulong StormMix(ulong h)
    {
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
        return h;
    }

    static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));

    /// <summary>The day's steering wind: which way the storms go (radians, 0 east, π/2 south) and how fast (km/h).</summary>
    public static (double Heading, double Speed) SteeringWind(long clockDay, WeatherKind kind, int strength)
    {
        ulong h = StormMix((ulong)clockDay * 0x9E3779B97F4A7C15UL ^ 0x5707_3D0FUL);
        // Mostly from the west, swinging south-west to north-west; a winter storm is quicker, a heatwave sluggish.
        double heading = (Unit(h) - 0.5) * 1.6;
        double speed = 22 + 20 * Unit(StormMix(h)) + (kind is WeatherKind.Snow or WeatherKind.Ice ? 10 : kind == WeatherKind.Heat ? -10 : 0) + 4 * strength;
        return (heading, speed);
    }

    /// <summary>The storm cells over the radar's ground at a clock minute.</summary>
    public List<StormCell> StormCells(long minute) => StormCells(minute, Weather, WeatherStrength, PlacedStorms, Month);

    /// <summary>The storm cells for a weather at a clock minute (pure: the same everywhere for the same inputs).</summary>
    public static List<StormCell> StormCells(long minute, WeatherKind kind, int strength, IReadOnlyList<PlacedStorm> placed, int month)
    {
        var cells = new List<StormCell>();
        double t = minute / 60.0;
        long hour = minute / 60;
        var (heading, speed) = SteeringWind(minute / MinutesPerClockDay, kind, strength);
        double ux = Math.Cos(heading), uy = Math.Sin(heading), vx = ux * speed, vy = uy * speed;
        strength = Math.Clamp(strength, 1, 3);
        bool precip = kind is WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Ice;
        var sk = kind switch { WeatherKind.Snow => StormKind.Snow, WeatherKind.Ice => StormKind.Ice, _ => StormKind.Rain };
        double PeakFor(StormKind k, double roll) => k switch
        {
            StormKind.Snow => 20 + 4 * strength + 5 * roll,
            StormKind.Ice => 28 + 4 * strength + 5 * roll,
            StormKind.Thunder => 50 + 3 * strength + 6 * roll,
            _ => 22 + 5 * strength + 5 * roll,
        };
        double TopFor(StormKind k) => k switch { StormKind.Snow => 4, StormKind.Ice => 5, StormKind.Thunder => 12 + strength, _ => 6 + 2 * strength };
        // A cell is born and dies over three hours, strongest in its middle.
        const double Life = 3.0;
        void Add(double tc, double lateral, double along, double across, StormKind k, double roll, ulong seed, double svx, double svy)
        {
            double age = t - tc;
            if (Math.Abs(age) > Life / 2) return;
            double grow = Math.Cos(age / (Life / 2) * Math.PI / 2);
            double sp = Math.Sqrt(svx * svx + svy * svy);
            double sx = sp > 0.01 ? svx / sp : 1, sy = sp > 0.01 ? svy / sp : 0;
            // Over the lateral offset at tc, then on down the track.
            double x = -sy * lateral + svx * age, y = sx * lateral + svy * age;
            double peak = PeakFor(k, roll) - 14 * (1 - grow);
            cells.Add(new StormCell(x, y, svx, svy, along * (0.7 + 0.3 * grow), across * (0.7 + 0.3 * grow), peak, TopFor(k), k, (uint)seed));
        }
        if (precip)
            for (long h = hour - 2; h <= hour + 2; h++)
            {
                ulong r = StormMix((ulong)h * 0xD6E8FEB86659FD93UL ^ (ulong)kind * 0x2545F4914F6CDD1DUL);
                // A shower hour: a cell long enough to cover the city for the whole hour, its track over the middle.
                double along = Math.Max(4, speed * 0.9), across = 3 + 2 * strength;
                if (ShowerHour(h, kind, strength))
                    Add(h + 0.5, (Unit(r) - 0.5) * 1.5, along, across, sk, Unit(StormMix(r + 1)), r, vx, vy);
                // And two more passing by, clear of the city.
                for (int k = 0; k < 2; k++)
                {
                    ulong q = StormMix(r + 10 + (ulong)k);
                    double side = Unit(q) < 0.5 ? -1 : 1;
                    double lateral = side * (across + 3 + 25 * Unit(StormMix(q)));
                    Add(h + Unit(StormMix(q + 2)), lateral, along * (0.6 + 0.6 * Unit(StormMix(q + 3))), across * (0.7 + 0.6 * Unit(StormMix(q + 4))),
                        sk, Unit(StormMix(q + 5)), q, vx, vy);
                }
            }
        // A heatwave's afternoons: thunderstorms out in the country, slow, never over the city itself.
        if (kind == WeatherKind.Heat)
            for (long h = hour - 2; h <= hour + 2; h++)
            {
                int hod = (int)(((h % 24) + 24) % 24);
                if (hod < 13 || hod > 20) continue;
                ulong r = StormMix((ulong)h * 0x94D049BB133111EBUL ^ 0x7E47UL);
                if (Unit(r) > 0.3 + 0.15 * strength) continue;
                double lateral = (Unit(StormMix(r)) < 0.5 ? -1 : 1) * (9 + 22 * Unit(StormMix(r + 1)));
                Add(h + Unit(StormMix(r + 2)), lateral, 4 + 2 * Unit(StormMix(r + 3)), 3 + 2 * Unit(StormMix(r + 4)), StormKind.Thunder, Unit(StormMix(r + 5)), r, vx * 0.5, vy * 0.5);
            }
        // Storms set going by players.
        foreach (var p in placed)
            if (PlacedCell(p, minute, month) is { } c) cells.Add(c);
        return cells;
    }

    /// <summary>A placed storm's cell at a clock minute, or null before it starts and once it's done.</summary>
    public static StormCell? PlacedCell(PlacedStorm p, long minute, int month)
    {
        double age = (minute - p.StartMinute) / 60.0;
        if (age < 0 || age > PlacedStormHours) return null;
        double hr = p.HeadingDeg * Math.PI / 180;
        double pvx = Math.Cos(hr) * p.SpeedKmh, pvy = Math.Sin(hr) * p.SpeedKmh;
        // Builds over the first hour, holds, and dies away over the last.
        double grow = Math.Min(1, Math.Min(age + 0.3, PlacedStormHours - age));
        var k = month is 12 or 1 or 2 ? StormKind.Snow : p.Strength >= 3 && month is >= 5 and <= 9 ? StormKind.Thunder : StormKind.Rain;
        int s = Math.Clamp(p.Strength, 1, 3);
        double peak = (k switch { StormKind.Snow => 22 + 4 * s, StormKind.Thunder => 58, _ => 32 + 6 * s }) - 14 * (1 - grow);
        double size = 3 + 1.5 * s;
        return new StormCell(p.X + pvx * age, p.Y + pvy * age, pvx, pvy, size * 1.4 * (0.7 + 0.3 * grow), size * (0.7 + 0.3 * grow), peak,
            k == StormKind.Thunder ? 13 : 7, k, (uint)StormMix((ulong)p.StartMinute * 31 + (ulong)p.HeadingDeg));
    }

    /// <summary>The strongest reflectivity (dBZ) at a point, km from the middle of the city; below 0 where it's dry.</summary>
    public static double DbzAt(List<StormCell> cells, double x, double y)
    {
        double best = -1;
        foreach (var c in cells) best = Math.Max(best, c.DbzAt(x, y));
        return best;
    }

    /// <summary>Rain (mm an hour, as water) from a reflectivity, by Marshall and Palmer: Z = 200 R^1.6.</summary>
    public static double RateFromDbz(double dbz) => dbz < 10 ? 0 : Math.Pow(Math.Pow(10, Math.Min(dbz, 55) / 10) / 200, 1 / 1.6);

    /// <summary>Snow (mm an hour, as water) from a reflectivity: Z = 2000 R^2.</summary>
    public static double SnowRateFromDbz(double dbz) => dbz < 5 ? 0 : Math.Sqrt(Math.Pow(10, dbz / 10) / 2000);

    /// <summary>The water falling at a point (mm an hour): the strongest cell there, by its kind.</summary>
    public static double RateAt(List<StormCell> cells, double x, double y)
    {
        double best = 0;
        foreach (var c in cells)
        {
            double z = c.DbzAt(x, y);
            if (z < 5) continue;
            best = Math.Max(best, c.Kind == StormKind.Snow ? SnowRateFromDbz(z) : RateFromDbz(z));
        }
        return best;
    }

    /// <summary>The kind of precipitation falling on the city now, or Clear: the front's in its shower hours, else a placed storm's.</summary>
    public WeatherKind FallingKind
    {
        get
        {
            if (Weather is WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Ice && ShowerHour(ClockMinute / 60, Weather, WeatherStrength)) return Weather;
            if (PlacedStorms.Count == 0) return WeatherKind.Clear;
            if (PlacedOverCity() == null) return WeatherKind.Clear;
            // A placed storm snows in the winter months, as its cell on the radar does.
            return Month is 12 or 1 or 2 ? WeatherKind.Snow : WeatherKind.Rain;
        }
    }

    /// <summary>How hard it's falling, 1 to 3.</summary>
    public int FallingStrength
    {
        get
        {
            if (Weather is WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Ice && ShowerHour(ClockMinute / 60, Weather, WeatherStrength)) return Math.Max(1, WeatherStrength);
            return PlacedOverCity()?.Strength ?? Math.Max(1, WeatherStrength);
        }
    }

    /// <summary>
    /// The placed storm raining on the middle of the city during this step, if any: looked for every six clock minutes
    /// of it, so a fast storm on a fast calendar doesn't jump clean over the city between one day and the next.
    /// </summary>
    PlacedStorm? PlacedOverCity()
    {
        foreach (var p in PlacedStorms)
            for (long m = ClockMinute; m < ClockMinute + Math.Max(1, ClockMinutesPerTick); m += 6)
                if (PlacedCell(p, m, Month) is { } c && c.DbzAt(0, 0) >= 20) return p;
        return null;
    }

    static readonly double[] NormalHigh = { 0, 0, 3, 9, 16, 22, 27, 29, 28, 24, 17, 9, 2 };
    static readonly double[] NormalLow = { 0, -9, -7, -2, 4, 10, 16, 19, 18, 13, 6, 0, -6 };

    /// <summary>The weather station's readings now.</summary>
    public StationReading Station() => Station(ClockMinute);

    public StationReading Station(long minute)
    {
        int month = Month;
        double hod = minute % MinutesPerClockDay / 60.0;
        // The day's curve: coldest at six in the morning, warmest at three in the afternoon.
        double day = 0.5 - 0.5 * Math.Cos((hod - 6) / 9 * Math.PI);
        if (hod > 15) day = 0.5 + 0.5 * Math.Cos((hod - 15) / 15 * Math.PI);
        double temp = NormalLow[month] + (NormalHigh[month] - NormalLow[month]) * day;
        ulong h = StormMix((ulong)(minute / 60) * 0xA0761D6478BD642FUL);
        temp += (Unit(h) - 0.5) * 2;
        int s = Math.Clamp(WeatherStrength, 1, 3);
        var cells = StormCells(minute);
        double dbz = DbzAt(cells, 0, 0);
        bool wet = dbz >= 15;
        double spread = Weather switch
        {
            WeatherKind.Heat => 13 + 3 * day,
            WeatherKind.Rain or WeatherKind.Ice => wet ? 0.8 : 3,
            WeatherKind.Snow => wet ? 1 : 2.5,
            WeatherKind.Wind => 9,
            _ => 5 + 6 * day,
        };
        switch (Weather)
        {
            case WeatherKind.Heat: temp += 5 + 2.5 * s; break;
            case WeatherKind.Snow: temp = Math.Min(temp, -1 - 2 * s + day * 2); break;
            case WeatherKind.Ice: temp = Math.Clamp(temp, -3, -0.5); break;
            case WeatherKind.Rain: temp -= wet ? 3 : 1; if (temp < 1.5) temp = 1.5; break;
            case WeatherKind.Wind: temp -= 2; break;
        }
        if (wet && Weather is not (WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Ice)) temp -= 4;
        double dew = temp - spread;
        // Relative humidity from the two by Magnus' formula.
        double Es(double c) => Math.Exp(17.62 * c / (243.12 + c));
        int humidity = (int)Math.Round(Math.Clamp(100 * Es(dew) / Es(temp), 5, 100));
        var (heading, speed) = SteeringWind(minute / MinutesPerClockDay, Weather, s);
        // The surface wind blows roughly the way the storms go: from the opposite way.
        int from = (int)Math.Round((heading * 180 / Math.PI + 180 + 90 + (Unit(StormMix(h)) - 0.5) * 30 + 360 * 2) % 360);
        double wind = 8 + 10 * Unit(StormMix(h + 1)) + (Weather == WeatherKind.Wind ? 22 * s : 0) + (wet ? 12 : 0) + speed * 0.1;
        double pressure = 1016 + (Unit(StormMix(h + 2)) - 0.5) * 3
            - (Weather is WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Ice ? 6 * s : 0) - (Weather == WeatherKind.Wind ? 9 * s : 0) - (Weather == WeatherKind.Heat ? 3 : 0);
        double rate = RateAt(cells, 0, 0);
        // Since midnight: the rate every ten clock minutes.
        double today = 0;
        long midnight = minute - minute % MinutesPerClockDay;
        for (long m = midnight + 5; m < minute; m += 10) today += RateAt(StormCells(m), 0, 0) / 6;
        return new StationReading(Math.Round(temp, 1), Math.Round(dew, 1), humidity, from, (int)Math.Round(wind), Math.Round(pressure, 1), Math.Round(rate, 1), Math.Round(today, 1));
    }

    void ApplyPlaceStorm(PlaceStormAction a)
    {
        if (a.Strength is < 1 or > 3 || a.SpeedKmh is < 0 or > 120) { Reject("A storm is light, heavy or severe, and moves at up to 120 km/h.", a.X, a.Y); return; }
        // From the middle of the map, in kilometres.
        double x = (a.X + 0.5 - Width / 2.0) * CellKm, y = (a.Y + 0.5 - HeightCells / 2.0) * CellKm;
        PlacedStorms.Add(new PlacedStorm(ClockMinute, x, y, ((a.HeadingDeg % 360) + 360) % 360, a.SpeedKmh, a.Strength));
        Notices.Add(new SimNotice($"A storm is building, moving {Compass((a.HeadingDeg + 90) % 360)} at {a.SpeedKmh} km/h.", "info"));
    }

    /// <summary>Every step: storms that have passed are let go.</summary>
    void TrimStorms()
    {
        if (PlacedStorms.Count > 0) PlacedStorms.RemoveAll(p => ClockMinute - p.StartMinute > PlacedStormHours * 60);
    }

    /// <summary>A bearing (0 north, 90 east) as a compass point.</summary>
    public static string Compass(int bearing)
    {
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        return names[(int)Math.Round(((bearing % 360) + 360) % 360 / 45.0) % 8];
    }

    void WriteStorms(BinaryWriter w)
    {
        w.Write(PlacedStorms.Count);
        foreach (var p in PlacedStorms) { w.Write(p.StartMinute); w.Write(p.X); w.Write(p.Y); w.Write(p.HeadingDeg); w.Write(p.SpeedKmh); w.Write(p.Strength); }
    }

    void ReadStorms(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++) PlacedStorms.Add(new PlacedStorm(r.ReadInt64(), r.ReadDouble(), r.ReadDouble(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
    }
}
