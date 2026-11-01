namespace ToyCity.Sim;

/// <summary>
/// Part 10: roads grade the ground they're laid on, and buildings level their own foundation pads, so a city can
/// climb off the flat land it started on.
/// </summary>
public sealed partial class World
{
    /// <summary>What a road paid for each step of earth it cut or filled, a cell at a time, before Beta 3.2.</summary>
    public const int GradeCostPerStep = PathGrade.CostPerStep;
    /// <summary>The most a lot's ground may rise across it for a building to level a pad there.</summary>
    public const int MaxPadRise = 3;
    /// <summary>What a placed building pays for each step of earth its pad moves, a cell at a time.</summary>
    public const int PadCostPerStep = 5;

    /// <summary>
    /// The heights (dm) a run is graded to at <paramref name="step"/> dm a cell; see <see cref="PathGrade.Grade"/>.
    /// </summary>
    public static int[]? Grade(IReadOnlyList<int> h, IReadOnlyList<bool> fixedCell, int floor, int step) => PathGrade.Grade(h, fixedCell, floor, step);

    /// <summary>The ground works a path needs: each run cell's graded height (dm), and the corners it moves.</summary>
    public sealed record PathWorks(int[] Target, Dictionary<int, int> Corners, long Cost, int GradePct, int StairsAbovePct);

    /// <summary>Whether a cell's corners stay put when a path is graded beside or over it.</summary>
    bool GradeLocked(int i) => BuildingAt[i] != 0 || IsWater(i) || Path[i] != 0;

    /// <summary>
    /// The earthworks for a path of a profile and surface along these cells (Beta 3.2): graded by the decimetre
    /// to at most the grade its type and surface allow. Null, with the cell and the grade it would need, when two
    /// paths it joins are too far apart in height for it.
    /// </summary>
    public PathWorks? PlanPath(List<(int X, int Y)> cells, int profile, int surfaceCode, out int badCell, out int neededPct)
    {
        badCell = -1; neededPct = 0;
        int pct = Content.GradePct(profile, surfaceCode);
        var idx = cells.Select(c => Idx(c.X, c.Y)).ToList();
        // A bridge's deck stands at least a step over the river (Beta 3.2: higher where the road's grade needs it); the river keeps its bed.
        var h = idx.Select(i => IsRiver(i) ? (River[i] + 1) * StepDm : PathGrade.CellDmOf(Corner, Width, i)).ToList();
        var fix = idx.Select(i => Path[i] != 0).ToList();
        var deck = idx.Select(i => IsRiver(i) ? (River[i] + 1) * StepDm : int.MinValue).ToList();
        var g = PathGrade.Grade(h, fix, WaterLevel * StepDm, PathGrade.MaxDeltaDm(pct), deck);
        if (g == null)
        {
            var (need, at) = PathGrade.Needed(h, fix);
            badCell = at; neededPct = need;
            return null;
        }
        var moved = PathGrade.Shape(Corner, Width, HeightCells, idx, g, GradeLocked, WaterLevel * StepDm);
        return new PathWorks(g, moved, PathGrade.Cost(Corner, Width, idx, moved), pct, Content.StairsAbovePct(profile, surfaceCode));
    }

    /// <summary>Moves the corners a path's works name, and re-derives the steps of the cells they touch.</summary>
    void ApplyPathWorks(PathWorks works)
    {
        if (works.Corners.Count == 0) return;
        FloodGroundChanged();
        int cw = Width + 1;
        var cells = new HashSet<int>();
        foreach (var (c, v) in works.Corners)
        {
            Corner[c] = (ushort)Math.Clamp(v, 0, MaxCornerDm);
            int cx = c % cw, cy = c / cw;
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
                if (InBounds(cx + dx, cy + dy)) cells.Add(Idx(cx + dx, cy + dy));
        }
        foreach (int i in cells.OrderBy(i => i))
            if (!GradeLocked(i)) DeriveHeights(i % Width, i / Width, i % Width, i / Width);
    }

    /// <summary>What earthworks cost: a step of earth moved per cell.</summary>
    long EarthworksCost(List<(int I, int H)> works, int perStep) => works.Sum(w => (long)Math.Abs(w.H - Height[w.I]) * perStep);

    /// <summary>Moves the earth. Earthworks never go under the waterline, so the water stays where it was.</summary>
    void ApplyEarthworks(List<(int I, int H)> works)
    {
        SetLevels(works);
    }

    /// <summary>
    /// The pad a building levels under its lot: every cell to the lot's average height, rounded. Null when the lot
    /// rises more than <see cref="MaxPadRise"/> across it.
    /// </summary>
    List<(int I, int H)>? Pad(int x0, int y0, int w, int h)
    {
        int lo = int.MaxValue, hi = int.MinValue, sum = 0;
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            int v = Height[Idx(x, y)];
            lo = Math.Min(lo, v); hi = Math.Max(hi, v); sum += v;
        }
        if (hi - lo > MaxPadRise) return null;
        int n = w * h, level = Math.Max(WaterLevel, (2 * sum + n) / (2 * n));
        var works = new List<(int I, int H)>();
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
            if (Height[Idx(x, y)] != level) works.Add((Idx(x, y), level));
        return works;
    }

    /// <summary>Whether a lot is gentle enough for a pad.</summary>
    bool PadFits(int x0, int y0, int w, int h)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            int v = Height[Idx(x, y)];
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
        }
        return hi - lo <= MaxPadRise;
    }
}
