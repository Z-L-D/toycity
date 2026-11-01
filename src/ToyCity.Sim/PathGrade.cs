namespace ToyCity.Sim;

/// <summary>
/// How a path is graded into the ground, in decimetres (Beta 3.2, part 1). Shared word for word by the sim and the
/// client model (linked into both), so the planner's preview and cost are exactly what the server builds.
/// <para>
/// A run of cells is given a height at each cell's middle so that no cell climbs more than the path type allows
/// (its grade, as a share of the 16 m cell), cut and fill roughly balancing. Cells already holding a path, and
/// bridges, keep their height. Then the ground is reshaped: every corner a new path cell touches goes to the average
/// of the heights of the path cells around it, so the path is a smooth ramp, and the open ground beside it is eased
/// so no bank is steeper than its material stands: soil at <see cref="BankDmPerCell"/>, a cut into rock at the rock's
/// angle (<see cref="RockKinds"/>). Lots, water, rivers and other paths are
/// never moved.
/// </para>
/// </summary>
internal static class PathGrade
{
    /// <summary>A cell is 16 m across: 160 dm.</summary>
    public const int CellDm = 160;
    /// <summary>The steepest a bank of earth may stand beside a cut or fill, in dm across a cell: about 32 degrees.</summary>
    public const int BankDmPerCell = 100;
    /// <summary>How far from the path a bank may reach, in cells.</summary>
    public const int BankRadius = 10;
    /// <summary>What moving earth costs: dollars per 2 m of one corner moved.</summary>
    public const int CostPerStepDm = 20;
    public const int CostPerStep = 10;

    /// <summary>
    /// How steep a path may climb, in percent: a footpath with stairs as steep as its type allows (as stairs past its
    /// surface's ramp), any other path the lower of its type's and its surface's (-1 for no surface).
    /// </summary>
    public static int GradePct(int profileMaxPct, bool stairs, int surfaceMaxPct) =>
        stairs || surfaceMaxPct < 0 ? profileMaxPct : Math.Min(profileMaxPct, surfaceMaxPct);

    /// <summary>A path this far under the ground (dm) can be a tunnel instead of a cut: 8 m.</summary>
    public const int TunnelMinDm = 80;
    /// <summary>A tunnel cell costs this many times its path on the ground.</summary>
    public const int TunnelCostTimes = 8;

    /// <summary>
    /// Which cells of a graded run are tunnel when the player asks for one: those at least <see cref="TunnelMinDm"/>
    /// under the ground, in runs of two or more (a single deep cell is cut). Cells already holding a path aren't.
    /// </summary>
    public static bool[] TunnelCells(IReadOnlyList<int> ground, IReadOnlyList<int> graded, IReadOnlyList<bool> fixedCell)
    {
        int n = graded.Count;
        var deep = new bool[n];
        for (int k = 0; k < n; k++) deep[k] = !fixedCell[k] && ground[k] - graded[k] >= TunnelMinDm;
        var t = new bool[n];
        for (int k = 0; k < n; k++)
            t[k] = deep[k] && ((k > 0 && deep[k - 1]) || (k + 1 < n && deep[k + 1]));
        return t;
    }

    /// <summary>The deepest cut (dm) a graded run makes below its ground, outside tunnel cells.</summary>
    public static int DeepestCut(IReadOnlyList<int> ground, IReadOnlyList<int> graded, IReadOnlyList<bool> fixedCell, bool[]? tunnel)
    {
        int deepest = 0;
        for (int k = 0; k < graded.Count; k++)
            if (!fixedCell[k] && (tunnel == null || !tunnel[k])) deepest = Math.Max(deepest, ground[k] - graded[k]);
        return deepest;
    }

    /// <summary>The most a path of this grade climbs across one cell, in dm.</summary>
    public static int MaxDeltaDm(int gradePct) => Math.Max(1, (gradePct * CellDm + 50) / 100);

    /// <summary>The grade, in percent, of a climb of <paramref name="dm"/> across <paramref name="cells"/> cells.</summary>
    public static int Pct(int dm, int cells) => cells <= 0 ? 0 : (int)Math.Ceiling(dm * 100.0 / (cells * CellDm));

