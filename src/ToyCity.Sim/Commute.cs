namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>Longest commute anyone accepts, in deciseconds (60 minutes).</summary>
    public const int MaxCommute = 60 * 600;

    // Scratch for the path search; never saved.
    int[]? _dist, _parent, _stamp;
    int _gen;
    readonly PriorityQueue<int, long> _pq = new();

    bool IsJobSite(Building s) => s.Operating && s.Powered && s.Jobs > 0;

    /// <summary>Unhooks a home from its job sites.</summary>
    internal void ReleaseAssignments(Building b)
    {
        foreach (var a in b.Assignments)
        {
            if (a.SiteId < Buildings.Count && Buildings[a.SiteId] is { } site)
                site.Workers = Math.Max(0, site.Workers - a.Count);
        }
        b.Assignments.Clear();
        b.Employed = 0;
        b.CommuteMinutes = 0;
    }

    /// <summary>
    /// Sends a home's workers to the nearest job sites with free places, by travel time over paths.
    /// Dijkstra with ties broken on cell index, so the result is deterministic.
    /// </summary>
    internal void MatchCommutes(Building home)
    {
        ReleaseAssignments(home);
        int need = LaborOf(home);
        if (need == 0 || !home.Operating) return;

        _dist ??= new int[N];
        _parent ??= new int[N];
        _stamp ??= new int[N];
        if (++_gen == int.MaxValue) { Array.Clear(_stamp); _gen = 1; }
        _pq.Clear();

        void Push(int cell, int cost, int parent)
        {
            if (_stamp[cell] == _gen && _dist[cell] <= cost) return;
            _stamp[cell] = _gen;
            _dist[cell] = cost;
            _parent[cell] = parent;
            _pq.Enqueue(cell, (long)cost * N + cell);
        }

        if (home.Access < 0) return;
        Push(home.Access, TravelCost[home.Access], -1);

        var seen = new HashSet<int>();
        long weightedMinutes = 0;
        while (_pq.TryDequeue(out int cell, out long pri) && need > 0)
        {
            int cost = (int)(pri / N);
            if (cost != _dist[cell]) continue;
            if (cost > MaxCommute) break;
            if (_sitesByAccess.TryGetValue(cell, out var sites))
            {
                foreach (int sid in sites)
                {
                    if (need == 0) break;
                    if (sid == home.Id || Buildings[sid] is not { } site || !IsJobSite(site) || !seen.Add(sid)) continue;
                    int free = site.Jobs - site.Workers;
                    if (free <= 0) continue;
                    int take = Math.Min(need, free);
                    var cells = new List<int>();
                    for (int p = cell; p >= 0; p = _parent[p]) cells.Add(p);
                    cells.Reverse();
                    int minutes = Math.Max(1, cost / 600);
                    home.Assignments.Add(new Assignment { SiteId = sid, Count = take, Minutes = minutes, PathCells = cells.ToArray() });
                    site.Workers += take;
                    home.Employed += take;
                    weightedMinutes += (long)minutes * take;
                    need -= take;
                }
            }
            int x = cell % Width, y = cell / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int n = Idx(nx, ny);
                if (Path[n] != 0) Push(n, cost + TravelCost[n], cell);
            }
        }
        home.CommuteMinutes = home.Employed > 0 ? (int)(weightedMinutes / home.Employed) : 0;
    }

    /// <summary>Daily: homes whose id falls on today re-pick their jobs.</summary>
    void RunCommutes()
    {
        int slot = Day - 1;
        for (int id = slot; id < Buildings.Count; id += DaysPerMonth)
        {
            if (Buildings[id] is { } b && b.Operating && b.Residents > 0) MatchCommutes(b);
        }
    }

    /// <summary>Monthly: rebuilds worker counts from the homes' assignments and drops trips to sites that are gone.</summary>
    void RecountWorkers()
    {
        foreach (var b in Buildings) if (b != null) b.Workers = 0;
        foreach (var home in Buildings)
        {
            if (home == null || home.Assignments.Count == 0) continue;
            home.Assignments.RemoveAll(a => a.SiteId >= Buildings.Count || Buildings[a.SiteId] is not { } s || !s.Operating);
            int employed = 0;
            long weighted = 0;
            foreach (var a in home.Assignments)
            {
                Buildings[a.SiteId]!.Workers += a.Count;
                employed += a.Count;
                weighted += (long)a.Minutes * a.Count;
            }
            home.Employed = employed;
            home.CommuteMinutes = employed > 0 ? (int)(weighted / employed) : 0;
        }
    }

    /// <summary>Monthly: commuter trips per path cell, congestion level and congested travel time.</summary>
    void RunTraffic()
    {
        Array.Clear(Trips);
        foreach (var home in Buildings)
        {
            if (home == null) continue;
            foreach (var a in home.Assignments)
                foreach (var c in a.PathCells) Trips[c] += a.Count * 2;
        }
        if (Mod(ModKey.Traffic) != 0)
            for (int i = 0; i < N; i++) if (Trips[i] > 0) Trips[i] = Scaled(Trips[i], ModKey.Traffic);
        for (int i = 0; i < N; i++)
        {
            var p = ProfileAt(i);
            if (p == null) { Traffic[i] = 0; TravelCost[i] = 0; continue; }
            // Commuters spread over two peak hours, so a path carries about twice its hourly capacity in a day.
            int capacityPerDay = Math.Max(1, p.CapacityPerHour * 2);
            int level = (int)Math.Min(255, (long)Trips[i] * 128 / capacityPerDay);
            Traffic[i] = (byte)level;
            // BPR congestion curve: time = free * (1 + 0.15 (v/c)^4), capped at 5x.
            double vc = level / 128.0;
            double factor = Math.Min(5.0, 1 + 0.15 * vc * vc * vc * vc);
            TravelCost[i] = (int)(p.CellTime * factor);
        }
    }
}
