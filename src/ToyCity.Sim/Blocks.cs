namespace ToyCity.Sim;

/// <summary>Curved paths snapped to the grid, and zoning by road-enclosed block.</summary>
public sealed partial class World
{
    /// <summary>Longest path run one action may place.</summary>
    public const int MaxPathRun = 512;
    /// <summary>Largest block the block zoning tool will fill; anything bigger is open land, not a block.</summary>
    public const int MaxBlockCells = 1600;

    public static List<(int X, int Y)> PathCellsOf(PlacePathAction a) =>
        a.Curved ? CurveCells(a.X0, a.Y0, a.CX, a.CY, a.X1, a.Y1) : LCells(a.X0, a.Y0, a.X1, a.Y1);

    /// <summary>
    /// Cells of a quadratic curve from P0 through control C to P1, snapped to the grid: sampled densely with integer
    /// maths, rounded to cells and joined edge to edge (a diagonal step goes horizontally first), each cell once. The
    /// web client runs the same steps for its preview.
    /// </summary>
    public static List<(int X, int Y)> CurveCells(int x0, int y0, int cx, int cy, int x1, int y1)
    {
        var cells = new List<(int X, int Y)>();
        var seen = new HashSet<(int, int)>();
        void Add(int x, int y) { if (seen.Add((x, y))) cells.Add((x, y)); }
        long n = 4L * (Math.Abs(cx - x0) + Math.Abs(cy - y0) + Math.Abs(x1 - cx) + Math.Abs(y1 - cy)) + 1;
        long nn = n * n;
        // Round half up: floor((2 * num + nn) / (2 * nn)) with num >= 0 once shifted by an offset.
        const long Off = 1 << 20;
        static long Round(long num, long nn) => (2 * num + nn) / (2 * nn) - Off;
        int lx = x0, ly = y0;
        Add(x0, y0);
        for (long k = 1; k <= n; k++)
        {
            long a = (n - k) * (n - k), b = 2 * (n - k) * k, c = k * k;
            int x = (int)Round(a * (x0 + Off) + b * (cx + Off) + c * (x1 + Off), nn);
            int y = (int)Round(a * (y0 + Off) + b * (cy + Off) + c * (y1 + Off), nn);
            while (lx != x || ly != y)
            {
                if (lx != x) lx += Math.Sign(x - lx);
                else ly += Math.Sign(y - ly);
                Add(lx, ly);
            }
        }
        return cells;
    }

    /// <summary>
    /// The block around a cell: every cell reachable edge to edge without crossing a path, water or a civic
    /// building. Null when it isn't enclosed (it reaches the map edge or is bigger than <see cref="MaxBlockCells"/>).
    /// </summary>
    public List<int>? BlockAt(int x, int y)
    {
        if (!InBounds(x, y)) return null;
        bool Open(int i) => Path[i] == 0 && !IsWater(i) && !(BuildingAtCell(i) is { } b && TypeOf(b).Placeable);
        int start = Idx(x, y);
        if (!Open(start)) return null;
        var cells = new List<int> { start };
        var seen = new HashSet<int> { start };
        for (int q = 0; q < cells.Count; q++)
        {
            int c = cells[q], cx = c % Width, cy = c / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + Dx4[k], ny = cy + Dy4[k];
                if (!InBounds(nx, ny)) return null;
                int n = Idx(nx, ny);
                if (!Open(n) || !seen.Add(n)) continue;
                cells.Add(n);
                if (cells.Count > MaxBlockCells) return null;
            }
        }
        cells.Sort();
        return cells;
    }

    /// <summary>How deep a lot of this density may reach from its road.</summary>
    public int LotDepth(Level density) => Content.LotSizes[(int)density].Max(z => Math.Max(z.W, z.D));

    /// <summary>
    /// Steps from the nearest frontage path for every cell of a block (1 = touching the road), or 0 when no road
    /// with frontage borders that part of the block.
    /// </summary>
    public Dictionary<int, int> FrontageDepth(List<int> block)
    {
        var inBlock = new HashSet<int>(block);
        var depth = new Dictionary<int, int>();
        var queue = new List<int>();
        foreach (int c in block)
        {
            int cx = c % Width, cy = c / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + Dx4[k], ny = cy + Dy4[k];
                if (InBounds(nx, ny) && ProfileAt(Idx(nx, ny)) is { Frontage: true }) { depth[c] = 1; queue.Add(c); break; }
            }
        }
        for (int q = 0; q < queue.Count; q++)
        {
            int c = queue[q], cx = c % Width, cy = c / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + Dx4[k], ny = cy + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int n = Idx(nx, ny);
                if (!inBlock.Contains(n) || depth.ContainsKey(n)) continue;
                depth[n] = depth[c] + 1;
                queue.Add(n);
            }
        }
        foreach (int c in block) depth.TryAdd(c, 0);
        return depth;
    }

    void ApplyZoneBlock(ZoneBlockAction a)
    {
        if (a.Zone == Zone.None) { Reject("Pick residential, commercial or industrial.", a.X, a.Y); return; }
        var block = BlockAt(a.X, a.Y);
        if (block == null)
        {
            Reject("That isn't a block: the land there isn't enclosed by paths (or is huge). Close it off with a road, or paint the zone by hand.", a.X, a.Y);
            return;
        }
        int reach = LotDepth(a.Density);
        var depth = FrontageDepth(block);
        var lots = block.Where(c => depth[c] is > 0 && depth[c] <= reach).ToList();
        var leftover = block.Where(c => !(depth[c] is > 0 && depth[c] <= reach)).ToList();
        var toZone = a.Leftover == Leftover.Zone ? block : lots;
        var parkType = Content.TypeIndex("pocket-park");
        var parks = a.Leftover == Leftover.Parks && parkType >= 0 ? leftover.Where(c => BuildingAt[c] == 0).ToList() : new List<int>();

        byte code = MakeZoneCode(a.Zone, a.Density);
        long cost = toZone.Count(c => ZoneCode[c] != code) * (long)Content.ZoneCostPerCell;
        if (parkType >= 0) cost += parks.Count * (long)Content.Types[parkType].Cost;
        if (!Spend(cost, a.X, a.Y)) return;
        foreach (int c in toZone)
        {
            if (BuildingAtCell(c) is { } b && (TypeOf(b).Placeable || TypeOf(b).Zone == a.Zone)) { ZoneCode[c] = TypeOf(b).Placeable ? ZoneCode[c] : code; continue; }
            if (BuildingAtCell(c) is { } other) RemoveBuilding(other);
            ZoneCode[c] = code;
        }
        foreach (int c in parks)
        {
            ZoneCode[c] = 0;
            AddBuilding(Content.Types[parkType], c % Width, c / Width, constructionDays: 0);
        }
        string what = $"Zoned {lots.Count} cells of the block {ZoneName(a.Zone)}";
        string rest = leftover.Count == 0 ? "; every cell is within reach of a road."
            : a.Leftover switch
            {
                Leftover.Parks => $"; {parks.Count} cells too deep for a lot became pocket parks.",
                Leftover.Zone => $"; {leftover.Count} more cells too deep for a lot were zoned anyway and will stay empty until a road reaches them.",
                _ => $"; {leftover.Count} cells too deep for a lot were left empty.",
            };
        Notices.Add(new SimNotice(what + rest, "info", a.X, a.Y));
    }
}
