namespace ToyCity.Sim;

/// <summary>One tile's generated ground in a growing region: what a city founded there starts from. Saved with the region.</summary>
/// <param name="Corners">(TileSize + 1)² corners in decimetres.</param>
/// <param name="Rivers">Each cell's river surface in steps, 0 for none.</param>
/// <param name="Downstream">For a river cell, the cell of this tile it flows on into; -1 off the tile or into the sea.</param>
/// <param name="Bedrock">The top of the rock at each corner, in decimetres.</param>
/// <param name="Rock">The kind of rock under each cell.</param>
public sealed record TileGround(ushort[] Corners, byte[] Rivers, int[] Downstream, ushort[] Bedrock, byte[] Rock);

/// <summary>
/// Growing regions (Beta 3.2, G4). A growing region starts as one tile in the middle of a frame of
/// <see cref="Region.MaxSide"/> tiles a side; the rest is wild until someone adds it. Each tile's ground, rivers and
/// rock are made when it's added, carrying on from the tiles already beside it:
/// <list type="bullet">
/// <item>The ground comes from the same noise at the same place in the region, so hills and coasts run on across
/// the edge, and the sea stands at one level for the whole region.</item>
/// <item>Rain wears each tile on its own, so the new ground is then bent to meet its neighbours' edges exactly, the
/// bend fading out over a few dozen cells.</item>
/// <item>A river that ran off a neighbour's edge comes in at the same place and carries on downhill. Water running
/// the other way, towards a tile already made, runs off the edge into it as it would off the map's edge: that tile's
/// ground (and any city on it) doesn't change, so no new river shows there.</item>
/// <item>The rock follows the same beds across the edge, and the soil at the edge is the neighbour's.</item>
/// </list>
/// </summary>
public sealed partial class Region
{
    /// <summary>True for a region that grows a tile at a time; false for a fixed map made all at once.</summary>
    public bool Grows { get; private set; }

    /// <summary>Each tile's ground in a growing region, null while it's wild.</summary>
    TileGround?[]? _grown;

    /// <summary>
    /// A new growing region's frame (Beta 4, part 6): so wide it's never in the way, 127 tiles every way from where it
    /// starts. Regions grown before Beta 4 keep the 8-tile frame they were saved with. Only the tiles in the region
    /// cost anything: the region's ground is kept for the box around them.
    /// </summary>
    public const int GrowFrame = 255;

    /// <summary>The tile a growing region starts from: the middle of its frame, so it can grow every way.</summary>
    public const int GrowStart = (GrowFrame - 1) / 2;

    /// <summary>A new growing region: one tile of open land, in the middle of its frame.</summary>
    public static Region CreateGrowing(ContentDb content, string name, ulong seed, int tileSize = 128)
    {
        var r = new Region(content, name, seed, GrowFrame, GrowFrame, tileSize, 0);
        r.BeGrowing();
        r.Grow(GrowStart, GrowStart);
        return r;
    }

    void BeGrowing()
    {
        Grows = true;
        _grown = new TileGround?[Tiles.Length];
        foreach (var t in Tiles) t.Wild = true;
    }

    /// <summary>A fixed region too big to make at once: all open land, each tile's ground made when it's first needed.</summary>
    void BeTileByTile() => _grown = new TileGround?[Tiles.Length];

    /// <summary>
    /// True when each tile's ground is made on its own and kept with the region: a growing region, or a fixed one too
    /// big to make at once.
    /// </summary>
    public bool PerTile => _grown != null;

    /// <summary>A fixed region too big to make at once (huge): its tiles' ground is made as each is first needed.</summary>
    public bool TileByTile => PerTile && !Grows;

    /// <summary>The ground of a tile made on its own so far, or null (wild, not made yet, or a region made at once).</summary>
    public TileGround? GrownGround(int col, int row) => PerTile && TileAt(col, row) is { } t ? _grown![t.Row * Cols + t.Col] : null;

    /// <summary>
    /// A tile's own ground, made now if it's open land whose ground isn't made yet (a huge region's). Null for a
    /// region made at once, whose tiles are cut from the region's ground, and for wild land.
    /// </summary>
    public TileGround? Survey(int col, int row)
    {
        if (!PerTile || TileAt(col, row) is not { Wild: false } t) return null;
        ref var g = ref _grown![row * Cols + col];
        if (g == null)
        {
            g = MakeTileGround(col, row);
            CarryRiversOn(col, row);
            GroundChanged();
        }
        return g;
    }

