namespace ToyCity.Sim;

/// <summary>
/// The region tells a city how much its rivers carry when that changes. Logged, so a city's log replays it alone.
/// Stage is the flood stage (0 in its banks, up to 3); FlowPct the flow as a share of usual, from the last month's
/// rain over the river's catchment (0 in a log written before flows: the stage's own flow).
/// </summary>
public sealed record FloodAction(int Stage, int FlowPct = 0) : SimAction;

/// <summary>
/// Beta 3.1 part 5: floods. Heavy rain over a river's catchment raises it, and the rise travels downstream, so a
/// storm over the hills floods the town below a day or two later, across a border if the river crosses one. The
/// water spreads from the river over low ground beside it: buildings standing in it are damaged each day it lasts.
/// </summary>
public sealed partial class World
{
    /// <summary>How far, in cells, floodwater spreads from a river over ground low enough to take it.</summary>
    public const int FloodReach = RiverFlow.Reach;
    /// <summary>The least and most a river carries, as a share of its usual flow: a drought's trickle to a great flood.</summary>
    public const int FlowLeast = RiverFlow.Least, FlowMost = RiverFlow.Most;

    /// <summary>
    /// How much the rivers carry, as a share of their usual flow (100), from the last month's rain upstream. Saved.
    /// Their water stands higher or lower by it: see <see cref="WaterDm"/>.
    /// </summary>
    public int RiverFlowPct = 100;

    /// <summary>The flood stage a flow brings (<see cref="RiverFlow.StageOf"/>).</summary>
    public static int StageOfFlow(int pct) => RiverFlow.StageOf(pct);
    /// <summary>The flow a stage stands for (<see cref="RiverFlow.OfStage"/>).</summary>
    public static int FlowOfStage(int stage) => RiverFlow.OfStage(stage);
    public static byte[] WaterSizes(int width, int height, byte[] ground, byte[] river) => RiverFlow.Sizes(width, height, ground, river);
    public static int WaterDm(int river, int size, int flowPct) => RiverFlow.WaterDm(river, size, flowPct);
    public static byte[] FloodedCells(int width, int height, byte[] ground, byte[] river, int waterLevel, int flowPct) =>
        RiverFlow.Flooded(width, height, ground, river, waterLevel, flowPct);

    /// <summary>The water's size around each cell, for its depth and rise. Worked out from the ground, not saved.</summary>
    public byte[] WaterSize => _waterSize ??= WaterSizes(Width, HeightCells, Height, River);
    byte[]? _waterSize;
    /// <summary>Condition a building loses each day it stands in floodwater.</summary>
    public const int FloodDamagePerDay = 6;

    /// <summary>How many steps the rivers stand over their usual surface today, from the region. Saved.</summary>
    public int FloodStage;

    /// <summary>
    /// Each cell's floodwater surface in steps, or 0 where it's dry: the ground beside a river that the rise reaches.
    /// Worked out from the flow, not saved.
    /// </summary>
    public byte[] Flooded => _flooded ??= RiverFlow.Flooded(Width, HeightCells, Height, River, WaterLevel, RiverFlowPct, Dam);
    byte[]? _flooded;

    void ApplyFlood(FloodAction a)
    {
        int was = FloodStage, wasFlow = RiverFlowPct;
        RiverFlowPct = a.FlowPct > 0 ? Math.Clamp(a.FlowPct, FlowLeast, FlowMost) : FlowOfStage(Math.Clamp(a.Stage, 0, 3));
        FloodStage = StageOfFlow(RiverFlowPct);
        _flooded = null;
        // A dry month leaves the rivers low: news when they drop to half their usual flow, and when they come back.
        if (RiverFlowPct < 50 && wasFlow >= 50 && River.Any(r => r != 0))
            Notices.Add(new SimNotice($"The rivers are running low through {Name} after a dry month: {RiverFlowPct}% of their usual flow.", "info"));
        else if (RiverFlowPct >= 70 && wasFlow < 70 && wasFlow > 0 && FloodStage == 0)
            Notices.Add(new SimNotice($"Rain has brought the rivers through {Name} back up.", "info"));
        if (FloodStage > was && FloodStage >= 1)
        {
            var (buildings, roads) = FloodToll();
            Notices.Add(new SimNotice(buildings + roads == 0
                ? $"The river is rising through {Name}, {FloodStage * 2} m over its usual level, but it's staying in its banks."
                : $"The river is in flood through {Name}, {FloodStage * 2} m up: {buildings} buildings and {roads} road cells are under water.",
                buildings > 0 ? "warning" : "info"));
        }
        else if (FloodStage == 0 && was > 0)
            Notices.Add(new SimNotice($"The river is back in its banks through {Name}.", "info"));
    }

