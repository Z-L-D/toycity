using System.Numerics;
using System.Text.Json.Nodes;

namespace ToyCity.ClientModel.Voxels.Assets;

public enum ShapeKind { Lathe, Extrude, Sweep, Wall, Imported }

/// <summary>Which way an extruded outline is pulled: up from a plan, or back from a front or side elevation.</summary>
public enum ExtrudeAxis { Up, Back, Across }

/// <summary>
/// A smooth shape drawn from a 2D profile, in metres: a lathe spins the profile (radius, height) round an upright
/// axis, an extrude pulls an outline straight, and a sweep draws the profile along a path. The city draws it as true
/// polygons, flat where the profile is straight and smoothly lit where it curves; its voxels (<see cref="Voxelize"/>)
/// are kept for when the building is wrecked. Model axes as everywhere in the asset set: x east, y south, z up.
/// </summary>
public sealed class Shape
{
    public ShapeKind Kind;
    /// <summary>
    /// The profile in metres. A lathe's points are (radius, height); an extrude's are its outline (across, up) in
    /// its plane, or (east, south) for one pulled up; a sweep's are its cross-section (left, up) of the path.
    /// </summary>
    public List<(double A, double B)> Profile = new();
    /// <summary>
    /// Whether the profile is a closed loop. A lathe profile that starts and ends on the axis is closed by it; an
    /// open profile is a thin shell (a bowl, an awning), drawn on both sides.
    /// </summary>
    public bool Closed = true;
    /// <summary>Where its origin sits in the model, in metres.</summary>
    public double X, Y, Z;
    /// <summary>Its turn about the upright, in radians.</summary>
    public double Turn;
    /// <summary>A lathe: the south radius over the east one, for an oval round.</summary>
    public double Squash = 1;
    /// <summary>An extrude: how far it is pulled, in metres.</summary>
    public double Depth = 1;
    public ExtrudeAxis Axis;
    /// <summary>A sweep's path, in metres from the origin.</summary>
    public List<Vector3> Path = new();
    public bool PathClosed;
    /// <summary>An open profile's thickness as voxels, in metres (0: one voxel).</summary>
    public double Thick;
    public byte Colour = AssetPalette.Concrete;
    /// <summary>A second colour for the faces that look down (a tree crown's shade), or 0 for none.</summary>
    public byte Under;
    /// <summary>A lathe's sides round the axis; 0 picks from its size.</summary>
    public int Segments;
    /// <summary>How lumpy a lathe's surface is (0 smooth, 0.2 a tree's crown).</summary>
    public double Lumpy;

    // RC part 5: finishes and walls.

    /// <summary>What its surface is made of (<see cref="Finishes"/>), drawn as a texture by the metre; "" for its colour.</summary>
    public string Finish = "";
    /// <summary>A wall: how long it runs along its own x, and how high. Its thickness is <see cref="Depth"/>, along its y.</summary>
    public double Width = 1, Height = 1;
    /// <summary>A wall's openings, cut right through: where along it and up it (from its corner), and how wide and high, in metres.</summary>
    public List<(double X, double Z, double W, double H)> Openings = new();

    /// <summary>
    /// An imported model (glTF or OBJ, RC part 5): its triangles in metres, world axes (x east, y up, z south), standing
    /// on the ground at its origin, with their colours and textures. Placed and turned like any shape; its voxels are
    /// its surface, in its texture's colours.
    /// </summary>
    public MeshData? Triangles;

    /// <summary>Corners sharper than this (degrees between the faces' normals) are drawn as a crease.</summary>
    public const double Crease = 35;

    public Shape Clone()
    {
        var s = (Shape)MemberwiseClone();
        s._turned = double.NaN;
        s.Profile = new(Profile);
        s.Path = new(Path);
        s.Openings = new(Openings);
        return s;
    }

    /// <summary>The shape flipped east to west across a model <paramref name="width"/> metres wide.</summary>
    public Shape MirroredX(double width)
    {
        var s = Clone();
        s.X = width - X;
        s.Turn = -Turn;
        // In its own frame, east becomes west: what lies along its local x goes the other way.
        switch (Kind)
        {
            case ShapeKind.Extrude when Axis == ExtrudeAxis.Across:
                s.X -= Depth * Math.Cos(s.Turn);
                s.Y -= Depth * Math.Sin(s.Turn);
                break;
            case ShapeKind.Extrude:
                s.Profile = Profile.Select(p => (-p.A, p.B)).ToList();
                break;
            case ShapeKind.Sweep:
                s.Path = Path.Select(p => new Vector3(-p.X, p.Y, p.Z)).ToList();
                s.Profile = Profile.Select(p => (-p.A, p.B)).ToList();
                break;
            case ShapeKind.Imported when Triangles != null:
                s.Triangles = MirrorTriangles(Triangles);
                break;
            case ShapeKind.Wall:
                // It runs from its other end, its openings the other way along it.
                s.X -= Width * Math.Cos(s.Turn);
                s.Y -= Width * Math.Sin(s.Turn);
                s.Openings = Openings.Select(o => (Width - o.X - o.W, o.Z, o.W, o.H)).ToList();
                break;
        }
        return s;
    }