    /// <summary>
    /// The box of tiles the region's own ground (<see cref="Ground"/>, <see cref="Heights"/>) covers: the whole map
    /// for a region made at once, else just around the tiles whose ground is made, so a wide frame costs nothing.
    /// </summary>
    public int BoxCol => Box().Col;
    public int BoxRow => Box().Row;
    public int BoxCols => Box().Cols;
    public int BoxRows => Box().Rows;
    (int Col, int Row, int Cols, int Rows)? _box;

    (int Col, int Row, int Cols, int Rows) Box()
    {
        if (_box is { } b) return b;
        if (!PerTile) return (_box = (0, 0, Cols, Rows)).Value;
        int c0 = int.MaxValue, r0 = int.MaxValue, c1 = -1, r1 = -1;
        for (int k = 0; k < _grown!.Length; k++)
        {
            if (_grown[k] == null) continue;
            int c = k % Cols, r = k / Cols;
            (c0, r0, c1, r1) = (Math.Min(c0, c), Math.Min(r0, r), Math.Max(c1, c), Math.Max(r1, r));
        }
        if (c1 < 0) return (_box = (0, 0, 1, 1)).Value;
        return (_box = (c0, r0, c1 - c0 + 1, r1 - r0 + 1)).Value;
    }

    /// <summary>The region-ground cell of a tile's cell, in the box (<see cref="BoxCol"/>).</summary>
    public (int X, int Y) BoxCell(RegionTile t, int x, int y) => ((t.Col - BoxCol) * TileSize + x, (t.Row - BoxRow) * TileSize + y);

    /// <summary>The tile under a region-ground cell, or null outside the region's tiles.</summary>
    public RegionTile? TileAtBox(int x, int y) => x < 0 || y < 0 ? null : TileAt(BoxCol + x / TileSize, BoxRow + y / TileSize);

    /// <summary>
    /// A rough look at a tile whose ground isn't made yet (a huge region's open land): the region's noise every few
    /// cells, unworn and without rivers, at the region's shore, as <paramref name="n"/> by <paramref name="n"/> step
    /// heights. For the region map, so it doesn't make a thousand tiles' ground to draw them.
    /// </summary>
    public byte[] Glimpse(int col, int row, int n)
    {
        int step = Math.Max(1, TileSize / n);
        var m = World.RawMetres(Seed, n + 1, n + 1, col * TileSize, row * TileSize, step, GrowPeriod);
        var (shore, lowest) = Sea();
        return World.StepsFromCorners(World.CornersFromMetres(m, shore, lowest, 8), n, n);
    }

    /// <summary>Why a tile can't be added to this region, or null.</summary>
    public string? CantGrow(int col, int row)
    {
        if (!Grows) return "This region's map was made all at once: only a growing region adds tiles.";
        if (TileAt(col, row) is not { } t) return $"The region can't grow past {Cols} tiles a side.";
        if (!t.Wild) return "That tile is part of the region already.";
        if (!Tiles.Any(o => !o.Wild)) return null;
        for (int side = 0; side < 4; side++)
            if (Across(t, side) is { Wild: false }) return null;
        return "A new tile joins on a side of one already in the region.";
    }

    /// <summary>
    /// Adds a wild tile to a growing region as open land, making its ground, rivers and rock to meet the tiles already
    /// beside it. Anyone may then found a city on it.
    /// </summary>
    public RegionTile Grow(int col, int row)
    {
        if (CantGrow(col, row) is { } why) throw new InvalidOperationException(why);
        var t = TileAt(col, row)!;
        _grown![row * Cols + col] = MakeTileGround(col, row);
        t.Wild = false;
        CarryRiversOn(col, row);
        GroundChanged();
        return t;
    }

