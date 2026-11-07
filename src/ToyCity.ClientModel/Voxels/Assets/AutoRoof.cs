using System.Numerics;

namespace ToyCity.ClientModel.Voxels.Assets;

public enum RoofStyle { None = 0, Gable = 1, Hip = 2, Flat = 3, Shed = 4 }

/// <summary>
/// An auto-roof's settings, saved with the asset rather than as voxels: the roof is made again from the walls
/// whenever they change. Pitch in degrees, overhang in centimetres past the walls' outer faces.
/// </summary>
public sealed record RoofSpec(RoofStyle Style = RoofStyle.Gable, double Pitch = 35, int OverhangCm = 40, byte Colour = AssetPalette.RoofTile, bool TurnRidge = false, string Finish = "")
{
    /// <summary>RC part 5: the roof's covering as a finish (shingles, slate, clay tile, metal); "" for its colour.</summary>
    public string Finish { get; init; } = Finish ?? "";
    /// <summary>RC part 5: the gable ends' finish, the walls' own (brick, render); "" for the walls' commonest colour.</summary>
    public string GableFinish { get; init; } = "";
}

/// <summary>
/// The Sims-style auto-roof: a smooth polygon roof over a voxel building's walls. It finds the top of the walls (the
/// highest level a good length of wall reaches), takes everything they enclose as the footprint, any shape of it,
/// and roofs it:
/// - hip: every side slopes, meeting in hips and valleys at the corners. On a footprint of right angles this is the
///   footprint's chessboard distance from its outline, so the faces always meet cleanly;
/// - gable: two slopes to a ridge across the footprint's shorter way, closed at the ends by gable walls in the
///   walls' colour;
/// - shed: one slope rising to the south (or east, turned);
/// - flat: a slab just over the walls.
/// The roof has a thickness: a fascia round its edge and a soffit under the overhang.
/// </summary>
public static class AutoRoof
{
    /// <summary>The level of the top of the walls in voxels, and which columns the roof covers; null when there are no walls to roof.</summary>
    public static (int Eaves, bool[,] Footprint)? Footprint(VoxModel m, int detail)
    {
        int W = m.SizeX, H = m.SizeY;
        var top = new int[W, H];
        var count = new int[m.SizeZ + 1];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int t = 0;
                for (int z = m.SizeZ - 1; z >= 0; z--) if (m[x, y, z] != 0) { t = z + 1; break; }
                top[x, y] = t;
                count[t]++;
            }
        // The eaves: the highest level that a straight run of wall at least a module long tops out at. A tree's
        // crown or a lamp has no such run, so only walls count.
        int M = Pieces.Module * detail, tol = detail / 4;
        bool[,] Walls(int z)
        {
            var near = new bool[W, H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) near[x, y] = Math.Abs(top[x, y] - z) <= tol;
            var wall = new bool[W, H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W;)
                {
                    if (!near[x, y]) { x++; continue; }
                    int s = x;
                    while (x < W && near[x, y]) x++;
                    if (x - s >= M) for (int i = s; i < x; i++) wall[i, y] = true;
                }
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H;)
                {
                    if (!near[x, y]) { y++; continue; }
                    int s = y;
                    while (y < H && near[x, y]) y++;
                    if (y - s >= M) for (int i = s; i < y; i++) wall[x, i] = true;
                }
            return wall;
        }
        int eaves = 0;
        bool[,]? wall = null;
        for (int z = m.SizeZ; z >= Pieces.Storey * detail / 2 && wall == null; z--)
        {
            if (count[z] < M) continue;
            var w = Walls(z);
            int n = 0;
            foreach (bool b in w) if (b) n++;
            if (n >= M * Math.Max(1, detail / 2)) { eaves = z; wall = w; }
        }
        if (wall == null) return null;
        // Within the tolerance, the level most of those walls actually top out at.
        var at = new int[m.SizeZ + 1];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (wall[x, y]) at[top[x, y]]++;
        eaves = Array.IndexOf(at, at.Max());
        // Everything the walls enclose: whatever can't be reached from outside without crossing a wall.
        var outside = new bool[W + 2, H + 2];
        var todo = new Stack<(int, int)>();
        todo.Push((0, 0));
        while (todo.Count > 0)
        {
            var (x, y) = todo.Pop();
            if (x < 0 || y < 0 || x > W + 1 || y > H + 1 || outside[x, y]) continue;
            if (x >= 1 && y >= 1 && x <= W && y <= H && wall[x - 1, y - 1]) continue;
            outside[x, y] = true;
            todo.Push((x + 1, y)); todo.Push((x - 1, y)); todo.Push((x, y + 1)); todo.Push((x, y - 1));
        }
        var foot = new bool[W, H];
        int area = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (!outside[x + 1, y + 1]) { foot[x, y] = true; area++; }
        // A footprint smaller than a quarter of a module square is a post or a chimney, not a building.
        return area >= M * M / 4 ? (eaves, foot) : null;
    }

    /// <summary>The roof's mesh, in the same units and axes as <see cref="VoxMesher.Mesh"/>: <paramref name="scale"/> world units a voxel, x east, y up, z south.</summary>
    public static MeshData Build(VoxModel m, int detail, RoofSpec spec, float scale)
    {
        var mesh = new MeshData();
        if (spec.Style == RoofStyle.None || Footprint(m, detail) is not { } found) return mesh;
        var (eaves, foot) = found;
        int W = m.SizeX, H = m.SizeY;
        double vm = 0.5 / detail;                                  // metres a voxel
        int o = Math.Max(0, (int)Math.Round(spec.OverhangCm / 100.0 / vm));
        int t = Math.Max(1, (int)Math.Round(0.15 / vm));          // the roof's thickness
        bool turn = spec.TurnRidge;
        // The footprint grown by the overhang, on a grid padded to hold it.
        int GW = W + 2 * o, GH = H + 2 * o;
        var grown = new bool[GW, GH];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (foot[x, y])
                    for (int dy = 0; dy <= 2 * o; dy++)
                        for (int dx = 0; dx <= 2 * o; dx++) grown[x + dx, y + dy] = true;
        bool In(int x, int y) => x >= 0 && y >= 0 && x < GW && y < GH && grown[x, y];
        // A gable's ridge runs the footprint's longer way, unless turned.
        int bx0 = GW, bx1 = 0, by0 = GH, by1 = 0;
        for (int y = 0; y < GH; y++) for (int x = 0; x < GW; x++) if (grown[x, y]) { bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y); }
        bool ridgeAlongX = (bx1 - bx0 >= by1 - by0) != turn;

        // Distances in half voxels on a doubled grid: even points are cell corners, odd ones edge middles and cell
        // centres, so each cell's centre is known and the fold through it can follow the roof's hips and valleys.
        int DW = 2 * GW + 1, DH = 2 * GH + 1;
        var d = new int[DW, DH];
        bool Inside(int px, int py)
        {
            // A point is inside when every cell touching it is under the roof.
            int x0 = (px - 1) >> 1, x1 = px >> 1, y0 = (py - 1) >> 1, y1 = py >> 1;
            return In(x0, y0) && In(x1, y0) && In(x0, y1) && In(x1, y1);
        }
        switch (spec.Style)
        {
            case RoofStyle.Hip:
            {
                var q = new Queue<(int, int)>();
                for (int py = 0; py < DH; py++)
                    for (int px = 0; px < DW; px++)
                        if (Inside(px, py)) d[px, py] = int.MaxValue; else { d[px, py] = 0; q.Enqueue((px, py)); }
                while (q.Count > 0)
                {
                    var (px, py) = q.Dequeue();
                    for (int ny = Math.Max(0, py - 1); ny <= Math.Min(DH - 1, py + 1); ny++)
                        for (int nx = Math.Max(0, px - 1); nx <= Math.Min(DW - 1, px + 1); nx++)
                            if (d[nx, ny] > d[px, py] + 1) { d[nx, ny] = d[px, py] + 1; q.Enqueue((nx, ny)); }
                }
                break;
            }
            case RoofStyle.Gable:
            case RoofStyle.Shed:
            {
                // Along each line across the ridge: the distance to the nearer edge (gable), or from the low edge (shed).
                bool shed = spec.Style == RoofStyle.Shed;
                int lines = ridgeAlongX ? DW : DH, len = ridgeAlongX ? DH : DW;
                for (int a = 0; a < lines; a++)
                {
                    int last = int.MinValue / 2;
                    var fwd = new int[len];
                    for (int b = 0; b < len; b++)
                    {
                        var (px, py) = ridgeAlongX ? (a, b) : (b, a);
                        if (!Inside(px, py)) last = b;
                        fwd[b] = b - last;
                    }
                    last = int.MaxValue / 2;
                    for (int b = len - 1; b >= 0; b--)
                    {
                        var (px, py) = ridgeAlongX ? (a, b) : (b, a);
                        if (!Inside(px, py)) last = b;
                        d[px, py] = shed ? fwd[b] : Math.Min(fwd[b], last - b);
                    }
                }
                break;
            }
            // Flat: every distance 0, the slab sits at the eaves.
        }

        double rise = spec.Style == RoofStyle.Flat ? 0 : Math.Tan(Math.Clamp(spec.Pitch, 5, 70) * Math.PI / 180);
        // Heights in voxels: zero at the walls' outer faces, so the overhang dips below the eaves as a real one does.
        double Hgt(int px, int py) => eaves + (spec.Style == RoofStyle.Flat ? t : rise * (d[px, py] / 2.0 - o));
        Vector3 P(double gx, double gy, double z) => new((float)((gx - o) * scale), (float)(z * scale), (float)((gy - o) * scale));
        uint top = m.Palette[spec.Colour], under = m.Palette[AssetPalette.WoodDark], fascia = m.Palette[AssetPalette.Trim];

        // The roof's top: planar cells merged into rectangles; a cell a hip or valley crosses is split along it.
        var done = new bool[GW, GH];
        (int A, int B, int C)? Plane(int x, int y)
        {
            int d00 = d[2 * x, 2 * y], d10 = d[2 * x + 2, 2 * y], d01 = d[2 * x, 2 * y + 2], d11 = d[2 * x + 2, 2 * y + 2];
            if (d00 + d11 != d10 + d01) return null;
            if (2 * d[2 * x + 1, 2 * y + 1] != d00 + d11) return null;
            int a = d10 - d00, b = d01 - d00;
            return (a, b, d00 - a * x - b * y);
        }
        var steepTop = new HashSet<int>();
        void Tri(Vector3 a, Vector3 b, Vector3 c, uint colour, bool up)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-12f) return;
            if ((n.Y > 0) != up) { (b, c) = (c, b); n = -n; }
            n = Vector3.Normalize(n);
            int s = mesh.Positions.Count;
            // A steep face of the top (the gable end's overhang, standing almost upright): drawn in the walls' finish.
            if (up && n.Y < 0.2f) steepTop.Add(mesh.Indices.Count);
            mesh.Positions.AddRange(new[] { a, b, c });
            for (int k = 0; k < 3; k++) { mesh.Normals.Add(n); mesh.Colors.Add(colour); }
            mesh.Indices.AddRange(new[] { s, s + 1, s + 2 });
        }
        void Surface(int x0, int y0, int x1, int y1, double h00, double h10, double h01, double h11, double drop, bool fold00to11, bool up, uint colour)
        {
            var a = P(x0, y0, h00 - drop); var b = P(x1, y0, h10 - drop); var c = P(x0, y1, h01 - drop); var e = P(x1, y1, h11 - drop);
            if (fold00to11) { Tri(a, b, e, colour, up); Tri(a, e, c, colour, up); }
            else { Tri(a, b, c, colour, up); Tri(b, e, c, colour, up); }
        }
        for (int y = 0; y < GH; y++)
            for (int x = 0; x < GW; x++)
            {
                if (!grown[x, y] || done[x, y]) continue;
                double h00 = Hgt(2 * x, 2 * y), h10 = Hgt(2 * x + 2, 2 * y), h01 = Hgt(2 * x, 2 * y + 2), h11 = Hgt(2 * x + 2, 2 * y + 2);
                if (Plane(x, y) is not { } pl)
                {
                    // The fold follows whichever diagonal passes through the true height at the cell's centre.
                    double centre = Hgt(2 * x + 1, 2 * y + 1);
                    bool fold = Math.Abs((h00 + h11) / 2 - centre) <= Math.Abs((h10 + h01) / 2 - centre);
                    Surface(x, y, x + 1, y + 1, h00, h10, h01, h11, 0, fold, true, top);
                    Surface(x, y, x + 1, y + 1, h00, h10, h01, h11, t, fold, false, under);
                    done[x, y] = true;
                    continue;
                }
                int w = 1;
                while (x + w < GW && grown[x + w, y] && !done[x + w, y] && Plane(x + w, y) == pl) w++;
                int h = 1;
                for (; y + h < GH; h++)
                {
                    bool row = true;
                    for (int k = 0; k < w && row; k++) row = grown[x + k, y + h] && !done[x + k, y + h] && Plane(x + k, y + h) == pl;
                    if (!row) break;
                }
                for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) done[x + xx, y + yy] = true;
                double g00 = Hgt(2 * x, 2 * y), g10 = Hgt(2 * (x + w), 2 * y), g01 = Hgt(2 * x, 2 * (y + h)), g11 = Hgt(2 * (x + w), 2 * (y + h));
                Surface(x, y, x + w, y + h, g00, g10, g01, g11, 0, true, true, top);
                Surface(x, y, x + w, y + h, g00, g10, g01, g11, t, true, false, under);
            }

        // Walls round the roof's edge: the fascia, the roof's thickness all round its outline, and the gable ends,
        // from the top of the walls up to the roof's underside, in the walls' colour.
        uint wallColour = WallColour(m, foot, eaves);
        Edges(grown, GW, GH, (x0, y0, x1, y1, nx, ny) =>
        {
            Strip(x0, y0, x1, y1, nx, ny, (px, py) => Hgt(px, py), (px, py) => Hgt(px, py) - t, fascia);
        });
        int gableFrom = mesh.Indices.Count;
        if (spec.Style is RoofStyle.Gable or RoofStyle.Shed)
        {
            var padded = new bool[GW, GH];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) padded[x + o, y + o] = foot[x, y];
            Edges(padded, GW, GH, (x0, y0, x1, y1, nx, ny) =>
                Strip(x0, y0, x1, y1, nx, ny, (px, py) => Math.Max(eaves, Hgt(px, py) - t), (_, _) => eaves, wallColour));
        }

        void Strip(int x0, int y0, int x1, int y1, int nx, int ny, Func<int, int, double> upper, Func<int, int, double> lower, uint colour)
        {
            // One straight run of edge, split where its height stops changing evenly.
            int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
            int sx = Math.Sign(x1 - x0), sy = Math.Sign(y1 - y0);
            double U(int i) => upper(2 * (x0 + sx * i), 2 * (y0 + sy * i));
            double L(int i) => lower(2 * (x0 + sx * i), 2 * (y0 + sy * i));
            int start = 0;
            for (int i = 1; i <= steps; i++)
            {
                bool end = i == steps || Math.Abs(U(i + 1) - 2 * U(i) + U(i - 1)) > 1e-6 || Math.Abs(L(i + 1) - 2 * L(i) + L(i - 1)) > 1e-6;
                if (!end) continue;
                double ua = U(start), ub = U(i), la = L(start), lb = L(i);
                if (ua - la > 1e-6 || ub - lb > 1e-6)
                {
                    var a = P(x0 + sx * start, y0 + sy * start, la); var b = P(x0 + sx * i, y0 + sy * i, lb);
                    var c = P(x0 + sx * i, y0 + sy * i, ub); var e = P(x0 + sx * start, y0 + sy * start, ua);
                    var n = new Vector3(nx, 0, ny);
                    Wall(a, b, c, colour, n); Wall(a, c, e, colour, n);
                }
                start = i;
            }
        }
        void Wall(Vector3 a, Vector3 b, Vector3 c, uint colour, Vector3 outward)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-12f) return;
            if (Vector3.Dot(n, outward) < 0) (b, c) = (c, b);
            int s = mesh.Positions.Count;
            mesh.Positions.AddRange(new[] { a, b, c });
            for (int k = 0; k < 3; k++) { mesh.Normals.Add(outward); mesh.Colors.Add(colour); }
            mesh.Indices.AddRange(new[] { s, s + 1, s + 2 });
        }
        int gableTo = mesh.Indices.Count;
        // RC part 5: a covering finish on the slopes, laid along them by the metre, and the walls' finish on the gable
        // ends; the fascia keeps its colour.
        float mpu = (float)(vm / scale);
        if (spec.Finish.Length > 0) mesh = mesh.FinishFaces(spec.Finish, mpu, n => n.Y > 0.2f);
        if (spec.GableFinish.Length > 0) mesh = mesh.FinishFaces(spec.GableFinish, mpu, (i, _) => i >= gableFrom && i < gableTo || steepTop.Contains(i));
        return mesh;
    }

    /// <summary>The commonest colour along the top of the walls, for the gable ends.</summary>
    static uint WallColour(VoxModel m, bool[,] foot, int eaves)
    {
        var count = new int[256];
        for (int y = 0; y < m.SizeY; y++)
            for (int x = 0; x < m.SizeX; x++)
                if (foot[x, y] && m[x, y, eaves - 1] is var c and > 0) count[c]++;
        int best = AssetPalette.Brick;
        for (int c = 1; c < 256; c++) if (count[c] > count[best]) best = c;
        return m.Palette[best];
    }

    /// <summary>
    /// The straight runs of a mask's outline, each with the side it faces: (x0, y0)–(x1, y1) along cell corners, and
    /// the outward direction.
    /// </summary>
    static void Edges(bool[,] mask, int W, int H, Action<int, int, int, int, int, int> run)
    {
        bool At(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && mask[x, y];
        // Horizontal edges (between rows y-1 and y), facing north or south.
        for (int y = 0; y <= H; y++)
            foreach (int ny in new[] { -1, 1 })
                for (int x = 0; x < W;)
                {
                    bool Edge(int xx) => ny < 0 ? At(xx, y) && !At(xx, y - 1) : At(xx, y - 1) && !At(xx, y);
                    if (!Edge(x)) { x++; continue; }
                    int s = x;
                    while (x < W && Edge(x)) x++;
                    run(s, y, x, y, 0, ny);
                }
        // Vertical edges (between columns x-1 and x), facing west or east.
        for (int x = 0; x <= W; x++)
            foreach (int nx in new[] { -1, 1 })
                for (int y = 0; y < H;)
                {
                    bool Edge(int yy) => nx < 0 ? At(x, yy) && !At(x - 1, yy) : At(x - 1, yy) && !At(x, yy);
                    if (!Edge(y)) { y++; continue; }
                    int s = y;
                    while (y < H && Edge(y)) y++;
                    run(x, s, x, y, nx, 0);
                }
    }
}
