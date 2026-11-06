using System.Numerics;
using System.Text.Json.Nodes;

namespace ToyCity.ClientModel.Voxels.Assets;

public enum ShapeKind { Lathe, Extrude, Sweep }

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

    /// <summary>Corners sharper than this (degrees between the faces' normals) are drawn as a crease.</summary>
    public const double Crease = 35;

    public Shape Clone()
    {
        var s = (Shape)MemberwiseClone();
        s._turned = double.NaN;
        s.Profile = new(Profile);
        s.Path = new(Path);
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
        palette ??= DefaultPalette;
        var tris = new List<(Vector3 P, Vector3 N, bool Down)>();
        switch (Kind)
        {
            case ShapeKind.Lathe: LatheTris(tris, quality); break;
            case ShapeKind.Extrude: ExtrudeTris(tris); break;
            case ShapeKind.Sweep: SweepTris(tris); break;
        }
        var mesh = new MeshData();
        uint top = palette[Colour], under = Under != 0 ? palette[Under] : top;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            var a = World(tris[i].P, perMetre); var b = World(tris[i + 1].P, perMetre); var c = World(tris[i + 2].P, perMetre);
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-14f) continue;
            var na = Dir(tris[i].N); var nb = Dir(tris[i + 1].N); var nc = Dir(tris[i + 2].N);
            int s = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
            mesh.Normals.Add(na); mesh.Normals.Add(nb); mesh.Normals.Add(nc);
            mesh.Colors.Add(tris[i].Down ? under : top); mesh.Colors.Add(tris[i + 1].Down ? under : top); mesh.Colors.Add(tris[i + 2].Down ? under : top);
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
        var seen = new Dictionary<(Vector3, Vector3, uint), int>();
        foreach (int i in m.Indices)
        {
            var key = (m.Positions[i], m.Normals[i], m.Colors[i]);
            if (!seen.TryGetValue(key, out int j))
            {
                seen[key] = j = o.Positions.Count;
                o.Positions.Add(key.Item1); o.Normals.Add(key.Item2); o.Colors.Add(key.Item3);
            }
            o.Indices.Add(j);
        }
        return o;
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
    (double X, double Y, double Z) Local(double x, double y, double z)
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
        var (lo, hi) = Bounds();
        double mid = (lo.Z + hi.Z) / 2 - 0.1 * (hi.Z - lo.Z);
        double v = 0.5 / detail;
        ForEachVoxel(m, detail, (x, y, z) => { m[x, y, z] = Under != 0 && (z + 0.5) * v < mid ? Under : Colour; n++; });
        return n;
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
            };
            if (o["at"] is JsonArray at && at.Count == 3) (s.X, s.Y, s.Z) = ((double)at[0]!, (double)at[1]!, (double)at[2]!);
            return s.Profile.Count >= 2 ? s : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException) { return null; }
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
