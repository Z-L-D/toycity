namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// Drawing in metres on a voxel model, for the realistic asset set: boxes, cylinders, cones and blobs, walls with
/// windows, flat roofs, the library's pieces and props. x runs east, y south (the front of a lot), z up; every
/// size is in metres and lands on the nearest voxel at the model's detail, so one recipe draws at any detail.
/// </summary>
public sealed class Draw
{
    public readonly VoxModel M;
    public readonly int K;
    public readonly Random R;
    /// <summary>Metres a voxel.</summary>
    public double V => 0.5 / K;
    /// <summary>The model's size in metres.</summary>
    public double W => M.SizeX * V;
    public double D => M.SizeY * V;
    public double Top => M.SizeZ * V;

    public Draw(VoxModel m, int detail, int seed) { M = m; K = detail; R = new Random(seed); }

    public int I(double metres) => (int)Math.Round(metres / V);

    /// <summary>A box from one corner to the other, in metres (its far faces exclusive), at least a voxel thick.</summary>
    public void Box(double x0, double y0, double z0, double x1, double y1, double z1, byte c)
    {
        int a = I(Math.Min(x0, x1)), b = I(Math.Min(y0, y1)), e = I(Math.Min(z0, z1));
        int a1 = Math.Max(a + 1, I(Math.Max(x0, x1))), b1 = Math.Max(b + 1, I(Math.Max(y0, y1))), e1 = Math.Max(e + 1, I(Math.Max(z0, z1)));
        M.Fill(a, b, e, a1 - 1, b1 - 1, e1 - 1, c);
    }

    /// <summary>
    /// The smooth shapes drawn so far (cylinders, cones, cooling towers, blobs): the city draws them as polygons in
    /// place of their voxels, which stay in the model for when the building is wrecked.
    /// </summary>
    public readonly List<Shape> Shapes = new();

    /// <summary>Thinner than this a round thing is left as voxels: a post or a pipe isn't worth its polygons.</summary>
    public const double ThinnestShape = 0.15;

    /// <summary>An upright cylinder, or a cone when the radius changes from bottom to top; hollow leaves a wall a voxel thick.</summary>
    public void Cyl(double cx, double cy, double r0, double z0, double z1, byte c, double? r1 = null, bool hollow = false)
    {
        double top = r1 ?? r0;
        if (Math.Max(r0, top) < ThinnestShape)
        {
            int za = I(z0), zb = Math.Max(za + 1, I(z1));
            for (int z = za; z < zb; z++)
            {
                double f = zb - za <= 1 ? 0 : (z - za) / (double)(zb - za - 1);
                Disc(cx / V, cy / V, (r0 + (top - r0) * f) / V, z, c, hollow);
            }
            return;
        }
        Add(Shape.Cylinder(cx, cy, r0, z0, Math.Max(z1, z0 + V), c, top, hollow ? V : 0));
    }

    /// <summary>Adds a shape and fills its voxels; one too thin to fill any still gets the voxel at its middle.</summary>
    public Shape Add(Shape s)
    {
        if (s.Voxelize(M, K) == 0)
        {
            var (lo, hi) = s.Bounds();
            int x = (int)Math.Floor((lo.X + hi.X) / 2 / V), y = (int)Math.Floor((lo.Y + hi.Y) / 2 / V);
            for (int z = I(s.Z); z < Math.Max(I(s.Z) + 1, I(hi.Z)); z++) if (M.In(x, y, z)) M[x, y, z] = s.Colour;
        }
        Shapes.Add(s);
        return s;
    }

