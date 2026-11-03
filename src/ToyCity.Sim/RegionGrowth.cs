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

    /// <summary>The tile a growing region starts from: near the middle, so it can grow every way.</summary>
    public const int GrowStart = (MaxSide - 1) / 2;

    /// <summary>
    /// A new growing region: one tile of open land, in the middle of a frame of <see cref="MaxSide"/> tiles a side.
    /// </summary>
    public static Region CreateGrowing(ContentDb content, string name, ulong seed, int tileSize = 128)
    {
        var r = new Region(content, name, seed, MaxSide, MaxSide, tileSize, 0);
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

    /// <summary>The ground of a tile of a growing region, or null (wild, or a fixed region).</summary>
    public TileGround? GrownGround(int col, int row) => Grows && TileAt(col, row) is { } t ? _grown![t.Row * Cols + t.Col] : null;

    /// <summary>Why a tile can't be added to this region, or null.</summary>
    public string? CantGrow(int col, int row)
    {
        if (!Grows) return "This region's map was made all at once: only a growing region adds tiles.";
        if (TileAt(col, row) is not { } t) return $"The region can't grow past {MaxSide} tiles a side.";
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
        GroundChanged();
        return t;
    }

    /// <summary>Puts back a tile's ground from a save.</summary>
    public void RestoreGrown(int col, int row, TileGround g)
    {
        if (!Grows) throw new InvalidOperationException("only a growing region keeps its tiles' ground");
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
        int stride = Math.Max(1, TileSize / 16), side = Cols * TileSize / stride + 1;
        var m = World.RawMetres(Seed, side, side, 0, 0, stride, GrowPeriod);
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
        var rivers = World.CarveRivers(corners, s, s, Water, downstream, i => outlet[i], inflow, start);
        // The river's cut mustn't move the shared edge.
        for (int k = 0; k < known.Length; k++) if (known[k] >= 0) corners[k] = (ushort)known[k];

        // 4. Rock: the same beds as the neighbours', and their soil at the edge.
        var (bed, rock) = World.GenerateGeology(tile.Seed, corners, rivers, s, s, Water, ox, oy);
        for (int k = 0; k < known.Length; k++) if (known[k] >= 0) bed[k] = (ushort)Math.Min(knownBed[k], corners[k]);
        return new TileGround(corners, rivers, downstream, bed, rock);
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
        int s = TileSize, wide = Cols * s, high = Rows * s, cw = wide + 1;
        var corners = new ushort[cw * (high + 1)];
        var rivers = new byte[wide * high];
        var down = new int[wide * high];
        Array.Fill(down, -1);
        foreach (var t in Tiles)
        {
            if (GrownGround(t.Col, t.Row) is not { } g) continue;
            int ox = t.Col * s, oy = t.Row * s;
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
                    var (ac, ar) = (ax / s, ay / s);
                    if (GrownGround(ac, ar) is { } n && n.Rivers[ay % s * s + ax % s] != 0) { down[at] = ay * wide + ax; break; }
                }
            }
        }
        return new World.Ground(corners, rivers, down);
    }

    (ushort[] Bedrock, byte[] Rock) GrownGeology()
    {
        int s = TileSize, wide = Cols * s, cw = wide + 1;
        var bed = new ushort[cw * (Rows * s + 1)];
        var rock = new byte[wide * Rows * s];
        foreach (var t in Tiles)
        {
            if (GrownGround(t.Col, t.Row) is not { } g) continue;
            int ox = t.Col * s, oy = t.Row * s;
            for (int y = 0; y <= s; y++) Array.Copy(g.Bedrock, y * (s + 1), bed, (oy + y) * cw + ox, s + 1);
            for (int y = 0; y < s; y++) Array.Copy(g.Rock, y * s, rock, (oy + y) * wide + ox, s);
        }
        return (bed, rock);
    }
}
