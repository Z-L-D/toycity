using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Graphics;

namespace ToyCity.Godot;

/// <summary>
/// The plant layer in 3D (Beta 4.1, part 1): every crown of every cell, where <see cref="TreeLayout"/> puts it, on the
/// ground's surface. One instanced batch per kind of plant and 32-cell square of the map, so the camera skips what's
/// off screen and a change redraws only the squares it touched. Broadleaf trees are a trunk under a lumpy crown,
/// conifers a trunk under stacked cones, scrub a low bush, reeds a clump of stems and orchard trees a small round
/// crown; far off, each is a crown of a few triangles (<see cref="Far"/>). They sway in the wind and turn with the seasons, and hold snow (shaders/tree.gdshader). The graphics
/// detail draws a quarter, half or all of each cell's crowns, bigger as there are fewer.
/// </summary>
public partial class TreesView : Node3D
{
    const int Square = 32;
    readonly CityMirror _city;
    readonly CityView _view;
    readonly Dictionary<(int Kind, int Sx, int Sy), MultiMeshInstance3D> _batches = new();
    readonly Mesh?[] _meshes = new Mesh?[6], _farMeshes = new Mesh?[6];
    bool _far;
    // Each batch's crowns (the headless renderer keeps no instance counts of its own).
    readonly Dictionary<(int Kind, int Sx, int Sy), int> _counts = new();
    readonly ShaderMaterial?[] _materials = new ShaderMaterial?[6];
    byte[] _drawn = System.Array.Empty<byte>();
    int _plantsFor = -1, _terrainFor = -1;
    BuildingDetail _detail = BuildingDetail.High, _drawnDetail = BuildingDetail.High;

    public TreesView(CityMirror city, CityView view) => (_city, _view) = (city, view);

    /// <summary>The graphics detail, which sets how many crowns a cell draws.</summary>
    public BuildingDetail Detail { set => _detail = value; }

    /// <summary>
    /// Far from the camera (classic zoom 0 to 2, or the free camera high up) each plant is a handful of triangles: a
    /// crown without its trunk, and no reeds at all, so a whole wooded map stays cheap.
    /// </summary>
    public bool Far { set => _far = value; }

    // Close up, only the squares near the camera draw full trees; the rest keep the cheap shapes.
    const float NearReach = 56f;
    readonly Dictionary<(int Kind, int Sx, int Sy), bool> _near = new();

    void PickShapes()
    {
        var cam = GetViewport()?.GetCamera3D();
        var eye = cam?.GlobalPosition ?? Vector3.Zero;
        if (cam != null && cam.Projection == Camera3D.ProjectionType.Orthogonal)
        {
            // A flat view has no distance: measure from where the middle of the screen meets the ground instead.
            var fwd = -cam.GlobalBasis.Z;
            if (fwd.Y < -0.01f) eye = new Vector3(eye.X - fwd.X * eye.Y / fwd.Y, 0, eye.Z - fwd.Z * eye.Y / fwd.Y);
            else eye.Y = 0;
        }
        foreach (var (key, mmi) in _batches)
        {
            float cx = (key.Sx + 0.5f) * Square, cz = (key.Sy + 0.5f) * Square;
            float dx = Math.Max(0, Math.Abs(eye.X - cx) - Square / 2f), dz = Math.Max(0, Math.Abs(eye.Z - cz) - Square / 2f);
            bool near = !_far && cam != null && dx * dx + dz * dz + eye.Y * eye.Y * 0.25f < NearReach * NearReach;
            if (_near.TryGetValue(key, out var was) && was == near) continue;
            _near[key] = near;
            mmi.Multimesh.Mesh = (near ? _meshes : _farMeshes)[key.Kind];
            mmi.Visible = near || key.Kind != TreeLayout.Reeds;
        }
    }

    /// <summary>Crowns drawn now, for the gate's checks.</summary>
    public int Crowns { get; private set; }

