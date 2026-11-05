namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// One entry of the asset library (RC part 1). A snap piece fills a frame a whole number of grid modules wide and
/// storeys tall, and is placed on the build grid like Fortnite's walls, floors, stairs and roofs; a prop is a
/// ready-made object (a bench, a lamp, a tree, a car) placed anywhere. <see cref="Build"/> makes it in a material at
/// a detail: <c>k</c> voxels for every half metre, 1 for buildings' own scale, 4 for a fine model.
/// </summary>
public sealed record Piece(string Id, string Name, string Category, bool Snap, bool HasMaterial, Func<Material, int, VoxModel> Build);

/// <summary>The built-in library: snap pieces in six materials, and props.</summary>
public static class Pieces
{
    /// <summary>A grid module: 4 m across (8 half-metre voxels at detail 1).</summary>
    public const int Module = 8;
    /// <summary>A storey: 3 m (6 half-metre voxels at detail 1).</summary>
    public const int Storey = 6;

    public static readonly IReadOnlyList<Piece> All = new List<Piece>
    {
        new("wall", "Wall", "Walls", true, true, (m, k) => Wall(m, k, Opening.None)),
        new("wall-window", "Wall with a window", "Walls", true, true, (m, k) => Wall(m, k, Opening.Window)),
        new("wall-wide-window", "Wall with a wide window", "Walls", true, true, (m, k) => Wall(m, k, Opening.WideWindow)),
        new("wall-door", "Wall with a door", "Walls", true, true, (m, k) => Wall(m, k, Opening.Door)),
        new("wall-arch", "Wall with an arch", "Walls", true, true, (m, k) => Wall(m, k, Opening.Arch)),
        new("wall-half", "Half wall", "Walls", true, true, (m, k) => HalfWall(m, k)),
        new("floor", "Floor", "Floors", true, true, (m, k) => Floor(m, k)),
        new("floor-hatch", "Floor with a stairwell", "Floors", true, true, (m, k) => Floor(m, k, hatch: true)),
        new("stairs", "Stairs", "Stairs", true, true, (m, k) => Stairs(m, k)),
        new("roof-slope", "Sloping roof", "Roofs", true, false, (m, k) => RoofSlope(k)),
        new("gable-right", "Gable end, rising right", "Roofs", true, true, (m, k) => Gable(m, k, risingEast: true)),
        new("gable-left", "Gable end, rising left", "Roofs", true, true, (m, k) => Gable(m, k, risingEast: false)),
        new("roof-hip", "Hipped roof", "Roofs", true, false, (m, k) => RoofHip(k)),
        new("roof-flat", "Flat roof with a parapet", "Roofs", true, true, (m, k) => RoofFlat(m, k)),
        new("pillar", "Pillar", "Structure", true, true, (m, k) => Pillar(m, k)),
        new("fence", "Fence", "Structure", true, true, (m, k) => Fence(m, k)),
        new("bench", "Bench", "Props", false, false, (m, k) => Bench(k)),
        new("lamp", "Street lamp", "Props", false, false, (m, k) => Lamp(k)),
        new("tree", "Tree", "Props", false, false, (m, k) => Tree(k)),
        new("conifer", "Conifer", "Props", false, false, (m, k) => Conifer(k)),
        new("car", "Car", "Props", false, false, (m, k) => Car(k)),
        new("planter", "Planter", "Props", false, false, (m, k) => Planter(k)),
        new("bin", "Bin", "Props", false, false, (m, k) => Bin(k)),
        new("table", "Café table", "Props", false, false, (m, k) => Table(k)),
        new("crate", "Crate", "Props", false, false, (m, k) => Crate(k)),
        new("hedge", "Hedge", "Props", false, false, (m, k) => Hedge(k)),
    };

    public static Piece? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static IEnumerable<string> Categories => All.Select(p => p.Category).Distinct();

    static VoxModel New(int sx, int sy, int sz)
    {
        var m = new VoxModel(Math.Clamp(sx, 1, 256), Math.Clamp(sy, 1, 256), Math.Clamp(sz, 1, 256));
        AssetPalette.Apply(m);
        return m;
    }

    enum Opening { None, Window, WideWindow, Door, Arch }

