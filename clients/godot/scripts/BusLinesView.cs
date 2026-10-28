using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;

namespace ToyCity.Godot;

/// <summary>
/// The bus company's lines along their streets, and their buses: each runs the line end to end and back on a
/// cosmetic clock (half a minute a round trip), spaced evenly, standing still while the game is paused. A line
/// that isn't running is drawn faint, without buses. Shown in the company's view, or on the city's map when the
/// Layers tab asks for bus routes.
/// </summary>
public partial class BusLinesView : Node3D
{
    readonly CityMirror _city;
    readonly PanelModel _panel;
    readonly CityView _view;
    MeshInstance3D? _lines;
    MultiMeshInstance3D? _buses;
    int _builtFor = -1;
    double _clock;

    /// <summary>Where each running bus is and which way it's going, for riding along.</summary>
    public readonly List<(string Line, Vector3 Pos, Vector3 Dir)> Buses = new();

    public BusLinesView(CityMirror city, PanelModel panel, CityView view) => (_city, _panel, _view) = (city, panel, view);

    public override void _Ready()
    {
        _lines = new MeshInstance3D
        {
            Mesh = new ImmediateMesh(),
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_lines);
        _buses = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.35f, 0.5f) } },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true },
        };
        AddChild(_buses);
    }

    Vector3 At(int cell, float lift)
    {
        int x = cell % _city.Width, y = cell / _city.Width;
        return new Vector3(x + 0.5f, _view.Ground(_city.Idx(x, y)) + lift, y + 0.5f);
    }

    public override void _Process(double delta)
    {
        var v = _panel.Company.View;
        Visible = v != null && (_panel.Company.Active || _panel.ShowRoutes);
        Buses.Clear();
        if (v == null) return;
        if (_panel.Company.MapVersion != _builtFor)
        {
            _builtFor = _panel.Company.MapVersion;
            // Each line as a flat ribbon a third of a cell wide, just over the road.
            var mesh = (ImmediateMesh)_lines!.Mesh;
            mesh.ClearSurfaces();
            bool any = false;
            foreach (var line in v.Lines.Where(l => l.RouteCells.Count > 1))
            {
                if (!any) { mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles); any = true; }
                var colour = Color.FromString(line.Color, Colors.Yellow) with { A = line.HeadwayMinutes > 0 ? 0.8f : 0.35f };
                for (int k = 0; k + 1 < line.RouteCells.Count; k++)
                {
                    Vector3 a = At(line.RouteCells[k], 0.08f), b = At(line.RouteCells[k + 1], 0.08f);
                    var side = (b - a).Cross(Vector3.Up).Normalized() * 0.16f;
                    mesh.SurfaceSetColor(colour);
                    foreach (var p in new[] { a - side, a + side, b + side, a - side, b + side, b - side }) mesh.SurfaceAddVertex(p);
                }
            }
            if (any) mesh.SurfaceEnd();
        }
        if (_city.Stats.Speed > 0) _clock += delta;
        var mm = _buses!.Multimesh;
        int total = v.Lines.Where(l => l.HeadwayMinutes > 0 && l.RouteCells.Count > 1).Sum(l => Math.Max(0, l.Buses - l.Broken));
        if (mm.InstanceCount < total) mm.InstanceCount = total;
        int n = 0;
        foreach (var line in v.Lines)
        {
            var cells = line.RouteCells;
            int running = Math.Max(0, line.Buses - line.Broken);
            if (line.HeadwayMinutes <= 0 || cells.Count < 2) continue;
            int last = cells.Count - 1;
            var colour = Color.FromString(line.Color, Colors.Yellow);
            for (int b = 0; b < running; b++)
            {
                double phase = (_clock / 30 + (double)b / running) % 1, u = phase * 2 * last;
                if (u > last) u = 2 * last - u;
                int i = Math.Min(last - 1, (int)u);
                float f = (float)(u - i);
                Vector3 p0 = At(cells[i], 0.25f), p1 = At(cells[i + 1], 0.25f);
                var dir = p1 - p0;
                var basis = dir.LengthSquared() > 0 ? Basis.LookingAt(dir.Normalized(), Vector3.Up) : Basis.Identity;
                mm.SetInstanceTransform(n, new Transform3D(basis, p0.Lerp(p1, f)));
                Buses.Add((line.Name, p0.Lerp(p1, f), dir));
                mm.SetInstanceColor(n, colour);
                n++;
            }
        }
        mm.VisibleInstanceCount = n;
    }
}