    /// <summary>A cooling tower's waisted shape: wide at the foot, narrowest two thirds up, flaring at the lip.</summary>
    public void Waisted(double cx, double cy, double rFoot, double rWaist, double rLip, double z0, double z1, byte c)
    {
        const double w = 0.68;
        double Radius(double f) => f < w ? rFoot + (rWaist - rFoot) * Math.Sin(f / w * Math.PI / 2) : rWaist + (rLip - rWaist) * ((f - w) / (1 - w)) * ((f - w) / (1 - w));
        double wall = Math.Max(1, K / 2) * V, h = z1 - z0;
        var s = new Shape { Kind = ShapeKind.Lathe, X = cx, Y = cy, Z = z0, Colour = c };
        const int steps = 16;
        for (int i = 0; i <= steps; i++) s.Profile.Add((Radius(i / (double)steps), h * i / steps));
        for (int i = steps; i >= 0; i--) s.Profile.Add((Radius(i / (double)steps) - wall, h * i / steps));
        Add(s);
    }

    void Disc(double cx, double cy, double r, int z, byte c, bool hollow, int wall = 1)
    {
        for (int y = (int)Math.Floor(cy - r - 1); y <= (int)Math.Ceiling(cy + r + 1); y++)
            for (int x = (int)Math.Floor(cx - r - 1); x <= (int)Math.Ceiling(cx + r + 1); x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= r && (!hollow || d > r - wall) && M.In(x, y, z)) M[x, y, z] = c;
            }
    }

    /// <summary>An ellipsoid: a tree's crown, a mound, a duck.</summary>
    public void Blob(double cx, double cy, double cz, double rx, double ry, double rz, byte c, double lumpy = 0, byte? dark = null)
    {
        for (int z = I(cz - rz) - 1; z <= I(cz + rz) + 1; z++)
            for (int y = I(cy - ry) - 1; y <= I(cy + ry) + 1; y++)
                for (int x = I(cx - rx) - 1; x <= I(cx + rx) + 1; x++)
                {
                    if (!M.In(x, y, z)) continue;
                    double dx = ((x + 0.5) * V - cx) / rx, dy = ((y + 0.5) * V - cy) / ry, dz = ((z + 0.5) * V - cz) / rz;
                    double lump = lumpy * (((x * 73856093) ^ (y * 19349663) ^ (z * 83492791)) & 7) / 7.0;
                    if (dx * dx + dy * dy + dz * dz <= 1 - lump)
                        M[x, y, z] = dark is { } dk && (dz < -0.2 || ((x + y + z) & 3) == 0) ? dk : c;
                }
        if (Math.Min(rx, Math.Min(ry, rz)) >= ThinnestShape)
        {
            var s = Shape.Ellipsoid(cx, cy, cz, rx, ry, rz, c, dark ?? 0);
            s.Lumpy = lumpy;
            Shapes.Add(s);
        }
    }

    /// <summary>A copy of the model without its shapes' voxels: what the city meshes as voxels beside the shapes' polygons.</summary>
    public VoxModel Boxy() => Without(M, K, Shapes);

    /// <summary>A copy of a model at a detail without the voxels of these shapes (inside one and of its colours).</summary>
    public static VoxModel Without(VoxModel m, int detail, IReadOnlyList<Shape> shapes)
    {
        var o = new VoxModel(m.SizeX, m.SizeY, m.SizeZ);
        Array.Copy(m.Voxels, o.Voxels, m.Voxels.Length);
        Array.Copy(m.Palette, o.Palette, 256);
        Array.Copy(m.Smooth, o.Smooth, 256);
        Array.Copy(m.Ghost, o.Ghost, 256);
        if (shapes.Count == 0) return o;
        // The shapes' voxels become a ghost colour, one the model doesn't use: solid, so the faces round them (a
        // dome's hollow, a tank's inside) aren't drawn, but with none of their own.
        var used = new bool[256];
        foreach (byte c in m.Voxels) used[c] = true;
        int g = 255;
        while (g > 1 && (used[g] || o.Ghost[g])) g--;
        if (o.Ghost.Any(b => b)) g = Array.IndexOf(o.Ghost, true);
        o.Ghost[g] = true;
        byte ghost = (byte)g;
        foreach (var s in shapes)
            // Grown by most of a voxel: one whose middle is just outside the polygons (between a curve's facets) would
            // still poke through them.
            s.ForEachVoxel(o, detail, (x, y, z) => { byte c = o[x, y, z]; if (c != 0 && (c == s.Colour || c == s.Under)) o[x, y, z] = ghost; }, 0.9 * 0.5 / detail);
        return o;
    }

    /// <summary>Replaces one colour with another inside a box (the whole model if no box): repaints a library piece.</summary>
    public void Recolour(byte from, byte to, double x0 = 0, double y0 = 0, double z0 = 0, double x1 = 1e9, double y1 = 1e9, double z1 = 1e9)
    {
        for (int z = Math.Max(0, I(z0)); z < Math.Min(M.SizeZ, I(z1)); z++)
            for (int y = Math.Max(0, I(y0)); y < Math.Min(M.SizeY, I(y1)); y++)
                for (int x = Math.Max(0, I(x0)); x < Math.Min(M.SizeX, I(x1)); x++)
                    if (M[x, y, z] == from) M[x, y, z] = to;
    }

    /// <summary>Stamps a model with its turned footprint's north-west corner at (x, y, z) metres, only its solid voxels.</summary>
    public void Stamp(VoxModel m, double x, double y, double z, int rot, Func<byte, byte>? map = null)
    {
        int ox = I(x), oy = I(y), oz = I(z);
        for (int c = 0; c < m.SizeZ; c++)
            for (int b = 0; b < m.SizeY; b++)
                for (int a = 0; a < m.SizeX; a++)
                {
                    byte col = m[a, b, c];
                    if (col == 0) continue;
                    var (tx, ty) = AssetDoc.Turn(a, b, m.SizeX, m.SizeY, rot);
                    if (M.In(ox + tx, oy + ty, oz + c)) M[ox + tx, oy + ty, oz + c] = map?.Invoke(col) ?? col;
                }
    }

    /// <summary>A library snap piece in the 4 m frame whose north-west corner is at (x, y) metres, at storey height z.</summary>
    public void Piece(string id, Material mat, double x, double y, double z, int rot, Func<byte, byte>? map = null) =>
        Stamp(Built(id, mat), x, y, z, rot, map);

    readonly Dictionary<(string, Material), VoxModel> _built = new();
    VoxModel Built(string id, Material mat)
    {
        if (!_built.TryGetValue((id, mat), out var m)) _built[(id, mat)] = m = Pieces.Find(id)!.Build(mat, K);
        return m;
    }

    /// <summary>A library prop centred on (x, y) metres, standing at z.</summary>
    public void Prop(string id, double x, double y, int rot = 0, double z = 0, Func<byte, byte>? map = null)
    {
        var m = Built(id, Material.Wood);
        var (tx, ty) = AssetDoc.Turned(m, rot);
        Stamp(m, x - tx * V / 2, y - ty * V / 2, z, rot, map);
    }

    /// <summary>The ground of the lot: a voxel-thin cover over all of it.</summary>
    public void Ground(byte c) => M.Fill(0, 0, 0, M.SizeX - 1, M.SizeY - 1, 0, c);

    public enum Glazing { Punched, Ribbon, Curtain, Shop }

    /// <summary>
    /// A block of storeys: walls round a rectangle (west, north, east, south), a slab band at each floor, and windows
    /// cut into every face in a pattern. A door goes in the middle of the south face (the front). Returns the top.
    /// </summary>
    public double Block(double x0, double y0, double x1, double y1, double z0, int storeys, double storeyH, byte wall, byte band,
        Glazing glazing, double bay = 3, bool door = true, Glazing ground = Glazing.Punched, byte glass = AssetPalette.Glass,
        byte frame = AssetPalette.Frame, double thick = 0.3)
    {
        double t = Math.Max(V, thick), top = z0 + storeys * storeyH;
        Box(x0, y0, z0, x1, y1, top, wall);
        Box(x0 + t, y0 + t, z0, x1 - t, y1 - t, top, 0);
        for (int s = 0; s <= storeys; s++)
        {
            double z = z0 + s * storeyH;
            // A floor inside, and a band round the outside at each floor line.
            if (s < storeys) Box(x0 + t, y0 + t, z, x1 - t, y1 - t, z + Math.Max(V, 0.25), AssetPalette.ConcreteDark);
            if (s > 0) Box(x0, y0, z - Math.Max(V, 0.3), x1, y1, z, band);
        }
        for (int s = 0; s < storeys; s++)
        {
            var g = s == 0 ? ground : glazing;
            double z = z0 + s * storeyH;
            Windows(x0, y0, x1, y0, z, storeyH, g, bay, t, glass, frame, 0, -1);
            Windows(x0, y1, x1, y1, z, storeyH, g, bay, t, glass, frame, 0, 1);
            Windows(x0, y0, x0, y1, z, storeyH, g, bay, t, glass, frame, -1, 0);
            Windows(x1, y0, x1, y1, z, storeyH, g, bay, t, glass, frame, 1, 0);
        }
        if (door)
        {
            double cx = (x0 + x1) / 2, dw = Math.Min(2.0, (x1 - x0) / 3);
            Box(cx - dw / 2, y1 - t, z0, cx + dw / 2, y1, z0 + Math.Min(2.4, storeyH - 0.4), 0);
            Box(cx - dw / 2, y1 - t / 2, z0, cx + dw / 2, y1 - t / 2 + V, z0 + Math.Min(2.4, storeyH - 0.4), AssetPalette.WoodDark);
            Box(cx - dw / 2 - 0.2, y1 - t, z0 + Math.Min(2.4, storeyH - 0.4), cx + dw / 2 + 0.2, y1 + 0.1, z0 + Math.Min(2.6, storeyH - 0.2), frame);
        }
        return top;
    }

    /// <summary>One storey of windows along a face from (ax, ay) to (bx, by), whose outside is the (nx, ny) side.</summary>
    void Windows(double ax, double ay, double bx, double by, double z, double h, Glazing g, double bay, double t, byte glass, byte frame, int nx, int ny)
    {
        double len = Math.Abs(bx - ax) + Math.Abs(by - ay);
        int bays = Math.Max(1, (int)Math.Round(len / bay));
        double step = len / bays;
        double sill = g switch { Glazing.Curtain => 0.25, Glazing.Shop => 0.35, Glazing.Ribbon => 0.9, _ => 0.9 };
        double head = g switch { Glazing.Curtain => h - 0.35, Glazing.Shop => h - 0.6, Glazing.Ribbon => h - 0.6, _ => Math.Min(h - 0.5, 2.3) };
        double wfrac = g switch { Glazing.Curtain => 0.94, Glazing.Ribbon => 0.9, Glazing.Shop => 0.86, _ => 0.45 };
        bool alongX = ay == by;
        // The face's outside plane and the glass's plane set back in the wall.
        double o0 = alongX ? (ny < 0 ? ay : ay - t) : (nx < 0 ? ax : ax - t), o1 = o0 + t;
        double gl = alongX ? (ny < 0 ? ay + t * 0.6 : ay - t * 0.6) : (nx < 0 ? ax + t * 0.6 : ax - t * 0.6);
        for (int i = 0; i < bays; i++)
        {
            double c = (alongX ? Math.Min(ax, bx) : Math.Min(ay, by)) + step * (i + 0.5);
            // Leave the corners solid.
            double half = Math.Min(step * wfrac / 2, step / 2 - (i == 0 || i == bays - 1 ? t : 0));
            if (half < V / 2) continue;
            double s0 = c - half, s1 = c + half;
            if (alongX)
            {
                Box(s0, o0, z + sill, s1, o1, z + head, 0);
                Box(s0, gl, z + sill, s1, gl + V, z + head, glass);
                Box(s0, o0, z + sill - Math.Max(V, 0.1), s1, o1, z + sill, frame);
                if (g != Glazing.Curtain && s1 - s0 > 1.6) Box(c - V / 2, gl, z + sill, c + V / 2, gl + V, z + head, frame);
            }
            else
            {
                Box(o0, s0, z + sill, o1, s1, z + head, 0);
                Box(gl, s0, z + sill, gl + V, s1, z + head, glass);
                Box(o0, s0, z + sill - Math.Max(V, 0.1), o1, s1, z + sill, frame);
                if (g != Glazing.Curtain && s1 - s0 > 1.6) Box(gl, c - V / 2, z + sill, gl + V, c + V / 2, z + head, frame);
            }
        }
    }

    /// <summary>A flat roof: a slab, a parapet round the edge, and plant on top (boxes of air handling, a tank).</summary>
    public void FlatRoof(double x0, double y0, double x1, double y1, double z, byte parapet, bool plant = true, double rim = 0.9)
    {
        Box(x0, y0, z, x1, y1, z + Math.Max(V, 0.2), AssetPalette.Asphalt);
        double t = Math.Max(V, 0.3);
        Box(x0, y0, z, x1, y0 + t, z + rim, parapet);
        Box(x0, y1 - t, z, x1, y1, z + rim, parapet);
        Box(x0, y0, z, x0 + t, y1, z + rim, parapet);
        Box(x1 - t, y0, z, x1, y1, z + rim, parapet);
        if (!plant || x1 - x0 < 6 || y1 - y0 < 6) return;
        int n = 1 + R.Next(3);
        for (int i = 0; i < n; i++)
        {
            double w = 1.5 + R.NextDouble() * 2, d = 1.2 + R.NextDouble() * 1.5;
            double px = x0 + 1.5 + R.NextDouble() * (x1 - x0 - 3 - w), py = y0 + 1.5 + R.NextDouble() * (y1 - y0 - 3 - d);
            Box(px, py, z, px + w, py + d, z + 1.2 + R.NextDouble(), AssetPalette.Metal);
            Box(px + 0.2, py + 0.2, z + 1.2, px + w - 0.2, py + d - 0.2, z + 1.4, AssetPalette.MetalDark);
        }
    }

    /// <summary>Painted bays on a car park, and parked cars in some of them.</summary>
    public void CarPark(double x0, double y0, double x1, double y1, double fill = 0.6)
    {
        Box(x0, y0, 0, x1, y1, V, AssetPalette.Asphalt);
        double bay = 2.6;
        for (double x = x0 + 0.5; x + bay < x1; x += bay)
        {
            Box(x, y0 + 0.5, V * 0.5, x + Math.Max(V, 0.12), y0 + 5.5, V, AssetPalette.Trim);
            if (R.NextDouble() < fill && y0 + 6 < y1)
                Prop("car", x + bay / 2, y0 + 3, 1, V, c => c == AssetPalette.Red ? CarColour() : c);
        }
    }

    public byte CarColour() => new[] { AssetPalette.Red, AssetPalette.Blue, AssetPalette.Trim, AssetPalette.Black, AssetPalette.Metal, AssetPalette.Yellow, AssetPalette.Chrome }[R.Next(7)];

    /// <summary>A tree of a random size near (x, y): broadleaf or a conifer.</summary>
    public void Tree(double x, double y, bool conifer = false, double scale = 1)
    {
        double h = (conifer ? 9 : 7) * scale * (0.8 + R.NextDouble() * 0.4);
        if (conifer)
        {
            Cyl(x, y, 0.25 * scale, 0, h * 0.25, AssetPalette.Bark);
            Cyl(x, y, 2.2 * scale, h * 0.2, h, AssetPalette.LeafDark, 0.15);
        }
        else
        {
            Cyl(x, y, 0.22 * scale, 0, h * 0.6, AssetPalette.Bark);
            double r = 2.4 * scale * (0.85 + R.NextDouble() * 0.3);
            Blob(x, y, h * 0.62, r, r, r * 0.85, AssetPalette.Leaf, 0.12, AssetPalette.LeafDark);
        }
    }

    /// <summary>A ramp colour: hue 0..13 round the wheel from red, lightness 0..15 from dark to pale.</summary>
    public static byte Ramp(int hue, int light) => (byte)(32 + Math.Clamp(hue, 0, 13) * 16 + Math.Clamp(light, 0, 15));
}