    /// <summary>Puts back a tile's ground from a save.</summary>
    public void RestoreGrown(int col, int row, TileGround g)
    {
        if (!PerTile) throw new InvalidOperationException("only a growing or huge region keeps its tiles' ground");
        var t = TileAt(col, row) ?? throw new InvalidDataException($"{col},{row} is outside the region");
        int s = TileSize;
        if (g.Corners.Length != (s + 1) * (s + 1) || g.Bedrock.Length != g.Corners.Length || g.Rivers.Length != s * s || g.Rock.Length != s * s || g.Downstream.Length != s * s)
            throw new InvalidDataException($"the ground saved for tile {col},{row} is the wrong size");
        _grown![row * Cols + col] = g;
        t.Wild = false;
        GroundChanged();
    }

    void GroundChanged()
    {
        _ground = null;
        _geology = null;
        _heights = null;
        _floodSamples = null;
        _waterExits = null;
        _box = null;
    }

    /// <summary>The noise's scale for a growing region: the same as a fixed region of these tiles.</summary>
    double GrowPeriod => Math.Min(128, Math.Max(16, TileSize));

    (double Shore, double Lowest)? _sea;

    /// <summary>
    /// Where the shore stands for the whole growing region, in the noise's metres: the lowest fifth of the frame's
    /// ground, sampled every few cells, is under water, as a fixed region's is. One level for every tile, so the sea
    /// meets itself across their edges.
    /// </summary>
    (double Shore, double Lowest) Sea()
    {
        if (_sea is { } s) return s;
        // Sampled over a set window, so the shore never moves as the region grows: the whole frame of an 8-tile
        // (pre-Beta 4) or huge region, else the 8 tiles a side around where a growing region starts.
        int window = Cols <= MaxSide ? Cols : 8, at = Cols <= MaxSide ? 0 : (GrowStart - 3) * TileSize;
        int stride = Math.Max(1, TileSize / 16), side = window * TileSize / stride + 1;
        var m = World.RawMetres(Seed, side, side, at, at, stride, GrowPeriod);
        Array.Sort(m);
        return (_sea = (m[(int)(m.Length * 0.18)], m[0])).Value;
    }

    /// <summary>How far, in cells, a tile's ground bends to meet a neighbour's edge.</summary>
    public const int StitchReach = 24;

    TileGround MakeTileGround(int col, int row)
    {
        const int Water = 8;
        int s = TileSize, cw = s + 1, ox = col * s, oy = row * s;
        var tile = TileAt(col, row)!;
        // 1. The ground from the region's noise at this place, worn by its own rain, with the region's shore.
        var m = World.RawMetres(Seed, cw, cw, ox, oy, 1, GrowPeriod);
        World.Erode(m, cw, cw, tile.Seed);
        var (shore, lowest) = Sea();
        var corners = World.CornersFromMetres(m, shore, lowest, Water);

        // 2. The corners the neighbours already fix: their shared edges, and a lone shared corner across a diagonal.
        var known = new int[cw * cw];
        Array.Fill(known, -1);
        var knownBed = new int[cw * cw];
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if ((dx | dy) == 0 || GrownGround(col + dx, row + dy) is not { } n) continue;
            // The corners of this tile that lie on that tile, in each's own coordinates.
            for (int y = 0; y <= s; y++)
            for (int x = 0; x <= s; x++)
            {
                int nx = x - dx * s, ny = y - dy * s;
                if (nx < 0 || ny < 0 || nx > s || ny > s) continue;
                known[y * cw + x] = n.Corners[ny * cw + nx];
                knownBed[y * cw + x] = n.Bedrock[ny * cw + nx];
            }
        }
        Stitch(corners, known, cw);