    /// <summary>Buildings and road cells standing in floodwater now.</summary>
    public (int Buildings, int Roads) FloodToll()
    {
        var water = Flooded;
        var hit = new HashSet<int>();
        int roads = 0;
        for (int i = 0; i < N; i++)
        {
            if (water[i] == 0) continue;
            if (BuildingAt[i] != 0) hit.Add(BuildingAt[i]);
            if (Path[i] != 0) roads++;
        }
        return (hit.Count, roads);
    }

    /// <summary>Daily, while the river is up: each building in the water loses condition.</summary>
    void RunFloods()
    {
        if (FloodStage <= 0) return;
        var water = Flooded;
        var hit = new HashSet<int>();
        for (int i = 0; i < N; i++)
            if (water[i] != 0 && BuildingAt[i] != 0) hit.Add(BuildingAt[i] - 1);
        foreach (int id in hit.Order())
            if (id < Buildings.Count && Buildings[id] is { } b && b.ConstructionLeft == 0)
                b.Condition = Math.Max(0, b.Condition - FloodDamagePerDay);
    }

    /// <summary>The cells the river would cover at a stage, for the map's flood layer and the advisors.</summary>
    public byte[] FloodPlain(int stage) => RiverFlow.Flooded(Width, HeightCells, Height, River, WaterLevel, FlowOfStage(stage), Dam);

    /// <summary>Called when the ground changes, so the floodwater is worked out again.</summary>
    void FloodGroundChanged() => (_flooded, _waterSize, _held) = (null, null, null);
}

public sealed partial class Region
{
    /// <summary>River cells rain is measured over for each tile, with the days the rise takes to reach it.</summary>
    List<(long X, long Y, int Lag)>[]? _floodSamples;

    /// <summary>How many cells a day a rise travels down a river.</summary>
    public const int FloodCellsPerDay = 48;

    List<(long X, long Y, int Lag)>[] FloodSamples()
    {
        if (_floodSamples != null) return _floodSamples;
        var g = Ground;
        int wide = BoxCols * TileSize;
        var samples = new List<(long, long, int)>[Tiles.Length];
        for (int k = 0; k < samples.Length; k++) samples[k] = new();
        int TileIndex(int i) => TileAtBox(i % wide, i / wide) is { } t ? t.Row * Cols + t.Col : -1;
        // Every seventh river cell stands for its stretch: walk down from it, noting the day it reaches each tile.
        for (int i = 0; i < g.Rivers.Length; i += 7)
        {
            if (g.Rivers[i] == 0) continue;
            var seen = new HashSet<int>();
            int c = i, steps = 0;
            while (c >= 0 && steps <= 3 * TileSize)
            {
                int t = TileIndex(c);
                if (t >= 0 && seen.Add(t) && samples[t].Count < 600) samples[t].Add((i % wide, i / wide, steps / FloodCellsPerDay));
                c = g.Downstream[c];
                steps++;
            }
        }
        return _floodSamples = samples;
    }

    /// <summary>The rain a usual month brings over a river's catchment, in the units <see cref="RainAt"/> counts.</summary>
    public const double UsualMonthsRain = 2.5;
    /// <summary>The share of usual flow the ground keeps feeding a river through a season without rain.</summary>
    public const int BaseFlowPct = 35;
    /// <summary>The most stretches of river upstream a tile's rain is measured over.</summary>
    const int RainSamples = 48;

