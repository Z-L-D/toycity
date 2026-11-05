namespace ToyCity.Sim;

/// <summary>The mayor sets the road crews' budget (Beta 3.2, part 4.4): 0 to 200%, like a civic building's funding.</summary>
public sealed record SetRoadCrewsAction(int Percent) : SimAction;

/// <summary>
/// Weather on the roads (Beta 3.2, part 4). The region's fronts follow the calendar, but each shower plays out in
/// hours of the day clock, and the ground remembers it:
/// <list type="bullet">
/// <item>rain wets the roads, which dry in a few clock hours, and turns dirt to mud that takes a day or more;</item>
/// <item>snow and freezing rain build up on each tier of road (highways, roads, streets) until they melt or the road
/// crews clear them, highways first, as far as their budget goes;</item>
/// <item>drained roads (highways and the bigger roads) shed rain and shallow floodwater while the crews are funded.</item>
/// </list>
/// Each surface carries a share of its traffic in each condition (the content's rainPct, stormPct, snowPct and
/// icePct). That share cuts the road's capacity and slows it, so commuters, buses and the dispatcher's crews all feel
/// it; ice and snow also bring more crashes, and some people stay home. In clear weather, on dry roads, nothing
/// changes: a lone city with no region sky plays exactly as before.
/// </summary>
public sealed partial class World
{
    /// <summary>Road crews' budget, 0..200 percent of their usual. Saved.</summary>
    public int RoadCrewFunding { get; private set; } = 100;
    /// <summary>How wet the roads are, 0..1000 (1000 while it rains). Saved.</summary>
    public int RoadWet { get; private set; }
    /// <summary>How muddy dirt roads are, 0..1000. Saved.</summary>
    public int RoadMud { get; private set; }
    /// <summary>Snow or ice on each tier of road (0 highways, 1 roads, 2 streets), 0..1000. Saved.</summary>
    public readonly int[] RoadCover = new int[3];
    /// <summary>Whether that tier's cover is ice (freezing rain) rather than snow. Saved.</summary>
    public readonly bool[] RoadIce = new bool[3];
    /// <summary>Snow the crews have pushed to the kerbs of each tier, 0..1000, for the clients to draw. Saved.</summary>
    public readonly int[] RoadBanked = new int[3];
    bool _roadsUsual = true, _snowedIn;

    public const int Tiers = 3;
    static readonly string[] TierNames = { "highways", "roads", "streets" };
    /// <summary>The crews' monthly cost per cell of each tier at full budget.</summary>
    static readonly double[] CrewCostPerCell = { 0.3, 0.15, 0.05 };

    /// <summary>
    /// Whether rain, snow or freezing rain is falling this hour of the day clock. A front brings showers, more of the
    /// hours the stronger it is; the hours are the same over every city in the region.
    /// </summary>
    public bool Falling => FallingKind != WeatherKind.Clear;

    public static bool ShowerHour(long hour, WeatherKind kind, int strength)
    {
        ulong h = (ulong)hour * 0x9E3779B97F4A7C15UL ^ (ulong)kind * 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29;
        return (int)(h % 100) < 35 + 20 * Math.Clamp(strength, 1, 3);
    }

    /// <summary>True when the roads are as they'd be on a dry, clear day.</summary>
    public bool RoadsUsual => RoadWet == 0 && RoadMud == 0 && RoadCover.All(c => c == 0) && !(Falling && FallingKind is WeatherKind.Snow or WeatherKind.Ice) && FloodStage <= 0;

    static int Lerp(int a, int b, int t1000) => a + (b - a) * t1000 / 1000;

    /// <summary>
    /// The share of its usual traffic a path cell carries now, in percent (5..100), from its surface, the weather on it
    /// and the crews. 100 on a dry day.
    /// </summary>
    public int RoadSharePct(int i) => RoadShare(i, out _);

    /// <summary>As <see cref="RoadSharePct"/>, with what cut it most ("snow", "ice", "mud", "rain", "flood"), or "".</summary>
    public int RoadShare(int i, out string cause)
    {
        string why = "";
        int share = RoadShareOf(i, ref why);
        cause = why;
        return share;
    }

