using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Farm country in 3D (Beta 4.1, part 2): a farmstead on each farm's yard, a white house, a red barn and a silo, all in
/// one instanced batch. The fields and tracks are the ground's own colours (shaders/terrain.gdshader).
/// </summary>
public partial class FarmsView : Node3D
{
    readonly CityMirror _city;
    readonly CityView _view;
    MultiMeshInstance3D? _batch;
    int _farmsFor = -1, _terrainFor = -1;

    public FarmsView(CityMirror city, CityView view) => (_city, _view) = (city, view);

    /// <summary>Farmsteads drawn now, for the gate's checks.</summary>
    public int Farmsteads { get; private set; }

    public override void _Ready()
    {
        _batch = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = MakeFarmstead() },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        };
        AddChild(_batch);
    }

    public override void _Process(double delta)
    {
        if (!_city.Loaded || _city.Farms.Length != _city.N) return;
        if (_city.FarmsVersion == _farmsFor && _city.TerrainVersion == _terrainFor) return;
        (_farmsFor, _terrainFor) = (_city.FarmsVersion, _city.TerrainVersion);
        int w = _city.Width, h = _city.Height;
        bool Yard(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && (_city.Farms[y * w + x] & 3) == 3;
        var at = new List<Transform3D>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // A yard's north-west corner: the farmstead stands over the yard from there, up to three cells a side.
                if (!Yard(x, y) || Yard(x - 1, y) || Yard(x, y - 1)) continue;
                int sx = 1, sy = 1;
                while (sx < 3 && Yard(x + sx, y)) sx++;
                while (sy < 3 && Yard(x, y + sy)) sy++;
                if (sx < 2 || sy < 2) continue;
                float cx = x + sx / 2f, cy = y + sy / 2f;
                uint hsh = (uint)(y * w + x) * 2654435761u;
                float turn = (hsh >> 28) % 4 * Mathf.Pi / 2;
                float lowest = Math.Min(Math.Min(_view.Surface(x + 0.3f, y + 0.3f), _view.Surface(x + sx - 0.3f, y + 0.3f)), Math.Min(_view.Surface(x + 0.3f, y + sy - 0.3f), _view.Surface(x + sx - 0.3f, y + sy - 0.3f)));
                at.Add(new Transform3D(new Basis(Vector3.Up, turn), new Vector3(cx, lowest - 0.02f, cy)));
            }
        var mm = _batch!.Multimesh;
        mm.InstanceCount = at.Count;
        for (int k = 0; k < at.Count; k++) mm.SetInstanceTransform(k, at[k]);
        Farmsteads = at.Count;
    }

    // ---- The farmstead, in cells (16 m) about its middle: the yard is three cells, 48 m, a side. ----

    static Mesh MakeFarmstead()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Color white = new(0.86f, 0.84f, 0.78f), red = new(0.55f, 0.16f, 0.12f), roof = new(0.30f, 0.28f, 0.27f), tin = new(0.62f, 0.63f, 0.64f), grey = new(0.66f, 0.66f, 0.64f);
        // The house: 11 m by 8 m, two storeys, a gable roof.
        Box(st, new Vector3(-0.85f, 0, -0.75f), new Vector3(0.69f, 0.38f, 0.5f), white);
        Gable(st, new Vector3(-0.85f, 0.38f, -0.75f), new Vector3(0.69f, 0.22f, 0.5f), roof, alongX: true);
        // The barn: 22 m by 13 m, a tall gable roof.
        Box(st, new Vector3(0.1f, 0, -0.35f), new Vector3(0.8f, 0.42f, 1.37f), red);
        Gable(st, new Vector3(0.1f, 0.42f, -0.35f), new Vector3(0.8f, 0.36f, 1.37f), roof, alongX: false);
        // Two silos: 6 m across, 18 m high, domed.
        Silo(st, new Vector3(-0.35f, 0, 0.55f), 0.19f, 1.1f, tin);
        Silo(st, new Vector3(-0.78f, 0, 0.55f), 0.17f, 0.9f, grey);
        // A machine shed, open grey.
        Box(st, new Vector3(-1.25f, 0, -0.12f), new Vector3(0.6f, 0.28f, 0.35f), tin * 0.85f);
        return st.Commit();
    }

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        var n = (b - a).Cross(d - a).Normalized();
        st.SetColor(col);
        foreach (var v in new[] { a, b, c, a, c, d }) { st.SetNormal(n); st.AddVertex(v); }
    }

    static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        var n = (b - a).Cross(c - a).Normalized();
        st.SetColor(col);
        foreach (var v in new[] { a, b, c }) { st.SetNormal(n); st.AddVertex(v); }
    }

    /// <summary>A box from its low corner <paramref name="o"/>, <paramref name="s"/> in size, its sides facing out.</summary>
    static void Box(SurfaceTool st, Vector3 o, Vector3 s, Color c)
    {
        Vector3 p(float x, float y, float z) => o + new Vector3(x * s.X, y * s.Y, z * s.Z);
        Quad(st, p(0, 0, 1), p(1, 0, 1), p(1, 1, 1), p(0, 1, 1), c * 0.95f);  // south
        Quad(st, p(1, 0, 0), p(0, 0, 0), p(0, 1, 0), p(1, 1, 0), c * 0.85f);  // north
        Quad(st, p(1, 0, 1), p(1, 0, 0), p(1, 1, 0), p(1, 1, 1), c * 0.9f);   // east
        Quad(st, p(0, 0, 0), p(0, 0, 1), p(0, 1, 1), p(0, 1, 0), c * 0.88f);  // west
        Quad(st, p(0, 1, 1), p(1, 1, 1), p(1, 1, 0), p(0, 1, 0), c);          // top
    }

    /// <summary>A gable roof over a box's top, its ridge along x or along z.</summary>
    static void Gable(SurfaceTool st, Vector3 o, Vector3 s, Color c, bool alongX)
    {
        Vector3 p(float x, float y, float z) => o + new Vector3(x * s.X, y * s.Y, z * s.Z);
        if (alongX)
        {
            Quad(st, p(0, 0, 1), p(1, 0, 1), p(1, 1, 0.5f), p(0, 1, 0.5f), c);
            Quad(st, p(1, 0, 0), p(0, 0, 0), p(0, 1, 0.5f), p(1, 1, 0.5f), c * 0.85f);
            Tri(st, p(0, 0, 0), p(0, 0, 1), p(0, 1, 0.5f), c * 0.9f);
            Tri(st, p(1, 0, 1), p(1, 0, 0), p(1, 1, 0.5f), c * 0.9f);
        }
        else
        {
            Quad(st, p(1, 0, 1), p(1, 0, 0), p(0.5f, 1, 0), p(0.5f, 1, 1), c);
            Quad(st, p(0, 0, 0), p(0, 0, 1), p(0.5f, 1, 1), p(0.5f, 1, 0), c * 0.85f);
            Tri(st, p(0, 0, 1), p(1, 0, 1), p(0.5f, 1, 1), c * 0.9f);
            Tri(st, p(1, 0, 0), p(0, 0, 0), p(0.5f, 1, 0), c * 0.9f);
        }
    }

    static void Silo(SurfaceTool st, Vector3 at, float r, float h, Color c)
    {
        const int sides = 10;
        for (int k = 0; k < sides; k++)
        {
            float a0 = Mathf.Tau * k / sides, a1 = Mathf.Tau * (k + 1) / sides;
            Vector3 e0 = new(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r), e1 = new(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
            Quad(st, at + e1, at + e0, at + e0 + Vector3.Up * h, at + e1 + Vector3.Up * h, c * (0.85f + 0.15f * Mathf.Cos(a0)));
            Tri(st, at + e1 + Vector3.Up * h, at + e0 + Vector3.Up * h, at + Vector3.Up * (h + r * 0.8f), c * 1.05f);
        }
    }
}
