namespace ToyCity.Sim;

/// <summary>
/// Digs a river along a straight stroke from (X0,Y0) to (X1,Y1) at any angle: a channel <c>Width</c> cells wide
/// (1 to <see cref="World.DigWidest"/>) and <c>Depth</c> steps deep (1 to <see cref="World.DigDeepest"/>). Its water
/// starts at the river it's dug from (or the ground at the start) and only ever falls along the stroke.
/// </summary>
public sealed record DigRiverAction(int X0, int Y0, int X1, int Y1, int Width, int Depth) : SimAction;

/// <summary>
/// Builds a dam along a straight line from (X0,Y0) to (X1,Y1) across a river, holding its water <c>Rise</c> steps
/// (1 to <see cref="World.DamHighest"/>) over the river's surface there. The reservoir fills behind it at once.
/// </summary>
public sealed record PlaceDamAction(int X0, int Y0, int X1, int Y1, int Rise) : SimAction;

/// <summary>
/// River brush and dams (river round 2, step 3): a player can dig new channels at a chosen width and depth, and wall
/// a river with a dam whose reservoir fills behind it. The reservoir holds its level whatever the rain, the river
/// below it never floods or runs dry, and a hydro plant at the dam's foot makes power from the fall.
/// </summary>
public sealed partial class World
{
    /// <summary>The widest a dug river is, in cells, and the deepest, in steps (2 m each).</summary>
    public const int DigWidest = 5, DigDeepest = 4;
    /// <summary>The longest stroke one dig takes, in cells; a client sends a long stroke as several.</summary>
    public const int DigLongest = 64;
    /// <summary>A dug river costs this a cell for each step it goes down.</summary>
    public const int DigCostPerCellStep = 60;
    /// <summary>The highest a dam holds its water over the river, in steps, and the longest one is, in cells.</summary>
    public const int DamHighest = 8, DamLongest = 40;
    /// <summary>A dam costs this a cell, and this more a cell for each step its crest stands over the ground.</summary>
    public const int DamCostPerCell = 800, DamCostPerCellStep = 300;
    /// <summary>The most cells a reservoir covers; a dam whose water would spread further isn't built.</summary>
    public const int ReservoirMost = 4000;

    /// <summary>A dam's crest in steps on each of its cells, 0 elsewhere. Saved.</summary>
    public readonly byte[] Dam;
    /// <summary>
    /// What a reservoir or dam cell's river surface was before the dam, plus one (0 where no dam changed it), so
    /// bulldozing the dam lets the water back down. Saved.
    /// </summary>
    public readonly byte[] RiverBefore;

    /// <summary>What the dams do to each river cell (<see cref="RiverFlow.Held"/>). Worked out, not saved.</summary>
    public byte[] DamHeld => _held ??= RiverFlow.Held(Width, HeightCells, Height, River, Dam);
    byte[]? _held;

    void DamsChanged()
    {
        _held = null;
        FloodGroundChanged();
        RecomputeWaterDistance();
    }