        // 3. Rivers: in where a neighbour's ran off its edge towards this tile; out past the region's wild edges, and
        // into a neighbour's water or ground no higher than here.
        var inflow = new int[s * s];
        var start = new byte[s * s];
        var outlet = new bool[s * s];
        for (int i = 0; i < s * s; i++)
        {
            int x = i % s, y = i / s;
            if (x != 0 && y != 0 && x != s - 1 && y != s - 1) continue;
            bool open = false, any = false;
            for (int side = 0; side < 4; side++)
            {
                if (!OnSide(x, y, s, side)) continue;
                var (ax, ay) = AcrossCell(x, y, s, side);
                var (ncol, nrow) = side switch { 0 => (col, row - 1), 1 => (col + 1, row), 2 => (col, row + 1), _ => (col - 1, row) };
                if (GrownGround(ncol, nrow) is not { } n) { open = true; continue; }
                any = true;
                int j = ay * s + ax;
                // A river (its water above its bed, not the sea's) that flows on off the neighbour's edge.
                bool exits = n.Rivers[j] > CellStep(n.Corners, s, j) && n.Downstream[j] < 0;
                if (exits)
                {
                    inflow[i] = Math.Max(inflow[i], 2 * World.RiverCatchment);
                    start[i] = start[i] == 0 ? n.Rivers[j] : Math.Min(start[i], n.Rivers[j]);
                }
                // Water may run on into the neighbour where its ground is no higher: off the edge, as at the map's
                // edge, rather than along the seam looking for a way out. The neighbour's ground stays as it is.
                else if (CellStep(n.Corners, s, j) < Water || n.Rivers[j] != 0 || CellStep(n.Corners, s, j) <= CellStep(corners, s, i)) open = true;
            }
            outlet[i] = inflow[i] == 0 && (open || !any);
        }
        var downstream = new int[s * s];
        var rivers = World.CarveRivers(corners, s, s, Water, downstream, i => outlet[i], inflow, start, Channels, Seed, col * s, row * s);
        // The river's cut mustn't move the shared edge.
        for (int k = 0; k < known.Length; k++) if (known[k] >= 0) corners[k] = (ushort)known[k];