    int RoadShareOf(int i, ref string cause)
    {
        if (Path[i] == 0) return 100;
        var p = Content.Profiles[Path[i] - 1];
        if (p.WinterTier < 0) return 100;
        var s = PathSurface[i] > 0 ? Content.Surfaces[PathSurface[i] - 1] : Asphalt;
        int share = 100;
        string c0 = "";
        bool rain = FallingKind == WeatherKind.Rain;
        // Drains and crews: a funded drained road holds up in the wet (the plan's "treated and funded" row).
        int fund = Math.Min(1000, RoadCrewFunding * 10);
        bool drained = p.Drained && RoadCrewFunding > 0;
        void Cut(int pct, string why) { if (pct < share) { share = pct; c0 = why; } }
        if (RoadWet > 0)
        {
            int col = rain && WeatherStrength >= 2 ? s.StormPct : s.RainPct;
            if (drained) col = Lerp(col, rain && WeatherStrength >= 2 ? 95 : 100, fund);
            Cut(100 - (100 - col) * RoadWet / 1000, "rain");
        }
        if (s.Muddy && RoadMud > 0) Cut(100 - (100 - s.StormPct) * RoadMud / 1000, "mud");
        int t = Math.Clamp(p.WinterTier, 0, Tiers - 1);
        if (RoadCover[t] > 0)
        {
            int col = RoadIce[t] ? s.IcePct : s.SnowPct;
            Cut(100 - (100 - col) * RoadCover[t] / 1000, RoadIce[t] ? "ice" : "snow");
        }
        if (Falling && FallingKind is WeatherKind.Snow or WeatherKind.Ice)
        {
            // While it falls even a cleared road is slow: the treated row for a funded drained road, half the
            // surface's own cut for the rest.
            bool ice = FallingKind == WeatherKind.Ice;
            int col = ice ? s.IcePct : s.SnowPct;
            Cut(drained ? Lerp(100 - (100 - col) / 2, ice ? 75 : 85, fund) : 100 - (100 - col) / 2, ice ? "ice" : "snow");
        }
        if (FloodStage > 0 && Flooded[i] != 0)
            Cut(drained && FloodStage <= 1 && RoadCrewFunding >= 50 ? 60 : 5, "flood");
        cause = c0;
        return Math.Max(5, share);
    }

    SurfaceDef Asphalt => _asphalt ??= Content.SurfaceIndex("asphalt") is var k and >= 0 ? Content.Surfaces[k] : new SurfaceDef();
    SurfaceDef? _asphalt;

    /// <summary>Percent of commuters who stay home today: some do when it snows or the roads ice over.</summary>
    public int StayHomePct
    {
        get
        {
            int pct = 0;
            if (FallingKind == WeatherKind.Snow) pct += 5 * FallingStrength;
            if (FallingKind == WeatherKind.Ice) pct += 10 * FallingStrength;
            // Snowed-in streets keep people home after it stops too.
            pct += RoadCover[Tiers - 1] / 100;
            return Math.Min(40, pct);
        }
    }

    /// <summary>Percent on a busy road's chance of a crash: wet roads a little, snow and ice a lot.</summary>
    public int CrashPct(int i)
    {
        int share = RoadShare(i, out var cause);
        return cause switch
        {
            "ice" => 100 + 4 * (100 - share),
            "snow" => 100 + 2 * (100 - share),
            "rain" or "mud" => 100 + (100 - share),
            _ => 100,
        };
    }