    /// <summary>
    /// The heights (dm) a run is graded to so it climbs at most <paramref name="step"/> a cell: halfway between the
    /// highest such profile under the ground and the lowest one over it, so cut and fill roughly balance. Fixed
    /// cells keep their height, nothing goes under <paramref name="floor"/>, and no cell under its
    /// <paramref name="atLeast"/> (a bridge's deck clears its river). Null when fixed cells are too far
    /// apart in height to join at this grade.
    /// </summary>
    public static int[]? Grade(IReadOnlyList<int> h, IReadOnlyList<bool> fixedCell, int floor, int step, IReadOnlyList<int>? atLeast = null)
    {
        int n = h.Count;
        const int Inf = 1 << 28;
        var under = new int[n];
        var over = new int[n];
        var lo = new int[n];
        var hi = new int[n];
        for (int k = 0; k < n; k++)
        {
            under[k] = k > 0 ? Math.Min(h[k], under[k - 1] + step) : h[k];
            over[k] = k > 0 ? Math.Max(h[k], over[k - 1] - step) : h[k];
            lo[k] = Math.Max(Math.Max(fixedCell[k] ? h[k] : -Inf, atLeast != null ? atLeast[k] : -Inf), k > 0 ? lo[k - 1] - step : -Inf);
            hi[k] = Math.Min(fixedCell[k] ? h[k] : Inf, k > 0 ? hi[k - 1] + step : Inf);
        }
        for (int k = n - 2; k >= 0; k--)
        {
            under[k] = Math.Min(under[k], under[k + 1] + step);
            over[k] = Math.Max(over[k], over[k + 1] - step);
            lo[k] = Math.Max(lo[k], lo[k + 1] - step);
            hi[k] = Math.Min(hi[k], hi[k + 1] + step);
        }
        var g = new int[n];
        for (int k = 0; k < n; k++)
        {
            int low = Math.Max(lo[k], floor);
            if (fixedCell[k]) low = lo[k];
            if (low > hi[k]) return null;
            g[k] = fixedCell[k] ? h[k] : Math.Clamp((under[k] + over[k]) >> 1, low, hi[k]);
        }
        return g;
    }

    /// <summary>
    /// When fixed cells can't be joined: the steepest grade (percent) the run would need between two of them, and the
    /// index of the cell where that climb ends.
    /// </summary>
    public static (int Pct, int At) Needed(IReadOnlyList<int> h, IReadOnlyList<bool> fixedCell)
    {
        int best = 0, at = 0, last = -1;
        for (int k = 0; k < h.Count; k++)
        {
            if (!fixedCell[k]) continue;
            if (last >= 0)
            {
                int p = Pct(Math.Abs(h[k] - h[last]), k - last);
                if (p > best) { best = p; at = k; }
            }
            last = k;
        }
        return (best, at);
    }

    /// <summary>A cell's ground, in dm: its four corners averaged.</summary>
    public static int CellDmOf(ushort[] corners, int width, int i)
    {
        int cw = width + 1, x = i % width, y = i / width, c = y * cw + x;
        return (corners[c] + corners[c + 1] + corners[c + cw] + corners[c + cw + 1] + 2) / 4;
    }