    /// <summary>
    /// How much the rivers through a tile carry on a day, as a share of their usual flow: the rain over the
    /// stretches upstream as their water reaches the tile, half from the last month's (which runs off fast) and half
    /// from the last three months' (which the ground soaks up and feeds back slowly), against a usual month's. With
    /// no rain for a season rivers run at a third of their flow; under weeks of storms they rise to six times it.
    /// </summary>
    public int FlowAt(int tileIndex, long tick, Dictionary<long, List<Front>>? cache = null)
    {
        if (FloodSamples()[tileIndex].Count == 0) return 100;
        var (month, season) = RainAt(tileIndex, tick, cache);
        // Storms over a wet catchment run straight off: rain past five usual months' raises the water fastest.
        double over = Math.Max(0, month - 5 * UsualMonthsRain) / UsualMonthsRain;
        double wet = 0.5 * month / UsualMonthsRain + 0.5 * season / (3 * UsualMonthsRain);
        double pct = BaseFlowPct + (100 - BaseFlowPct) * Math.Min(wet, 2.0) + 12 * over * over;
        return (int)Math.Clamp(Math.Round(pct), World.FlowLeast, World.FlowMost);
    }

    /// <summary>
    /// The rain over a tile's rivers' catchment, as their water reaches the tile: over the last thirty days (this
    /// week's counting half as much again, the oldest fortnight's less) and over the last ninety. Each day a rain
    /// front lay over a stretch upstream counts its strength (1 light to 3 storms), averaged over the stretches.
    /// </summary>
    public (double Month, double Season) RainAt(int tileIndex, long tick, Dictionary<long, List<Front>>? cache = null)
    {
        var all = FloodSamples()[tileIndex];
        if (all.Count == 0) return (0, 0);
        // A sky an admin has pinned has lasted the whole season: storms flood the rivers, anything else dries them.
        if (PinnedWeather is { } pin)
            return pin.Kind == WeatherKind.Rain ? (pin.Strength * 27.1, pin.Strength * 90.0) : (0, 0);
        cache ??= new Dictionary<long, List<Front>>();
        int stride = Math.Max(1, all.Count / RainSamples), n = 0;
        double month = 0, season = 0;
        for (int k = 0; k < all.Count; k += stride)
        {
            var (x, y, lag) = all[k];
            n++;
            for (int d = 0; d < 90; d++)
            {
                long when = tick - lag - d;
                if (when < 0) break;
                if (!cache.TryGetValue(when, out var fronts)) cache[when] = fronts = Fronts(when).Where(f => f.Kind == WeatherKind.Rain).ToList();
                foreach (var f in fronts)
                {
                    long ddx = f.X - x, ddy = f.Y - y;
                    if (ddx * ddx + ddy * ddy > (long)f.Radius * f.Radius) continue;
                    season += f.Strength;
                    if (d < 30) month += f.Strength * (d < 7 ? 1.5 : d < 14 ? 1.0 : 0.6);
                }
            }
        }
        return (month / n, season / n);
    }

    /// <summary>The flood stage of a tile's rivers on a day (see <see cref="FlowAt"/>).</summary>
    public int FloodStageAt(int tileIndex, long tick) => World.StageOfFlow(FlowAt(tileIndex, tick));

    /// <summary>Tells each city its rivers' stage when it changed. A lone city has no region sky, so no floods.</summary>
    void SendFloods()
    {
        if (Tiles.Length < 2) return;
        // A city of several tiles runs as its wettest tile does.
        var flow = new Dictionary<World, int>();
        var cache = new Dictionary<long, List<Front>>();
        for (int k = 0; k < Tiles.Length; k++)
        {
            if (Tiles[k].City is not { } w) continue;
            flow[w] = Math.Max(flow.GetValueOrDefault(w), FlowAt(k, Tick, cache));
        }
        // Told when the stage changes, or the flow by a tenth of usual or more: not every day's trickle.
        foreach (var t in Anchors)
            if (flow.TryGetValue(t.City!, out int f) && (World.StageOfFlow(f) != t.City!.FloodStage || Math.Abs(f - t.City!.RiverFlowPct) >= 10))
                t.City!.Enqueue(new FloodAction(World.StageOfFlow(f), f));
    }
}
