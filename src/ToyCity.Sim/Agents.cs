using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToyCity.Sim;

public enum AgentTier : byte { Aggregate = 0, Light = 1, Full = 2 }

/// <summary>
/// How many citizens this machine follows in detail. Full agents are simulated trip by trip every day and drawn on
/// the map; light agents get a monthly record; everyone else is counted in their household. Tiers are a per-machine
/// setting chosen by the benchmark: they only change what is shown, never the city, so two machines with
/// different tiers still run the same simulation.
/// </summary>
public sealed record AgentTiers(int Full, int Light)
{
    public static readonly AgentTiers Default = new(2000, 20000);

    /// <summary>Loads a profile written by the benchmark, or null when there is none or it can't be read.</summary>
    public static AgentProfile? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AgentProfile>(File.ReadAllText(path), AgentProfile.Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>What the benchmark measured on one machine and the tiers it chose.</summary>
public sealed record AgentProfile(
    string Machine, int Cores, string Os, string Runtime, DateTime MeasuredUtc, int Population,
    double StepMs, double FullMicrosPerAgentDay, double LightMicrosPerAgentMonth, double BudgetMs, int Full, int Light)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    [JsonIgnore] public AgentTiers Tiers => new(Full, Light);
}

/// <summary>
/// A full agent's trip to work: the cells it passes and when it leaves. Offset is the part of the trip the cells'
/// travel times don't cover (walking to a stop, waiting, no car), in the same units, so the agent's minutes agree
/// with its household's.
/// </summary>
public sealed record AgentTrip(long Id, int HomeId, int Slot, int SiteId, bool ByTransit, int DepartMinute, int[] Cells, int Offset);

/// <summary>Running totals for a full agent, kept by the view, not the city.</summary>
public sealed class AgentStats
{
    public int Days;
    public int Late;
    public long Minutes;
}

/// <summary>
/// The per-machine view of the city's people. It reads the world and never writes it, and draws no random
/// numbers, so the city's hash is the same whatever tiers it runs with.
/// </summary>
public sealed class AgentView
{
    readonly World _w;
    public AgentTiers Tiers { get; set; }
    readonly Dictionary<long, AgentStats> _stats = new();
    readonly List<AgentTrip> _trips = new();
    readonly HashSet<long> _light = new();
    long _selectedAt = -1;

    /// <summary>Work starts at nine; departures spread from 7:45 by a per-person hash, plus a little each day.</summary>
    public const int ShiftStart = 9 * 60, DepartBase = 7 * 60 + 45;

    public AgentView(World w, AgentTiers tiers) { _w = w; Tiers = tiers; }

    public IReadOnlyList<AgentTrip> Trips => _trips;
    public int LightCount => _light.Count;
    public int Citizens { get; private set; }
    /// <summary>Bumped whenever the set of full agents or their trips change, so a host knows to resend them.</summary>
    public int Version { get; private set; }

    public AgentTier TierOf(long id) =>
        _stats.ContainsKey(id) ? AgentTier.Full : _light.Contains(id) ? AgentTier.Light : AgentTier.Aggregate;

    public AgentStats? StatsOf(long id) => _stats.GetValueOrDefault(id);

    /// <summary>
    /// Picks who is followed: employed citizens in home order fill the full tier, then everyone else in home order
    /// fills the light tier. Runs monthly, or when the tiers change.
    /// </summary>
    public void Select()
    {
        _selectedAt = _w.MonthIndex;
        var trips = new List<AgentTrip>();
        _light.Clear();
        int citizens = 0;
        var rest = new List<long>();
        foreach (var home in _w.Buildings)
        {
            if (home is not { Residents: > 0, Operating: true }) continue;
            foreach (var c in _w.CitizensOf(home))
            {
                citizens++;
                if (c.SiteId >= 0 && trips.Count < Tiers.Full && AssignmentOf(home, c.Slot) is { } a) trips.Add(TripOf(home, c, a));
                else rest.Add(c.Id);
            }
        }
        foreach (var id in rest) { if (_light.Count >= Tiers.Light) break; _light.Add(id); }
        Citizens = citizens;
        bool changed = trips.Count != _trips.Count || trips.Where((t, k) => !SameTrip(t, _trips[k])).Any();
        _trips.Clear();
        _trips.AddRange(trips);
        foreach (var id in _stats.Keys.Except(trips.Select(t => t.Id)).ToList()) _stats.Remove(id);
        foreach (var t in trips) _stats.TryAdd(t.Id, new AgentStats());
        if (changed) Version++;
    }

