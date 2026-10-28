using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;

namespace ToyCity.Godot;

/// <summary>
/// The client's root: connects to the server, keeps the city mirror current and hands each frame's changes to the
/// 3D view and the minimap. Command line (after "--"): --server ws://host:port/ws; --smoke to connect, check the
/// city loads and picking works at every rotation, and quit; --shots DIR to also save a screenshot of each view;
/// --bench FILE to swap in the dense benchmark city and write frame times and memory per zoom level to FILE.
/// </summary>
public partial class Main : Node
{
    CityClient? _client;
    CityView? _view;
    ClassicCamera? _camera;
    MapView? _map;
    ToolController? _tools;
    PanelView? _panels;
    AgentsView? _agents;
    StreetLabels? _streets;
    int _overlayVersion = -1;
    bool _under;
    long? _followed;
    double _bannerClock;
    long _savedReport = -1;
    const string ReportFile = "user://seen-report.txt";
    DirectionalLight3D? _sun;
    DisasterView? _disasters;
    Label? _status, _hover;
    bool _smoke, _centred;
    string? _shots, _bench;
    bool _frozen;  // the bench city is drawn from a fixed state; the server's deltas would undo it
    double _smokeTime;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        string server = "ws://localhost:5180/ws";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            if (args[i] == "--smoke") _smoke = true;
            if (args[i] == "--shots" && i + 1 < args.Length) { _shots = args[++i]; _smoke = true; }
            if (args[i] == "--bench" && i + 1 < args.Length) _bench = args[++i];
        }
        _client = new CityClient(new Uri(server));
        if (_bench != null)
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            _client.Message += m =>
            {
                if (_frozen || m.BodyCase != Protocol.V0.ServerMessage.BodyOneofCase.Full) return;
                _client.City.ApplyFull(DenseCity.Make(m.Full));
                _frozen = true;
            };
        }
        _client.Connection.StatusChanged += open => SetStatus(open ? "" : $"Connecting to {server}…");

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
        AddChild(_sun = new DirectionalLight3D { RotationDegrees = new Vector3(-50, -150, 0), LightEnergy = 1.1f, ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal });

        _view = new CityView(_client.City);
        AddChild(_view);
        _disasters = new DisasterView(_client.City);
        AddChild(_disasters);
        _camera = new ClassicCamera { EdgePan = !_smoke && _bench == null };
        AddChild(_camera);

        var hud = new CanvasLayer();
        AddChild(hud);
        // Below the toolbar, which runs across the top.
        _status = new Label { Text = $"Connecting to {server}…", Position = new Vector2(12, 84) };
        _hover = new Label { Position = new Vector2(12, 108) };
        foreach (var l in new[] { _status, _hover })
        {
            l.AddThemeColorOverride("font_outline_color", Colors.Black);
            l.AddThemeConstantOverride("outline_size", 4);
            hud.AddChild(l);
        }
        _map = new MapView(_client.City);
        hud.AddChild(_map);
        _tools = new ToolController(_client, _view, _camera, _map);
        AddChild(_tools);

        // The panels, and what they ask of the map.
        _panels = new PanelView(_client) { Tools = _tools };
        AddChild(_panels);
        var pm = _panels.Model;
        if (global::Godot.FileAccess.FileExists(ReportFile) && long.TryParse(global::Godot.FileAccess.GetFileAsString(ReportFile).Trim(), out var seen)) pm.ShownReport = _savedReport = seen;
        _client.Message += pm.Handle;
        pm.JumpTo += (x, y) => _camera.CentreOn(x + 0.5f, y + 0.5f);
        pm.Flash += r => _view.Flash(r.X0, r.Y0, r.X1, r.Y1);
        pm.PickBuilding += (id, x, y) => { _tools.Pick(id); if (x >= 0) _camera.CentreOn(x + 0.5f, y + 0.5f); };
        pm.Toast += _tools.Toast;
        pm.UnlocksChanged += _tools.RebuildOptions;
        _tools.Queried += (x, y) => pm.Query(x, y);
        _tools.Act = pm.Act;
        _camera.Panned += () => _panels.FollowCam = false;
        _agents = new AgentsView(_client.City, pm, _view);
        AddChild(_agents);
        _streets = new StreetLabels(_client.City, _view);
        AddChild(_streets);
    }

    void SetStatus(string text) { if (_status != null) _status.Text = text; }

    public override void _Process(double delta)
    {
        if (!_frozen) _client!.Poll();
        var c = _client!.City;
        if (c.Loaded)
        {
            var (all, cells, buildings) = c.TakeDirty();
            if (all || cells.Length > 0 || buildings.Length > 0)
            {
                _view!.Refresh(all, cells, buildings);
                _map!.Refresh(all, cells);
            }
            Layers(c, all || cells.Length > 0);
            _view!.Tick(delta, c.TakeRemoved());
            _camera!.Shake = _disasters!.Shake;
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
            var cell = _camera!.Pick(GetViewport().GetMousePosition(), _view!);
            _view!.Hover(cell);
            _hover!.Text = cell is { } h ? Describe(c, h.X, h.Y) : "";
            _map!.Marker = _camera.View.Target;
            _view.SetFar(_camera.View.Zoom <= 2);
            // Shadows only as far as the view reaches, so close zoom levels don't cast the whole city.
            _sun!.DirectionalShadowMaxDistance = _camera.View.Distance + _camera.View.Size * 1.5f;
        }
        if (_bench != null) Bench(c, delta);
        else if (_smoke) Smoke(c, delta);
    }

    /// <summary>The map layers the panel asks for, street names, the selected cell, and following a commuter.</summary>
    void Layers(CityMirror c, bool cellsChanged)
    {
        var pm = _panels!.Model;
        _view!.Select(pm.Selected);
        // The pipes show while the layer is on, or while the bulldozer clears pipes.
        bool under = pm.ShowUnderground || _tools!.State is { Kind: ToolKind.Bulldoze, Underground: true };
        if (under != _under || pm.OverlayVersion != _overlayVersion || (under && cellsChanged))
        {
            (_under, _overlayVersion) = (under, pm.OverlayVersion);
            Image? image = null;
            if (under) image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, MapLayer.Pipes(c));
            else if (pm.Overlay is { } o && pm.ActiveOverlay != 0 && o.Values.Length == c.Width * c.Height)
            {
                string scheme = PanelModel.OverlayKinds.First(k => k.Kind == pm.ActiveOverlay).Scheme;
                image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, MapLayer.Pixels(o.Values.Span, scheme));
            }
            _view.SetOverlay(image);
        }
        _streets!.Refresh(GetProcessDeltaTime());
        _streets.Visible = _camera!.View.Zoom >= 3;
        // A newly followed commuter takes the camera, until the player pans away.
        if (pm.Followed != _followed) { _followed = pm.Followed; _panels.FollowCam = _followed != null; if (_followed != null) _camera.View.Zoom = Math.Max(_camera.View.Zoom, 3); }
        if (_agents!.FollowedAt is { } f)
        {
            if (_panels.FollowCam) _camera.CentreOn(f.X, f.Y);
            if ((_bannerClock += GetProcessDeltaTime()) > 0.2) { _bannerClock = 0; _panels.FollowStatus(_agents.FollowedStatus, _agents.Minute); }
        }
        if (pm.ShownReport != _savedReport)
        {
            _savedReport = pm.ShownReport;
            using var file = global::Godot.FileAccess.Open(ReportFile, global::Godot.FileAccess.ModeFlags.Write);
            file?.StoreString(_savedReport.ToString());
        }
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
          (0, 0, ViewMode.Classic), (0, 3, ViewMode.Classic), (0, 1, ViewMode.TopDown), (0, 1, ViewMode.Side),
          (0, 2, ViewMode.Classic), (0, 2, ViewMode.Classic),  // the zone and road tools' previews
          (0, 3, ViewMode.Classic) };  // a building's Why panel over the land value layer, with street names

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
        int tx = (int)v.Target.X, ty = (int)v.Target.Y;
        if (_shot == ShotViews.Length - 2) { _tools!.SetTool(ToolKind.Zone); _tools.Hold((tx + 4, ty + 3), (tx - 4, ty - 3)); }
        else if (_shot == ShotViews.Length - 1) { _tools!.SetTool(ToolKind.Path); _tools.Hold((tx + 6, ty), (tx - 6, ty)); }
        else if (_shot == ShotViews.Length)
        {
            _tools!.SetTool(ToolKind.Query);
            _tools.Hold(null);
            var pm = _panels!.Model;
            if (c.Buildings.Values.FirstOrDefault(b => c.TypeById.TryGetValue(b.TypeId, out var t) && t.Placeable && !t.Roadside) is { } b)
            {
                pm.Query(b.X, b.Y);
                _camera.CentreOn(b.X + 0.5f, b.Y + 0.5f);
            }
            pm.Click("overlay:1");
            _wait = 30;  // for the server's replies
        }
        _wait = 5;
    }

    int _benchZoom = -1, _benchFrame;
    double _benchClock;
    readonly List<double> _times = new();
    readonly List<string> _benchRows = new();

    /// <summary>
    /// The frame-budget run: the dense city at each zoom level, 30 frames to settle and up to 240 measured, with the
    /// draw calls, triangles and memory each costs. The map is centred and the classic angle used throughout.
    /// </summary>
    void Bench(CityMirror c, double delta)
    {
        _smokeTime += delta;
        if (_smokeTime > 600) { GD.PrintErr("BENCH FAILED: timed out"); GetTree().Quit(1); return; }
        if (!_frozen || !_map!.Drawn) return;
        var v = _camera!.View;
        if (_benchZoom < 0)
        {
            _camera.CentreOn(c.Width / 2f, c.Height / 2f);
            _benchZoom = 0;
            _benchFrame = 0;
            v.Zoom = 0;
            return;
        }
        // Settle for 30 frames, then measure 240 frames or 8 seconds, whichever comes first (slow machines and CI).
        _benchFrame++;
        if (_benchFrame <= 30) { _benchClock = 0; return; }
        _times.Add(delta * 1000);
        _benchClock += delta;
        if (_times.Count < 240 && _benchClock < 8) return;
        _times.Sort();
        double avg = _times.Average(), p95 = _times[(int)(_times.Count * 0.95)], worst = _times[^1];
        long tris = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        long calls = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        long objects = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double video = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0;
        double engine = OS.GetStaticMemoryUsage() / 1048576.0, managed = GC.GetTotalMemory(false) / 1048576.0;
        double process = System.Environment.WorkingSet / 1048576.0;
        string row = $"{{\"zoom\":{v.Zoom},\"lod\":\"{(v.Zoom <= 2 ? "far" : "near")}\",\"avgMs\":{avg:F2},\"p95Ms\":{p95:F2},\"worstMs\":{worst:F2},\"fps\":{1000 / avg:F1},"
            + $"\"triangles\":{tris},\"drawCalls\":{calls},\"objects\":{objects},\"videoMB\":{video:F0},\"engineMB\":{engine:F0},\"managedMB\":{managed:F0},\"processMB\":{process:F0}}}";
        _benchRows.Add(row);
        GD.Print($"BENCH zoom {v.Zoom}: {avg:F2} ms avg ({1000 / avg:F0} fps), p95 {p95:F2} ms, {tris:N0} triangles, {calls} draw calls, process {process:F0} MB, video {video:F0} MB");
        if (_shots != null && DisplayServer.GetName() != "headless")
        {
            DirAccess.MakeDirRecursiveAbsolute(_shots);
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/bench-z{v.Zoom}.png");
        }
        _times.Clear();
        _benchFrame = 0;
        if (v.Zoom < ClassicView.ZoomSizes.Length - 1) { v.Zoom++; return; }
        string json = $"{{\"city\":\"{c.Width}x{c.Height}\",\"buildings\":{c.Buildings.Count},\"models\":{_view!.Models},\"batches\":{_view.Batches},"
            + $"\"renderer\":\"{RenderingServer.GetVideoAdapterName()} / {RenderingServer.GetCurrentRenderingDriverName()}\",\"levels\":[{string.Join(",", _benchRows)}]}}";
        using (var f = global::Godot.FileAccess.Open(_bench, global::Godot.FileAccess.ModeFlags.Write)) f.StoreString(json);
        GD.Print($"BENCH wrote {_bench}");
        GetTree().Quit(0);
    }

    public override void _ExitTree() => _client?.Dispose();
}