    /// <summary>A wall along the north edge of its module, a storey tall, half a metre thick.</summary>
    static VoxModel Wall(Material mat, int k, Opening opening)
    {
        int M = Module * k, S = Storey * k, T = k;
        var (main, trim) = AssetPalette.Of(mat);
        var w = New(M, M, S);
        w.Fill(0, 0, 0, M - 1, T - 1, S - 1, main);
        // A plinth and a band under the eaves in the trim.
        w.Fill(0, 0, 0, M - 1, T - 1, Math.Max(0, k / 2 - 1), trim);
        // Brick isn't one flat colour: about one brick in five on the face is a darker one.
        if (mat == Material.Brick)
            for (int z = 1; z < S; z++)
                for (int x = 0; x < M; x++)
                    if ((x / Math.Max(1, k / 2) * 73856093 ^ z * 19349663) % 5 == 0) w[x, 0, z] = AssetPalette.RoofTile;
        switch (opening)
        {
            case Opening.Window: Window(w, 2 * k, 6 * k - 1, 2 * k, 5 * k - 1, T, mat); break;
            case Opening.WideWindow: Window(w, k, 7 * k - 1, 2 * k, 5 * k - 1, T, mat); break;
            case Opening.Door:
                w.Fill(3 * k, 0, 0, 5 * k - 1, T - 1, 4 * k - 1, 0);
                Frame(w, 3 * k, 5 * k - 1, 0, 4 * k - 1, T, AssetPalette.Of(mat).Trim, bottom: false);
                break;
            case Opening.Arch:
                for (int z = 0; z < S - k; z++)
                    for (int x = k; x < M - k; x++)
                    {
                        double cx = (M - 1) / 2.0, r = (M - 2 * k) / 2.0, top = 3 * k;
                        bool open = z < top || (x - cx) * (x - cx) + (z - top) * (z - top) <= r * r;
                        if (open) w.Fill(x, 0, z, x, T - 1, z, 0);
                    }
                break;
        }
        return w;
    }

    static void Window(VoxModel w, int x0, int x1, int z0, int z1, int t, Material mat)
    {
        w.Fill(x0, 0, z0, x1, t - 1, z1, 0);
        // The glass sits in the middle of the wall's thickness, in a dark frame with a sill.
        int mid = (t - 1) / 2;
        w.Fill(x0, mid, z0, x1, mid, z1, mat == Material.Glass ? AssetPalette.Glass : AssetPalette.Glass);
        Frame(w, x0, x1, z0, z1, t, AssetPalette.Frame, bottom: true);
        if (x1 - x0 > 6) { int cx = (x0 + x1) / 2; w.Fill(cx, mid, z0, cx, mid, z1, AssetPalette.Frame); }
    }

    static void Frame(VoxModel w, int x0, int x1, int z0, int z1, int t, byte c, bool bottom)
    {
        int mid = (t - 1) / 2;
        w.Fill(x0, mid, z1, x1, mid, z1, c);
        w.Fill(x0, mid, z0, x0, mid, z1, c);
        w.Fill(x1, mid, z0, x1, mid, z1, c);
        if (bottom) w.Fill(x0, 0, z0, x1, t - 1, z0, c);
    }

    static VoxModel HalfWall(Material mat, int k)
    {
        int M = Module * k, T = k;
        var (main, trim) = AssetPalette.Of(mat);
        var w = New(M, M, Storey * k);
        w.Fill(0, 0, 0, M - 1, T - 1, 2 * k - 1, main);
        w.Fill(0, 0, 2 * k - 1, M - 1, T - 1, 2 * k - 1, trim);
        return w;
    }

    static VoxModel Floor(Material mat, int k, bool hatch = false)
    {
        int M = Module * k;
        var (main, trim) = AssetPalette.Of(mat == Material.Glass ? Material.Concrete : mat);
        var f = New(M, M, Storey * k);
        f.Fill(0, 0, 0, M - 1, M - 1, Math.Max(0, k / 2), main);
        if (k >= 2) f.Fill(0, 0, 0, M - 1, M - 1, 0, trim);
        if (hatch) f.Fill(2 * k, k, 0, 6 * k - 1, M - 1, Math.Max(0, k / 2), 0);
        return f;
    }

