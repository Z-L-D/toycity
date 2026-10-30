using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;
using ToyCity.Emergency.Wire;

namespace ToyCity.Godot;

/// <summary>
/// An emergency scene in the 3D city, where it's happening: crews, suspects, onlookers and vehicles standing on the
/// city's own streets and roofs, the fire on the buildings, the paths everyone is on and the lines of fire. Command is
/// here too, Emergency-style: click a unit or drag a box over several to select, right-click to send them, pick an
/// order and click its target, or drag a path. Positions arrive twice a second and are eased between.
/// </summary>
public partial class SceneWorld : Node3D
{
    readonly EmergencyModel _model;
    readonly CityView _view;
    readonly ClassicCamera _camera;
    readonly ImmediateMesh _solid = new(), _lines = new();
    readonly Dictionary<int, Vector2> _shown = new();
    Vector2? _boxFrom, _rightFrom;
    Vector2 _boxTo;
    bool _boxing;
    int _scene;
    Control? _boxView;
    /// <summary>Whether the player is commanding: the dispatch screen is up with this scene on it.</summary>
    public bool Commanding { get; set; }
    /// <summary>The box being dragged, in screen pixels, for the panels to draw.</summary>
    public Rect2? DragBox => _boxing && _boxFrom is { } f ? new Rect2(f, _boxTo - f).Abs() : null;

    static readonly Color[] Roles = { new("#e04f3a"), new("#f2f2f2"), new("#3f7fe0"), new("#9aa0a8"), new("#f0c030"), new("#a050d0"), new("#f08ad0") };
    static readonly Color[] Vehicles = { new("#c0392b"), new("#2c5aa0"), new("#e8e8e8"), new("#20262e") };
    static readonly Color Selected = new("#ffe14d");

    public SceneWorld(EmergencyModel model, CityView view, ClassicCamera camera) => (_model, _view, _camera) = (model, view, camera);

