namespace ToyCity.Sim;

/// <summary>
/// Part 10: roads grade the ground they're laid on, and buildings level their own foundation pads, so a city can
/// climb off the flat land it started on.
/// </summary>
public sealed partial class World
{
    /// <summary>What a road pays for each step of earth it cuts or fills, a cell at a time.</summary>
    public const int GradeCostPerStep = 10;
    /// <summary>The most a lot's ground may rise across it for a building to level a pad there.</summary>
    public const int MaxPadRise = 3;
    /// <summary>What a placed building pays for each step of earth its pad moves, a cell at a time.</summary>
    public const int PadCostPerStep = 5;

    /// <summary>
    /// The heights a run of cells is graded to so it climbs at most one step a cell: halfway between the highest
    /// gentle profile under the ground and the lowest one over it, so cut and fill roughly balance. Cells marked
    /// fixed (a road already there) keep their height, and nothing goes under <paramref name="floor"/> (the water).
    /// Null when the fixed cells are too far apart in height to join.
    /// </summary>
    public static int[]? Grade(IReadOnlyList<int> h, IReadOnlyList<bool> fixedCell, int floor)
    {
        int n = h.Count;
        const int Inf = 1 << 20;
        var under = new int[n];  // the highest profile with steps of one that never rises above the ground
        var over = new int[n];   // the lowest such profile that never dips below it
        var lo = new int[n];
        var hi = new int[n];
        for (int k = 0; k < n; k++)
        {
            under[k] = k > 0 ? Math.Min(h[k], under[k - 1] + 1) : h[k];
            over[k] = k > 0 ? Math.Max(h[k], over[k - 1] - 1) : h[k];
            lo[k] = Math.Max(fixedCell[k] ? h[k] : -Inf, k > 0 ? lo[k - 1] - 1 : -Inf);
            hi[k] = Math.Min(fixedCell[k] ? h[k] : Inf, k > 0 ? hi[k - 1] + 1 : Inf);
        }
        for (int k = n - 2; k >= 0; k--)
        {
            under[k] = Math.Min(under[k], under[k + 1] + 1);
            over[k] = Math.Max(over[k], over[k + 1] - 1);
            lo[k] = Math.Max(lo[k], lo[k + 1] - 1);
            hi[k] = Math.Min(hi[k], hi[k + 1] + 1);
        }
        var g = new int[n];
        for (int k = 0; k < n; k++)
        {
            int low = Math.Max(lo[k], floor);
            if (low > hi[k]) return null;
            g[k] = Math.Clamp((under[k] + over[k]) >> 1, low, hi[k]);
        }
        return g;
    }

    /// <summary>
    /// The earthworks for a path along these cells: each cell's new height, then the banks beside it (open land next
    /// to a cut or fill eased to within a step of the road). Null with the cell that can't be graded.
    /// </summary>
    List<(int I, int H)>? Earthworks(List<(int X, int Y)> cells, out int badCell)
    {
        badCell = -1;
        var h = cells.Select(c => (int)Height[Idx(c.X, c.Y)]).ToList();
        var fix = cells.Select(c => Path[Idx(c.X, c.Y)] != 0).ToList();
        var g = Grade(h, fix, WaterLevel);
        if (g == null)
        {
            // The first cell where the climb between two fixed roads is too much.
            for (int k = 1; k < cells.Count; k++)
                if (Math.Abs(h[k] - h[k - 1]) > 1) { badCell = k; break; }
            if (badCell < 0) badCell = 0;
            return null;
        }
        var works = new List<(int I, int H)>();
        var road = new HashSet<int>(cells.Select(c => Idx(c.X, c.Y)));
        var banks = new Dictionary<int, int>();
        for (int k = 0; k < cells.Count; k++)
        {
            if (g[k] == h[k]) continue;
            var (x, y) = cells[k];
            works.Add((Idx(x, y), g[k]));
            for (int d = 0; d < 4; d++)
            {
                int nx = x + Dx4[d], ny = y + Dy4[d];
                if (!InBounds(nx, ny)) continue;
                int j = Idx(nx, ny);
                if (road.Contains(j) || Path[j] != 0 || BuildingAt[j] != 0 || IsWater(j)) continue;
                int now = banks.TryGetValue(j, out var b) ? b : Height[j];
                int eased = Math.Clamp(now, Math.Max(g[k] - 1, WaterLevel), g[k] + 1);
                if (eased != now) banks[j] = eased;
            }
        }
        foreach (var (j, eh) in banks.OrderBy(p => p.Key))
            if (eh != Height[j]) works.Add((j, eh));
        return works;
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