    /// <summary>A flight from the north edge (bottom) to the south (top), a storey's rise over a module's run.</summary>
    static VoxModel Stairs(Material mat, int k)
    {
        int M = Module * k, S = Storey * k;
        var (main, trim) = AssetPalette.Of(mat == Material.Glass ? Material.Concrete : mat);
        var s = New(M, M, S);
        int steps = 2 * Storey;
        for (int i = 0; i < steps; i++)
        {
            int y0 = i * M / steps, y1 = (i + 1) * M / steps - 1, top = (i + 1) * S / steps - 1;
            s.Fill(2 * k, y0, 0, 6 * k - 1, y1, top, main);
            s.Fill(2 * k, y0, top, 6 * k - 1, y0, top, trim);
        }
        return s;
    }

    /// <summary>A roof rising from the north edge to a ridge on the south edge: put two back to back for a gable.</summary>
    static VoxModel RoofSlope(int k)
    {
        int M = Module * k, S = Storey * k;
        var r = New(M, M, S);
        for (int y = 0; y < M; y++)
        {
            int top = (y + 1) * (S / 2) / M + k / 2;
            for (int z = Math.Max(0, top - k); z <= Math.Min(S - 1, top); z++) r.Fill(0, y, z, M - 1, y, z, AssetPalette.RoofTile);
            // A darker course along the ridge.
            if (y >= M - Math.Max(1, k / 2)) r.Fill(0, y, top, M - 1, y, top, AssetPalette.Slate);
        }
        return r;
    }

    /// <summary>
    /// The wall under a sloping roof's end, on the north edge of its frame: it rises along the edge as the slope does
    /// across its frame, so turned onto the slope's side it closes the gable. Seen from outside, one rises to the
    /// right and the other to the left; a gable is a pair of them.
    /// </summary>
    static VoxModel Gable(Material mat, int k, bool risingEast)
    {
        int M = Module * k, S = Storey * k, T = k;
        var (main, trim) = AssetPalette.Of(mat);
        var g = New(M, M, S);
        for (int i = 0; i < M; i++)
        {
            // Under the roof's underside: the slope's top less its thickness.
            int top = (i + 1) * (S / 2) / M + k / 2 - k;
            int x = risingEast ? i : M - 1 - i;
            if (top >= 0) g.Fill(x, 0, 0, x, T - 1, top, main);
            if (top >= 0 && k >= 2) g.Fill(x, 0, top, x, T - 1, top, trim);
        }
        return g;
    }

    /// <summary>A pyramid over the module.</summary>
    static VoxModel RoofHip(int k)
    {
        int M = Module * k, S = Storey * k;
        var r = New(M, M, S);
        for (int y = 0; y < M; y++)
            for (int x = 0; x < M; x++)
            {
                int edge = Math.Min(Math.Min(x, M - 1 - x), Math.Min(y, M - 1 - y));
                int top = (edge + 1) * (S / 2) * 2 / M;
                for (int z = Math.Max(0, top - k); z <= Math.Min(S - 1, top); z++) r[x, y, z] = AssetPalette.Slate;
            }
        return r;
    }

    static VoxModel RoofFlat(Material mat, int k)
    {
        int M = Module * k;
        var (main, trim) = AssetPalette.Of(mat == Material.Glass ? Material.Concrete : mat);
        var r = New(M, M, Storey * k);
        r.Fill(0, 0, 0, M - 1, M - 1, Math.Max(0, k / 2), AssetPalette.Asphalt);
        r.Fill(0, 0, 0, M - 1, k - 1, 2 * k - 1, main);
        r.Fill(0, 0, 2 * k - 1, M - 1, k - 1, 2 * k - 1, trim);
        return r;
    }

    static VoxModel Pillar(Material mat, int k)
    {
        var (main, trim) = AssetPalette.Of(mat == Material.Glass ? Material.Metal : mat);
        var p = New(Module * k, Module * k, Storey * k);
        int S = Storey * k;
        p.Fill(0, 0, 0, 2 * k - 1, 2 * k - 1, S - 1, main);
        p.Fill(0, 0, 0, 2 * k - 1, 2 * k - 1, Math.Max(0, k / 2), trim);
        p.Fill(0, 0, S - Math.Max(1, k / 2), 2 * k - 1, 2 * k - 1, S - 1, trim);
        return p;
    }

    static VoxModel Fence(Material mat, int k)
    {
        int M = Module * k;
        var (main, trim) = AssetPalette.Of(mat == Material.Glass ? Material.Metal : mat);
        var f = New(M, M, Storey * k);
        for (int x = 0; x < M; x += 4 * k) f.Fill(x, 0, 0, x + Math.Max(1, k / 2) - 1, Math.Max(1, k / 2) - 1, 2 * k, trim);
        f.Fill(0, 0, k, M - 1, Math.Max(1, k / 2) - 1, k + Math.Max(0, k / 4), main);
        f.Fill(0, 0, 2 * k - 1, M - 1, Math.Max(1, k / 2) - 1, 2 * k - 1 + Math.Max(0, k / 4), main);
        return f;
    }

