using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// The client's root: connects to the server, keeps the city mirror current and hands each frame's changes to the
/// 3D view and the minimap. Command line (after "--"): --server ws://host:port/ws; --smoke to connect, check the
/// city loads and picking works at every rotation, and quit; --shots DIR to also save a screenshot of each view.
/// </summary>
public partial class Main : Node
{
    CityClient? _client;
    CityView? _view;
    ClassicCamera? _camera;
    MapView? _map;
    Label? _status, _hover;
    bool _smoke, _centred;
    string? _shots;
    double _smokeTime;

    const string Hints = ", .  rotate     + −  wheel  zoom     T  top-down     Y  side view\n"
        + "WASD  arrows  right-drag  screen edge  pan     click the minimap to jump";

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        string server = "ws://localhost:5180/ws";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            if (args[i] == "--smoke") _smoke = true;
            if (args[i] == "--shots" && i + 1 < args.Length) { _shots = args[++i]; _smoke = true; }
        }
        _client = new CityClient(new Uri(server));
        _client.Connection.StatusChanged += open => SetStatus(open ? "Connected" : $"Connecting to {server}…");

        AddChild(new WorldEnvironment
        {
            Environment = new global::Godot.Environment
            {
                BackgroundMode = global::Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.70f, 0.85f),
                AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.75f, 0.78f, 0.85f),
                AmbientLightEnergy = 0.55f,
            },
        });
        // Afternoon sun from the south-west, fixed to the map so shading turns with the camera as in SC4.
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, -150, 0), LightEnergy = 1.1f, ShadowEnabled = true });

        _view = new CityView(_client.City);
        AddChild(_view);
        _camera = new ClassicCamera { EdgePan = !_smoke };
        AddChild(_camera);

        var hud = new CanvasLayer();
        AddChild(hud);
        _status = new Label { Text = $"Connecting to {server}…", Position = new Vector2(12, 8) };
        _hover = new Label { Position = new Vector2(12, 32) };
        var hints = new Label { Text = Hints, Modulate = new Color(1, 1, 1, 0.8f) };
        hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 12);
        hints.GrowVertical = Control.GrowDirection.Begin;
        foreach (var l in new[] { _status, _hover, hints })
        {
            l.AddThemeColorOverride("font_outline_color", Colors.Black);
            l.AddThemeConstantOverride("outline_size", 4);
            hud.AddChild(l);
        }
        _map = new MapView(_client.City);
        hud.AddChild(_map);
    }

    void SetStatus(string text) { if (_status != null) _status.Text = text; }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b && _map!.CellAt(b.Position) is { } c)
        {
            _camera!.CentreOn(c.X + 0.5f, c.Y + 0.5f);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        _client!.Poll();
        var c = _client.City;
        if (c.Loaded)
        {
            var (all, cells, buildings) = c.TakeDirty();
            if (all || cells.Length > 0 || buildings.Length > 0)
            {
                _view!.Refresh(all, cells, buildings);
                _map!.Refresh(all, cells);
            }
            if (!_centred)
            {
                _camera!.MapWidth = c.Width;
                _camera.MapHeight = c.Height;
                _camera.GroundAt = (x, y) => c.InBounds((int)x, (int)y) ? _view!.Ground(c.Idx((int)x, (int)y)) : 0;
                // Start over the town, not the middle of the map.
                var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
                if (built.Count > 0) _camera.CentreOn(built.Average(b => b.X + b.Width / 2f), built.Average(b => b.Y + b.Height / 2f));
                else _camera.CentreOn(c.Width / 2f, c.Height / 2f);
                _centred = true;
            }
            SetStatus($"{c.Stats.CityName}  ·  {c.Stats.Year}-{c.Stats.Month:00}-{c.Stats.Day:00}  ·  pop {c.Stats.Population:N0}  ·  ${c.Stats.Funds:N0}");
            var cell = _camera!.Pick(GetViewport().GetMousePosition(), _view!);
            _view!.Hover(cell);
            _hover!.Text = cell is { } h ? Describe(c, h.X, h.Y) : "";
            _map!.Marker = _camera.View.Target;
        }
        if (_smoke) Smoke(c, delta);
    }

    /// <summary>What's under the cursor: a building, a street, or the ground.</summary>
    static string Describe(CityMirror c, int x, int y)
    {
        int i = c.Idx(x, y);
        string at = $"({x}, {y})";
        if (c.BuildingAtCell(x, y) is { } b && c.TypeById.TryGetValue(b.TypeId, out var t))
            return $"{(string.IsNullOrEmpty(b.Name) ? t.Name : $"{b.Name} · {t.Name}")}  {at}";
        if (c.Paths[i] != 0)
        {
            var stops = c.StopsAt(x, y).Select(s => c.TypeById.TryGetValue(s.TypeId, out var st) ? st.Name : "stop").ToList();
            return $"{c.StreetAt(x, y)}{(stops.Count > 0 ? " · " + string.Join(", ", stops) : "")}  {at}";
        }
        return c.IsWater(i) ? $"Water  {at}" : $"Height {c.Heights[i] - c.WaterLevel}  {at}";
    }

    int _shot = -1, _wait;
    static readonly (int Rotation, int Zoom, ViewMode Mode)[] ShotViews =
        { (0, 1, ViewMode.Classic), (1, 1, ViewMode.Classic), (2, 1, ViewMode.Classic), (3, 1, ViewMode.Classic),
          (0, 0, ViewMode.Classic), (0, 3, ViewMode.Classic), (0, 1, ViewMode.TopDown), (0, 1, ViewMode.Side) };

    /// <summary>The smoke test: once the city is drawn, picking at the screen centre must find it at every rotation.</summary>
    void Smoke(CityMirror c, double delta)
    {
        _smokeTime += delta;
        if (_smokeTime > 60) { GD.PrintErr("SMOKE FAILED: no city within 60 s"); GetTree().Quit(1); return; }
        if (!c.Loaded || !_map!.Drawn || !_centred) return;
        var v = _camera!.View;
        var centre = GetViewport().GetVisibleRect().Size / 2;
        if (_shot < 0)
        {
            for (int r = 0; r < 4; r++)
            {
                v.Rotation = r;
                var t = v.Target;
                if (_camera.Pick(centre, _view!) is not { } p || Math.Abs(p.X - t.X) > 8 || Math.Abs(p.Y - t.Y) > 8)
                {
                    GD.PrintErr($"SMOKE FAILED: rotation {r} picked {_camera.Pick(centre, _view!)?.ToString() ?? "nothing"} at the centre");
                    GetTree().Quit(1);
                    return;
                }
            }
            v.Rotation = 0;
            GD.Print($"SMOKE OK {c.Width}x{c.Height} buildings={c.Buildings.Count} tick={c.Stats.Tick} picking=4/4");
            if (_shots == null || DisplayServer.GetName() == "headless") { GetTree().Quit(0); return; }
            DirAccess.MakeDirRecursiveAbsolute(_shots);
            _shot = 0;
        }
        // One view per few frames: set it, let it draw, save it.
        if (_wait-- > 0) return;
        if (_shot > 0)
        {
            var (pr, pz, pm) = ShotViews[_shot - 1];
            string file = $"{_shots}/view-{_shot - 1}-{pm.ToString().ToLowerInvariant()}-r{pr}-z{pz}.png";
            GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"SHOT {file}");
        }
        if (_shot == ShotViews.Length) { GetTree().Quit(0); return; }
        (v.Rotation, v.Zoom, v.Mode) = ShotViews[_shot++];
        _wait = 5;
    }

    public override void _ExitTree() => _client?.Dispose();
}
