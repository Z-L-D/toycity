namespace ToyCity.Sim;

/// <summary>
/// Bus, tram and rail. Stops are civic buildings; vehicles run on paths their profile allows. The commute search
/// (Commute.cs) has one street layer and one ride layer per vehicle kind: people walk to a stop within its
/// catchment, wait, ride, may change at a nearby stop, and walk from the last stop to work. They pick whichever way
/// is fastest, so transit wins where roads are slow or congested, and for households without a car.
/// </summary>
public sealed partial class World
{
    /// <summary>Layers of the commute search: 0 is the street (own car, bike or feet), then one per vehicle kind.</summary>
    public const int Layers = 4;
    /// <summary>Walking to or from a stop: half a minute, plus half a minute a cell (deciseconds).</summary>
    public const int StopWalkBase = 300, StopWalkPerCell = 300;
    /// <summary>How far apart two stops may be for a change between them (cells).</summary>
    public const int TransferReach = 3;
    /// <summary>Getting off: half a minute.</summary>
    public const int AlightTime = 300;
    /// <summary>Households without a car still get to work (a lift, a long walk) but it costs them this much more.</summary>
    public const int CarlessPenalty = 8 * 600;
    /// <summary>What the city keeps from each boarding, in cents.</summary>
    public const int FareCents = 4;

    /// <summary>Share of workers without a car, by wealth (percent).</summary>
    static readonly int[] CarlessPercent = { 35, 15, 5 };

    /// <summary>Tram speed when it runs on its own lane.</summary>
    static readonly int TramCellTime = CellTimeAt(40);

    /// <summary>Transit riders per cell, both directions, per day: derived monthly, saved like traffic.</summary>
    public readonly int[] Riders;
    /// <summary>Boardings across the city per day, from last month's commutes.</summary>
    public int TransitBoardings;

    public static int CellTimeAt(int kmh) => Math.Max(1, (int)Math.Round(16.0 / (Math.Max(1, kmh) / 3.6) * 30.0 * 10));

    sealed class StopInfo
    {
        public int Id;
        public TransitKind Kind;
        /// <summary>Search states (layer * N + cell) where its vehicles stop.</summary>
        public int[] RideStates = Array.Empty<int>();
        /// <summary>Job sites within walking reach, with the walk in cells, nearest first.</summary>
        public (int Site, int Dist)[] Sites = Array.Empty<(int, int)>();
        public (int Stop, int Dist)[] Transfers = Array.Empty<(int, int)>();
    }

    // Rebuilt with the driveways every tick. Never saved.
    readonly List<StopInfo> _stops = new();
    readonly Dictionary<int, StopInfo> _stopById = new();
    readonly Dictionary<int, List<int>> _stopsAtState = new();

    /// <summary>Whether a vehicle of this kind may drive through a path cell.</summary>
    public bool Carries(TransitKind k, int cell) => ProfileAt(cell) is { } p && k switch
    {
        TransitKind.Bus => p.Cars,
        TransitKind.Tram => p.Tram != "",
        TransitKind.Rail => p.Rail,
        _ => false,
    };

    /// <summary>Whether people can drive, cycle or walk through a cell (rail lines are trains only).</summary>
    bool StreetPassable(PathProfile p) => p.Cars || p.Bikes || p.Pedestrians;

    /// <summary>Time for a vehicle to cross a cell: buses crawl with the traffic, trams too unless they have a lane.</summary>
    int RideCost(TransitKind k, int cell)
    {
        var p = ProfileAt(cell)!;
        return k switch
        {
            TransitKind.Bus => TravelCost[cell] * 11 / 10,
            TransitKind.Tram => p.Tram == "dedicated" ? TramCellTime : Math.Max(TravelCost[cell], TramCellTime),
            _ => p.CellTime,
        };
    }

    public bool StopWorks(Building b) => b.Operating && (TypeOf(b).PowerKw == 0 || b.Powered) && b.Funding > 0;

