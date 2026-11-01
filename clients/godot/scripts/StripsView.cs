using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Arcs and diagonals drawn as the line the player drew (part 9): ribbons laid on the ground over the stairs of
/// cells the sim runs it on, in the same layers as a road cell (sidewalks, carriageway, kerbs and paint, from the
/// profile's RoadLook), with rails on a rail or tram line. The cells under it are drawn as open ground.
/// </summary>
public partial class StripsView : Node3D
{
    readonly CityMirror _city;
    readonly CityView _view;
    MeshInstance3D? _mesh;
    (int, int) _builtFor = (-1, -1);

    public StripsView(CityMirror city, CityView view) => (_city, _view) = (city, view);

    public override void _Ready()
    {
        _mesh = new MeshInstance3D
        {
            Mesh = new ImmediateMesh(),
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_mesh);
    }

    public override void _Process(double delta)
    {
        if (!_city.Loaded || (_city.RoadVersion, _city.TerrainVersion) == _builtFor) return;
        _builtFor = (_city.RoadVersion, _city.TerrainVersion);
        var mesh = (ImmediateMesh)_mesh!.Mesh;
        mesh.ClearSurfaces();
        if (_city.Strips.Count == 0) return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var s in _city.Strips)
        {
            var prof = s.Path >= 1 && s.Path <= _city.Profiles.Count ? _city.Profiles[s.Path - 1] : null;
            var look = RoadLook.For(prof, s.Cells.Count > 0 ? _city.SurfaceAt(s.Cells[0]) : null);
            float half = look.Width / 2;
            var pts = RoadShape.Points(s);
            // The same layers as a road cell (RoadsView): sidewalks, carriageway, kerbs, paint.
            if (look.Sidewalks) Ribbon(mesh, pts, 0.5f, 0.022f, new Color(0.66f, 0.65f, 0.62f));
            var surface = look.Paving switch
            {
                Paving.Dirt => new Color(0.48f, 0.40f, 0.30f), Paving.Gravel => new Color(0.62f, 0.58f, 0.52f),
                Paving.BikeLane => new Color(0.52f, 0.25f, 0.22f), Paving.Concrete => new Color(0.70f, 0.68f, 0.64f),
                Paving.Limestone => new Color(0.76f, 0.73f, 0.66f), Paving.Brick => new Color(0.55f, 0.31f, 0.24f),
                Paving.Rail => new Color(0.45f, 0.42f, 0.38f), _ => Palette.Road,
            };
            Ribbon(mesh, pts, half, 0.028f, surface);
            var white = new Color(0.90f, 0.90f, 0.88f);
            if (look.Painted || look.Paving == Paving.Brick)
            {
                if (look.Sidewalks)
                {
                    var kerb = new Color(0.78f, 0.77f, 0.74f);
                    Ribbon(mesh, pts, 0.011f, 0.031f, kerb, -half - 0.011f);
                    Ribbon(mesh, pts, 0.011f, 0.031f, kerb, half + 0.011f);
                }
                float mh = look.MedianM / RoadLook.CellM / 2;
                if (look.Centre == CentreLine.DoubleYellow)
                {
                    var yellow = new Color(0.88f, 0.72f, 0.20f);
                    Ribbon(mesh, pts, 0.0056f, 0.033f, yellow, -0.012f);
                    Ribbon(mesh, pts, 0.0056f, 0.033f, yellow, 0.012f);
                }
                else if (look.Centre == CentreLine.GrassMedian) Ribbon(mesh, pts, mh, 0.033f, new Color(0.30f, 0.40f, 0.19f));
                foreach (var (off, centre) in look.LaneLines())
                    if (!centre) Ribbon(mesh, pts, 0.004f, 0.033f, white, off, dashed: true);
                if (look.EdgeLines)
                {
                    Ribbon(mesh, pts, 0.004f, 0.033f, white, -half + 0.024f);
                    Ribbon(mesh, pts, 0.004f, 0.033f, white, half - 0.024f);
                }
            }
            if (prof?.Rail == true || !string.IsNullOrEmpty(prof?.Tram))
            {
                // Two rails along the line.
                var rail = new Color(0.72f, 0.73f, 0.75f);
                float gauge = prof!.Rail ? half * 0.55f : 0.045f;
                Ribbon(mesh, pts, 0.006f, 0.045f, rail, -gauge);
                Ribbon(mesh, pts, 0.006f, 0.045f, rail, gauge);
            }
        }
        mesh.SurfaceEnd();
    }

    /// <summary>A flat band along the points, <paramref name="half"/> either side of a line <paramref name="offset"/> from them.</summary>
    void Ribbon(ImmediateMesh mesh, List<(float X, float Y)> pts, float half, float lift, Color colour, float offset = 0, bool dashed = false)
    {
        Vector3? pl = null, pr = null;
        for (int k = 0; k < pts.Count; k++)
        {
            var (ax, ay) = pts[Math.Max(0, k - 1)];
            var (bx, by) = pts[Math.Min(pts.Count - 1, k + 1)];
            var dir = new Vector2(bx - ax, by - ay).Normalized();
            var side = new Vector2(-dir.Y, dir.X);
            var (x, y) = pts[k];
            Vector3 At(float o) { float px = x + side.X * o, py = y + side.Y * o; return new Vector3(px, _view.Surface(px, py) + lift, py); }
            Vector3 l = At(offset - half), r = At(offset + half);
            // Points come about four to a cell: a dash is every other piece.
            if (pl is { } a && pr is { } b && (!dashed || k % 2 == 1))
            {
                mesh.SurfaceSetColor(colour);
                mesh.SurfaceSetNormal(Vector3.Up);
                foreach (var v in new[] { a, b, r, a, r, l }) mesh.SurfaceAddVertex(v);
            }
            (pl, pr) = (l, r);
        }
    }
}