    public override void _Ready()
    {
        // The selection box, drawn over everything while it's dragged.
        var layer = new CanvasLayer { Layer = 5 };
        AddChild(layer);
        var box = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.Draw += () =>
        {
            if (DragBox is not { } r) return;
            box.DrawRect(r, new Color(1, 0.88f, 0.3f, 0.12f));
            box.DrawRect(r, Selected, false, 1.5f);
        };
        layer.AddChild(box);
        _boxView = box;
        AddChild(new MeshInstance3D
        {
            Mesh = _solid, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
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

    // ---------------------------------------------------------------------------------------------------------
    // Where things are: scene tiles are a quarter of a city cell, and people's positions sixteenths of a tile.

    const int TilesPerCell = 4;
    const float PerTile = 1f / TilesPerCell;

    Vector3 World(SceneView s, float tx, float ty, float lift = 0)
    {
        float x = s.OriginX + tx * PerTile, z = s.OriginY + ty * PerTile;
        int cx = (int)Mathf.Floor(x), cy = (int)Mathf.Floor(z);
        // People stand on the ground, or on the roof of a building they're inside, so they can be seen and clicked.
        float y = _view.City.InBounds(cx, cy) ? (InBuilding(s, (int)tx, (int)ty) ? _view.Top(cx, cy) : _view.Surface(x, z)) : 0;
        return new Vector3(x, y + 0.02f + lift, z);
    }

    static bool InBuilding(SceneView s, int tx, int ty)
    {
        int i = ty * s.Width + tx;
        return tx >= 0 && ty >= 0 && tx < s.Width && ty < s.Height && i < s.Tiles.Length && s.Tiles[i] == 2;  // a building
    }

    /// <summary>The scene tile under a screen point, if it's on the scene.</summary>
    (int X, int Y)? TileAt(SceneView s, Vector2 screen)
    {
        if (_camera.PickPoint(screen, _view) is not { } p) return null;
        int tx = (int)Mathf.Floor((p.X + p.Fx - s.OriginX) * TilesPerCell), ty = (int)Mathf.Floor((p.Y + p.Fy - s.OriginY) * TilesPerCell);
        return tx < 0 || ty < 0 || tx >= s.Width || ty >= s.Height ? null : (tx, ty);
    }

    Vector2 Shown(int id, float x, float y)
    {
        var target = new Vector2(x, y);
        return _shown.TryGetValue(id, out var at) && at.DistanceTo(target) < 6 ? at : target;
    }

    /// <summary>Everyone and everything on screen, crews and vehicles first, as (id, screen point, crew or vehicle).</summary>
    IEnumerable<(int Id, Vector2 At, bool Crew)> OnScreen(SceneView s)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) yield break;
        foreach (var v in s.Vehicles)
        {
            var w = World(s, Shown(v.Id, v.X / 16f, v.Y / 16f).X, Shown(v.Id, v.X / 16f, v.Y / 16f).Y, 0.07f);
            if (!cam.IsPositionBehind(w)) yield return (v.Id, cam.UnprojectPosition(w), true);
        }
        foreach (var p in s.People)
        {
            if (p.State == "escaped") continue;
            var at = Shown(p.Id, p.X / 16f, p.Y / 16f);
            var w = World(s, at.X, at.Y, 0.1f);
            if (!cam.IsPositionBehind(w)) yield return (p.Id, cam.UnprojectPosition(w), (int)p.Role <= 2);
        }
    }

    /// <summary>Who is under a screen point: a crew member or vehicle if one is near, else anyone.</summary>
    int? HitAt(SceneView s, Vector2 screen)
    {
        var near = OnScreen(s).Select(o => (o.Id, o.Crew, D: o.At.DistanceTo(screen))).Where(o => o.D < 16).ToList();
        if (near.Count == 0) return null;
        return near.OrderBy(o => !o.Crew).ThenBy(o => o.D).First().Id;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Command

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Commanding || _model.Scene is not { } s || _model.SceneOver || !_model.Mine) { _boxing = false; _boxFrom = null; return; }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } down:
            {
                if (TileAt(s, down.Position) is not { } t) return;
                var hit = HitAt(s, down.Position);
                _model.PointerDown(t.X, t.Y, hit, down.ShiftPressed);
                // A press on open ground with Move picked may become a box.
                if (_model.Drawing == null && hit == null && _model.Current == EmergencyModel.Order.Move) (_boxFrom, _boxTo, _boxing) = (down.Position, down.Position, false);
                GetViewport().SetInputAsHandled();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up:
                if (_boxing && _boxFrom is { } f)
                {
                    var r = new Rect2(f, up.Position - f).Abs();
                    _model.SelectBox(OnScreen(s).Where(o => o.Crew && r.HasPoint(o.At)).Select(o => o.Id), up.ShiftPressed);
                }
                else _model.PointerUp();
                (_boxing, _boxFrom) = (false, null);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rb:
                // The camera pans on a right drag; a right click without a drag sends the selected.
                if (rb.Pressed) _rightFrom = rb.Position;
                else if (_rightFrom is { } from && from.DistanceTo(rb.Position) < 6 && TileAt(s, rb.Position) is { } to) _model.MoveTo(to.X, to.Y);
                if (!rb.Pressed) _rightFrom = null;
                break;
            case InputEventMouseMotion m:
                if (_boxFrom is { } bf)
                {
                    _boxTo = m.Position;
                    _boxing |= bf.DistanceTo(m.Position) > 6;
                }
                else if (_model.Drawing != null && TileAt(s, m.Position) is { } dt) _model.PointerMove(dt.X, dt.Y);
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Drawing

    public override void _Process(double delta)
    {
        _solid.ClearSurfaces();
        _lines.ClearSurfaces();
        if (_model.Scene is not { } s || !_view.City.Loaded) { (_scene, _boxing) = (0, false); _shown.Clear(); _boxView?.QueueRedraw(); return; }
        if (s.Id != _scene)
        {
            // A scene coming up on the dispatch screen: the camera goes to it.
            _shown.Clear();
            _scene = s.Id;
            _camera.CentreOn(s.OriginX + s.Width * PerTile / 2, s.OriginY + s.Height * PerTile / 2);
        }
        _boxView?.QueueRedraw();
        float k = Mathf.Min(1, (float)delta * 5);
        void Ease(int id, float x, float y) => _shown[id] = _shown.TryGetValue(id, out var at) && at.DistanceTo(new Vector2(x, y)) < 6 ? at.Lerp(new Vector2(x, y), k) : new Vector2(x, y);
        foreach (var v in s.Vehicles) Ease(v.Id, v.X / 16f, v.Y / 16f);
        foreach (var p in s.People) Ease(p.Id, p.X / 16f, p.Y / 16f);

        _solid.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        // The fire: a glow on each burning tile.
        var fire = s.Fire.Span;
        for (int i = 0; i < fire.Length && i < s.Width * s.Height; i++)
            if (fire[i] > 0)
            {
                int tx = i % s.Width, ty = i / s.Width;
                float h = 0.05f + fire[i] / 400f;
                Box(World(s, tx + 0.5f, ty + 0.5f), new Vector3(PerTile * 0.45f, h, PerTile * 0.45f), new Color(1, Mathf.Max(0.15f, 0.65f - fire[i] / 300f), 0.05f));
            }
        foreach (var v in s.Vehicles)
        {
            var at = _shown[v.Id];
            Box(World(s, at.X, at.Y), new Vector3(0.16f, 0.07f, 0.1f), Vehicles[(int)v.Kind % Vehicles.Length]);
            Box(World(s, at.X, at.Y, 0.07f), new Vector3(0.08f, 0.03f, 0.08f), new Color(1, 1, 1, 1) * 0.85f);
        }
        foreach (var p in s.People)
        {
            if (p.State == "escaped") continue;
            var at = World(s, _shown[p.Id].X, _shown[p.Id].Y);
            var c = Roles[(int)p.Role % Roles.Length];
            if (p.State == "dead") c = c.Darkened(0.7f);
            // Down: lying flat. Standing: a figure with a darker head.
            if (p.Down) Box(at, new Vector3(0.09f, 0.025f, 0.04f), c);
            else
            {
                Box(at, new Vector3(0.035f, 0.11f, 0.035f), c);
                Box(at + new Vector3(0, 0.11f, 0), new Vector3(0.025f, 0.025f, 0.025f), p.Armed ? Colors.Black : c.Darkened(0.35f));
            }
        }
        _solid.SurfaceEnd();

        _lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
        // The scene's edge, so it reads as a place in the city.
        var corners = new[] { World(s, 0, 0), World(s, s.Width, 0), World(s, s.Width, s.Height), World(s, 0, s.Height) };
        for (int i = 0; i < 4; i++) Line(corners[i] + Vector3.Up * 0.03f, corners[(i + 1) % 4] + Vector3.Up * 0.03f, new Color(1, 1, 1, 0.45f));
        void Path(IList<int> cells, Color c)
        {
            for (int i = 1; i < cells.Count; i++)
                Line(World(s, cells[i - 1] % s.Width + 0.5f, cells[i - 1] / s.Width + 0.5f, 0.03f), World(s, cells[i] % s.Width + 0.5f, cells[i] / s.Width + 0.5f, 0.03f), c);
        }
        foreach (var v in s.Vehicles)
        {
            Path(v.Path, new Color(1, 1, 1, 0.6f));
            if (_model.Selected.Contains(v.Id)) Ring(World(s, _shown[v.Id].X, _shown[v.Id].Y), 0.14f, Selected);
        }
        foreach (var p in s.People)
        {
            if (p.State == "escaped") continue;
            var at = World(s, _shown[p.Id].X, _shown[p.Id].Y);
            if ((int)p.Role <= 2) Path(p.Path, new Color(1, 0.92f, 0.23f, 0.5f));
            if (_model.Selected.Contains(p.Id)) Ring(at, 0.07f, Selected);
            // A health bar over anyone hurt.
            if (p.Health < 100 && p.State != "dead")
            {
                var top = at + Vector3.Up * 0.18f;
                Line(top - new Vector3(0.05f, 0, 0), top + new Vector3(0.05f, 0, 0), new Color("#300"));
                Line(top - new Vector3(0.05f, 0, 0), top + new Vector3(-0.05f + 0.1f * p.Health / 100f, 0, 0), new Color("#4caf50"));
            }
        }
        // Lines of fire: police blue, the suspect's red, a taser's yellow, fading over a few seconds.
        foreach (var shot in s.Shots)
        {
            float age = (s.Step - shot.Step) / 12f;
            if (age >= 1) continue;
            var c = shot.Taser ? new Color("#ffd23a") : shot.Police ? new Color("#5aa0ff") : new Color("#ff3b30");
            c.A = 1 - age;
            Line(World(s, shot.FromX / 16f, shot.FromY / 16f, 0.09f), World(s, shot.ToX / 16f, shot.ToY / 16f, 0.09f), c);
            if (shot.Hit) Ring(World(s, shot.ToX / 16f, shot.ToY / 16f, 0.09f), 0.03f, c);
        }
        if (_model.Drawing is { } d) Path(d, Selected);
        _lines.SurfaceEnd();
    }

    void Line(Vector3 a, Vector3 b, Color c)
    {
        _lines.SurfaceSetColor(c); _lines.SurfaceAddVertex(a);
        _lines.SurfaceSetColor(c); _lines.SurfaceAddVertex(b);
    }

    void Ring(Vector3 at, float r, Color c)
    {
        const int n = 16;
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.Tau * i / n, a1 = Mathf.Tau * (i + 1) / n;
            Line(at + new Vector3(Mathf.Cos(a0) * r, 0.01f, Mathf.Sin(a0) * r), at + new Vector3(Mathf.Cos(a1) * r, 0.01f, Mathf.Sin(a1) * r), c);
        }
    }

    /// <summary>A box standing on a point: half its width and depth, and its full height.</summary>
    void Box(Vector3 at, Vector3 half, Color c)
    {
        var lo = at - new Vector3(half.X, 0, half.Z);
        var hi = at + new Vector3(half.X, half.Y, half.Z);
        void Quad(Vector3 n, Vector3 a, Vector3 b, Vector3 cc, Vector3 dd)
        {
            foreach (var v in new[] { a, b, cc, a, cc, dd })
            {
                _solid.SurfaceSetNormal(n);
                _solid.SurfaceSetColor(c);
                _solid.SurfaceAddVertex(v);
            }
        }
        Quad(Vector3.Up, new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z));
        Quad(Vector3.Back, new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z));
        Quad(Vector3.Forward, new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(lo.X, lo.Y, lo.Z));
        Quad(Vector3.Right, new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z));
        Quad(Vector3.Left, new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, lo.Y, hi.Z));
    }
}