    /// <summary>Average wait at a stop, in deciseconds: slower when it handles more people than it was built for.</summary>
    public int StopWait(Building b)
    {
        var t = TypeOf(b);
        double vc = t.Capacity > 0 ? b.Riders / (double)t.Capacity : 0;
        double factor = Math.Min(5.0, 1 + 0.15 * vc * vc * vc * vc);
        // Underfunded lines run less often.
        int funding = Math.Clamp(b.Funding, 25, 200);
        return (int)(t.Wait * 600 * factor * 100 / funding);
    }

    static int RectGap(Building a, Building b)
    {
        int dx = Math.Max(0, Math.Max(a.X - (b.X + b.W - 1), b.X - (a.X + a.W - 1)));
        int dy = Math.Max(0, Math.Max(a.Y - (b.Y + b.H - 1), b.Y - (a.Y + a.H - 1)));
        return dx + dy;
    }

    /// <summary>Indexes stops, the cells their vehicles serve, and what is in walking reach. Runs with RebuildAccess.</summary>
    void RebuildTransit()
    {
        _stops.Clear();
        _stopById.Clear();
        _stopsAtState.Clear();
        foreach (var b in Buildings)
        {
            if (b == null || TypeOf(b).Transit == TransitKind.None || !StopWorks(b)) continue;
            var t = TypeOf(b);
            var info = new StopInfo { Id = b.Id, Kind = t.Transit };
            var states = new List<int>();
            for (int y = b.Y - 1; y <= b.Y + b.H; y++)
            for (int x = b.X - 1; x <= b.X + b.W; x++)
            {
                bool corner = (x == b.X - 1 || x == b.X + b.W) && (y == b.Y - 1 || y == b.Y + b.H);
                if (corner || !InBounds(x, y)) continue;
                int i = Idx(x, y);
                if (Carries(t.Transit, i)) states.Add((int)t.Transit * N + i);
            }
            if (states.Count == 0) continue;
            info.RideStates = states.ToArray();
            var sites = new List<(int, int)>();
            var seen = new HashSet<int>();
            int r = t.Catchment;
            for (int y = Math.Max(0, b.Y - r); y <= Math.Min(HeightCells - 1, b.Y + b.H - 1 + r); y++)
            for (int x = Math.Max(0, b.X - r); x <= Math.Min(Width - 1, b.X + b.W - 1 + r); x++)
            {
                int at = BuildingAt[Idx(x, y)];
                if (at == 0 || !seen.Add(at - 1) || Buildings[at - 1] is not { } s || s.Jobs == 0) continue;
                int d = RectGap(b, s);
                if (d <= r) sites.Add((s.Id, d));
            }
            sites.Sort((p, q) => p.Item2 != q.Item2 ? p.Item2.CompareTo(q.Item2) : p.Item1.CompareTo(q.Item1));
            info.Sites = sites.ToArray();
            _stops.Add(info);
            _stopById[b.Id] = info;
            foreach (int s in states)
            {
                if (!_stopsAtState.TryGetValue(s, out var list)) _stopsAtState[s] = list = new List<int>();
                list.Add(b.Id);
            }
        }
        foreach (var a in _stops)
        {
            var tr = new List<(int, int)>();
            foreach (var c in _stops)
            {
                if (c == a) continue;
                int d = RectGap(Buildings[a.Id]!, Buildings[c.Id]!);
                if (d <= TransferReach) tr.Add((c.Id, d));
            }
            a.Transfers = tr.ToArray();
        }
    }

    /// <summary>Workers in a home who have no car.</summary>
    int CarlessOf(Building home) => (LaborOf(home) * CarlessPercent[(int)TypeOf(home).Wealth] + 50) / 100;

    /// <summary>Monthly fares: <see cref="FareCents"/> a boarding, thirty days a month.</summary>
    public long TransitFares() => (long)TransitBoardings * 30 * FareCents / 100;
}
