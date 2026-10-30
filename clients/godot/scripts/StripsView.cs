using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Arcs and diagonals drawn as the line the player drew (part 9): a ribbon of road laid on the ground over the
/// stairs of cells the sim runs it on, with rails on a rail line. The cells under it are drawn as open ground.
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
            float half = Mathf.Clamp((prof?.WidthM ?? 8) / 16f, 0.3f, 1f) / 2;
            var pts = RoadShape.Points(s);
            Ribbon(mesh, pts, half, 0.04f, Palette.Road);
            if (prof?.Rail == true || !string.IsNullOrEmpty(prof?.Tram))
            {
                // Two rails along the line.
                var rail = new Color(0.79f, 0.8f, 0.82f);
                float gauge = half * (prof!.Rail ? 0.6f : 0.45f);
                Ribbon(mesh, pts, 0.03f, 0.06f, rail, -gauge);
                Ribbon(mesh, pts, 0.03f, 0.06f, rail, gauge);
            }
        }
        mesh.SurfaceEnd();
    }

    /// <summary>A flat band along the points, <paramref name="half"/> either side of a line <paramref name="offset"/> from them.</summary>
    void Ribbon(ImmediateMesh mesh, List<(float X, float Y)> pts, float half, float lift, Color colour, float offset = 0)
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
            if (pl is { } a && pr is { } b)
            {
                mesh.SurfaceSetColor(colour);
                mesh.SurfaceSetNormal(Vector3.Up);
                foreach (var v in new[] { a, b, r, a, r, l }) mesh.SurfaceAddVertex(v);
            }
            (pl, pr) = (l, r);
        }
    }
}
