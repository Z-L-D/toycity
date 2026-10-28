using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// Side games' transit lines in the commute search. A leased stop is served only by the lines of the company that
/// leases it; an unleased stop keeps the city's own bus service. Each line is a chain of nodes, one per stop: people
/// board at a stop (waiting half the headway, longer when the line is crowded, plus the fare weighed as time), ride
/// from node to node along the street route between stops, and get off at any stop on the way.
/// </summary>
public sealed partial class World
{
    /// <summary>Fare weighed as time: a $1.50 fare feels like a minute and a half on the way.</summary>
    public const int FareDecisecondsPerCent = 6;
    /// <summary>A bus stopping at a stop: thirty seconds.</summary>
    public const int DwellTime = 300;

    sealed class CompanyLine
    {
        public int GameSlot;
        public int LineId;
        public int[] StopIds = Array.Empty<int>();
        /// <summary>Time from stop i to stop i + 1, in deciseconds, and the street cells between them.</summary>
        public int[] SegCost = Array.Empty<int>();
        public int[][] SegCells = Array.Empty<int[]>();
        public int BoardCost;
        /// <summary>Index of this line's first node among the line nodes.</summary>
        public int Base;
    }

    // Rebuilt with the stops every tick from the side games' last output. Never saved.
    readonly List<CompanyLine> _lines = new();
    readonly Dictionary<int, List<int>> _lineNodesAt = new();
    int _lineNodes;
    /// <summary>Line node → its line.</summary>
    int[] _lineOfNode = Array.Empty<int>();
    /// <summary>Bus routes between two cells, kept until the streets or their traffic change. Never saved.</summary>
    readonly Dictionary<(int, int), (int Cost, int[] Cells)?> _busRoutes = new();

    void ForgetBusRoutes() => _busRoutes.Clear();

    /// <summary>Whether a stop is leased: then only its company's lines stop there.</summary>
    bool StopLeased(int buildingId) => _leasedStops.Contains(buildingId);
    readonly HashSet<int> _leasedStops = new();

    void RebuildLines()
    {
        _lines.Clear();
        _lineNodesAt.Clear();
        _lineNodes = 0;
        for (int slot = 0; slot < SideGames.Count; slot++)
        {
            var hg = SideGames[slot];
            foreach (var line in hg.Last.Lines)
            {
                if (line.StopLeaseIds.Count < 2 || line.HeadwayMinutes <= 0 || line.SeatsPerHour <= 0) continue;
                var stops = new List<int>();
                int fare = Math.Max(0, line.FareCents);
                bool ok = true;
                foreach (int leaseId in line.StopLeaseIds)
                {
                    var lease = LeaseById(leaseId);
                    if (lease == null || lease.GameId != hg.Id || lease.Kind != "bus-stop" || !_stopById.ContainsKey(lease.BuildingId)) { ok = false; break; }
                    if (Buildings[lease.BuildingId]!.Access < 0) { ok = false; break; }
                    if (lease.Terms.Mode == LeaseMode.PublicService) fare = Math.Min(fare, lease.Terms.FareCapCents);
                    stops.Add(lease.BuildingId);
                }
                if (!ok) continue;
                var cl = new CompanyLine { GameSlot = slot, LineId = line.LineId, StopIds = stops.ToArray() };
                cl.SegCost = new int[stops.Count - 1];
                cl.SegCells = new int[stops.Count - 1][];
                for (int i = 0; i + 1 < stops.Count; i++)
                {
                    var route = BusRoute(Buildings[stops[i]]!.Access, Buildings[stops[i + 1]]!.Access);
                    if (route is not var (cost, cells)) { ok = false; break; }
                    cl.SegCost[i] = cost + DwellTime;
                    cl.SegCells[i] = cells;
                }
                if (!ok) continue;
                // Waiting: half the headway on a frequent line; on a sparse one people check the timetable, so three
                // minutes and a tenth of the headway. Stretched when riders outnumber seats at the peak.
                hg.Ridership.TryGetValue(line.LineId, out var r);
                double vc = r.Riders / 4.0 / line.SeatsPerHour;
                double crowd = Math.Min(5.0, 1 + 0.15 * vc * vc * vc * vc);
                int wait = Math.Min(line.HeadwayMinutes * 300, 1800 + line.HeadwayMinutes * 60);
                cl.BoardCost = (int)(wait * crowd) + fare * FareDecisecondsPerCent;
                cl.Base = _lineNodes;
                for (int i = 0; i < stops.Count; i++)
                {
                    if (!_lineNodesAt.TryGetValue(stops[i], out var list)) _lineNodesAt[stops[i]] = list = new List<int>();
                    list.Add(_lineNodes + i);
                }
                _lineNodes += stops.Count;
                _lines.Add(cl);
            }
        }
        if (_lineOfNode.Length < _lineNodes) _lineOfNode = new int[_lineNodes + 64];
        for (int k = 0; k < _lines.Count; k++)
            for (int i = 0; i < _lines[k].StopIds.Length; i++) _lineOfNode[_lines[k].Base + i] = k;
    }

