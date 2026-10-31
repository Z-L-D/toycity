namespace ToyCity.Sim;

/// <summary>
/// The region tells a city how high its rivers stand when that changes. Logged, so a city's log replays it alone.
/// Stage is the steps (2 m each) the water rises over its usual surface: 0 normal, up to 3.
/// </summary>
public sealed record FloodAction(int Stage) : SimAction;

/// <summary>
/// Beta 3.1 part 5: floods. Heavy rain over a river's catchment raises it, and the rise travels downstream, so a
/// storm over the hills floods the town below a day or two later, across a border if the river crosses one. The
/// water spreads from the river over low ground beside it: buildings standing in it are damaged each day it lasts.
/// </summary>
public sealed partial class World
{
    /// <summary>How far, in cells, floodwater spreads from a river over ground low enough to take it.</summary>
    public const int FloodReach = 5;
    /// <summary>Condition a building loses each day it stands in floodwater.</summary>
    public const int FloodDamagePerDay = 6;

    /// <summary>How many steps the rivers stand over their usual surface today, from the region. Saved.</summary>
    public int FloodStage;

    /// <summary>
    /// Each cell's floodwater surface in steps, or 0 where it's dry: the ground beside a river that the rise reaches.
    /// Worked out from the stage, not saved.
    /// </summary>
    public byte[] Flooded => _flooded ??= FloodedCells(Width, HeightCells, Height, River, WaterLevel, FloodStage);
    byte[]? _flooded;

    /// <summary>
    /// The floodwater for a map at a stage (the client works it out the same way from what it's sent): from each
    /// river cell outward over dry land, at most <see cref="FloodReach"/> cells, onto every cell lower than the
    /// river's raised surface, through cells that are themselves under it.
    /// </summary>
    public static byte[] FloodedCells(int width, int height, byte[] ground, byte[] river, int waterLevel, int stage)
    {
        int n = width * height;
        var water = new byte[n];
        if (stage <= 0) return water;
        var dist = new int[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (river[i] > ground[i])
            {
                water[i] = (byte)Math.Min(255, river[i] + stage);
                queue.Enqueue(i);
            }
        ReadOnlySpan<int> dx = [0, 1, 0, -1], dy = [-1, 0, 1, 0];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (dist[c] >= FloodReach) continue;
            int x = c % width, y = c / width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int j = ny * width + nx;
                if (water[j] != 0 || ground[j] < waterLevel || river[j] > ground[j] || ground[j] >= water[c]) continue;
                water[j] = water[c];
                dist[j] = dist[c] + 1;
                queue.Enqueue(j);
            }
        }
        // The river cells themselves are rivers, not flood.
        for (int i = 0; i < n; i++) if (river[i] > ground[i]) water[i] = 0;
        return water;
    }

    void ApplyFlood(FloodAction a)
    {
        int was = FloodStage;
        FloodStage = Math.Clamp(a.Stage, 0, 3);
        _flooded = null;
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
    public byte[] FloodPlain(int stage) => FloodedCells(Width, HeightCells, Height, River, WaterLevel, stage);

    /// <summary>Called when the ground changes, so the floodwater is worked out again.</summary>
    void FloodGroundChanged() => _flooded = null;
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
        int wide = Cols * TileSize, high = Rows * TileSize;
        var samples = new List<(long, long, int)>[Tiles.Length];
        for (int k = 0; k < samples.Length; k++) samples[k] = new();
        int TileIndex(int i)
        {
            int col = i % wide / TileSize, row = i / wide / TileSize;
            return Array.FindIndex(Tiles, t => t.Col == col && t.Row == row);
        }
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

    /// <summary>
    /// How high the rivers through a tile stand on a day: heavy rain (stage 1) or storms (stage 2) over any stretch
    /// upstream when its water would reach the tile, staying up two days after; 3 when storms meet a river already up.
    /// </summary>
    public int FloodStageAt(int tileIndex, long tick)
    {
        var samples = FloodSamples()[tileIndex];
        if (samples.Count == 0) return 0;
        var cache = new Dictionary<long, List<Front>>();
        int best = 0, wet = 0;
        foreach (var (x, y, lag) in samples)
            for (int d = 0; d <= 2; d++)
            {
                long when = tick - lag - d;
                if (when < 0) continue;
                if (!cache.TryGetValue(when, out var fronts)) cache[when] = fronts = Fronts(when).Where(f => f.Kind == WeatherKind.Rain).ToList();
                foreach (var f in fronts)
                {
                    long ddx = f.X - x, ddy = f.Y - y;
                    if (ddx * ddx + ddy * ddy > (long)f.Radius * f.Radius) continue;
                    int stage = f.Strength - 1;
                    if (stage > 0) wet++;
                    best = Math.Max(best, stage);
                }
            }
        // Storms over much of the catchment at once push it a step higher.
        if (best == 2 && wet > samples.Count) best = 3;
        return best;
    }

    /// <summary>Tells each city its rivers' stage when it changed. A lone city has no region sky, so no floods.</summary>
    void SendFloods()
    {
        if (Tiles.Length < 2) return;
        for (int k = 0; k < Tiles.Length; k++)
        {
            if (Tiles[k].City is not { } w) continue;
            int s = FloodStageAt(k, Tick);
            if (s != w.FloodStage) w.Enqueue(new FloodAction(s));
        }
    }
}
