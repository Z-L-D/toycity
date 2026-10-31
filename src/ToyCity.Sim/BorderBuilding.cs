namespace ToyCity.Sim;

/// <summary>
/// Beta 3.1 part 6: a computer mayor (or any city on autopilot) builds out to meet its neighbours. Each month, where
/// a neighbour's road, power line or pipe reaches the border and this city has nothing there to meet it, the computer
/// runs one of its own from the town to that crossing, if it can afford it within its monthly cap. A road goes the
/// gentlest way it can find (a step a cell if it can, so it needs little grading; steeper if it must, for the grading
/// to cut and fill), over a river on a bridge if it must;
/// a line or pipe goes the shortest way to the nearest part of its network.
/// </summary>
public sealed partial class World
{
    /// <summary>The farthest, in cells, the computer will build to meet a crossing.</summary>
    public const int BorderReach = 160;

    enum Link { Road, Power, Pipe }

    void AutopilotBorders()
    {
        foreach (var link in new[] { Link.Road, Link.Power, Link.Pipe })
            for (int side = 0; side < 4; side++)
            {
                if (NextDoor[side] is not { } n || n.Name == "") continue;
                if (MeetCrossing(n, side, link)) break;
            }
    }

    /// <summary>Builds to the first unmet crossing of this kind on a side. True when it built one.</summary>
    bool MeetCrossing(NeighbourEdge n, int side, Link link)
    {
        int len = SideLength(side);
        for (int k = 1; k < len - 1; k++)
        {
            int edge = SideCell(side, k);
            string? profile = null;
            switch (link)
            {
                case Link.Road:
                    if (k >= n.Path.Length || n.Path[k] == 0 || Path[edge] != 0) continue;
                    var p = Content.Profiles[n.Path[k] - 1];
                    if (p.Rail) continue;
                    profile = p.Id;
                    break;
                case Link.Power:
                    if (k >= n.Power.Length || !n.Power[k] || ConductsPower(edge)) continue;
                    break;
                default:
                    if (k >= n.Pipe.Length || !n.Pipe[k] || WaterPipe[edge] != 0) continue;
                    break;
            }
            var route = RouteInward(edge, link);
            if (route == null) continue;
            var legs = Straights(route);
            // The price first: each run of road as the server would charge it, grading and bridges included.
            long guess = link switch
            {
                Link.Road => legs.Sum(l => ValidatePath(new PlacePathAction(l.A % Width, l.A / Width, l.B % Width, l.B / Width, profile!), out long c, out _, out _) == null ? c : long.MaxValue / 64),
                Link.Power => route.Count * (long)PowerLineCost,
                _ => route.Count * (long)WaterPipeCost,
            };
            if (!Affordable(guess)) return false;
            bool ok = true;
            foreach (var (a, b) in legs)
            {
                SimAction act = link == Link.Road
                    ? new PlacePathAction(a % Width, a / Width, b % Width, b / Width, profile!)
                    : new PlaceUtilityAction(a % Width, a / Width, b % Width, b / Width, link == Link.Power ? UtilityKind.PowerLine : UtilityKind.WaterPipe);
                if (!Do(act)) { ok = false; break; }
            }
            if (!ok) return false;
            if (link == Link.Road)
            {
                // As after a player's road: lots reach it, and the crossing counts from today.
                CheckStrips();
                RebuildAccess();
                RunNeighborConnections();
            }
            string what = link switch { Link.Road => "a road", Link.Power => "a power line", _ => "a water pipe" };
            AwayNoteAdd($"Built {what} out to meet {n.Name}'s at the border.", edge % Width, edge / Width);
            Notices.Add(new SimNotice($"{MayorSeat.Owner} built {what} out to meet {n.Name}'s at the border.", "info", edge % Width, edge / Width));
            return true;
        }
        return false;
    }

    /// <summary>
    /// The cells from a border cell inward to the nearest thing it should join, border first and that thing left out;
    /// null when nothing is within reach. A line or pipe takes the shortest way. A road takes the cheapest: each cell
    /// costs more the more it climbs from the last (the grading), a bridge more again, and a turn a little, so it
    /// follows valleys and keeps straight. Ties go by cell, so it's the same every time.
    /// </summary>
    List<int>? RouteInward(int start, Link link)
    {
        bool Open(int i) => BuildingAt[i] == 0 && (!IsWater(i) || IsRiver(i));
        bool Goal(int i) => link switch
        {
            Link.Road => Path[i] != 0 && ProfileAt(i) is { Rail: false },
            Link.Power => ConductsPower(i),
            _ => WaterPipe[i] != 0,
        };
        if (!Open(start)) return null;
        // A road leaves the border straight inward, so the crossing counts (World.RunNeighborConnections).
        int sx = start % Width, sy = start / Width;
        int inward = sx == 0 ? start + 1 : sx == Width - 1 ? start - 1 : sy == 0 ? start + Width : start - Width;
        var from = new Dictionary<int, int> { [start] = -1 };
        var cost = new Dictionary<int, int> { [start] = 0 };
        var steps = new Dictionary<int, int> { [start] = 0 };
        var queue = new PriorityQueue<int, (int, int)>();
        queue.Enqueue(start, (0, start));
        var done = new HashSet<int>();
        while (queue.TryDequeue(out int c, out _))
        {
            if (!done.Add(c) || steps[c] >= BorderReach) continue;
            int x = c % Width, y = c / Width;
            int hc = IsRiver(c) ? River[c] + 1 : Height[c];
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int j = Idx(nx, ny);
                if (done.Contains(j) || (link == Link.Road && c == start && j != inward)) continue;
                if (Goal(j))
                {
                    var route = new List<int>();
                    for (int at = c; at >= 0; at = from[at]) route.Add(at);
                    route.Reverse();
                    return route;
                }
                if (!Open(j) || (link == Link.Road && Path[j] != 0)) continue;
                int step = 1;
                if (link == Link.Road)
                {
                    int hj = IsRiver(j) ? River[j] + 1 : Height[j];
                    bool turn = from[c] >= 0 && j - c != c - from[c];
                    step = 10 + 30 * Math.Abs(hj - hc) + (IsRiver(j) ? 60 : 0) + (turn ? 4 : 0);
                }
                int next = cost[c] + step;
                if (cost.TryGetValue(j, out int had) && had <= next) continue;
                cost[j] = next;
                from[j] = c;
                steps[j] = steps[c] + 1;
                queue.Enqueue(j, (next, j));
            }
        }
        return null;
    }

    /// <summary>A route cut into straight runs, as (first cell, last cell); each run's last cell starts the next.</summary>
    List<(int A, int B)> Straights(List<int> route)
    {
        var legs = new List<(int, int)>();
        int start = 0;
        for (int k = 1; k <= route.Count; k++)
        {
            bool turn = k == route.Count
                || (k >= 2 && (route[k] - route[k - 1]) != (route[k - 1] - route[k - 2]));
            if (!turn) continue;
            legs.Add((route[start], route[k - 1]));
            start = k - 1;
        }
        return legs;
    }
}