    /// <summary>
    /// Every step: the showers wet, muddy, snow on and ice the roads, the sun and the crews clear them, by the clock
    /// minutes this step covers. Once a turn the roads' capacity and travel times follow.
    /// </summary>
    void RunRoadWeather()
    {
        int minutes = ClockMinutesPerTick;
        var fallingKind = FallingKind;
        bool falling = fallingKind != WeatherKind.Clear;
        int strength = Math.Max(1, falling ? FallingStrength : WeatherStrength);
        // Per clock hour, in thousandths, scaled to this step.
        int PerStep(int perHour) => (int)Math.Max(1, (long)perHour * minutes / 60);
        int month = Month;
        bool cold = month is 12 or 1 or 2, warm = Weather == WeatherKind.Heat || month is >= 5 and <= 9;

        // Rain: wet now, and mud on dirt that takes a day or more to dry.
        if (falling && fallingKind is WeatherKind.Rain or WeatherKind.Ice)
        {
            RoadWet = 1000;
            if (fallingKind == WeatherKind.Rain) RoadMud = Math.Min(1000, RoadMud + PerStep(250 * strength));
        }
        else
        {
            if (RoadWet > 0) RoadWet = Math.Max(0, RoadWet - PerStep(warm ? 600 : 330));
            if (RoadMud > 0) RoadMud = Math.Max(0, RoadMud - PerStep(warm ? 90 : cold ? 20 : 40));
        }

        // Snow and ice on each tier.
        bool any = falling && fallingKind is WeatherKind.Snow or WeatherKind.Ice || RoadCover.Any(c => c > 0) || RoadBanked.Any(c => c > 0);
        if (any)
        {
            var cells = TierCells();
            for (int t = 0; t < Tiers; t++)
            {
                // A tier the city hasn't built has nothing to lie on.
                if (cells[t] == 0) { RoadCover[t] = RoadBanked[t] = 0; RoadIce[t] = false; continue; }
                if (falling && fallingKind is WeatherKind.Snow or WeatherKind.Ice)
                {
                    RoadCover[t] = Math.Min(1000, RoadCover[t] + PerStep((fallingKind == WeatherKind.Ice ? 150 : 100) * strength));
                    RoadIce[t] = fallingKind == WeatherKind.Ice || RoadIce[t] && RoadCover[t] > 500;
                }
                else
                {
                    int melt = PerStep(Weather == WeatherKind.Heat ? 300 : Weather == WeatherKind.Rain ? 120 : cold ? 15 : 60);
                    RoadCover[t] = Math.Max(0, RoadCover[t] - melt);
                    RoadBanked[t] = Math.Max(0, RoadBanked[t] - melt / 2);
                }
                if (RoadCover[t] == 0) RoadIce[t] = false;
            }
            ClearRoads(PerStep(120 * RoadCrewFunding / 100), cells);
            NoticeSnowedIn();
        }

        // Once a turn the roads carry what the weather lets them.
        if (NewTurn)
        {
            bool usual = RoadsUsual;
            if (!usual || !_roadsUsual) RecostRoads();
            _roadsUsual = usual;
        }
    }

    /// <summary>
    /// The crews clear snow and salt ice, highways first, then roads, then streets: <paramref name="work"/>
    /// thousandths of cover per cell, over the city's whole network, this step.
    /// </summary>
    void ClearRoads(int work, int[] cells)
    {
        if (RoadCrewFunding <= 0) return;
        long budget = (long)work * (cells[0] + cells[1] + cells[2]);
        for (int t = 0; t < Tiers && budget > 0; t++)
        {
            if (cells[t] == 0 || RoadCover[t] == 0) continue;
            long need = (long)RoadCover[t] * cells[t];
            long done = Math.Min(need, budget);
            budget -= done;
            int cleared = (int)(done / cells[t]);
            RoadCover[t] -= cleared;
            if (!RoadIce[t]) RoadBanked[t] = Math.Min(1000, RoadBanked[t] + cleared);
            if (RoadCover[t] == 0) RoadIce[t] = false;
        }
    }

    /// <summary>Path cells on each tier that carry traffic (rail and the like aren't counted).</summary>
    int[] TierCells()
    {
        var n = new int[Tiers];
        for (int i = 0; i < N; i++)
            if (Path[i] != 0 && Content.Profiles[Path[i] - 1].WinterTier is var t and >= 0) n[Math.Min(t, Tiers - 1)]++;
        return n;
    }

    void NoticeSnowedIn()
    {
        int streets = RoadCover[Tiers - 1];
        if (!_snowedIn && streets >= 600)
        {
            _snowedIn = true;
            string what = RoadIce[Tiers - 1] ? "iced over" : "snowed in";
            string text = RoadCrewFunding < 100
                ? $"Streets {what} in {Name}: the road crews, on {RoadCrewFunding}% of their budget, haven't reached them."
                : $"Streets {what} in {Name}: the road crews are still on the highways and main roads.";
            Notices.Add(new SimNotice(text, "warning"));
            AddHeadline(text, "warning");
        }
        else if (_snowedIn && streets < 200) _snowedIn = false;
    }

