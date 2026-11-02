using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// The underground view (Beta 3.2), drawn under a cutaway ground (CityView.Underground): every pipe's water main
/// (blue, shallow) and sewer (brown, deeper) side by side, following the ground; road tunnels at their decks; and
/// the pumps and sewage plants picked out at the surface. Everything is drawn through the ground, deepest first.
/// </summary>
public partial class UndergroundView : Node3D
{
    readonly CityMirror _city;
    readonly CityView _view;
    MultiMeshInstance3D? _water, _sewer, _tunnels, _subways, _pumps, _plants;
    bool _dirty = true;

    public UndergroundView(CityMirror city, CityView view) => (_city, _view) = (city, view);

    /// <summary>Water mains, sewers and tunnel lengths last drawn, for the gate.</summary>
    public (int Water, int Sewer, int Tunnel, int Works) Drawn { get; private set; }
    /// <summary>Lengths of subway tunnel and subway stations last drawn, for the gate.</summary>
    public (int Subway, int Stations) DrawnSubway { get; private set; }

    static float Metres(float m) => m / 2f * ClassicView.HeightStep;

    MultiMeshInstance3D Layer(Color colour, int priority, bool shaded)
    {
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh { Size = Vector3.One } },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = colour, NoDepthTest = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RenderPriority = priority, Roughness = 0.6f,
                ShadingMode = shaded ? BaseMaterial3D.ShadingModeEnum.PerPixel : BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mmi);
        return mmi;
    }

    public override void _Ready()
    {
        // Deepest first, so what's nearer the surface draws over it.
        _subways = Layer(new Color(0.72f, 0.30f, 0.28f, 0.95f), 0, true);
        _tunnels = Layer(new Color(0.42f, 0.44f, 0.47f, 0.95f), 1, true);
        _sewer = Layer(new Color(0.62f, 0.45f, 0.24f, 0.97f), 2, true);
        _water = Layer(new Color(0.30f, 0.72f, 1.00f, 0.97f), 3, true);
        _pumps = Layer(new Color(0.30f, 0.72f, 1.00f, 0.35f), 4, false);
        _plants = Layer(new Color(0.75f, 0.55f, 0.28f, 0.45f), 4, false);
        Visible = false;
    }

    /// <summary>Redraws when shown, and while shown when cells or buildings changed.</summary>
    public void Show(bool on, bool changed)
    {
        if (changed) _dirty = true;
        Visible = on;
        if (on && _dirty) Build();
    }

    /// <summary>A box from <paramref name="a"/> to <paramref name="b"/>, <paramref name="width"/> across and <paramref name="height"/> high.</summary>
    static Transform3D Segment(Vector3 a, Vector3 b, float width, float height)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return new Transform3D(new Basis(Vector3.Right * width, Vector3.Up * height, Vector3.Back * width), a);
        var x = d / len;
        var z = x.Cross(Vector3.Up).Normalized();
        var y = z.Cross(x).Normalized();
        return new Transform3D(new Basis(x * (len + width), y * height, z * width), (a + b) / 2);
    }

    void Build()
    {
        _dirty = false;
        var c = _city;
        if (!c.Loaded) return;
        var water = new List<Transform3D>();
        var sewer = new List<Transform3D>();
        Vector3 At(int x, int y, float depth, float side) =>
            new(x + 0.5f + side, _view.Surface(x + 0.5f, y + 0.5f) - Metres(depth), y + 0.5f + side);
        foreach (var r in Underground.PipeRuns(c))
        {
            // Each run cell by cell, so the mains follow the ground over hills.
            int n = Math.Max(Math.Abs(r.X1 - r.X0), Math.Abs(r.Y1 - r.Y0));
            int dx = Math.Sign(r.X1 - r.X0), dy = Math.Sign(r.Y1 - r.Y0);
            for (int k = 0; k <= n; k++)
            {
                int x = r.X0 + dx * k, y = r.Y0 + dy * k;
                int nx = k < n ? x + dx : x, ny = k < n ? y + dy : y;
                water.Add(Segment(At(x, y, Underground.WaterDepthM, -Underground.Offset), At(nx, ny, Underground.WaterDepthM, -Underground.Offset), 0.09f, 0.09f));
                sewer.Add(Segment(At(x, y, Underground.SewerDepthM, Underground.Offset), At(nx, ny, Underground.SewerDepthM, Underground.Offset), 0.13f, 0.13f));
                if (n == 0) break;
            }
        }
        var tunnels = new List<Transform3D>();
        Vector3 Deck(int x, int y) => new(x + 0.5f, c.PathDecks[c.Idx(x, y)] / 20f * ClassicView.HeightStep + 0.05f, y + 0.5f);
        foreach (var r in Underground.TunnelRuns(c))
        {
            int n = Math.Max(Math.Abs(r.X1 - r.X0), Math.Abs(r.Y1 - r.Y0));
            int dx = Math.Sign(r.X1 - r.X0), dy = Math.Sign(r.Y1 - r.Y0);
            var p = c.ProfileAt(c.Idx(r.X0, r.Y0));
            float width = Math.Clamp((p?.WidthM ?? 8) / 16f, 0.3f, 1f);
            for (int k = 0; k < Math.Max(1, n); k++)
            {
                int x = r.X0 + dx * k, y = r.Y0 + dy * k;
                tunnels.Add(Segment(Deck(x, y), n == 0 ? Deck(x, y) : Deck(x + dx, y + dy), width, 0.1f));
            }
        }
        // Subways, deepest of all (12 m down), a broad tube under whatever stands above.
        var subways = new List<Transform3D>();
        foreach (var r in Underground.SubwayRuns(c))
        {
            int n = Math.Max(Math.Abs(r.X1 - r.X0), Math.Abs(r.Y1 - r.Y0));
            int dx = Math.Sign(r.X1 - r.X0), dy = Math.Sign(r.Y1 - r.Y0);
            for (int k = 0; k <= n; k++)
            {
                int x = r.X0 + dx * k, y = r.Y0 + dy * k;
                int nx = k < n ? x + dx : x, ny = k < n ? y + dy : y;
                subways.Add(Segment(At(x, y, Underground.SubwayDepthM, 0), At(nx, ny, Underground.SubwayDepthM, 0), 0.42f, 0.3f));
                if (n == 0) break;
            }
        }
        var pumps = new List<Transform3D>();
        var plants = new List<Transform3D>();
        foreach (var (b, plant) in Underground.Works(c))
        {
            float g = _view.Surface(b.X + b.Width / 2f, b.Y + b.Height / 2f);
            var t = new Transform3D(new Basis(Vector3.Right * (b.Width - 0.1f), Vector3.Up * 0.06f, Vector3.Back * (b.Height - 0.1f)), new Vector3(b.X + b.Width / 2f, g + 0.03f, b.Y + b.Height / 2f));
            (plant ? plants : pumps).Add(t);
        }
        int stations = 0;
        foreach (var b in c.Buildings.Values)
        {
            if (!c.TypeById.TryGetValue(b.TypeId, out var st) || st.Transit != "subway") continue;
            // A station's box reaches down from the street to its platforms.
            float g = _view.Surface(b.X + b.Width / 2f, b.Y + b.Height / 2f), d = Metres(Underground.SubwayDepthM);
            subways.Add(new Transform3D(new Basis(Vector3.Right * (b.Width - 0.2f), Vector3.Up * d, Vector3.Back * (b.Height - 0.2f)), new Vector3(b.X + b.Width / 2f, g - d / 2, b.Y + b.Height / 2f)));
            stations++;
        }
        Fill(_water!, water);
        Fill(_sewer!, sewer);
        Fill(_tunnels!, tunnels);
        Fill(_subways!, subways);
        Fill(_pumps!, pumps);
        Fill(_plants!, plants);
        Drawn = (water.Count, sewer.Count, tunnels.Count, pumps.Count + plants.Count);
        DrawnSubway = (subways.Count - stations, stations);
    }

    static void Fill(MultiMeshInstance3D mmi, List<Transform3D> list)
    {
        var mm = mmi.Multimesh;
        mm.InstanceCount = list.Count;
        for (int k = 0; k < list.Count; k++) mm.SetInstanceTransform(k, list[k]);
    }
}