    /// <summary>
    /// The new heights of the corners the path and its banks move, keyed by corner index. <paramref name="run"/> are
    /// the path's cells, <paramref name="target"/> their graded heights; <paramref name="locked"/> says which cells'
    /// corners must stay where they are (lots, water, rivers, paths already there).
    /// </summary>
    public static Dictionary<int, int> Shape(ushort[] corners, int width, int height, IReadOnlyList<int> run, IReadOnlyList<int> target, Func<int, bool> locked, int floorDm, ushort[]? bedrock = null, byte[]? rock = null)
    {
        int cw = width + 1, ch = height + 1;
        var lockedCorner = new HashSet<int>();
        var sum = new Dictionary<int, (long S, int N)>();
        void Lock(int cell)
        {
            int x = cell % width, y = cell / width;
            for (int dy = 0; dy <= 1; dy++) for (int dx = 0; dx <= 1; dx++) lockedCorner.Add((y + dy) * cw + x + dx);
        }
        bool IsLocked(int c)
        {
            if (lockedCorner.Contains(c)) return true;
            int cx = c % cw, cy = c / cw;
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                if (locked(y * width + x)) { lockedCorner.Add(c); return true; }
            }
            return false;
        }
        var inRun = new HashSet<int>(run);
        for (int k = 0; k < run.Count; k++)
        {
            int x = run[k] % width, y = run[k] / width;
            for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                int c = (y + dy) * cw + x + dx;
                sum[c] = sum.TryGetValue(c, out var s) ? (s.S + target[k], s.N + 1) : (target[k], 1);
            }
        }
        // The path's own corners.
        var val = new Dictionary<int, int>();
        var queue = new Queue<int>();
        var dist = new Dictionary<int, int>();
        foreach (var (c, s) in sum.OrderBy(p => p.Key))
        {
            int v = IsLocked(c) ? corners[c] : (int)((s.S + s.N / 2) / s.N);
            val[c] = v;
            dist[c] = 0;
            // Banks only ease the ground next to corners the path actually moved.
            if (v != corners[c]) queue.Enqueue(c);
        }
        // Banks: each open corner near the path is held to within a bank's slope of its neighbours, outward ring by
        // ring, so a deep cut gets a sloping side rather than a wall.
        int Get(int c) => val.TryGetValue(c, out var v) ? v : corners[c];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int d = dist[c];
            if (d >= BankRadius) continue;
            int cx = c % cw, cy = c / cw, here = Get(c);
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                int n = ny * cw + nx;
                if (sum.ContainsKey(n) || IsLocked(n)) continue;
                // A bank stands no steeper than soil does, unless the ground there was already steeper: a cut
                // eases what it steepens, and leaves a natural slope as it was.
                bool diag = dx != 0 && dy != 0;
                int soil = diag ? BankDmPerCell * 141 / 100 : BankDmPerCell, natural = Math.Abs(corners[c] - corners[n]);
                int now = Get(n);
                // A fill is earth tipped in, so it slopes like soil; a cut stands at its material's angle.
                int below = here - Math.Max(soil, natural);
                int above = bedrock == null ? here + Math.Max(soil, natural)
                    : Math.Max(here + natural, CutTop(here, bedrock[n], soil, RockReach(rock, width, height, nx, ny, diag)));
                int eased = Math.Clamp(now, below, above);
                // A bank never digs below the waterline where the ground stood above it.
                if (eased < floorDm && now >= floorDm) eased = floorDm;
                if (eased == now) continue;
                val[n] = eased;
                if (!dist.ContainsKey(n) || dist[n] > d + 1) dist[n] = d + 1;
                queue.Enqueue(n);
            }
        }
        var moved = new Dictionary<int, int>();
        foreach (var (c, v) in val)
            if (v != corners[c]) moved[c] = v;
        return moved;
    }

    /// <summary>
    /// Rocks under the ground (index = <c>World.Rock</c>): how steep a cut in it stands, in dm across a cell, and how
    /// many times soil's cost it is to dig. Index 0 is no rock: soil all the way.
    /// </summary>
    public static readonly (string Name, int CutDm, int CostTimes)[] RockKinds =
    {
        ("soil", BankDmPerCell, 1),
        ("shale", 300, 2),        // about 62 degrees: it flakes
        ("sandstone", 600, 2),    // about 75
        ("limestone", 900, 2),    // about 80
        ("granite", 1600, 3),     // about 84: nearly sheer
    };

    /// <summary>How far a cut in the rock at a corner rises across a cell (or diagonal), in dm.</summary>
    static int RockReach(byte[]? rock, int width, int height, int cx, int cy, bool diag)
    {
        int k = rock == null ? 0 : rock[Math.Min(cy, height - 1) * width + Math.Min(cx, width - 1)];
        int r = RockKinds[k < RockKinds.Length ? k : 0].CutDm;
        return diag ? r * 141 / 100 : r;
    }

    /// <summary>
    /// The highest a cut face may reach one cell out from <paramref name="here"/>: sheer at the rock's angle up to the
    /// top of the rock, then soil's angle for whatever of the cell is left.
    /// </summary>
    public static int CutTop(int here, int bed, int soilReach, int rockReach)
    {
        if (here >= bed || rockReach <= soilReach) return here + soilReach;
        if (here + rockReach <= bed) return here + rockReach;
        // Through the rock part of the way, then soil over it.
        return bed + (int)((long)soilReach * (here + rockReach - bed) / rockReach);
    }

    /// <summary>The share (dm) of a corner's cut from <paramref name="from"/> to <paramref name="to"/> that's rock.</summary>
    public static int RockDm(int from, int to, int bed) => to >= from ? 0 : Math.Max(0, Math.Min(from, bed) - to);

    /// <summary>Banks beside the path cost this share of the path's own earthworks: they're shaped, not engineered.</summary>
    public const int BankCostDivisor = 10;

    /// <summary>
    /// What the earthworks cost, in dollars: <see cref="CostPerStep"/> for each 2 m a corner under the path moves, and
    /// a tenth of that for the banks beside it.
    /// </summary>
    public static long Cost(ushort[] corners, int width, IReadOnlyList<int> run, Dictionary<int, int> moved, ushort[]? bedrock = null, byte[]? rock = null)
    {
        int cw = width + 1, height = corners.Length / cw - 1;
        var own = new HashSet<int>();
        foreach (int i in run)
        {
            int x = i % width, y = i / width;
            own.Add(y * cw + x); own.Add(y * cw + x + 1); own.Add((y + 1) * cw + x); own.Add((y + 1) * cw + x + 1);
        }
        long path = 0, banks = 0;
        foreach (var (c, v) in moved)
        {
            long d = Math.Abs(v - corners[c]);
            // Rock costs its kind's times soil's to dig.
            if (bedrock != null)
            {
                int cx = c % cw, cy = c / cw, k = rock == null ? 0 : rock[Math.Min(cy, height - 1) * width + Math.Min(cx, width - 1)];
                int r = RockDm(corners[c], v, bedrock[c]);
                d += (long)r * (RockKinds[k < RockKinds.Length ? k : 0].CostTimes - 1);
            }
            if (own.Contains(c)) path += d; else banks += d;
        }
        return (path * CostPerStep * BankCostDivisor + banks * CostPerStep + CostPerStepDm * BankCostDivisor - 1) / (CostPerStepDm * BankCostDivisor);
    }
}
