using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;

namespace ToyCity.Godot;

/// <summary>
/// Direct dispatch in the 3D city (Beta 4, part 1): every fire engine, patrol car, ambulance and SWAT van is a little
/// vehicle in its service's colours on the city's own streets, eased along its route between the city's days. While
/// the service's screen is up and the player runs it, they're commanded Emergency-style: click one, shift-click more,
/// or drag a box; right-click a place to send them (shift queues more places), a call to answer it, or another unit to
/// follow it. The picked units are ringed, with the rest of their route drawn on the streets.
/// </summary>
public partial class DispatchWorld : Node3D
{
    readonly EmergencyModel _model;
    readonly CityView _view;
    readonly ClassicCamera _camera;
    readonly ImmediateMesh _solid = new(), _lines = new();
    readonly TickClock _clock = new();
    Vector2? _press, _rightFrom;
    Vector2 _boxTo;
    bool _boxing;
    Control? _boxView;
    List<(int Id, Vector3 At)> _drawn = new();

    static readonly Color[] Body = DispatchMap.Body.Select(c => new Color(c)).ToArray();
    static readonly Color[] Stripe = DispatchMap.Stripe.Select(c => new Color(c)).ToArray();
    static readonly Color Picked = new(DispatchMap.SelectedColour);

    public DispatchWorld(EmergencyModel model, CityView view, ClassicCamera camera) => (_model, _view, _camera) = (model, view, camera);

    /// <summary>The box being dragged, in screen pixels.</summary>
    public Rect2? DragBox => _boxing && _press is { } f ? new Rect2(f, _boxTo - f).Abs() : null;