    /// <summary>The fastest bus route between two street cells at today's traffic, in deciseconds, or null.</summary>
    (int Cost, int[] Cells)? BusRoute(int from, int to)
    {
        if (_busRoutes.TryGetValue((from, to), out var known)) return known;
        (int, int[])? result = null;
        if (_rDist.Length < N) { _rDist = new int[N]; _rParent = new int[N]; _rStamp = new int[N]; _rGen = 0; }
        if (++_rGen == int.MaxValue) { Array.Clear(_rStamp); _rGen = 1; }
        _rPq.Clear();
        void Push(int c, int cost, int parent)
        {
            if (_rStamp[c] == _rGen && _rDist[c] <= cost) return;
            _rStamp[c] = _rGen; _rDist[c] = cost; _rParent[c] = parent;
            _rPq.Enqueue(c, (long)cost * N + c);
        }
        if (Carries(TransitKind.Bus, from) && Carries(TransitKind.Bus, to)) Push(from, 0, -1);
        while (_rPq.TryDequeue(out int c, out long pri))
        {
            int cost = (int)(pri / N);
            if (cost != _rDist[c]) continue;
            if (c == to)
            {
                var cells = new List<int>();
                for (int p = c; p >= 0; p = _rParent[p]) cells.Add(p);
                cells.Reverse();
                result = (cost, cells.ToArray());
                break;
            }
            int x = c % Width, y = c / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int i = Idx(nx, ny);
                if (Carries(TransitKind.Bus, i)) Push(i, cost + RideCost(TransitKind.Bus, i), c);
            }
        }
        _busRoutes[(from, to)] = result;
        return result;
    }

    /// <summary>
    /// Riders on each company line from today's commutes: each worker rides there and back, boarding once at each end.
    /// Runs daily before the side games' tick.
    /// </summary>
    void CountLineRiders()
    {
        foreach (var hg in SideGames) hg.Ridership.Clear();
        if (SideGames.Count == 0) return;
        var stopsOf = new Dictionary<(int, int), int>();
        for (int slot = 0; slot < SideGames.Count; slot++)
            foreach (var l in SideGames[slot].Last.Lines) stopsOf[(slot, l.LineId)] = l.StopLeaseIds.Count;
        foreach (var home in Buildings)
        {
            if (home == null) continue;
            foreach (var a in home.Assignments)
            {
                var legs = a.LineLegs;
                for (int k = 0; k + 3 < legs.Length; k += 4)
                {
                    int slot = legs[k], line = legs[k + 1], from = legs[k + 2], to = legs[k + 3];
                    if (slot >= SideGames.Count || !stopsOf.TryGetValue((slot, line), out int n) || from >= n || to >= n) continue;
                    var rid = SideGames[slot].Ridership;
                    if (!rid.TryGetValue(line, out var r)) r = (0, new int[n]);
                    r.Boardings[from] += a.Count;
                    r.Boardings[to] += a.Count;
                    rid[line] = (r.Riders + a.Count * 2, r.Boardings);
                }
            }
        }
    }
}
