namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>Longest commute anyone accepts, in deciseconds (60 minutes).</summary>
    public const int MaxCommute = 60 * 600;

    // Scratch for the path search; never saved.
    int[] _dist = Array.Empty<int>(), _parent = Array.Empty<int>(), _stamp = Array.Empty<int>(), _via = Array.Empty<int>();
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
    /// Sends a home's workers to the nearest job sites with free places, by travel time. Workers without a car
    /// search first, with a penalty on the street; everyone may take transit when it is faster.
    /// </summary>
    internal void MatchCommutes(Building home)
    {
        ReleaseAssignments(home);
        int labor = LaborOf(home);
        if (labor == 0 || !home.Operating) return;
        int carless = CarlessOf(home);
        long weighted = 0;
        if (carless > 0) weighted += Search(home, carless, CarlessPenalty);
        if (labor > carless) weighted += Search(home, labor - carless, 0);
        home.CommuteMinutes = home.Employed > 0 ? (int)(weighted / home.Employed) : 0;
    }

    /// <summary>
    /// Dijkstra over the street layer, the ride layers, stops and job sites, with ties broken on node number so the
    /// result is deterministic. Nodes: layer * N + cell, then one per stop (just got off), then one per job site
    /// (reached on foot from a stop). Returns the workers' minutes, summed.
    /// </summary>
    long Search(Building home, int need, int streetPenalty)
    {
        int ln = Layers * N, nb = Buildings.Count, m = ln + 2 * nb;
        if (_dist.Length < m)
        {
            int size = m + 4096;
            _dist = new int[size]; _parent = new int[size]; _stamp = new int[size]; _via = new int[size];
            _gen = 0;
        }
        if (++_gen == int.MaxValue) { Array.Clear(_stamp); _gen = 1; }
        _pq.Clear();

        void Push(int node, int cost, int parent, int via)
        {
            if (_stamp[node] == _gen && _dist[node] <= cost) return;
            _stamp[node] = _gen;
            _dist[node] = cost;
            _parent[node] = parent;
            _via[node] = via;
            _pq.Enqueue(node, (long)cost * m + node);
        }

        if (home.Access >= 0) Push(home.Access, TravelCost[home.Access] + streetPenalty, -1, -1);
        foreach (var s in _stops)
        {
            var sb = Buildings[s.Id]!;
            int d = RectGap(home, sb);
            if (d > TypeOf(sb).Catchment) continue;
            int c = StopWalkBase + d * StopWalkPerCell + StopWait(sb);
            foreach (int st in s.RideStates) Push(st, c, -1, s.Id);
        }

        var seen = new HashSet<int>();
        long weightedMinutes = 0;

        void Take(int sid, int node, int cost)
        {
            if (sid == home.Id || Buildings[sid] is not { } site || !IsJobSite(site) || seen.Contains(sid)) return;
            int free = site.Jobs - site.Workers;
            if (free <= 0) return;
            seen.Add(sid);
            int take = Math.Min(need, free);
            var cells = new List<int>();
            var ride = new List<int>();
            var stops = new List<int>();
            for (int p = node; p >= 0; p = _parent[p])
            {
                if (p < N) cells.Add(p);
                else if (p < ln) { ride.Add(p); if (_via[p] >= 0) stops.Add(_via[p]); }
                else if (p < ln + nb) stops.Add(p - ln);
            }
            cells.Reverse();
            ride.Reverse();
            stops.Reverse();
            int minutes = Math.Max(1, cost / 600);
            home.Assignments.Add(new Assignment
            {
                SiteId = sid, Count = take, Minutes = minutes, PathCells = cells.ToArray(), RideCells = ride.ToArray(), Stops = stops.ToArray(),
            });
            site.Workers += take;
            home.Employed += take;
            weightedMinutes += (long)minutes * take;
            need -= take;
        }

        while (need > 0 && _pq.TryDequeue(out int node, out long pri))
        {
            int cost = (int)(pri / m);
            if (cost != _dist[node]) continue;
            if (cost > MaxCommute) break;
            if (node < N)
            {
                // Street: arrive at job sites by their driveway, or carry on along any path people can use.
                if (_sitesByAccess.TryGetValue(node, out var sites))
                    foreach (int sid in sites) { if (need == 0) break; Take(sid, node, cost); }
                int x = node % Width, y = node / Width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + Dx4[k], ny = y + Dy4[k];
                    if (!InBounds(nx, ny)) continue;
                    int n = Idx(nx, ny);
                    if (ProfileAt(n) is { } p && StreetPassable(p)) Push(n, cost + TravelCost[n], node, -1);
                }
            }
            else if (node < ln)
            {
                // Riding: get off at a stop, or stay on to the next cell.
                var kind = (TransitKind)(node / N);
                int cell = node % N;
                if (_stopsAtState.TryGetValue(node, out var here))
                    foreach (int sid in here) Push(ln + sid, cost + AlightTime, node, -1);
                int x = cell % Width, y = cell / Width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + Dx4[k], ny = y + Dy4[k];
                    if (!InBounds(nx, ny)) continue;
                    int n = Idx(nx, ny);
                    if (Carries(kind, n)) Push((int)kind * N + n, cost + RideCost(kind, n), node, -1);
                }
            }
            else if (node < ln + nb)
            {
                // Just got off: walk to work, or to another stop to change.
                var info = _stopById[node - ln];
                foreach (var (site, d) in info.Sites) Push(ln + nb + site, cost + StopWalkBase + d * StopWalkPerCell, node, -1);
                foreach (var (t, d) in info.Transfers)
                {
                    int c = cost + StopWalkBase + d * StopWalkPerCell + StopWait(Buildings[t]!);
                    foreach (int st in _stopById[t].RideStates) Push(st, c, node, t);
                }
            }
            else Take(node - ln - nb, node, cost);
        }
        return weightedMinutes;
    }

    /// <summary>Daily: homes whose (jittered) day this is re-pick their jobs.</summary>
    void RunCommutes()
    {
        int day = Day - 1;
        foreach (var b in Buildings)
            if (b is { Operating: true, Residents: > 0 } && RematchDay(b) == day) MatchCommutes(b);
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
        Array.Clear(Riders);
        TransitBoardings = 0;
        foreach (var b in Buildings) if (b != null && TypeOf(b).Transit != TransitKind.None) b.Riders = 0;
        var bus = new int[N];
        foreach (var home in Buildings)
        {
            if (home == null) continue;
            foreach (var a in home.Assignments)
            {
                foreach (var c in a.PathCells) Trips[c] += a.Count * 2;
                foreach (var st in a.RideCells)
                {
                    Riders[st % N] += a.Count * 2;
                    if (st / N == (int)TransitKind.Bus) bus[st % N] += a.Count * 2;
                }
                foreach (var sid in a.Stops)
                    if (sid < Buildings.Count && Buildings[sid] is { } stop) stop.Riders += a.Count * 2;
                TransitBoardings += a.Count * 2 * (a.Stops.Length / 2);
            }
        }
        // A bus carries about twenty commuters: it adds that much less to the traffic than their cars would.
        for (int i = 0; i < N; i++) if (bus[i] > 0) Trips[i] += Math.Max(1, bus[i] / 20);
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