    public override void _Ready()
    {
        var shader = GD.Load<Shader>("res://shaders/tree.gdshader");
        for (int k = 1; k <= 5; k++)
        {
            _meshes[k] = MakeMesh(k);
            _farMeshes[k] = MakeFarMesh(k);
            _materials[k] = new ShaderMaterial { Shader = shader };
            _materials[k]!.SetShaderParameter("kind", k);
        }
    }

    public override void _Process(double delta)
    {
        if (!_city.Loaded || _city.Plants.Length != _city.Width * _city.Height) return;
        PickShapes();
        bool all = _drawn.Length != _city.Plants.Length || _city.TerrainVersion != _terrainFor || _detail != _drawnDetail;
        if (!all && _city.PlantsVersion == _plantsFor) return;
        int sw = (_city.Width + Square - 1) / Square, sh = (_city.Height + Square - 1) / Square;
        var dirty = new HashSet<(int, int)>();
        if (all)
        {
            foreach (var b in _batches.Values) b.QueueFree();
            _batches.Clear();
            _counts.Clear();
            _near.Clear();
            for (int sy = 0; sy < sh; sy++) for (int sx = 0; sx < sw; sx++) dirty.Add((sx, sy));
            _drawn = new byte[_city.Plants.Length];
        }
        else
            for (int i = 0; i < _city.Plants.Length; i++)
                if (_drawn[i] != _city.Plants[i]) dirty.Add((i % _city.Width / Square, i / _city.Width / Square));
        (_plantsFor, _terrainFor, _drawnDetail) = (_city.PlantsVersion, _city.TerrainVersion, _detail);
        System.Array.Copy(_city.Plants, _drawn, _drawn.Length);
        var spots = new List<TreeSpot>();
        foreach (var (sx, sy) in dirty)
        {
            spots.Clear();
            for (int y = sy * Square; y < Math.Min(_city.Height, (sy + 1) * Square); y++)
                for (int x = sx * Square; x < Math.Min(_city.Width, (sx + 1) * Square); x++)
                    TreeLayout.Spots(x, y, _city.Width, _city.Plants[_city.Idx(x, y)], _detail, spots);
            for (int kind = 1; kind <= 5; kind++) Fill(kind, sx, sy, spots);
        }
        Crowns = 0;
        foreach (var n in _counts.Values) Crowns += n;
    }