        // 4. Rock: the same beds as the neighbours', and their soil at the edge.
        var (bed, rock) = World.GenerateGeology(tile.Seed, corners, rivers, s, s, Water, ox, oy);
        for (int k = 0; k < known.Length; k++) if (known[k] >= 0) bed[k] = (ushort)Math.Min(knownBed[k], corners[k]);
        return new TileGround(corners, rivers, downstream, bed, rock);
    }

    /// <summary>
    /// Rivers that run off a new tile's edge into a tile already made carry on across it while nobody has founded a
    /// city there (Beta 4.1, part 4): down the open land's lowest way, cutting a channel a step deep, until they join
    /// a river or the sea, or run off its far edge, where they carry on again into the next open tile. A tile with a
    /// city keeps its ground: the water runs off the edge into it, as before. The shared edges never move.
    /// </summary>
    void CarryRiversOn(int col, int row)
    {
        int s = TileSize;
        var queue = new Queue<(int Col, int Row, int Cell, int Surface)>();
        void Exits(int c, int r)
        {
            if (GrownGround(c, r) is not { } g) return;
            for (int i = 0; i < s * s; i++)
            {
                int x = i % s, y = i / s;
                if (x != 0 && y != 0 && x != s - 1 && y != s - 1) continue;
                if (g.Rivers[i] <= CellStep(g.Corners, s, i) || g.Downstream[i] >= 0) continue;
                // Off the side whose open land across is lowest.
                (int Col, int Row, int Cell, int Step)? best = null;
                bool wildSide = false;
                for (int side = 0; side < 4; side++)
                {
                    if (!OnSide(x, y, s, side)) continue;
                    var (ncol, nrow) = side switch { 0 => (c, r - 1), 1 => (c + 1, r), 2 => (c, r + 1), _ => (c - 1, r) };
                    var nt = TileAt(ncol, nrow);
                    if (nt == null || nt.Wild || GrownGround(ncol, nrow) is not { } n) { wildSide = true; continue; }
                    var (ax, ay) = AcrossCell(x, y, s, side);
                    int j = ay * s + ax, st = CellStep(n.Corners, s, j);
                    if (nt.Founded || n.Rivers[j] != 0 || st < 8 || st > g.Rivers[i]) continue;
                    if (best == null || st < best.Value.Step) best = (ncol, nrow, j, st);
                }
                if (best is { } b && !wildSide) queue.Enqueue((b.Col, b.Row, b.Cell, g.Rivers[i]));
            }
        }
        Exits(col, row);
        for (int guard = 0; queue.Count > 0 && guard < 64; guard++)
        {
            var (c, r, entry, surface) = queue.Dequeue();
            var g = GrownGround(c, r)!;
            var corners = (ushort[])g.Corners.Clone();
            var rivers = (byte[])g.Rivers.Clone();
            var down = (int[])g.Downstream.Clone();
            var bed = (ushort[])g.Bedrock.Clone();
            if (rivers[entry] != 0) continue;
            int cw = s + 1, cur = entry, prev = -1;
            var seen = new HashSet<int>();
            bool offEdge = false;
            for (int k = 0; k < s * s && cur >= 0; k++)
            {
                int ground = CellStep(corners, s, cur);
                if (rivers[cur] != 0 || ground < 8) { if (prev >= 0) down[prev] = cur; break; }
                seen.Add(cur);
                // A step's cut under the cell, but never on the tile's own edge, which its neighbours share.
                int x = cur % s, y = cur / s;
                // A channel a step deep, cut deeper (up to three steps) through a rise to keep the water running
                // downhill. On the edge only the inner two corners can go down, so they go twice as far.
                bool onEdge = x == 0 || y == 0 || x == s - 1 || y == s - 1;
                int[] four = { y * cw + x, y * cw + x + 1, (y + 1) * cw + x, (y + 1) * cw + x + 1 };
                int cut = ground;
                for (int d = 0; d < 3 && (d == 0 || cut >= surface); d++)
                {
                    foreach (int cc in four)
                    {
                        int ccx = cc % cw, ccy = cc / cw;
                        if (ccx == 0 || ccy == 0 || ccx == s || ccy == s) continue;
                        corners[cc] = (ushort)Math.Max(0, corners[cc] - (onEdge ? 2 : 1) * World.StepDm);
                        bed[cc] = Math.Min(bed[cc], corners[cc]);
                    }
                    cut = CellStep(corners, s, cur);
                }
                // Never higher than the water above it: where the cut leaves no room, the river ends here.
                surface = Math.Min(surface, Math.Max(cut + 1, ground));
                if (surface <= cut) { if (prev < 0) break; down[prev] = -1; break; }
                rivers[cur] = (byte)Math.Min(255, surface);
                down[cur] = -1;
                if (prev >= 0) down[prev] = cur;
                prev = cur;
                // On down the lowest way that isn't back up the river.
                int next = -1, nextStep = int.MaxValue;
                ReadOnlySpan<int> ddx = [0, 1, 0, -1], ddy = [-1, 0, 1, 0];
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + ddx[d], ny = y + ddy[d];
                    if (nx < 0 || ny < 0 || nx >= s || ny >= s) continue;
                    int j = ny * s + nx;
                    if (seen.Contains(j)) continue;
                    int st = rivers[j] != 0 ? Math.Min(rivers[j], CellStep(corners, s, j)) : CellStep(corners, s, j);
                    if (st < nextStep || st == nextStep && j < next) { next = j; nextStep = st; }
                }
                // At the edge, off it when nothing on the tile is lower.
                bool edge = x == 0 || y == 0 || x == s - 1 || y == s - 1;
                if (edge && (next < 0 || nextStep > ground)) { offEdge = true; break; }
                // A hollow with no way out: the river ends in it.
                if (next < 0 || nextStep > surface + 2) break;
                cur = next;
            }
            _grown![r * Cols + c] = g with { Corners = corners, Rivers = rivers, Downstream = down, Bedrock = bed };
            if (offEdge) Exits(c, r);
        }
    }

    /// <summary>
    /// Bends the ground to meet the corners already fixed: the difference at each fixed corner spreads inward, fading
    /// over about <see cref="StitchReach"/> cells (a screened Poisson relaxation, so the bend is smooth and has no
    /// creases). Free edges bend with the rest.
    /// </summary>
    static void Stitch(ushort[] corners, int[] known, int cw)
    {
        int n = corners.Length;
        bool any = false;
        var e = new double[n];
        for (int k = 0; k < n; k++) if (known[k] >= 0) { e[k] = known[k] - corners[k]; any = true; }
        if (!any) return;
        // The bend fades by e every third of the reach: about 5% left at its end.
        double screen = 9.0 / (StitchReach * StitchReach);
        // Gauss-Seidel with over-relaxation, in a fixed order, so the same tile always comes out the same.
        for (int it = 0; it < 600; it++)
        {
            for (int y = 0; y < cw; y++)
            for (int x = 0; x < cw; x++)
            {
                int k = y * cw + x;
                if (known[k] >= 0) continue;
                double sum = 0; int c = 0;
                if (x > 0) { sum += e[k - 1]; c++; }
                if (x < cw - 1) { sum += e[k + 1]; c++; }
                if (y > 0) { sum += e[k - cw]; c++; }
                if (y < cw - 1) { sum += e[k + cw]; c++; }
                double v = sum / (c + screen);
                e[k] += 1.7 * (v - e[k]);
            }
        }
        for (int k = 0; k < n; k++)
            corners[k] = known[k] >= 0 ? (ushort)known[k] : (ushort)Math.Clamp((int)Math.Round(corners[k] + e[k]), 0, World.MaxCornerDm);
    }

    static bool OnSide(int x, int y, int s, int side) => side switch { 0 => y == 0, 1 => x == s - 1, 2 => y == s - 1, _ => x == 0 };

    /// <summary>The cell across a side of a tile's edge cell, in the neighbour's own coordinates.</summary>
    static (int X, int Y) AcrossCell(int x, int y, int s, int side) => side switch { 0 => (x, s - 1), 1 => (0, y), 2 => (x, 0), _ => (s - 1, y) };

    static int CellStep(ushort[] corners, int s, int i)
    {
        int cw = s + 1, c = i / s * cw + i % s;
        return (corners[c] + corners[c + 1] + corners[c + cw] + corners[c + cw + 1] + 2 * World.StepDm) / (4 * World.StepDm);
    }

    /// <summary>
    /// A growing region's whole frame, from its tiles' ground: wild tiles are deep water. A river that runs off one
    /// tile's edge flows on into the river that carries on across it.
    /// </summary>
    World.Ground GrownFrame()
    {
        int s = TileSize, wide = BoxCols * s, high = BoxRows * s, cw = wide + 1;
        var corners = new ushort[cw * (high + 1)];
        var rivers = new byte[wide * high];
        var down = new int[wide * high];
        Array.Fill(down, -1);
        foreach (var t in BoxTiles())
        {
            if (GrownGround(t.Col, t.Row) is not { } g) continue;
            var (ox, oy) = BoxCell(t, 0, 0);
            for (int y = 0; y <= s; y++) Array.Copy(g.Corners, y * (s + 1), corners, (oy + y) * cw + ox, s + 1);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                int i = y * s + x, at = (oy + y) * wide + ox + x;
                rivers[at] = g.Rivers[i];
                if (g.Downstream[i] >= 0) { down[at] = (oy + g.Downstream[i] / s) * wide + ox + g.Downstream[i] % s; continue; }
                if (g.Rivers[i] == 0) continue;
                // Off the tile's edge: on into a river across it.
                for (int side = 0; side < 4; side++)
                {
                    if (!OnSide(x, y, s, side)) continue;
                    int ax = ox + x + (side == 1 ? 1 : side == 3 ? -1 : 0), ay = oy + y + (side == 2 ? 1 : side == 0 ? -1 : 0);
                    if (ax < 0 || ay < 0 || ax >= wide || ay >= high) continue;
                    var (ac, ar) = (BoxCol + ax / s, BoxRow + ay / s);
                    if (GrownGround(ac, ar) is { } n && n.Rivers[ay % s * s + ax % s] != 0) { down[at] = ay * wide + ax; break; }
                }
            }
        }
        return new World.Ground(corners, rivers, down);
    }

    (ushort[] Bedrock, byte[] Rock) GrownGeology()
    {
        int s = TileSize, wide = BoxCols * s, cw = wide + 1;
        var bed = new ushort[cw * (BoxRows * s + 1)];
        var rock = new byte[wide * BoxRows * s];
        foreach (var t in BoxTiles())
        {
            if (GrownGround(t.Col, t.Row) is not { } g) continue;
            var (ox, oy) = BoxCell(t, 0, 0);
            for (int y = 0; y <= s; y++) Array.Copy(g.Bedrock, y * (s + 1), bed, (oy + y) * cw + ox, s + 1);
            for (int y = 0; y < s; y++) Array.Copy(g.Rock, y * s, rock, (oy + y) * wide + ox, s);
        }
        return (bed, rock);
    }

    /// <summary>The tiles in the box, row by row.</summary>
    public IEnumerable<RegionTile> BoxTiles()
    {
        for (int r = BoxRow; r < BoxRow + BoxRows; r++)
        for (int c = BoxCol; c < BoxCol + BoxCols; c++)
            yield return Tiles[r * Cols + c];
    }
}
