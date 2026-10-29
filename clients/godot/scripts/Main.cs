using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;

namespace ToyCity.Godot;

/// <summary>
/// The client's root: connects to the server, keeps the city mirror current and hands each frame's changes to the
/// 3D view and the minimap. Command line (after "--"): --server ws://host:port/ws; --smoke to connect, check the
/// city loads and picking works at every rotation, and quit; --shots DIR to also save a screenshot of each view;
/// --bench FILE to swap in the dense benchmark city and write frame times and memory per zoom level to FILE;
/// --gate FILE to found the server's benchmark city and play the Alpha 3 loop on it, measuring as it goes;
/// --graphics PRESET (Low, Medium, High, Ultra) to use a preset for this run instead of the saved options;
/// --user NAME --password PASSWORD to sign in to a dedicated server without the sign-in panel (with --new-account,
/// making the account first).
/// </summary>
public partial class Main : Node
{
    CityClient? _client;
    CityView? _view;
    ClassicCamera? _camera;
    MapView? _map;
    ToolController? _tools;
    PanelView? _panels;
    FreeCamera? _free;
    AgentsView? _agents;
    StreetLabels? _streets;
    int _overlayVersion = -1;
    bool _under;
    bool _company;  // open in the bus company's view (--company)
    int _companyMap = -1;
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
        string server = global::Godot.FileAccess.FileExists(SignInPanel.ServerFile) && global::Godot.FileAccess.GetFileAsString(SignInPanel.ServerFile).Trim() is { Length: > 0 } last
            ? last : "ws://localhost:5180/ws";
        string? user = null, password = null;
        bool newAccount = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            if (args[i] == "--smoke") _smoke = true;
            if (args[i] == "--shots" && i + 1 < args.Length) { _shots = args[++i]; _smoke = true; }
            if (args[i] == "--bench" && i + 1 < args.Length) _bench = args[++i];
            if (args[i] == "--company") _company = true;
            if (args[i] == "--gate" && i + 1 < args.Length) _gateFile = args[++i];
            if (args[i] == "--graphics" && i + 1 < args.Length) _graphicsPreset = args[++i];
            if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
            if (args[i] == "--password" && i + 1 < args.Length) password = args[++i];
            if (args[i] == "--new-account") newAccount = true;
        }
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) uri = new Uri(server = "ws://localhost:5180/ws");
        var credentials = user != null && password != null ? new Protocol.V0.SignIn { Name = user, Password = password, Create = newAccount } : SignInPanel.StoredCredentials(server);
        _client = new CityClient(uri, credentials: credentials);
        if (_bench != null || _gateFile != null)
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        }
        if (_bench != null)
        {
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
        _camera = new ClassicCamera { EdgePan = !_smoke && _bench == null && _gateFile == null };
        AddChild(_camera);

        var hud = new CanvasLayer();
        AddChild(hud);
        // Below the toolbar, which runs across the top.
        _status = new Label { Text = $"Connecting to {server}…", Position = new Vector2(12, 84) };
        _hover = new Label { Position = new Vector2(12, 108) };
        var players = new Label { Position = new Vector2(12, 132), Modulate = new Color(1, 1, 1, 0.85f) };
        _client.Message += m => { if (m.BodyCase == Protocol.V0.ServerMessage.BodyOneofCase.Presence) players.Text = PresenceText(m.Presence); };
        foreach (var l in new[] { _status, _hover, players })
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
        var signIn = new SignInPanel(_client, server);
        signIn.Reconnect += address =>
        {
            // Start again pointed at the new server, as the company's own window is started.
            if (!address.Contains("://")) address = $"ws://{address}{(address.Contains(':') ? "" : ":5180")}/ws";
            using (var f = global::Godot.FileAccess.Open(SignInPanel.ServerFile, global::Godot.FileAccess.ModeFlags.Write)) f?.StoreString(address);
            OS.CreateProcess(OS.GetExecutablePath(), OS.GetCmdlineArgs().TakeWhile(a => a != "--").Concat(new[] { "--", "--server", address }).ToArray());
            GetTree().Quit();
        };
        if (!_smoke && _bench == null && _gateFile == null) AddChild(signIn);

        _panels = new PanelView(_client) { Tools = _tools, Camera = _camera, City = _view, Map = _map };
        AddChild(_panels);
        var pm = _panels.Model;
        pm.Me = () => _client.Connection.Player is { Length: > 0 } me ? me : "player";
        pm.OnServer = () => _client.Connection.Welcome?.SignInRequired == true;
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
        var buses = _buses = new BusLinesView(_client.City, pm, _view);
        AddChild(buses);
        _free = new FreeCamera
        {
            // What there is to ride along with: the followed commuter, then each running bus.
            Rides = () =>
            {
                var list = new List<(string, Vector3, Vector3)>();
                if (_agents.FollowedAt is { } f)
                    list.Add(($"{pm.FollowedName}", new Vector3(f.X, _agents.Height(f.X, f.Y), f.Y), new Vector3(_agents.FollowedDir.X, 0, _agents.FollowedDir.Y)));
                for (int k = 0; k < buses.Buses.Count; k++) list.Add(($"{buses.Buses[k].Line} bus", buses.Buses[k].Pos, buses.Buses[k].Dir));
                return list;
            },
        };
        AddChild(_free);
        _camera.FreeCam = _free;
        // The company's view in a window of its own: a second client, as the web client opens a second tab.
        LoadGraphics();
        _panels.GraphicsPressed += _graphicsPanel!.Toggle;
        ApplyGraphics();
        pm.Company.OwnWindow += () => OS.CreateProcess(OS.GetExecutablePath(),
            OS.GetCmdlineArgs().TakeWhile(a => a != "--").Concat(new[] { "--", "--server", server, "--company" }).ToArray());
    }

    void SetStatus(string text) { if (_status != null) _status.Text = text; }

    /// <summary>Who's here, under the status line: shown once there's anyone but this player.</summary>
    string PresenceText(Protocol.V0.Presence p)
    {
        if (p.Players.Count < 2) return "";
        var civic = _panels?.Model.Civic;
        string Seat(string id) => id == "mayor" ? "mayor" : civic?.Seats.FirstOrDefault(s => s.Id == id)?.Name ?? id;
        return "Here: " + string.Join("   ", p.Players.Select(pl =>
            $"{pl.Name}{(pl.Name == _client!.Connection.Player ? " (you)" : "")} · {(pl.Spectator ? "watching" : string.Join(", ", pl.Seats.Select(Seat)))}{(pl.Online ? "" : " · away")}"));
    }

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
            if (_centred && _camera!.MapWidth != c.Width) _centred = false;  // a new city of another size
            if (!_centred)
            {
                _camera!.MapWidth = c.Width;
                _camera.MapHeight = c.Height;
                _camera.GroundAt = (x, y) => c.InBounds((int)x, (int)y) ? _view!.Ground(c.Idx((int)x, (int)y)) : 0;
                (_free!.MapWidth, _free.MapHeight, _free.GroundAt) = (c.Width, c.Height, _camera.GroundAt);
                // Start over the town, not the middle of the map.
                var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
                if (built.Count > 0) _camera.CentreOn(built.Average(b => b.X + b.Width / 2f), built.Average(b => b.Y + b.Height / 2f));
                else _camera.CentreOn(c.Width / 2f, c.Height / 2f);
                _centred = true;
            }
            var cell = _camera!.Pick(GetViewport().GetMousePosition(), _view!);
            _view!.Hover(cell);
            _hover!.Text = cell is { } h ? Describe(c, h.X, h.Y) : "";
            _map!.Marker = _camera.IsFree ? new System.Numerics.Vector2(_free!.Target.X, _free.Target.Z) : _camera.View.Target;
            UpdateDetail();
        }
        if (_gateFile != null)
        {
            if (_gate == null) GateStart();
            Gate(delta);
        }
        else if (_bench != null) Bench(c, delta);
        else if (_smoke) Smoke(c, delta);
    }

    /// <summary>The map layers the panel asks for, street names, the selected cell, and following a commuter.</summary>
    void Layers(CityMirror c, bool cellsChanged)
    {
        var pm = _panels!.Model;
        _view!.Select(pm.Selected);
        // The pipes show while the layer is on, or while the bulldozer clears pipes.
        if (_company && pm.Company.View != null) { _company = false; pm.Company.SetActive(true); }
        bool under = pm.ShowUnderground || _tools!.State is { Kind: ToolKind.Bulldoze, Underground: true };
        int companyMap = pm.Company.Active ? pm.Company.MapVersion : -1;
        if (under != _under || pm.OverlayVersion != _overlayVersion || companyMap != _companyMap || ((under || companyMap >= 0) && cellsChanged))
        {
            (_under, _overlayVersion, _companyMap) = (under, pm.OverlayVersion, companyMap);
            Image? image = null;
            // The company's view dims the city and marks its lots; otherwise the pipes, or the panel's layer.
            if (companyMap >= 0) image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, pm.Company.MapPixels());
            else if (under) image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, MapLayer.Pipes(c));
            else if (pm.Overlay is { } o && pm.ActiveOverlay != 0 && o.Values.Length == c.Width * c.Height)
            {
                string scheme = PanelModel.OverlayKinds.First(k => k.Kind == pm.ActiveOverlay).Scheme;
                image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, MapLayer.Pixels(o.Values.Span, scheme));
            }
            _view.SetOverlay(image);
        }
        _streets!.Refresh(GetProcessDeltaTime());
        _streets.Visible = _camera!.IsFree ? _free!.Distance < 40 || _free.Riding >= 0 : _camera.View.Zoom >= 3;
        _streets.Visible &= _graphics.StreetLabels;
        // A newly followed commuter takes the camera, until the player pans away.
        if (pm.Followed != _followed) { _followed = pm.Followed; _panels.FollowCam = _followed != null; if (_followed != null) _camera.View.Zoom = Math.Max(_camera.View.Zoom, 3); }
        if (_agents!.FollowedAt is { } f)
        {
            if (_panels.FollowCam && _camera.IsFree) { if (_free!.Riding < 0) _free.Target = new Vector3(f.X, 0, f.Y); }
            else if (_panels.FollowCam) _camera.CentreOn(f.X, f.Y);
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
          (0, 3, ViewMode.Classic),  // a building's Why panel over the land value layer, with street names
          (0, 1, ViewMode.Classic),  // the bus company's view
          (0, 4, ViewMode.Classic), (0, 3, ViewMode.Classic) };  // the free camera: smoothed buildings up close, then from farther off

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
        if (_shot == ShotViews.Length && _graphicsPanel!.Visible)
        {
            // Last, the graphics options over the free camera's view.
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/view-{_shot}-graphics.png");
            GetTree().Quit(0);
            return;
        }
        if (_shot > 0)
        {
            var (pr, pz, pm) = ShotViews[_shot - 1];
            string file = $"{_shots}/view-{_shot - 1}-{pm.ToString().ToLowerInvariant()}-r{pr}-z{pz}.png";
            GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"SHOT {file}");
        }
        if (_shot == ShotViews.Length)
        {
            _graphicsPanel!.Visible = true;
            _wait = 5;
            return;
        }
        (v.Rotation, v.Zoom, v.Mode) = ShotViews[_shot++];
        int tx = (int)v.Target.X, ty = (int)v.Target.Y;
        _wait = 5;
        if (_shot == ShotViews.Length - 1 || _shot == ShotViews.Length)
        {
            if (_shot == ShotViews.Length - 1)
            {
                _panels!.Model.Click("co-city");
                var town = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
                if (town.Count > 0) _camera.CentreOn(town.Average(b => b.X + b.Width / 2f), town.Average(b => b.Y + b.Height / 2f));
                _camera.ToggleFree();
                (_free!.Pitch, _free.Distance, _free.Yaw) = (22, 10, 30);
            }
            else (_free!.Pitch, _free.Distance, _free.Yaw) = (40, 45, 60);
            _wait = 20;
        }
        else if (_shot == ShotViews.Length - 5) { _tools!.SetTool(ToolKind.Zone); _tools.Hold((tx + 4, ty + 3), (tx - 4, ty - 3)); }
        else if (_shot == ShotViews.Length - 4) { _tools!.SetTool(ToolKind.Path); _tools.Hold((tx + 6, ty), (tx - 6, ty)); }
        else if (_shot == ShotViews.Length - 2)
        {
            _panels!.Model.Click("overlay:0");
            _panels.Model.Click("co-open");
            _panels.Model.Click("co-tab:lots");
            _wait = 10;
        }
        else if (_shot == ShotViews.Length - 3)
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
        string row = $"{{\"zoom\":{v.Zoom},\"lod\":\"{_graphics.ClassicDetail(v.Zoom).ToString().ToLowerInvariant()}\",\"avgMs\":{avg:F2},\"p95Ms\":{p95:F2},\"worstMs\":{worst:F2},\"fps\":{1000 / avg:F1},"
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
            + $"\"renderer\":\"{RenderingServer.GetVideoAdapterName()} / {RenderingServer.GetCurrentRenderingDriverName()}\",\"graphics\":\"{_graphics.PresetName}\",\"levels\":[{string.Join(",", _benchRows)}]}}";
        using (var f = global::Godot.FileAccess.Open(_bench, global::Godot.FileAccess.ModeFlags.Write)) f.StoreString(json);
        GD.Print($"BENCH wrote {_bench}");
        GetTree().Quit(0);
    }

    public override void _ExitTree() => _client?.Dispose();
}