    void Fill(int kind, int sx, int sy, List<TreeSpot> spots)
    {
        int n = 0;
        foreach (var s in spots) if (s.Kind == kind) n++;
        _counts[(kind, sx, sy)] = n;
        if (!_batches.TryGetValue((kind, sx, sy), out var mmi))
        {
            if (n == 0) return;
            mmi = new MultiMeshInstance3D
            {
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _farMeshes[kind] },
                Visible = kind != TreeLayout.Reeds,
                MaterialOverride = _materials[kind],
                // Reeds and scrub are too low to throw a shadow worth its cost.
                CastShadow = kind is TreeLayout.Reeds or TreeLayout.Scrub ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On,
            };
            AddChild(mmi);
            _batches[(kind, sx, sy)] = mmi;
            _near.Remove((kind, sx, sy));
        }
        var mm = mmi.Multimesh;
        if (mm.InstanceCount < n) mm.InstanceCount = Math.Max(n, mm.InstanceCount * 2);
        int k = 0;
        foreach (var s in spots)
        {
            if (s.Kind != kind) continue;
            var basis = new Basis(Vector3.Up, s.Turn).Scaled(new Vector3(s.Scale, s.Scale * (0.9f + 0.2f * s.Shade), s.Scale));
            mm.SetInstanceTransform(k, new Transform3D(basis, new Vector3(s.X, _view.Surface(s.X, s.Y) - 0.01f, s.Y)));
            k++;
        }
        mm.VisibleInstanceCount = n;
    }

    // ---- The plants' shapes, in cells (16 m): a broadleaf tree about 14 m tall, a conifer 18 m. ----

    static readonly Color Bark = new(0.33f, 0.24f, 0.16f);

    static Mesh MakeMesh(int kind)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        switch (kind)
        {
            case TreeLayout.Broadleaf:
                Cylinder(st, 0.025f, 0.018f, 0f, 0.42f, 5, Bark, 0f, 0.4f);
                Blob(st, new Vector3(0, 0.6f, 0), new Vector3(0.24f, 0.22f, 0.24f), new Color(0.26f, 0.42f, 0.17f), 0.45f, 1f);
                Blob(st, new Vector3(0.06f, 0.48f, 0.02f), new Vector3(0.18f, 0.14f, 0.17f), new Color(0.24f, 0.4f, 0.15f), 0.35f, 0.8f);
                break;
            case TreeLayout.Conifer:
                Cylinder(st, 0.03f, 0.015f, 0f, 0.3f, 5, Bark, 0f, 0.25f);
                Cylinder(st, 0.22f, 0f, 0.18f, 0.7f, 7, new Color(0.16f, 0.3f, 0.17f), 0.2f, 0.8f);
                Cylinder(st, 0.16f, 0f, 0.5f, 1.05f, 7, new Color(0.18f, 0.33f, 0.19f), 0.6f, 1f);
                break;
            case TreeLayout.Scrub:
                Blob(st, new Vector3(0, 0.08f, 0), new Vector3(0.16f, 0.1f, 0.14f), new Color(0.37f, 0.4f, 0.2f), 0f, 0.6f);
                Blob(st, new Vector3(0.09f, 0.06f, 0.06f), new Vector3(0.09f, 0.07f, 0.09f), new Color(0.33f, 0.38f, 0.19f), 0f, 0.5f);
                break;
            case TreeLayout.Reeds:
                for (int k = 0; k < 7; k++)
                {
                    float a = k * 2.4f, r = 0.03f + 0.05f * (k % 3);
                    var at = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
                    Blade(st, at, 0.13f + 0.03f * (k % 2), k % 2 == 0 ? new Color(0.5f, 0.55f, 0.28f) : new Color(0.58f, 0.56f, 0.32f));
                }
                break;
            case TreeLayout.Orchard:
                Cylinder(st, 0.02f, 0.016f, 0f, 0.18f, 5, Bark, 0f, 0.3f);
                Blob(st, new Vector3(0, 0.27f, 0), new Vector3(0.12f, 0.11f, 0.12f), new Color(0.3f, 0.46f, 0.2f), 0.4f, 1f);
                break;
        }
        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>The far shapes: a crown as a bare octahedron, a conifer as one cone; reeds aren't drawn far off.</summary>
    static Mesh MakeFarMesh(int kind)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        switch (kind)
        {
            case TreeLayout.Broadleaf: Blob(st, new Vector3(0, 0.56f, 0), new Vector3(0.26f, 0.28f, 0.26f), new Color(0.26f, 0.42f, 0.17f), 0.4f, 1f, split: false); break;
            case TreeLayout.Conifer: Cylinder(st, 0.22f, 0f, 0.1f, 1.05f, 5, new Color(0.17f, 0.31f, 0.18f), 0.2f, 1f); break;
            case TreeLayout.Scrub: Blob(st, new Vector3(0, 0.08f, 0), new Vector3(0.17f, 0.1f, 0.15f), new Color(0.37f, 0.4f, 0.2f), 0f, 0.6f, split: false); break;
            case TreeLayout.Orchard: Blob(st, new Vector3(0, 0.25f, 0), new Vector3(0.13f, 0.14f, 0.13f), new Color(0.3f, 0.46f, 0.2f), 0.4f, 1f, split: false); break;
            default: Blade(st, Vector3.Zero, 0.1f, new Color(0.5f, 0.55f, 0.28f)); break;
        }
        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>A tapered cylinder (a cone when the top radius is 0) from y0 to y1, sides only.</summary>
    static void Cylinder(SurfaceTool st, float r0, float r1, float y0, float y1, int sides, Color c, float up0, float up1)
    {
        for (int s = 0; s < sides; s++)
        {
            float a0 = Mathf.Tau * s / sides, a1 = Mathf.Tau * (s + 1) / sides;
            Vector3 b0 = new(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0), b1 = new(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0);
            Vector3 t0 = new(Mathf.Cos(a0) * r1, y1, Mathf.Sin(a0) * r1), t1 = new(Mathf.Cos(a1) * r1, y1, Mathf.Sin(a1) * r1);
            Tri(st, b0, t0, b1, c, up0, up1, up0);
            if (r1 > 0) Tri(st, b1, t0, t1, c, up0, up1, up1);
        }
        // A cone's skirt underneath, so it isn't hollow from below.
        if (r1 == 0 && y0 > 0)
            for (int s = 0; s < sides; s++)
            {
                float a0 = Mathf.Tau * s / sides, a1 = Mathf.Tau * (s + 1) / sides;
                Tri(st, new Vector3(0, y0, 0), new Vector3(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0), new Vector3(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0), c * 0.8f, up0, up0, up0);
            }
    }

    /// <summary>A lumpy, low-poly ellipsoid: an octahedron split once, its points pushed out a little unevenly.</summary>
    static void Blob(SurfaceTool st, Vector3 at, Vector3 size, Color c, float up0, float up1, bool split = true)
    {
        Vector3[] v = { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward };
        int[] f = { 2, 4, 0, 2, 0, 5, 2, 5, 1, 2, 1, 4, 3, 0, 4, 3, 5, 0, 3, 1, 5, 3, 4, 1 };
        Vector3 P(Vector3 d)
        {
            d = d.Normalized();
            float bump = 1f + 0.12f * Mathf.Sin(d.X * 7.1f + d.Z * 5.3f + d.Y * 3.7f);
            return at + d * size * bump;
        }
        float Up(Vector3 p) => Mathf.Lerp(up0, up1, Mathf.Clamp((p.Y - (at.Y - size.Y)) / (2 * size.Y), 0, 1));
        for (int k = 0; k < f.Length; k += 3)
        {
            Vector3 a = v[f[k]], b = v[f[k + 1]], d = v[f[k + 2]];
            if (!split)
            {
                Vector3 pa = P(a), pb = P(b), pd = P(d);
                Tri(st, pa, pb, pd, c, Up(pa), Up(pb), Up(pd));
                continue;
            }
            Vector3 ab = (a + b) / 2, bd = (b + d) / 2, da = (d + a) / 2;
            foreach (var (p, q, r) in new[] { (a, ab, da), (ab, b, bd), (da, bd, d), (ab, bd, da) })
            {
                Vector3 pp = P(p), qq = P(q), rr = P(r);
                Tri(st, pp, qq, rr, c, Up(pp), Up(qq), Up(rr));
            }
        }
    }

    /// <summary>A reed: a thin upright blade, two-sided.</summary>
    static void Blade(SurfaceTool st, Vector3 at, float height, Color c)
    {
        Vector3 a = at + new Vector3(-0.008f, 0, 0), b = at + new Vector3(0.008f, 0, 0), t = at + new Vector3(0.01f, height, 0.01f);
        Tri(st, a, t, b, c, 0, 1, 0);
        Tri(st, b, t, a, c, 0, 1, 0);
    }

    static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Color col, float ua, float ub, float uc)
    {
        // Godot's front faces wind clockwise seen from outside; these are given counter-clockwise, so swap two.
        st.SetColor(new Color(col.R, col.G, col.B, ua)); st.AddVertex(a);
        st.SetColor(new Color(col.R, col.G, col.B, uc)); st.AddVertex(c);
        st.SetColor(new Color(col.R, col.G, col.B, ub)); st.AddVertex(b);
    }
}