    /// <summary>The month's trips over each path, as the weather lets it carry them now: congestion and travel times.</summary>
    void RecostRoads()
    {
        int stay = StayHomePct;
        for (int i = 0; i < N; i++)
        {
            var p = ProfileAt(i);
            if (p == null) { Traffic[i] = 0; TravelCost[i] = 0; continue; }
            int share = RoadsUsual ? 100 : RoadSharePct(i);
            // Commuters spread over two peak hours, so a path carries about twice its hourly capacity in a day.
            int capacityPerDay = Math.Max(1, p.CapacityPerHour * 2 * share / 100);
            long trips = stay > 0 ? (long)Trips[i] * (100 - stay) / 100 : Trips[i];
            int level = (int)Math.Min(255, trips * 128 / capacityPerDay);
            Traffic[i] = (byte)level;
            // BPR congestion curve: time = free * (1 + 0.15 (v/c)^4), capped at 5x. Bad going slows even an empty
            // road, up to twice as long.
            double vc = level / 128.0;
            double factor = Math.Min(5.0, 1 + 0.15 * vc * vc * vc * vc);
            if (share < 100) factor *= (200 - share) / 100.0;
            TravelCost[i] = (int)(PathCellTime(i) * factor);
        }
    }

    string CrashText(int i)
    {
        RoadShare(i, out var cause);
        return cause switch { "ice" => "a crash on the ice", "snow" => "a crash in the snow", "mud" => "a car stuck in the mud", _ => "a crash on the wet road" };
    }

    void ApplyRoadCrews(SetRoadCrewsAction a)
    {
        RoadCrewFunding = Math.Clamp(a.Percent, 0, 200);
    }

    /// <summary>The crews' monthly cost: per cell of each tier, at their budget.</summary>
    long RoadCrewCost()
    {
        var cells = TierCells();
        double cost = 0;
        for (int t = 0; t < Tiers; t++) cost += cells[t] * CrewCostPerCell[t];
        return (long)Math.Round(cost * RoadCrewFunding / 100);
    }

    /// <summary>A line for the city's state of the roads, or "" on a dry day: "Snow on the streets: they carry 40%".</summary>
    public string RoadWeatherText()
    {
        var parts = new List<string>();
        if (Falling) parts.Add(FallingKind switch { WeatherKind.Snow => "snow falling", WeatherKind.Ice => "freezing rain falling", _ => "rain falling" });
        else if (RoadWet > 0) parts.Add("wet roads");
        if (RoadMud > 100) parts.Add("mud on dirt roads");
        for (int t = 0; t < Tiers; t++)
            if (RoadCover[t] >= 100) parts.Add($"{(RoadIce[t] ? "ice" : "snow")} on the {TierNames[t]}");
        if (FloodStage > 0) parts.Add("flooded roads by the river");
        // Beta 4, part 4: the snow lying on the ground.
        if (GroundSnowMm >= 20) parts.Add($"{GroundSnowMm / 10} cm of snow lying");
        if (parts.Count == 0) return "";
        string text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A path cell's going, for the "why" query: "Snow: carries 35% of its usual traffic."</summary>
    public string RoadWeatherWhy(int i)
    {
        int share = RoadShare(i, out var cause);
        if (share >= 100) return "";
        string what = cause switch
        {
            "snow" => "Snow on it", "ice" => "Ice on it", "mud" => "Mud", "flood" => "Floodwater over it", _ => "Wet",
        };
        string crews = cause is "snow" or "ice"
            ? $" Road crews are on {RoadCrewFunding}% of their budget and clear highways, then roads, then streets."
            : cause == "flood" && Content.Profiles[Path[i] - 1].Drained ? " Its drains shed shallow water while the crews are funded." : "";
        return $"{what}: it carries {share}% of its usual traffic and crashes are more likely.{crews}";
    }

    void WriteRoadWeather(BinaryWriter w)
    {
        w.Write(RoadCrewFunding);
        w.Write(RoadWet);
        w.Write(RoadMud);
        for (int t = 0; t < Tiers; t++) { w.Write(RoadCover[t]); w.Write(RoadIce[t]); w.Write(RoadBanked[t]); }
        w.Write(_roadsUsual);
        w.Write(_snowedIn);
    }

    void ReadRoadWeather(BinaryReader r)
    {
        RoadCrewFunding = Math.Clamp(r.ReadInt32(), 0, 200);
        RoadWet = r.ReadInt32();
        RoadMud = r.ReadInt32();
        for (int t = 0; t < Tiers; t++) { RoadCover[t] = r.ReadInt32(); RoadIce[t] = r.ReadBoolean(); RoadBanked[t] = r.ReadInt32(); }
        _roadsUsual = r.ReadBoolean();
        _snowedIn = r.ReadBoolean();
    }
}