    // Props, built to size in half metres: at detail k each is k times as many voxels a side.
    static int H(int k, double halfMetres) => Math.Max(1, (int)Math.Round(halfMetres * k));

    static VoxModel Bench(int k)
    {
        var b = New(H(k, 4), H(k, 2), H(k, 2));
        b.Fill(0, 0, H(k, 0.8), b.SizeX - 1, b.SizeY - 1, H(k, 1) - 1, AssetPalette.Wood);
        b.Fill(0, b.SizeY - Math.Max(1, k / 2), H(k, 1), b.SizeX - 1, b.SizeY - 1, b.SizeZ - 1, AssetPalette.Wood);
        foreach (int x in new[] { 0, b.SizeX - Math.Max(1, k / 2) })
            b.Fill(x, 0, 0, x + Math.Max(1, k / 2) - 1, b.SizeY - 1, H(k, 0.8) - 1, AssetPalette.MetalDark);
        return b;
    }

    static VoxModel Lamp(int k)
    {
        var l = New(H(k, 3), H(k, 1), H(k, 9));
        int pole = Math.Max(1, k / 2);
        l.Fill(0, 0, 0, pole - 1, pole - 1, l.SizeZ - 1, AssetPalette.MetalDark);
        l.Fill(0, 0, l.SizeZ - pole, l.SizeX - 1, pole - 1, l.SizeZ - 1, AssetPalette.MetalDark);
        l.Fill(l.SizeX - H(k, 1), 0, l.SizeZ - pole - Math.Max(1, k / 2), l.SizeX - 1, pole - 1, l.SizeZ - pole - 1, AssetPalette.Lamp);
        return l;
    }

    static VoxModel Tree(int k)
    {
        var t = New(H(k, 8), H(k, 8), H(k, 14));
        int c = t.SizeX / 2, trunk = Math.Max(1, k / 2 + k / 4);
        t.Fill(c - trunk / 2, c - trunk / 2, 0, c - trunk / 2 + trunk - 1, c - trunk / 2 + trunk - 1, H(k, 7), AssetPalette.Bark);
        double cz = H(k, 9.5), r = H(k, 4) - 0.5;
        for (int z = 0; z < t.SizeZ; z++)
            for (int y = 0; y < t.SizeY; y++)
                for (int x = 0; x < t.SizeX; x++)
                {
                    double dx = x + 0.5 - t.SizeX / 2.0, dy = y + 0.5 - t.SizeY / 2.0, dz = (z + 0.5 - cz) * 1.15;
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    // A lumpy crown: a little noise from the voxel's place so it isn't a ball.
                    double lump = ((x * 73856093 ^ y * 19349663 ^ z * 83492791) & 7) / 7.0 * 0.12 * r;
                    if (d < r - lump) t[x, y, z] = dz > 0.3 * r || ((x + y + z) & 3) == 0 ? AssetPalette.Leaf : AssetPalette.LeafDark;
                }
        return t;
    }

    static VoxModel Conifer(int k)
    {
        var t = New(H(k, 6), H(k, 6), H(k, 16));
        int c = t.SizeX / 2;
        t.Fill(c - Math.Max(1, k / 2), c - Math.Max(1, k / 2), 0, c, c, H(k, 3), AssetPalette.Bark);
        for (int z = H(k, 2); z < t.SizeZ; z++)
        {
            double r = (t.SizeZ - z) / (double)(t.SizeZ - H(k, 2)) * (t.SizeX / 2.0);
            for (int y = 0; y < t.SizeY; y++)
                for (int x = 0; x < t.SizeX; x++)
                {
                    double dx = x + 0.5 - t.SizeX / 2.0, dy = y + 0.5 - t.SizeY / 2.0;
                    if (dx * dx + dy * dy <= r * r) t[x, y, z] = (z / Math.Max(1, k)) % 3 == 0 ? AssetPalette.LeafDark : AssetPalette.Leaf;
                }
        }
        return t;
    }