    // ---- Polygons ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shape as polygons in world units (x east, y up, z south, as <see cref="VoxMesher"/> gives), at
    /// <paramref name="perMetre"/> world units a metre. Quality under 1 draws fewer sides round a lathe.
    /// </summary>
    public MeshData Mesh(float perMetre, double quality = 1, uint[]? palette = null)
    {
        if (Kind == ShapeKind.Imported) return ImportedMesh(perMetre);
        palette ??= DefaultPalette;
        var tris = new List<(Vector3 P, Vector3 N, bool Down)>();
        switch (Kind)
        {
            case ShapeKind.Lathe: LatheTris(tris, quality); break;
            case ShapeKind.Extrude: ExtrudeTris(tris); break;
            case ShapeKind.Sweep: SweepTris(tris); break;
            case ShapeKind.Wall: WallTris(tris); break;
        }
        var mesh = new MeshData();
        uint top = palette[Colour], under = Under != 0 ? palette[Under] : top;
        // A finish is drawn by the metre, mapped onto each face from the side it faces most, in the shape's own frame.
        int fin = Assets.Finishes.Has(Finish) ? mesh.FinishIndex(Finish) : 0;
        if (fin > 0) top = under = Assets.Finishes.VertexColour(Finish);
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            var a = World(tris[i].P, perMetre); var b = World(tris[i + 1].P, perMetre); var c = World(tris[i + 2].P, perMetre);
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-14f) continue;
            var na = Dir(tris[i].N); var nb = Dir(tris[i + 1].N); var nc = Dir(tris[i + 2].N);
            int s = mesh.Positions.Count;
            if (fin > 0)
            {
                var face = Vector3.Cross(tris[i + 1].P - tris[i].P, tris[i + 2].P - tris[i].P);
                var ln = LocalDir(Vector3.Dot(face, tris[i].N + tris[i + 1].N + tris[i + 2].N) < 0 ? -face : face);
                for (int k = 0; k < 3; k++)
                    mesh.Add(new[] { a, b, c }[k], new[] { na, nb, nc }[k], top, fin, BoxUv(tris[i + k].P, ln));
            }
            else
            {
                mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
                mesh.Normals.Add(na); mesh.Normals.Add(nb); mesh.Normals.Add(nc);
                mesh.Colors.Add(tris[i].Down ? under : top); mesh.Colors.Add(tris[i + 1].Down ? under : top); mesh.Colors.Add(tris[i + 2].Down ? under : top);
            }
            // Counter-clockwise seen from where the normals point.
            if (Vector3.Dot(cross, na + nb + nc) > 0) mesh.Indices.AddRange(new[] { s, s + 1, s + 2 });
            else mesh.Indices.AddRange(new[] { s, s + 2, s + 1 });
        }
        return Weld(mesh);
    }

    static readonly uint[] DefaultPalette = MakePalette();
    static uint[] MakePalette() { var m = new VoxModel(1, 1, 1); AssetPalette.Apply(m); return m.Palette; }

    /// <summary>Shares the corners that are the same point, normal and colour, so a mesh holds a third the vertices.</summary>
    static MeshData Weld(MeshData m)
    {
        var o = new MeshData();
        bool tex = m.Textured;
        if (tex) foreach (var id in m.FinishIds.Skip(1)) o.FinishIndex(id);
        var seen = new Dictionary<(Vector3, Vector3, uint, Vector2, int), int>();
        foreach (int i in m.Indices)
        {
            var key = (m.Positions[i], m.Normals[i], m.Colors[i], tex ? m.UVs[i] : Vector2.Zero, tex ? m.Finish[i] : 0);
            if (!seen.TryGetValue(key, out int j))
            {
                if (tex) j = o.Add(key.Item1, key.Item2, key.Item3, key.Item5, key.Item4);
                else { j = o.Positions.Count; o.Positions.Add(key.Item1); o.Normals.Add(key.Item2); o.Colors.Add(key.Item3); }
                seen[key] = j;
            }
            o.Indices.Add(j);
        }
        return o;
    }

    /// <summary>A model direction back in the shape's own frame (before its turn).</summary>
    Vector3 LocalDir(Vector3 n)
    {
        double c = Math.Cos(Turn), s = Math.Sin(Turn);
        return new Vector3((float)(n.X * c + n.Y * s), (float)(-n.X * s + n.Y * c), n.Z);
    }

    /// <summary>
    /// Where a point of the shape (in the model, metres) falls on its finish: mapped from the side its face looks to
    /// most, in the shape's own frame, so a wall's front reads left to right and up from its corner.
    /// </summary>
    Vector2 BoxUv(Vector3 p, Vector3 localNormal)
    {
        var (x, y, z) = Local(p.X, p.Y, p.Z);
        float ax = Math.Abs(localNormal.X), ay = Math.Abs(localNormal.Y), az = Math.Abs(localNormal.Z);
        if (az >= ax && az >= ay) return new Vector2((float)x, (float)(localNormal.Z > 0 ? -y : y));
        if (ay >= ax) return new Vector2((float)(localNormal.Y < 0 ? x : -x), (float)z);
        return new Vector2((float)(localNormal.X > 0 ? y : -y), (float)z);
    }

    /// <summary>
    /// A wall's triangles: a slab <see cref="Width"/> long, <see cref="Depth"/> thick and <see cref="Height"/> high
    /// from its corner, its front facing its -y, with each opening cut through and lined.
    /// </summary>
    void WallTris(List<(Vector3, Vector3, bool)> tris)
    {
        double w = Math.Max(0.01, Width), h = Math.Max(0.01, Height), d = Math.Max(0.005, Depth);
        var holes = ClampedOpenings();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 e, Vector3 n)
        {
            var pn = PlaceDir(n);
            var (pa, pb, pc, pe) = (Place(a), Place(b), Place(c), Place(e));
            tris.Add((pa, pn, false)); tris.Add((pb, pn, false)); tris.Add((pc, pn, false));
            tris.Add((pa, pn, false)); tris.Add((pc, pn, false)); tris.Add((pe, pn, false));
        }
        static Vector3 V(double x, double y, double z) => new((float)x, (float)y, (float)z);
        // Front and back: the face cut into a grid at every opening's edges, and every cell not in an opening drawn.
        var xs = new SortedSet<double> { 0, w }; var zs = new SortedSet<double> { 0, h };
        foreach (var o in holes) { xs.Add(o.X); xs.Add(o.X + o.W); zs.Add(o.Z); zs.Add(o.Z + o.H); }
        var xl = xs.ToList(); var zl = zs.ToList();
        for (int i = 0; i + 1 < xl.Count; i++)
            for (int j = 0; j + 1 < zl.Count; j++)
            {
                double cx = (xl[i] + xl[i + 1]) / 2, cz = (zl[j] + zl[j + 1]) / 2;
                if (holes.Any(o => cx > o.X && cx < o.X + o.W && cz > o.Z && cz < o.Z + o.H)) continue;
                Quad(V(xl[i], 0, zl[j]), V(xl[i + 1], 0, zl[j]), V(xl[i + 1], 0, zl[j + 1]), V(xl[i], 0, zl[j + 1]), -Vector3.UnitY);
                Quad(V(xl[i + 1], d, zl[j]), V(xl[i], d, zl[j]), V(xl[i], d, zl[j + 1]), V(xl[i + 1], d, zl[j + 1]), Vector3.UnitY);
            }
        // Its ends, top and bottom.
        Quad(V(0, d, 0), V(0, 0, 0), V(0, 0, h), V(0, d, h), -Vector3.UnitX);
        Quad(V(w, 0, 0), V(w, d, 0), V(w, d, h), V(w, 0, h), Vector3.UnitX);
        Quad(V(0, 0, h), V(w, 0, h), V(w, d, h), V(0, d, h), Vector3.UnitZ);
        Quad(V(0, d, 0), V(w, d, 0), V(w, 0, 0), V(0, 0, 0), -Vector3.UnitZ);
        // Each opening lined: its sides, its head, and its sill unless it comes down to the ground (a door).
        foreach (var o in holes)
        {
            double x0 = o.X, x1 = o.X + o.W, z0 = o.Z, z1 = o.Z + o.H;
            Quad(V(x0, 0, z0), V(x0, d, z0), V(x0, d, z1), V(x0, 0, z1), Vector3.UnitX);
            Quad(V(x1, d, z0), V(x1, 0, z0), V(x1, 0, z1), V(x1, d, z1), -Vector3.UnitX);
            if (z1 < h - 1e-6) Quad(V(x0, d, z1), V(x1, d, z1), V(x1, 0, z1), V(x0, 0, z1), -Vector3.UnitZ);
            if (z0 > 1e-6) Quad(V(x0, 0, z0), V(x1, 0, z0), V(x1, d, z0), V(x0, d, z0), Vector3.UnitZ);
        }
    }

    /// <summary>A wall's openings kept inside it.</summary>
    List<(double X, double Z, double W, double H)> ClampedOpenings()
    {
        var list = new List<(double, double, double, double)>();
        foreach (var o in Openings)
        {
            double x0 = Math.Clamp(o.X, 0, Width), x1 = Math.Clamp(o.X + o.W, 0, Width), z0 = Math.Clamp(o.Z, 0, Height), z1 = Math.Clamp(o.Z + o.H, 0, Height);
            if (x1 - x0 > 1e-3 && z1 - z0 > 1e-3) list.Add((x0, z0, x1 - x0, z1 - z0));
        }
        return list;
    }

    /// <summary>A point in the wall's own frame (along it, into it, up it) placed in the model, for parts set into it.</summary>
    public (double X, double Y, double Z) At(double along, double into, double up)
    {
        var p = Place(new Vector3((float)along, (float)into, (float)up));
        return (p.X, p.Y, p.Z);
    }

    /// <summary>A local point (metres, x east, y south, z up from the origin, before the turn) in the model.</summary>
    Vector3 Place(Vector3 p)
    {
        double c = Math.Cos(Turn), s = Math.Sin(Turn);
        return new Vector3((float)(X + p.X * c - p.Y * s), (float)(Y + p.X * s + p.Y * c), (float)(Z + p.Z));
    }

    Vector3 PlaceDir(Vector3 n)
    {
        double c = Math.Cos(Turn), s = Math.Sin(Turn);
        return new Vector3((float)(n.X * c - n.Y * s), (float)(n.X * s + n.Y * c), n.Z);
    }

    double _turned = double.NaN, _cos, _sin;
    public (double X, double Y, double Z) Local(double x, double y, double z)
    {
        if (_turned != Turn) { _turned = Turn; _cos = Math.Cos(Turn); _sin = Math.Sin(Turn); }
        double dx = x - X, dy = y - Y, c = _cos, s = _sin;
        return (dx * c + dy * s, -dx * s + dy * c, z - Z);
    }

    static Vector3 World(Vector3 p, float perMetre) => new(p.X * perMetre, p.Z * perMetre, p.Y * perMetre);
    static Vector3 Dir(Vector3 n) { var w = new Vector3(n.X, n.Z, n.Y); return w.LengthSquared() > 0 ? Vector3.Normalize(w) : Vector3.UnitY; }

    /// <summary>The profile's points, counter-clockwise for a closed one (so its outward normals are (dB, -dA)).</summary>
    List<(double A, double B)> Oriented()
    {
        var p = Profile.ToList();
        // Drop repeated points: a zero-length side has no normal.
        for (int i = p.Count - 1; i > 0; i--) if (Near(p[i], p[i - 1])) p.RemoveAt(i);
        if (p.Count > 2 && Near(p[0], p[^1])) p.RemoveAt(p.Count - 1);
        if (IsClosed && Area(p) < 0) p.Reverse();
        return p;
    }

    static bool Near((double A, double B) a, (double A, double B) b) => Math.Abs(a.A - b.A) < 1e-6 && Math.Abs(a.B - b.B) < 1e-6;

    /// <summary>A lathe whose profile starts and ends on the axis is closed by it.</summary>
    bool OnAxis => Kind == ShapeKind.Lathe && Profile.Count > 1 && Math.Abs(Profile[0].A) < 1e-6 && Math.Abs(Profile[^1].A) < 1e-6;
    bool IsClosed => Closed || OnAxis;

    public static double Area(IReadOnlyList<(double A, double B)> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var q = p[i]; var r = p[(i + 1) % p.Count]; a += q.A * r.B - r.A * q.B; }
        return a / 2;
    }

    /// <summary>The sides of a profile with each side's outward normal and each end's normal, smooth or creased.</summary>
    List<((double A, double B) P, (double A, double B) Q, Vector2 N, Vector2 NP, Vector2 NQ)> Sides(List<(double A, double B)> p, bool closed, bool axisClosed)
    {
        int n = p.Count, sides = closed && !axisClosed ? n : n - 1;
        var normals = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n];
            var v = new Vector2((float)(b.B - a.B), (float)-(b.A - a.A));
            normals[i] = v.LengthSquared() > 0 ? Vector2.Normalize(v) : Vector2.UnitX;
        }
        double cos = Math.Cos(Crease * Math.PI / 180);
        Vector2 End(int side, int other)
        {
            if (other < 0 || other >= sides) return normals[side];
            return Vector2.Dot(normals[side], normals[other]) >= cos ? Vector2.Normalize(normals[side] + normals[other]) : normals[side];
        }
        var list = new List<((double, double), (double, double), Vector2, Vector2, Vector2)>();
        bool loop = sides == n;
        for (int i = 0; i < sides; i++)
            list.Add((p[i], p[(i + 1) % n], normals[i], End(i, loop ? (i - 1 + sides) % sides : i - 1), End(i, loop ? (i + 1) % sides : i + 1)));
        return list;
    }

    public int SidesRound(double quality)
    {
        if (Segments > 0) return Math.Max(3, (int)Math.Round(Segments * Math.Min(1, quality)));
        double r = Profile.Count == 0 ? 0 : Profile.Max(p => Math.Abs(p.A)) * Math.Max(1, Squash);
        int n = Math.Clamp((int)Math.Ceiling(2 * Math.PI * r / 1.2), 12, 32);
        return Math.Max(6, (int)Math.Round(n * quality));
    }

    void LatheTris(List<(Vector3, Vector3, bool)> tris, double quality)
    {
        var p = Oriented();
        if (p.Count < 2) return;
        bool closed = IsClosed;
        int n = SidesRound(quality);
        var sides = Sides(p, closed, OnAxis);
        (Vector3 P, Vector3 N) Spin((double A, double B) q, Vector2 nn, int j)
        {
            double t = 2 * Math.PI * (j % n) / n, c = Math.Cos(t), s = Math.Sin(t);
            double r = q.A * (1 - Lumpy * Bump(j % n, q.B));
            var pos = new Vector3((float)(r * c), (float)(r * s * Squash), (float)q.B);
            var nor = new Vector3((float)(nn.X * c), (float)(nn.X * s / Squash), nn.Y);
            return (Place(pos), PlaceDir(nor));
        }
        foreach (var (a, b, nn, na, nb) in sides)
            for (int j = 0; j < n; j++)
            {
                var a0 = Spin(a, na, j); var a1 = Spin(a, na, j + 1); var b0 = Spin(b, nb, j); var b1 = Spin(b, nb, j + 1);
                Quad(tris, a0, a1, b1, b0, !closed);
            }
    }

    /// <summary>A lump on a lathe's surface, the same at the seam all the way round.</summary>
    static double Bump(int j, double z)
    {
        int h = (j * 73856093) ^ ((int)Math.Round(z * 8) * 19349663);
        return ((h >> 4) & 7) / 7.0;
    }

    static void Quad(List<(Vector3, Vector3, bool)> tris, (Vector3 P, Vector3 N) a, (Vector3 P, Vector3 N) b, (Vector3 P, Vector3 N) c, (Vector3 P, Vector3 N) d, bool twoSided)
    {
        void Tri((Vector3 P, Vector3 N) x, (Vector3 P, Vector3 N) y, (Vector3 P, Vector3 N) z, bool flip)
        {
            foreach (var q in new[] { x, y, z })
            {
                var n = flip ? -q.N : q.N;
                tris.Add((q.P, n, n.Z < -0.2f));
            }
        }
        Tri(a, b, c, false); Tri(a, c, d, false);
        if (twoSided) { Tri(a, c, b, true); Tri(a, d, c, true); }
    }

    /// <summary>An extrude's local point for an outline point and how far along: up from a plan, or back from an elevation.</summary>
    Vector3 Pull(double u, double v, double w) => Axis switch
    {
        ExtrudeAxis.Up => new Vector3((float)u, (float)v, (float)w),
        ExtrudeAxis.Back => new Vector3((float)u, (float)w, (float)v),
        _ => new Vector3((float)w, (float)u, (float)v),
    };

    Vector3 PullDir(Vector2 n, double w) => Axis switch
    {
        ExtrudeAxis.Up => new Vector3(n.X, n.Y, (float)w),
        ExtrudeAxis.Back => new Vector3(n.X, (float)w, n.Y),
        _ => new Vector3((float)w, n.X, n.Y),
    };

    void ExtrudeTris(List<(Vector3, Vector3, bool)> tris)
    {
        var p = Oriented();
        if (p.Count < 2) return;
        bool closed = Closed && p.Count > 2;
        foreach (var (a, b, _, na, nb) in Sides(p, closed, false))
        {
            (Vector3, Vector3) V((double A, double B) q, Vector2 nn, double w) => (Place(Pull(q.A, q.B, w)), PlaceDir(PullDir(nn, 0)));
            Quad(tris, V(a, na, 0), V(b, nb, 0), V(b, nb, Depth), V(a, na, Depth), !closed);
        }
        if (!closed) return;
        // The two ends: the outline cut into triangles, facing out each way. The mesh step winds each to its normal.
        var ends = EarClip(p);
        // For an outline in the plan, "up" is +z; in an elevation (across, up), its plane's normal points along the pull.
        var into = PlaceDir(Pull(0, 0, 1) - Pull(0, 0, 0));
        foreach (var (i, j, k) in ends)
        {
            foreach (var (w, n) in new[] { (0.0, -into), (Depth, into) })
            {
                tris.Add((Place(Pull(p[i].A, p[i].B, w)), n, n.Z < -0.2f));
                tris.Add((Place(Pull(p[j].A, p[j].B, w)), n, n.Z < -0.2f));
                tris.Add((Place(Pull(p[k].A, p[k].B, w)), n, n.Z < -0.2f));
            }
        }
    }

    /// <summary>A sweep's frame on each side of its path: along, left and up.</summary>
    List<(Vector3 A, Vector3 B, Vector3 Left, Vector3 Up)> Legs()
    {
        var legs = new List<(Vector3, Vector3, Vector3, Vector3)>();
        var path = Path.ToList();
        if (PathClosed && path.Count > 2) path.Add(path[0]);
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var d = path[i + 1] - path[i];
            if (d.LengthSquared() < 1e-12f) continue;
            d = Vector3.Normalize(d);
            var left = Vector3.Cross(Vector3.UnitZ, d);
            left = left.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(left);
            var up = Vector3.Normalize(Vector3.Cross(d, left));
            legs.Add((path[i], path[i + 1], left, up));
        }
        return legs;
    }

    void SweepTris(List<(Vector3, Vector3, bool)> tris)
    {
        var p = Oriented();
        var legs = Legs();
        if (p.Count < 2 || legs.Count == 0) return;
        bool closed = Closed && p.Count > 2;
        var sides = Sides(p, closed, false);
        int m = legs.Count;
        bool loop = PathClosed && Path.Count > 2;
        // Where two legs meet, the profile is mitred: its left offset is stretched along the bisector.
        (Vector3 Left, Vector3 Up) Joint(int i)
        {
            bool first = i == 0 && !loop, last = i == m && !loop;
            var a = legs[(i - 1 + m) % m]; var b = legs[i % m];
            if (first) return (b.Left, b.Up);
            if (last) return (a.Left, a.Up);
            var l = a.Left + b.Left;
            if (l.LengthSquared() < 1e-6f) return (b.Left, b.Up);
            l = Vector3.Normalize(l);
            float k = Vector3.Dot(l, b.Left);
            return (l / Math.Max(0.2f, k), Vector3.Normalize(a.Up + b.Up));
        }
        for (int i = 0; i < m; i++)
        {
            var (ja, jb) = (Joint(i), Joint(i + 1));
            var leg = legs[i];
            foreach (var (a, b, _, na, nb) in sides)
            {
                (Vector3, Vector3) V((double A, double B) q, Vector2 nn, Vector3 at, (Vector3 Left, Vector3 Up) j) =>
                    (Place(at + j.Left * (float)q.A + j.Up * (float)q.B), PlaceDir(leg.Left * nn.X + leg.Up * nn.Y));
                Quad(tris, V(a, na, leg.A, ja), V(b, nb, leg.A, ja), V(b, nb, leg.B, jb), V(a, na, leg.B, jb), !closed);
            }
        }
        if (!closed || loop) return;
        var ends = EarClip(p);
        foreach (var (at, j, n) in new[] { (legs[0].A, Joint(0), -Vector3.Normalize(legs[0].B - legs[0].A)), (legs[^1].B, Joint(m), Vector3.Normalize(legs[^1].B - legs[^1].A)) })
            foreach (var (a, b, c) in ends)
                foreach (int k in new[] { a, b, c })
                    tris.Add((Place(at + j.Left * (float)p[k].A + j.Up * (float)p[k].B), PlaceDir(n), n.Z < -0.2f));
    }

    /// <summary>A simple polygon (counter-clockwise) cut into triangles by clipping ears.</summary>
    public static List<(int, int, int)> EarClip(IReadOnlyList<(double A, double B)> p)
    {
        var tris = new List<(int, int, int)>();
        var idx = Enumerable.Range(0, p.Count).ToList();
        if (Area(p) < 0) idx.Reverse();
        int guard = 0;
        while (idx.Count > 3 && guard++ < 10_000)
        {
            bool cut = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int a = idx[(i - 1 + idx.Count) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                double cross = (p[b].A - p[a].A) * (p[c].B - p[a].B) - (p[b].B - p[a].B) * (p[c].A - p[a].A);
                if (cross <= 1e-12) continue;
                bool inside = false;
                foreach (int q in idx)
                    if (q != a && q != b && q != c && InTriangle(p[q], p[a], p[b], p[c])) { inside = true; break; }
                if (inside) continue;
                tris.Add((a, b, c));
                idx.RemoveAt(i);
                cut = true;
                break;
            }
            if (!cut) break;  // not simple: give up on the rest rather than loop
        }
        if (idx.Count == 3) tris.Add((idx[0], idx[1], idx[2]));
        return tris;
    }

    static bool InTriangle((double A, double B) q, (double A, double B) a, (double A, double B) b, (double A, double B) c)
    {
        double S((double A, double B) p1, (double A, double B) p2, (double A, double B) p3) => (p1.A - p3.A) * (p2.B - p3.B) - (p2.A - p3.A) * (p1.B - p3.B);
        double d1 = S(q, a, b), d2 = S(q, b, c), d3 = S(q, c, a);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    // ---- Voxels --------------------------------------------------------------------------------------------------

    /// <summary>Whether a point in the model, in metres, is inside the shape (within half the thickness of an open one).</summary>
    public bool Inside(double x, double y, double z, double voxel, double grow = 0)
    {
        var (lx, ly, lz) = Local(x, y, z);
        double half = Math.Max(voxel, Thick) / 2 + grow;
        bool In(IReadOnlyList<(double A, double B)> p, double a, double b) => InPolygon(p, a, b) || grow > 0 && NearLine(p, a, b, grow, loop: true);
        switch (Kind)
        {
            case ShapeKind.Imported:
            {
                // Within its box: for picking it; its voxels are its surface (ImportedVoxels).
                if (Triangles is not { Positions.Count: > 0 } t) return false;
                var (lo, hi) = LocalBox();
                return lx >= lo.X - grow && lx <= hi.X + grow && ly >= lo.Y - grow && ly <= hi.Y + grow && lz >= lo.Z - grow && lz <= hi.Z + grow;
            }
            case ShapeKind.Wall:
            {
                if (lx < -grow || lx > Width + grow || ly < -grow - voxel / 2 || ly > Depth + grow + voxel / 2 || lz < -grow || lz > Height + grow) return false;
                foreach (var o in Openings)
                    if (lx > o.X + grow && lx < o.X + o.W - grow && lz > o.Z + grow && lz < o.Z + o.H - grow) return false;
                return true;
            }
            case ShapeKind.Lathe:
            {
                double r = Math.Sqrt(lx * lx + ly / Squash * (ly / Squash));
                return IsClosed ? In(Profile, r, lz) : NearLine(Profile, r, lz, half);
            }
            case ShapeKind.Extrude:
            {
                var (u, v, w) = Axis switch { ExtrudeAxis.Up => (lx, ly, lz), ExtrudeAxis.Back => (lx, lz, ly), _ => (ly, lz, lx) };
                if (w < -grow || w > Depth + grow) return false;
                return Closed && Profile.Count > 2 ? In(Profile, u, v) : NearLine(Profile, u, v, half);
            }
            default:
            {
                var at = new Vector3((float)lx, (float)ly, (float)lz);
                foreach (var (a, b, left, up) in Legs())
                {
                    var d = b - a;
                    float t = Vector3.Dot(at - a, d) / d.LengthSquared();
                    float slack = (float)(grow / Math.Sqrt(d.LengthSquared()));
                    if (t < -slack || t > 1 + slack) continue;
                    var off = at - a;
                    double u = Vector3.Dot(off, left), v = Vector3.Dot(off, up);
                    if (Closed && Profile.Count > 2 ? In(Profile, u, v) : NearLine(Profile, u, v, half)) return true;
                }
                return false;
            }
        }
    }

    static bool InPolygon(IReadOnlyList<(double A, double B)> p, double a, double b)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].B > b) != (p[j].B > b) && a < (p[j].A - p[i].A) * (b - p[i].B) / (p[j].B - p[i].B) + p[i].A) inside = !inside;
        return inside;
    }

    static bool NearLine(IReadOnlyList<(double A, double B)> p, double a, double b, double half, bool loop = false)
    {
        int n = loop ? p.Count : p.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var q = p[(i + 1) % p.Count];
            double dx = q.A - p[i].A, dy = q.B - p[i].B, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Clamp(((a - p[i].A) * dx + (b - p[i].B) * dy) / len, 0, 1);
            double ex = p[i].A + t * dx - a, ey = p[i].B + t * dy - b;
            if (ex * ex + ey * ey <= half * half) return true;
        }
        return false;
    }

    /// <summary>Its bounds in the model, in metres: min and max corners.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var m = Mesh(1, 0.5);
        if (m.Positions.Count == 0) return (new Vector3((float)X, (float)Y, (float)Z), new Vector3((float)X, (float)Y, (float)Z));
        // World y is up: back to model axes.
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var w in m.Positions) { var p = new Vector3(w.X, w.Z, w.Y); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        // A lathe's sides cut inside its circle between corners: pad by the gap.
        float pad = 0.25f + (float)Thick;
        return (min - new Vector3(pad), max + new Vector3(pad));
    }

    /// <summary>
    /// Visits every voxel of a model (at <paramref name="detail"/> voxels a half metre) whose middle is inside the shape.
    /// </summary>
    public void ForEachVoxel(VoxModel m, int detail, Action<int, int, int> visit, double grow = 0)
    {
        if (Kind == ShapeKind.Imported) { ImportedVoxels(m, detail, (x, y, z, _) => visit(x, y, z)); return; }
        double v = 0.5 / detail;
        var (min, max) = Bounds();
        int x0 = Math.Max(0, (int)Math.Floor(min.X / v)), x1 = Math.Min(m.SizeX - 1, (int)Math.Ceiling(max.X / v));
        int y0 = Math.Max(0, (int)Math.Floor(min.Y / v)), y1 = Math.Min(m.SizeY - 1, (int)Math.Ceiling(max.Y / v));
        int z0 = Math.Max(0, (int)Math.Floor(min.Z / v)), z1 = Math.Min(m.SizeZ - 1, (int)Math.Ceiling(max.Z / v));
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (Inside((x + 0.5) * v, (y + 0.5) * v, (z + 0.5) * v, v, grow)) visit(x, y, z);
    }

    /// <summary>Fills the shape's voxels in a model, in its colour (the shade below its middle for one with an under colour). Returns how many.</summary>
    public int Voxelize(VoxModel m, int detail)
    {
        int n = 0;
        ForEachVoxelColour(m, detail, (x, y, z, c) => { m[x, y, z] = c; n++; });
        return n;
    }

    /// <summary>
    /// Visits each of the shape's voxels with its colour: its finish's texture there as the nearest palette colour
    /// (RC part 5), else its colour, or the shade under its middle for one with an under colour.
    /// </summary>
    public void ForEachVoxelColour(VoxModel m, int detail, Action<int, int, int, byte> visit)
    {
        if (Kind == ShapeKind.Imported)
        {
            var palette = new MeshVoxels.PaletteCache();
            ImportedVoxels(m, detail, (x, y, z, rgb) => visit(x, y, z, palette.Of(rgb)));
            return;
        }
        var (lo, hi) = Bounds();
        double mid = (lo.Z + hi.Z) / 2 - 0.1 * (hi.Z - lo.Z);
        double v = 0.5 / detail;
        var finish = Assets.Finishes.Get(Finish);
        ForEachVoxel(m, detail, (x, y, z) => visit(x, y, z, finish != null ? FinishColour(finish, (x + 0.5) * v, (y + 0.5) * v, (z + 0.5) * v, lo, hi)
            : Under != 0 && (z + 0.5) * v < mid ? Under : Colour));
    }

    /// <summary>
    /// A voxel's colour from the shape's finish: the texture at that point, as the nearest palette colour. A wall and
    /// anything tall reads its front (along and up); something flat, its top.
    /// </summary>
    byte FinishColour(Finish f, double x, double y, double z, Vector3 lo, Vector3 hi)
    {
        var (lx, ly, lz) = Local(x, y, z);
        bool flat = Kind != ShapeKind.Wall && hi.Z - lo.Z < 0.5 * Math.Max(hi.X - lo.X, hi.Y - lo.Y);
        uint rgb = flat ? f.Sample(lx, -ly) : f.Sample(Kind == ShapeKind.Wall ? lx : lx + ly, lz);
        return Assets.Finishes.Nearest(rgb);
    }

    // ---- Imported models (RC part 5) ------------------------------------------------------------------------------

    /// <summary>A vertex of the triangles in the model, in metres (model axes).</summary>
    Vector3 ImportedPoint(Vector3 w) => Place(new Vector3(w.X, w.Z, w.Y));

    MeshData ImportedMesh(float perMetre)
    {
        var o = new MeshData();
        if (Triangles is not { } t) return o;
        bool tex = t.Textured && t.Finish.Count == t.Positions.Count && t.UVs.Count == t.Positions.Count;
        var remap = t.FinishIds.Select(id => o.FinishIndex(id)).ToArray();
        for (int i = 0; i < t.Positions.Count; i++)
        {
            var n = t.Normals[i];
            o.Add(World(ImportedPoint(t.Positions[i]), perMetre), Dir(PlaceDir(new Vector3(n.X, n.Z, n.Y))), t.Colors[i],
                tex ? remap[t.Finish[i]] : 0, tex ? t.UVs[i] : Vector2.Zero);
        }
        o.Indices.AddRange(t.Indices);
        return o;
    }

    /// <summary>The voxels the model's surface passes through, each once, with its colour there.</summary>
    void ImportedVoxels(VoxModel m, int detail, Action<int, int, int, uint> visit)
    {
        if (Triangles is not { } t) return;
        float v = 0.5f / detail;
        var seen = new HashSet<int>();
        MeshVoxels.Sample(t, w => ImportedPoint(w) / v, (x, y, z, rgb) =>
        {
            if (m.In(x, y, z) && seen.Add(x + m.SizeX * (y + m.SizeY * z))) visit(x, y, z, rgb);
        });
    }

    (Vector3 Lo, Vector3 Hi)? _box;
    MeshData? _boxOf;
    (Vector3 Lo, Vector3 Hi) LocalBox()
    {
        if (_box is { } b && _boxOf == Triangles) return b;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var w in Triangles!.Positions) { var p = new Vector3(w.X, w.Z, w.Y); lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        _boxOf = Triangles;
        return (_box = (lo, hi)).Value;
    }

    /// <summary>Triangles flipped east to west, each wound the other way so it still faces out.</summary>
    static MeshData MirrorTriangles(MeshData t)
    {
        var o = new MeshData();
        o.FinishIds.Clear(); o.FinishIds.AddRange(t.FinishIds);
        o.Positions.AddRange(t.Positions.Select(p => new Vector3(-p.X, p.Y, p.Z)));
        o.Normals.AddRange(t.Normals.Select(n => new Vector3(-n.X, n.Y, n.Z)));
        o.Colors.AddRange(t.Colors); o.UVs.AddRange(t.UVs); o.Finish.AddRange(t.Finish);
        for (int i = 0; i + 2 < t.Indices.Count; i += 3) o.Indices.AddRange(new[] { t.Indices[i], t.Indices[i + 2], t.Indices[i + 1] });
        return o;
    }

    /// <summary>The finishes an imported model's triangles use (beside its own <see cref="Finish"/>).</summary>
    public IEnumerable<string> FinishesUsed() => (Triangles?.FinishIds ?? Enumerable.Empty<string>()).Append(Finish).Where(f => f.Length > 0);

    static string PackTriangles(MeshData t)
    {
        using var mem = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(mem, System.IO.Compression.CompressionLevel.Optimal))
        using (var w = new BinaryWriter(z))
        {
            bool tex = t.Textured && t.Finish.Count == t.Positions.Count && t.UVs.Count == t.Positions.Count;
            w.Write(t.Positions.Count); w.Write(t.Indices.Count); w.Write(tex);
            for (int i = 0; i < t.Positions.Count; i++)
            {
                var p = t.Positions[i]; var n = t.Normals[i];
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(n.X); w.Write(n.Y); w.Write(n.Z); w.Write(t.Colors[i]);
                if (tex) { w.Write(t.UVs[i].X); w.Write(t.UVs[i].Y); w.Write((ushort)t.Finish[i]); }
            }
            foreach (int i in t.Indices) w.Write(i);
        }
        return Convert.ToBase64String(mem.ToArray());
    }

    static MeshData? UnpackTriangles(string packed, IList<string> finishIds)
    {
        var t = new MeshData();
        using var r = new BinaryReader(new System.IO.Compression.DeflateStream(new MemoryStream(Convert.FromBase64String(packed)), System.IO.Compression.CompressionMode.Decompress));
        int nv = r.ReadInt32(), ni = r.ReadInt32();
        bool tex = r.ReadBoolean();
        if (nv < 0 || ni < 0 || nv > 4_000_000 || ni > 12_000_000) return null;
        if (tex) foreach (string id in finishIds.Skip(1)) t.FinishIndex(id);
        for (int i = 0; i < nv; i++)
        {
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var n = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            uint c = r.ReadUInt32();
            if (tex) { var uv = new Vector2(r.ReadSingle(), r.ReadSingle()); int f = r.ReadUInt16(); t.Add(p, n, c, f < t.FinishIds.Count ? f : 0, uv); }
            else { t.Positions.Add(p); t.Normals.Add(n); t.Colors.Add(c); }
        }
        for (int i = 0; i < ni; i++) { int k = r.ReadInt32(); if (k < 0 || k >= nv) return null; t.Indices.Add(k); }
        return t;
    }

    // ---- Saving --------------------------------------------------------------------------------------------------

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["profile"] = new JsonArray(Profile.Select(p => (JsonNode)new JsonArray(Round(p.A), Round(p.B))).ToArray()),
            ["at"] = new JsonArray(Round(X), Round(Y), Round(Z)),
            ["colour"] = Colour,
        };
        if (!Closed) o["closed"] = false;
        if (Turn != 0) o["turn"] = Round(Turn);
        if (Squash != 1) o["squash"] = Round(Squash);
        if (Kind == ShapeKind.Extrude) { o["depth"] = Round(Depth); o["axis"] = Axis.ToString().ToLowerInvariant(); }
        if (Kind == ShapeKind.Sweep)
        {
            o["path"] = new JsonArray(Path.Select(p => (JsonNode)new JsonArray(Round(p.X), Round(p.Y), Round(p.Z))).ToArray());
            if (PathClosed) o["pathClosed"] = true;
        }
        if (Thick != 0) o["thick"] = Round(Thick);
        if (Under != 0) o["under"] = Under;
        if (Segments != 0) o["segments"] = Segments;
        if (Lumpy != 0) o["lumpy"] = Round(Lumpy);
        if (Finish.Length > 0) o["finish"] = Finish;
        if (Kind == ShapeKind.Imported && Triangles != null)
        {
            o["finishes"] = new JsonArray(Triangles.FinishIds.Select(f => (JsonNode)JsonValue.Create(f)).ToArray());
            o["triangles"] = PackTriangles(Triangles);
        }
        if (Kind == ShapeKind.Wall)
        {
            o["width"] = Round(Width); o["height"] = Round(Height); o["depth"] = Round(Depth);
            if (Openings.Count > 0) o["openings"] = new JsonArray(Openings.Select(p => (JsonNode)new JsonArray(Round(p.X), Round(p.Z), Round(p.W), Round(p.H))).ToArray());
        }
        return o;
    }

    static double Round(double v) => Math.Round(v, 4);

    /// <summary>A shape read back from its JSON, or null if it isn't one.</summary>
    public static Shape? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        try
        {
            var s = new Shape
            {
                Kind = Enum.TryParse<ShapeKind>((string?)o["kind"], true, out var k) ? k : ShapeKind.Lathe,
                Profile = o["profile"] is JsonArray pa ? pa.OfType<JsonArray>().Select(p => ((double)p[0]!, (double)p[1]!)).ToList() : new(),
                Closed = (bool?)o["closed"] ?? true,
                Turn = (double?)o["turn"] ?? 0, Squash = (double?)o["squash"] ?? 1, Depth = (double?)o["depth"] ?? 1,
                Axis = Enum.TryParse<ExtrudeAxis>((string?)o["axis"], true, out var ax) ? ax : ExtrudeAxis.Up,
                Path = o["path"] is JsonArray pp ? pp.OfType<JsonArray>().Select(p => new Vector3((float)(double)p[0]!, (float)(double)p[1]!, (float)(double)p[2]!)).ToList() : new(),
                PathClosed = (bool?)o["pathClosed"] ?? false,
                Thick = (double?)o["thick"] ?? 0, Colour = (byte?)(int?)o["colour"] ?? AssetPalette.Concrete, Under = (byte?)(int?)o["under"] ?? 0,
                Segments = (int?)o["segments"] ?? 0, Lumpy = (double?)o["lumpy"] ?? 0,
                Finish = (string?)o["finish"] ?? "", Width = (double?)o["width"] ?? 1, Height = (double?)o["height"] ?? 1,
                Openings = o["openings"] is JsonArray op ? op.OfType<JsonArray>().Select(p => ((double)p[0]!, (double)p[1]!, (double)p[2]!, (double)p[3]!)).ToList() : new(),
            };
            if (o["at"] is JsonArray at && at.Count == 3) (s.X, s.Y, s.Z) = ((double)at[0]!, (double)at[1]!, (double)at[2]!);
            if (s.Kind == ShapeKind.Imported && (string?)o["triangles"] is { } packed)
                s.Triangles = UnpackTriangles(packed, o["finishes"] is JsonArray fa ? fa.Select(f => (string?)f ?? "").ToList() : new List<string> { "" });
            return s.Profile.Count >= 2 || s.Kind == ShapeKind.Wall || s.Triangles != null ? s : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException or InvalidDataException or EndOfStreamException) { return null; }
    }

    // ---- Makers --------------------------------------------------------------------------------------------------

    /// <summary>An upright cylinder or cone standing at (x, y, z0) up to z1; hollow leaves a wall <paramref name="wall"/> thick.</summary>
    public static Shape Cylinder(double x, double y, double r0, double z0, double z1, byte c, double? r1 = null, double wall = 0)
    {
        double top = r1 ?? r0, h = z1 - z0;
        var s = new Shape { Kind = ShapeKind.Lathe, X = x, Y = y, Z = z0, Colour = c };
        s.Profile = wall > 0
            ? new() { (Math.Max(0, r0 - wall), 0), (r0, 0), (top, h), (Math.Max(0, top - wall), h) }
            : new() { (0, 0), (r0, 0), (top, h), (0, h) };
        return s;
    }

    /// <summary>An ellipsoid centred on (x, y, z): a crown, a dome, a duck's body.</summary>
    public static Shape Ellipsoid(double x, double y, double z, double rx, double ry, double rz, byte c, byte under = 0, int rings = 0)
    {
        // Rings about a metre apart round the curve: a crown gets seven, a dome sixteen.
        if (rings <= 0) rings = Math.Clamp((int)Math.Ceiling(Math.PI * Math.Max(rx, rz) / 2.0), 5, 12);
        var s = new Shape { Kind = ShapeKind.Lathe, X = x, Y = y, Z = z, Colour = c, Under = under, Squash = ry / rx };
        for (int i = 0; i <= rings; i++)
        {
            double f = -Math.PI / 2 + Math.PI * i / rings;
            s.Profile.Add((i == 0 || i == rings ? 0 : rx * Math.Cos(f), rz * Math.Sin(f)));
        }
        return s;
    }
}