    static bool SameTrip(AgentTrip a, AgentTrip b) => a.Id == b.Id && a.SiteId == b.SiteId && a.Cells.AsSpan().SequenceEqual(b.Cells);

    static Assignment? AssignmentOf(Building home, int slot)
    {
        int k = 0;
        foreach (var a in home.Assignments)
        {
            if (slot < k + a.Count) return a;
            k += a.Count;
        }
        return null;
    }

    AgentTrip TripOf(Building home, Citizen c, Assignment a)
    {
        // Street legs and ride legs in travel order: the cells the dot on the map moves through.
        var cells = new List<int>(a.PathCells.Length + a.RideCells.Length);
        if (a.ByTransit) foreach (var st in a.RideCells) cells.Add(st % _w.N);
        else cells.AddRange(a.PathCells);
        int depart = DepartBase + (int)(_w.Hash(c.HomeId, c.Slot, c.SinceMonth, 99) % 50);
        var arr = cells.ToArray();
        return new AgentTrip(c.Id, home.Id, c.Slot, a.SiteId, a.ByTransit, depart, arr, Math.Max(0, a.Minutes * 600 - CellsCost(arr)));
    }

    /// <summary>How often (days) a full agent picks its own route again at the day's congestion.</summary>
    public const int RerouteEvery = 7;

    /// <summary>
    /// Daily: each full agent makes its trip at today's congestion, leaving a few minutes earlier or later than
    /// usual, and is late when it arrives after nine. Drivers find their own way again once a week, staggered, so a
    /// jammed road loses the agents who know a better one.
    /// </summary>
    public void Daily()
    {
        int day = (int)_w.Tick;
        // Monthly, and weekly while nobody is followed yet (a new city has no jobs on its first day).
        if (_selectedAt != _w.MonthIndex || (_trips.Count == 0 && day % RerouteEvery == 0)) Select();
        for (int k = 0; k < _trips.Count; k++)
        {
            var t = _trips[k];
            if (!t.ByTransit && (t.Id ^ day) % RerouteEvery == 0 && _w.Buildings[t.HomeId] is { Access: >= 0 } home
                && t.SiteId < _w.Buildings.Count && _w.Buildings[t.SiteId] is { Access: >= 0 } site && Route(home.Access, site.Access) is { } cells)
                _trips[k] = t = t with { Cells = cells };
            int minutes = MinutesOf(t);
            int jitter = (int)(_w.Hash((int)(t.Id >> 32), t.Slot, day, 98) % 21) - 10;
            var s = _stats[t.Id];
            s.Days++;
            s.Minutes += minutes;
            if (t.DepartMinute + jitter + minutes > ShiftStart) s.Late++;
        }
    }

    /// <summary>Door-to-door minutes for a trip at today's travel times.</summary>
    public int MinutesOf(AgentTrip t) => Math.Max(1, (CellsCost(t.Cells) + t.Offset) / 600);

    int CellsCost(int[] cells)
    {
        long ds = 0;
        foreach (int cell in cells) ds += Math.Max(1, _w.TravelCost[cell]);
        return (int)Math.Min(int.MaxValue, ds);
    }

    public World World => _w;

    // Scratch for routing; the view's own, so it never disturbs the city's.
    int[] _dist = Array.Empty<int>(), _parent = Array.Empty<int>(), _stamp = Array.Empty<int>();
    int _gen;
    readonly PriorityQueue<int, long> _pq = new();
    static readonly int[] Dx = { 1, -1, 0, 0 }, Dy = { 0, 0, 1, -1 };

    /// <summary>Fastest street route between two cells at today's travel times, or null when there is none.</summary>
    int[]? Route(int from, int to)
    {
        int n = _w.N;
        if (_dist.Length < n) { _dist = new int[n]; _parent = new int[n]; _stamp = new int[n]; _gen = 0; }
        if (++_gen == int.MaxValue) { Array.Clear(_stamp); _gen = 1; }
        _pq.Clear();
        void Push(int c, int cost, int parent)
        {
            if (_stamp[c] == _gen && _dist[c] <= cost) return;
            _stamp[c] = _gen; _dist[c] = cost; _parent[c] = parent;
            _pq.Enqueue(c, (long)cost * n + c);
        }
        Push(from, _w.TravelCost[from], -1);
        while (_pq.TryDequeue(out int c, out long pri))
        {
            int cost = (int)(pri / n);
            if (cost != _dist[c]) continue;
            if (cost > World.MaxCommute) return null;
            if (c == to)
            {
                var cells = new List<int>();
                for (int p = c; p >= 0; p = _parent[p]) cells.Add(p);
                cells.Reverse();
                return cells.ToArray();
            }
            int x = c % _w.Width, y = c / _w.Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx[k], ny = y + Dy[k];
                if (!_w.InBounds(nx, ny)) continue;
                int i = _w.Idx(nx, ny);
                if (_w.ProfileAt(i) is { } p && World.StreetPassable(p)) Push(i, cost + _w.TravelCost[i], c);
            }
        }
        return null;
    }
}