    static VoxModel Car(int k)
    {
        var c = New(H(k, 9), H(k, 4), H(k, 3));
        int lx = c.SizeX, ly = c.SizeY;
        c.Fill(0, 0, H(k, 0.5), lx - 1, ly - 1, H(k, 1.6) - 1, AssetPalette.Red);
        c.Fill(H(k, 2), 0, H(k, 1.6), lx - H(k, 2) - 1, ly - 1, c.SizeZ - 1, AssetPalette.Red);
        c.Fill(H(k, 2.4), 0, H(k, 1.7), lx - H(k, 2.4) - 1, ly - 1, c.SizeZ - 2, AssetPalette.Glass);
        c.Fill(H(k, 2.4), Math.Max(1, k / 2), H(k, 1.7), lx - H(k, 2.4) - 1, ly - 1 - Math.Max(1, k / 2), c.SizeZ - 2, AssetPalette.Black);
        foreach (int x in new[] { H(k, 1), lx - H(k, 2.5) })
            foreach (int y in new[] { 0, ly - Math.Max(1, k / 2) })
                c.Fill(x, y, 0, x + H(k, 1.5) - 1, y + Math.Max(1, k / 2) - 1, H(k, 1) - 1, AssetPalette.Tyre);
        c.Fill(lx - 1, 0, H(k, 1), lx - 1, ly - 1, H(k, 1.3), AssetPalette.Lamp);
        return c;
    }

    static VoxModel Planter(int k)
    {
        var p = New(H(k, 3), H(k, 3), H(k, 3));
        p.Fill(0, 0, 0, p.SizeX - 1, p.SizeY - 1, H(k, 1.5) - 1, AssetPalette.Concrete);
        p.Fill(Math.Max(1, k / 2), Math.Max(1, k / 2), H(k, 1.5) - 1, p.SizeX - 1 - Math.Max(1, k / 2), p.SizeY - 1 - Math.Max(1, k / 2), H(k, 1.5) - 1, AssetPalette.Soil);
        p.Fill(Math.Max(1, k / 2), Math.Max(1, k / 2), H(k, 1.5), p.SizeX - 1 - Math.Max(1, k / 2), p.SizeY - 1 - Math.Max(1, k / 2), p.SizeZ - 1, AssetPalette.Leaf);
        return p;
    }

    static VoxModel Bin(int k)
    {
        var b = New(H(k, 1.5), H(k, 1.5), H(k, 2));
        b.Fill(0, 0, 0, b.SizeX - 1, b.SizeY - 1, b.SizeZ - 1, AssetPalette.MetalDark);
        b.Fill(0, 0, b.SizeZ - 1, b.SizeX - 1, b.SizeY - 1, b.SizeZ - 1, AssetPalette.Black);
        return b;
    }

    static VoxModel Table(int k)
    {
        var t = New(H(k, 2), H(k, 2), H(k, 2));
        t.Fill(0, 0, t.SizeZ - Math.Max(1, k / 2), t.SizeX - 1, t.SizeY - 1, t.SizeZ - 1, AssetPalette.Trim);
        int c = t.SizeX / 2;
        t.Fill(c - Math.Max(1, k / 4), c - Math.Max(1, k / 4), 0, c, c, t.SizeZ - 1, AssetPalette.MetalDark);
        return t;
    }

    static VoxModel Crate(int k)
    {
        var c = New(H(k, 2), H(k, 2), H(k, 2));
        c.Fill(0, 0, 0, c.SizeX - 1, c.SizeY - 1, c.SizeZ - 1, AssetPalette.Wood);
        int e = Math.Max(1, k / 2) - 1;
        foreach (var (x, y) in new[] { (0, 0), (c.SizeX - 1 - e, 0), (0, c.SizeY - 1 - e), (c.SizeX - 1 - e, c.SizeY - 1 - e) })
            c.Fill(x, y, 0, x + e, y + e, c.SizeZ - 1, AssetPalette.WoodDark);
        return c;
    }

    static VoxModel Hedge(int k)
    {
        var h = New(H(k, 8), H(k, 1.5), H(k, 2.5));
        for (int z = 0; z < h.SizeZ; z++)
            for (int y = 0; y < h.SizeY; y++)
                for (int x = 0; x < h.SizeX; x++)
                    h[x, y, z] = ((x * 31 + y * 17 + z * 7) % 5 == 0) ? AssetPalette.LeafDark : AssetPalette.Leaf;
        return h;
    }
}