    public override void _Ready()
    {
        var layer = new CanvasLayer { Layer = 5 };
        AddChild(layer);
        var box = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.Draw += () =>
        {
            if (DragBox is not { } r) return;
            box.DrawRect(r, new Color(1, 0.88f, 0.3f, 0.12f));
            box.DrawRect(r, Picked, false, 1.5f);
        };
        layer.AddChild(box);
        _boxView = box;
        AddChild(new MeshInstance3D
        {
            Mesh = _solid,
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.45f, Metallic = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        });
        AddChild(new MeshInstance3D
        {
            Mesh = _lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true, RenderPriority = 2,
            },
        });
    }

    double Minute()
    {
        var s = _view.City.Stats;
        return _clock.Exact(s.ClockMinute, s.ClockMinutesPerTick, s.Speed, DateTime.UtcNow);
    }

    Vector3 Ground(double x, double y, float lift = 0)
    {
        var c = _view.City;
        int cx = Math.Clamp((int)x, 0, c.Width - 1), cy = Math.Clamp((int)y, 0, c.Height - 1);
        float h = c.Paths.Length > cy * c.Width + cx && c.Paths[cy * c.Width + cx] != 0 ? _view.Surface((float)x, (float)y) : _view.Top(cx, cy);
        return new Vector3((float)x, h + 0.03f + lift, (float)y);
    }

    /// <summary>The units where they are this frame, those standing on one spot fanned out around it.</summary>
    List<(DispatchMap.Marker M, double X, double Y)> Placed()
    {
        var list = new List<(DispatchMap.Marker, double, double)>();
        var spots = new Dictionary<(int, int), int>();
        foreach (var m in _model.Map.Markers(Minute()))
        {
            double x = m.X, y = m.Y;
            if (!m.Moving)
            {
                var key = ((int)x, (int)y);
                int n = spots.GetValueOrDefault(key);
                spots[key] = n + 1;
                if (n > 0) { double a = n * 2.4, r = 0.3 * Math.Min(2, 1 + n / 6.0); x += Math.Cos(a) * r; y += Math.Sin(a) * r; }
            }
            list.Add((m, x, y));
        }
        return list;
    }

    public override void _Process(double delta)
    {
        _solid.ClearSurfaces();
        _lines.ClearSurfaces();
        _drawn.Clear();
        _boxView?.QueueRedraw();
        if (!_model.Active || _model.View == null || !_view.City.Loaded) { (_boxing, _press) = (false, null); return; }
        // A scene being commanded fills the middle with its own crews and vehicles.
        if (_model.Scene != null && !_model.SceneHidden) return;
        var placed = Placed();
        if (placed.Count == 0) return;
        double minute = Minute();
        bool flash = (Time.GetTicksMsec() / 220) % 2 == 0;
        _solid.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (m, x, y) in placed)
        {
            var at = Ground(x, y);
            var fwd = new Vector3((float)m.Dx, 0, (float)m.Dy);
            if (fwd.LengthSquared() < 1e-6f) fwd = Vector3.Right;
            fwd = fwd.Normalized();
            int k = (int)m.Unit.Kind % Body.Length;
            // Body, a stripe along each side, a darker cab window and a light bar that flashes while it drives.
            OBox(at, fwd, 0.38f, 0.2f, 0.22f, Body[k]);
            OBox(at + Vector3.Up * 0.09f, fwd, 0.385f, 0.205f, 0.045f, Stripe[k]);
            OBox(at + fwd * 0.22f + Vector3.Up * 0.22f, fwd, 0.1f, 0.17f, 0.06f, new Color("#1c2530"));
            OBox(at + fwd * 0.05f + Vector3.Up * 0.22f, fwd, 0.05f, 0.16f, 0.05f, m.Moving && flash ? new Color("#ff3030") : new Color("#3d8bff"));
            _drawn.Add((m.Unit.Id, at + Vector3.Up * 0.15f));
        }
        _solid.SurfaceEnd();

        _lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
        int w = _view.City.Width;
        foreach (var (m, x, y) in placed)
        {
            if (!m.Selected) continue;
            var at = Ground(x, y);
            Ring(at, 0.5f, Picked);
            // The rest of its route, then the places queued after it.
            var u = m.Unit;
            var cells = new List<int>();
            if (m.Moving && u.LegStart < 0) cells.AddRange(u.Leg.Skip(1));
            else if (m.Moving)
            {
                double t = Math.Clamp((minute - u.LegStart) / Math.Max(1, u.LegMinutes), 0, 1) * (u.Leg.Count - 1);
                cells.AddRange(u.Leg.Skip((int)t + 1));
            }
            cells.AddRange(u.Waypoints);
            var from = at;
            foreach (int c in cells)
            {
                var to = Ground(c % w + 0.5, c / w + 0.5);
                Line(from + Vector3.Up * 0.04f, to + Vector3.Up * 0.04f, Picked with { A = 0.8f });
                from = to;
            }
            foreach (int c in u.Waypoints) Ring(Ground(c % w + 0.5, c / w + 0.5), 0.2f, Picked);
        }
        _lines.SurfaceEnd();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Command

    /// <summary>The unit drawn nearest a screen point, within 20 pixels.</summary>
    int? UnitAt(Vector2 screen)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return null;
        int? best = null;
        float bestD = 20;
        foreach (var (id, at) in _drawn)
        {
            if (cam.IsPositionBehind(at)) continue;
            float d = cam.UnprojectPosition(at).DistanceTo(screen);
            if (d < bestD) (best, bestD) = (id, d);
        }
        return best;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_model.Directing) { (_boxing, _press, _rightFrom) = (false, null, null); return; }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } down:
                (_press, _boxTo, _boxing) = (down.Position, down.Position, false);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up when _press is { } from:
            {
                if (_boxing)
                {
                    var r = new Rect2(from, up.Position - from).Abs();
                    var cam = GetViewport().GetCamera3D();
                    _model.Map.Box(_drawn.Where(d => cam != null && !cam.IsPositionBehind(d.At) && r.HasPoint(cam.UnprojectPosition(d.At))).Select(d => d.Id), up.ShiftPressed);
                }
                else _model.Map.Click(UnitAt(up.Position), up.ShiftPressed);
                (_boxing, _press) = (false, null);
                _model.MapChanged();
                GetViewport().SetInputAsHandled();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rb:
                // The camera pans on a right drag; a right click without one sends the picked units.
                if (rb.Pressed) _rightFrom = rb.Position;
                else
                {
                    if (_rightFrom is { } rf && rf.DistanceTo(rb.Position) < 6 && _camera.PickPoint(rb.Position, _view) is { } p)
                    {
                        _model.Map.RightClick(p.X, p.Y, UnitAt(rb.Position), rb.ShiftPressed);
                        _model.MapChanged();
                    }
                    _rightFrom = null;
                }
                break;
            case InputEventMouseMotion m when _press is { } bf:
                _boxTo = m.Position;
                _boxing |= bf.DistanceTo(m.Position) > 6;
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Drawing

    void Line(Vector3 a, Vector3 b, Color c)
    {
        _lines.SurfaceSetColor(c); _lines.SurfaceAddVertex(a);
        _lines.SurfaceSetColor(c); _lines.SurfaceAddVertex(b);
    }

    void Ring(Vector3 at, float r, Color c)
    {
        const int n = 20;
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.Tau * i / n, a1 = Mathf.Tau * (i + 1) / n;
            Line(at + new Vector3(Mathf.Cos(a0) * r, 0.02f, Mathf.Sin(a0) * r), at + new Vector3(Mathf.Cos(a1) * r, 0.02f, Mathf.Sin(a1) * r), c);
        }
    }

    /// <summary>A box standing on a point, turned to face along <paramref name="fwd"/>: half its length and width, and its height.</summary>
    void OBox(Vector3 at, Vector3 fwd, float halfLen, float halfWid, float height, Color c)
    {
        var side = new Vector3(-fwd.Z, 0, fwd.X);
        Vector3 P(float f, float s, float h) => at + fwd * f * halfLen + side * s * halfWid + Vector3.Up * h * height;
        void Quad(Vector3 n, Vector3 a, Vector3 b, Vector3 cc, Vector3 dd)
        {
            foreach (var v in new[] { a, b, cc, a, cc, dd })
            {
                _solid.SurfaceSetNormal(n);
                _solid.SurfaceSetColor(c);
                _solid.SurfaceAddVertex(v);
            }
        }
        Quad(Vector3.Up, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1));
        Quad(fwd, P(1, -1, 0), P(1, 1, 0), P(1, 1, 1), P(1, -1, 1));
        Quad(-fwd, P(-1, 1, 0), P(-1, -1, 0), P(-1, -1, 1), P(-1, 1, 1));
        Quad(side, P(1, 1, 0), P(-1, 1, 0), P(-1, 1, 1), P(1, 1, 1));
        Quad(-side, P(-1, -1, 0), P(1, -1, 0), P(1, -1, 1), P(-1, -1, 1));
    }
}