/// <summary>
/// Measures this machine and picks agent tiers: how many full agents fit in half a tick at top speed next to the
/// city's own step, and ten times as many light ones (capped by what their monthly refresh costs).
/// </summary>
public static class AgentBenchmark
{
    /// <summary>
    /// What a day of agents may cost. The fastest speed allows 833 ms a day, but the world is locked while it steps, so
    /// this stays short enough that queries and clicks never wait on it.
    /// </summary>
    public const double BudgetMs = 25;

    public static AgentProfile Run(ContentDb content, bool quick, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var w = ReferenceCity(content, quick);
        int years = quick ? 1 : 3;
        w.Run(years * World.DaysPerMonth * World.MonthsPerYear);
        log?.Invoke($"reference city: {w.Population:N0} people, built in {sw.Elapsed.TotalSeconds:F1} s");

        // The city's own step, median of a month of days.
        var steps = new List<double>();
        for (int d = 0; d < World.DaysPerMonth; d++)
        {
            long t0 = Stopwatch.GetTimestamp();
            w.Step();
            steps.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        steps.Sort();
        double stepMs = steps[steps.Count / 2];

        // Everyone with a job as a full agent: the daily trip cost per agent.
        var view = new AgentView(w, new AgentTiers(int.MaxValue, 0));
        view.Select();
        int agents = Math.Max(1, view.Trips.Count);
        int reps = quick ? 10 : 30;
        long t1 = Stopwatch.GetTimestamp();
        for (int k = 0; k < reps; k++) view.Daily();
        double fullUs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds * 1000 / (reps * (double)agents);

        // Light agents: the monthly pick and record refresh, per citizen.
        var lightView = new AgentView(w, new AgentTiers(0, int.MaxValue));
        long t2 = Stopwatch.GetTimestamp();
        for (int k = 0; k < 3; k++) lightView.Select();
        double lightUs = Stopwatch.GetElapsedTime(t2).TotalMilliseconds * 1000 / (3 * (double)Math.Max(1, lightView.Citizens));

        // Scale the step to a city four times this size, so a growing city keeps its frame rate.
        double spare = Math.Max(BudgetMs * 0.2, BudgetMs - stepMs * 4);
        int full = (int)Math.Clamp(spare * 1000 / Math.Max(0.01, fullUs), 200, 200_000) / 100 * 100;
        // Light refreshes once a month, spread over its thirty days: give it a fifth of the budget.
        int light = (int)Math.Clamp(Math.Min(full * 10L, (long)(BudgetMs * 0.2 * 1000 * World.DaysPerMonth / Math.Max(0.01, lightUs))), full, 2_000_000) / 100 * 100;
        log?.Invoke($"step {stepMs:F2} ms, full agent {fullUs:F2} µs a day, light agent {lightUs:F2} µs a month -> full {full:N0}, light {light:N0}");
        return new AgentProfile(Environment.MachineName, Environment.ProcessorCount, System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, DateTime.UtcNow, w.Population,
            Math.Round(stepMs, 3), Math.Round(fullUs, 3), Math.Round(lightUs, 3), BudgetMs, full, light);
    }

    public static void Save(AgentProfile p, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(p, AgentProfile.Json));
    }

    /// <summary>The benchmark's city: one starter town for a quick run, a starter town on every dry 40x40 block otherwise.</summary>
    public static World ReferenceCity(ContentDb content, bool quick)
    {
        var w = World.Create(content, 7, quick ? 128 : 256, "Benchville");
        if (quick) { StarterTown.Apply(w); return w; }
        w.Funds = 50_000_000;
        for (int oy = 2; oy + 38 < w.HeightCells; oy += 40)
        for (int ox = 2; ox + 38 < w.Width; ox += 40)
        {
            bool dry = true;
            for (int y = oy; y < oy + 36 && dry; y++)
                for (int x = ox; x < ox + 38; x++) if (w.IsWater(w.Idx(x, y))) { dry = false; break; }
            if (dry) foreach (var a in StarterTown.Build(ox, oy)) w.Enqueue(a);
        }
        return w;
    }
}