    void ApplyDigRiver(DigRiverAction a)
    {
        if (!InBounds(a.X0, a.Y0) || !InBounds(a.X1, a.Y1)) { Reject("That runs off the map.", a.X0, a.Y0); return; }
        int width = Math.Clamp(a.Width, 1, DigWidest), depth = Math.Clamp(a.Depth, 1, DigDeepest);
        var line = LineCells(a.X0, a.Y0, a.X1, a.Y1);
        if (line.Count > DigLongest) { Reject($"Dig a river in strokes of at most {DigLongest} cells.", a.X0, a.Y0); return; }
        double reach = width / 2.0;
        int r = (int)Math.Ceiling(reach);
        // Each cell the brush covers, with the surface its water stands at: the start's (the river it's dug from, or
        // the ground), falling with the ground along the stroke, never rising.
        var surface = new Dictionary<int, int>();
        var order = new List<int>();
        int s = Height[Idx(a.X0, a.Y0)];
        for (int dy = -r - 1; dy <= r + 1; dy++)
        for (int dx = -r - 1; dx <= r + 1; dx++)
        {
            int x = a.X0 + dx, y = a.Y0 + dy;
            if (InBounds(x, y) && IsRiver(Idx(x, y)) && Dam[Idx(x, y)] == 0 && dx * dx + dy * dy <= (reach + 1) * (reach + 1)) s = Math.Max(s, River[Idx(x, y)]);
        }
        foreach (var (cx, cy) in line)
        {
            int c = Idx(cx, cy);
            s = Math.Min(s, IsRiver(c) ? River[c] : Height[c]);
            s = Math.Max(s, WaterLevel);
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (!InBounds(x, y) || dx * dx + dy * dy > reach * reach) continue;
                int i = Idx(x, y);
                if (surface.TryGetValue(i, out int had)) { surface[i] = Math.Min(had, s); continue; }
                surface[i] = s;
                order.Add(i);
            }
        }
        var cut = new List<(int I, int H)>();
        long cost = 0;
        foreach (int i in order)
        {
            int top = surface[i], bed = Math.Max(1, top - depth);
            if (BuildingAt[i] != 0 || Path[i] != 0) { Reject("A river can't be dug through buildings or roads: bulldoze them first, and bridge it after.", i % Width, i / Width); return; }
            if (Dam[i] != 0) { Reject("That runs into a dam.", i % Width, i / Width); return; }
            if (Height[i] < WaterLevel && !IsRiver(i)) continue; // the sea already
            if (IsRiver(i) && River[i] >= top && Height[i] <= bed) continue;
            cut.Add((i, Math.Min(Height[i], bed)));
            cost += (long)DigCostPerCellStep * Math.Max(1, Height[i] - bed);
        }
        if (cut.Count == 0) { Reject("There's river there already.", a.X0, a.Y0); return; }
        if (!Spend(cost, a.X0, a.Y0)) return;
        SetLevels(cut, made: false);
        foreach (var (i, _) in cut)
        {
            River[i] = (byte)Math.Max(IsRiver(i) ? River[i] : 0, surface[i]);
            ZoneCode[i] = 0;
            PowerLine[i] = 0;
        }
        DamsChanged();
        Notices.Add(new SimNotice($"Dug {line.Count * 16} m of river, {width * 16} m wide and {depth * 2} m deep.", "info", a.X0, a.Y0, _actor));
    }

    /// <summary>Checks a dam without building it. Null when it can be built, else why not; the reservoir it would fill.</summary>
    public string? ValidateDam(PlaceDamAction a, out long cost, out int crest, out List<int> wall, out List<int> pool, out int badX, out int badY)
    {
        cost = 0; crest = 0; wall = new(); pool = new(); badX = a.X0; badY = a.Y0;
        if (!InBounds(a.X0, a.Y0) || !InBounds(a.X1, a.Y1)) return "That runs off the map.";
        var line = LineCells(a.X0, a.Y0, a.X1, a.Y1);
        if (line.Count > DamLongest) return $"A dam is at most {DamLongest} cells long.";
        if (line.Count < 3) return "Draw the dam across the river, from bank to bank.";
        var onLine = new HashSet<int>();
        foreach (var (x, y) in line) onLine.Add(Idx(x, y));
        int crossing = 0, top = 0;
        foreach (var (x, y) in line)
        {
            int i = Idx(x, y);
            (badX, badY) = (x, y);
            if (BuildingAt[i] != 0 || Path[i] != 0) return "A dam can't go through buildings or roads.";
            if (Dam[i] != 0) return "There's a dam there already.";
            if (Height[i] < WaterLevel && !IsRiver(i)) return "A dam goes across a river, not the sea or a lake.";
            if (IsRiver(i)) { crossing++; top = Math.Max(top, River[i]); }
        }
        (badX, badY) = (a.X0, a.Y0);
        if (crossing == 0) return "A dam has to cross a river.";
        // Which side is upstream: the river beside the dam stands higher there.
        int ax = a.X1 - a.X0, ay = a.Y1 - a.Y0;
        int Side(int i) => Math.Sign(ax * (i / Width - a.Y0) - ay * (i % Width - a.X0));
        var beside = new SortedSet<int>();
        foreach (int i in onLine)
            for (int k = 0; k < 4; k++)
            {
                int x = i % Width + Dx4[k], y = i / Width + Dy4[k];
                if (InBounds(x, y) && !onLine.Contains(Idx(x, y))) beside.Add(Idx(x, y));
            }
        int high1 = -1, high2 = -1;
        foreach (int j in beside)
            if (IsRiver(j)) { if (Side(j) > 0) high1 = Math.Max(high1, River[j]); else if (Side(j) < 0) high2 = Math.Max(high2, River[j]); }
        if (high1 < 0 || high2 < 0) return "Draw the dam across the river, from bank to bank.";
        int up = high1 >= high2 ? 1 : -1;
        top = Math.Max(top, Math.Max(high1, high2));
        int level = top + Math.Clamp(a.Rise, 1, DamHighest);
        crest = level + 1;
        if (crest > MaxHeight) return "That's too high.";
        // The reservoir: the ground under the water's level, reached from the upstream side without crossing the dam.
        string? Fill(int up, out HashSet<int> inPool, out int fx, out int fy)
        {
            (fx, fy) = (a.X0, a.Y0);
            inPool = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (int j in beside)
                if (Side(j) == up && Height[j] < level && Dam[j] == 0 && inPool.Add(j)) queue.Enqueue(j);
            // The river below the dam: its water may not come back round into the reservoir.
            var below = new HashSet<int>();
            var bq = new Queue<int>();
            foreach (int j in beside)
                if (Side(j) == -up && IsRiver(j) && below.Add(j)) bq.Enqueue(j);
            while (bq.Count > 0)
            {
                int c = bq.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    int x = c % Width + Dx4[k], y = c / Width + Dy4[k];
                    if (!InBounds(x, y)) continue;
                    int j = Idx(x, y);
                    if (onLine.Contains(j) || !IsRiver(j) || River[j] >= level || !below.Add(j)) continue;
                    bq.Enqueue(j);
                }
            }
            int drowned = 0;
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                if (below.Contains(c) || (Height[c] < WaterLevel && !IsRiver(c)))
                {
                    (fx, fy) = (c % Width, c / Width);
                    return "The water would run round the end of the dam: make it longer, or lower.";
                }
                if (inPool.Count > ReservoirMost) return "The reservoir would spread too far: build the dam lower.";
                // Over the map's edge, except up the river it comes in on, the water would run off into the next city.
                int ex = c % Width, ey = c / Width;
                if ((ex == 0 || ey == 0 || ex == Width - 1 || ey == HeightCells - 1) && !IsRiver(c))
                {
                    (fx, fy) = (ex, ey);
                    return "The reservoir would spill over the city's edge: build the dam lower.";
                }
                if (BuildingAt[c] != 0 || (Path[c] != 0 && PathDeck[c] < (level + 1) * StepDm)) drowned++;
                for (int k = 0; k < 4; k++)
                {
                    int x = c % Width + Dx4[k], y = c / Width + Dy4[k];
                    if (!InBounds(x, y)) continue;
                    int j = Idx(x, y);
                    if (onLine.Contains(j) || Dam[j] != 0 || Height[j] >= level || !inPool.Add(j)) continue;
                    queue.Enqueue(j);
                }
            }
            return drowned > 0 ? $"The reservoir would drown {drowned} cells of buildings and roads: clear them first, or build the dam lower." : null;
        }
        // A river that stands level either side can't say which way it runs: whichever side holds the water.
        var err = Fill(up, out var inPool, out badX, out badY);
        if (err != null && high1 == high2 && Fill(-up, out var other, out _, out _) == null) (err, inPool) = (null, other);
        if (err != null) return err;
        foreach (var (x, y) in line)
        {
            int i = Idx(x, y);
            wall.Add(i);
            cost += DamCostPerCell + (long)DamCostPerCellStep * Math.Max(0, crest - Height[i]);
        }
        pool = inPool.Order().ToList();
        return null;
    }

    void ApplyPlaceDam(PlaceDamAction a)
    {
        var err = ValidateDam(a, out long cost, out int crest, out var wall, out var pool, out int bx, out int by);
        if (err != null) { Reject(err, bx, by); return; }
        if (!Spend(cost, a.X0, a.Y0)) return;
        int level = crest - 1, lowest = wall.Min(i => IsRiver(i) ? Height[i] : MaxHeight);
        foreach (int i in pool)
        {
            if (RiverBefore[i] == 0) RiverBefore[i] = (byte)(River[i] + 1);
            River[i] = (byte)level;
            ZoneCode[i] = 0;
            PowerLine[i] = 0;
        }
        int cw = Width + 1;
        foreach (int i in wall)
        {
            if (RiverBefore[i] == 0) RiverBefore[i] = (byte)(River[i] + 1);
            River[i] = 0;
            Dam[i] = (byte)crest;
            Height[i] = (byte)crest;
            ZoneCode[i] = 0;
            PowerLine[i] = 0;
            int x = i % Width, y = i / Width;
            for (int cy = y; cy <= y + 1; cy++)
            for (int cx = x; cx <= x + 1; cx++)
            {
                Corner[cy * cw + cx] = (ushort)(crest * StepDm);
                KeepBedrockUnder(cy * cw + cx);
                MarkMade(cx, cy);
            }
        }
        DamsChanged();
        Notices.Add(new SimNotice($"Built a dam {(crest - lowest) * 2} m high: its reservoir covers {pool.Count * 256 / 10000.0:0.#} ha.", "info", a.X0, a.Y0, _actor));
    }

    /// <summary>Bulldozing any cell of a dam takes the whole dam down and lets its reservoir back down. Returns the cost.</summary>
    long DamsIn(int x0, int y0, int x1, int y1, out List<List<int>> dams)
    {
        dams = new();
        var seen = new HashSet<int>();
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            if (Dam[i] == 0 || !seen.Add(i)) continue;
            var cells = new List<int> { i };
            var q = new Queue<int>();
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = c % Width + dx, ny = c / Width + dy;
                    if (!InBounds(nx, ny)) continue;
                    int j = Idx(nx, ny);
                    if (Dam[j] != Dam[i] || !seen.Add(j)) continue;
                    cells.Add(j);
                    q.Enqueue(j);
                }
            }
            dams.Add(cells);
        }
        return dams.Sum(d => (long)d.Count) * Content.BulldozeCostPerCell * 4;
    }

    void RemoveDams(List<List<int>> dams)
    {
        if (dams.Count == 0) return;
        int cw = Width + 1;
        foreach (var wall in dams)
        {
            int crest = Dam[wall[0]], level = crest - 1;
            // Its reservoir: water held at its level, joined to the dam.
            var q = new Queue<int>();
            var pool = new HashSet<int>();
            foreach (int i in wall)
                for (int k = 0; k < 4; k++)
                {
                    int x = i % Width + Dx4[k], y = i / Width + Dy4[k];
                    if (InBounds(x, y) && Dam[Idx(x, y)] == 0 && RiverBefore[Idx(x, y)] != 0 && River[Idx(x, y)] == level && pool.Add(Idx(x, y))) q.Enqueue(Idx(x, y));
                }
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    int x = c % Width + Dx4[k], y = c / Width + Dy4[k];
                    if (!InBounds(x, y)) continue;
                    int j = Idx(x, y);
                    if (Dam[j] != 0 || RiverBefore[j] == 0 || River[j] != level || !pool.Add(j)) continue;
                    q.Enqueue(j);
                }
            }
            foreach (int i in pool) { River[i] = (byte)(RiverBefore[i] - 1); RiverBefore[i] = 0; }
            // The wall goes back down to the river's bed where it crossed it, else to the ground beside it.
            foreach (int i in wall)
            {
                int was = RiverBefore[i] - 1;
                Dam[i] = 0;
                RiverBefore[i] = 0;
                int x = i % Width, y = i / Width, low = MaxHeight;
                for (int k = 0; k < 4; k++)
                    if (InBounds(x + Dx4[k], y + Dy4[k]) && Dam[Idx(x + Dx4[k], y + Dy4[k])] == 0) low = Math.Min(low, Height[Idx(x + Dx4[k], y + Dy4[k])]);
                if (low == MaxHeight) low = WaterLevel;
                River[i] = (byte)Math.Max(0, was);
                Height[i] = (byte)(was > 0 ? Math.Min(low, was - 1) : low);
            }
            foreach (int i in wall)
            {
                int x = i % Width, y = i / Width;
                for (int cy = y; cy <= y + 1; cy++)
                for (int cx = x; cx <= x + 1; cx++)
                    Corner[cy * cw + cx] = (ushort)CornerFromCells(cx, cy);
            }
            Notices.Add(new SimNotice("The dam is down and its reservoir is draining.", "info", wall[0] % Width, wall[0] / Width, _actor));
        }
        DamsChanged();
    }

    /// <summary>How near a hydro plant has to stand to its dam, in cells: its penstock runs that far.</summary>
    public const int HydroReach = 3;

    /// <summary>The dam a building stands by: the highest crest within <see cref="HydroReach"/> cells, 0 for none.</summary>
    public int DamBeside(int bx, int by, int bw, int bh)
    {
        int crest = 0;
        for (int y = by - HydroReach; y < by + bh + HydroReach; y++)
        for (int x = bx - HydroReach; x < bx + bw + HydroReach; x++)
            if (InBounds(x, y) && (x < bx || x >= bx + bw || y < by || y >= by + bh)) crest = Math.Max(crest, Dam[Idx(x, y)]);
        return crest;
    }

    /// <summary>
    /// How hard a hydro plant runs, in percent: with the fall from its dam's reservoir to the river at its foot (full
    /// at 7 steps, 14 m) and with the flow through it (the reservoir evens it out between 40% and 120%).
    /// </summary>
    public int HydroPct(Building b)
    {
        int crest = DamBeside(b.X, b.Y, b.W, b.H);
        if (crest == 0) return 0;
        // The river at the dam's foot: the lowest water just below its wall, not its reservoir.
        int below = int.MaxValue;
        var held = DamHeld;
        for (int i = 0; i < N; i++)
        {
            if (Dam[i] != crest) continue;
            int dx0 = i % Width, dy0 = i / Width;
            if (Math.Abs(dx0 - (b.X + b.W / 2)) > 3 * HydroReach + DamLongest || Math.Abs(dy0 - (b.Y + b.H / 2)) > 3 * HydroReach + DamLongest) continue;
            for (int y = dy0 - 2; y <= dy0 + 2; y++)
            for (int x = dx0 - 2; x <= dx0 + 2; x++)
                if (InBounds(x, y) && IsRiver(Idx(x, y)) && held[Idx(x, y)] != RiverFlow.Reservoir) below = Math.Min(below, River[Idx(x, y)]);
        }
        if (below == int.MaxValue) return 0;
        int head = Math.Max(0, crest - 1 - below);
        return Math.Min(100, head * 100 / 7) * Math.Clamp(RiverFlowPct, 40, 120) / 100;
    }
}
