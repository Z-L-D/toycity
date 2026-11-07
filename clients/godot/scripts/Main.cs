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
/// --together FILE to play the Beta 2 gate's live session as the mayor of a dedicated server, with three scripted
/// players beside it, and write its checks to FILE;
/// --gpu-test DIR to measure each graphics setup on the hill town and write a report into DIR (Main.GpuTest.cs);
/// --editor to open the asset editor on its own (AssetEditor.cs), with --shots DIR to save its pictures and quit;
/// --lot-editor for the lot editor (LotEditor.cs), the same way;
/// --gallery DIR to save pictures of every model in the realistic asset set (Gallery.cs) and quit;
/// --graphics PRESET (Low, Medium, High, Ultra) to use a preset for this run instead of the saved options;
/// --user NAME --password PASSWORD to sign in to a dedicated server without the sign-in panel (with --new-account,
/// making the account first).
/// </summary>
public partial class Main : Node
{
    CityClient? _client;
    RadarPanel? _radar;
    PathDesignerPanel? _designer;
    CityView? _view;
    ClassicCamera? _camera;
    MapView? _map;
    ToolController? _tools;
    PanelView? _panels;
    FreeCamera? _free;
    AgentsView? _agents;
    DispatchWorld? _dispatch3d;
    StreetLabels? _streets;
    RoadsView? _roads;
    TreesView? _trees;
    FarmsView? _farms;
    StripsView? _strips;
    UndergroundView? _underground;
    int _underBuildings = -1;
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
    float _monthPin;
    string? _tile;  // --tile COL,ROW
    string _cityShown = "";
    string? _user;  // --user, the name signed in with
    string? _shots, _bench;
    bool _regionShot;
    /// <summary>The middle of the straight run of the shots' path design, and whether its close-up is next.</summary>
    (int X, int Y)? _designRun;
    bool _designShot;
    bool _frozen;  // the bench city is drawn from a fixed state; the server's deltas would undo it
    double _smokeTime;
    bool _editorOnly;

    public override void _Ready()
    {
        UiTheme.Apply();
        var args = OS.GetCmdlineUserArgs();
        // --editor: the asset editor on its own, with no city or server (RC part 1); with --shots DIR, its pictures.
        // --gallery DIR: pictures of the realistic asset set (RC part 2), then quit.
        if (Array.IndexOf(args, "--gallery") is int g and >= 0 && g + 1 < args.Length)
        {
            _editorOnly = true;
            AddChild(new Gallery(args[g + 1]));
            return;
        }
        // --lot-editor: the lot editor on its own (RC part 3); with --shots DIR, its pictures.
        if (args.Contains("--lot-editor"))
        {
            int at = Array.IndexOf(args, "--shots");
            _editorOnly = true;
            AddChild(new LotEditor(at >= 0 && at + 1 < args.Length ? args[at + 1] : null));
            return;
        }
        if (args.Contains("--editor"))
        {
            int at = Array.IndexOf(args, "--shots");
            _editorOnly = true;
            AddChild(new AssetEditor(at >= 0 && at + 1 < args.Length ? args[at + 1] : null));
            return;
        }
        string server = global::Godot.FileAccess.FileExists(SignInPanel.ServerFile) && global::Godot.FileAccess.GetFileAsString(SignInPanel.ServerFile).Trim() is { Length: > 0 } last
            ? last : "ws://localhost:5180/ws";
        string? user = null, password = null;
        bool newAccount = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            if (args[i] == "--smoke") _smoke = true;
            // Pins the month the trees' leaves show (1..12), for pictures of each season (Beta 4.1).
            if (args[i] == "--month" && i + 1 < args.Length && float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var mo)) _monthPin = Math.Clamp(mo, 1, 12.9f);
            if (args[i] == "--shots" && i + 1 < args.Length) { _shots = args[++i]; _smoke = true; }
            if (args[i] == "--bench" && i + 1 < args.Length) _bench = args[++i];
            if (args[i] == "--company") _company = true;
            if (args[i] == "--gate" && i + 1 < args.Length) _gateFile = args[++i];
            if (args[i] == "--together" && i + 1 < args.Length) _togetherFile = args[++i];
            if (args[i] == "--mountains" && i + 1 < args.Length) _mountainFile = args[++i];
            if (args[i] == "--hills" && i + 1 < args.Length) { _mountainFile = args[++i]; _hills = true; }
            if (args[i] == "--graphics" && i + 1 < args.Length) _graphicsPreset = args[++i];
            if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
            if (args[i] == "--password" && i + 1 < args.Length) password = args[++i];
            if (args[i] == "--new-account") newAccount = true;
            if (args[i] == "--tile" && i + 1 < args.Length) _tile = args[++i];
            if (args[i] == "--gpu-test" && i + 1 < args.Length) _gpuDir = args[++i];
            if (args[i] == "--gpu-hold" && i + 1 < args.Length) _gpuHold = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            if (args[i] == "--gpu-quit") _gpuQuit = true;
        }
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) uri = new Uri(server = "ws://localhost:5180/ws");
        // --tile COL,ROW opens one city of the region: its own window, as the region map's "Own window" starts it.
        if (_tile != null) uri = new Uri($"{server}{(server.Contains('?') ? '&' : '?')}tile={Uri.EscapeDataString(_tile)}");
        _user = user;
        var credentials = user != null && password != null ? new Protocol.V0.SignIn { Name = user, Password = password, Create = newAccount } : SignInPanel.StoredCredentials(server);
        _client = new CityClient(uri, credentials: credentials);
        if (Scripted)
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

        // The sky, the shade and the sun, moved through the day by UpdateLight (Main.Lighting.cs).
        AddChild(new WorldEnvironment { Environment = MakeEnvironment() });
        AddChild(_sun = new DirectionalLight3D { RotationDegrees = new Vector3(-50, -150, 0), LightEnergy = 1.1f, ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal });

        _view = new CityView(_client.City);
        AddChild(_view);
        _disasters = new DisasterView(_client.City);
        AddChild(_disasters);
        _camera = new ClassicCamera { EdgePan = !_smoke && !Scripted };
        AddChild(_camera);

        var hud = new CanvasLayer();
        AddChild(hud);
        // The map's own corner, right of the tool column and under the top bar.
        _status = new Label { Text = $"Connecting to {server}…", Position = new Vector2(ToolController.RailWidth + 12, 56) };
        _hover = new Label { Position = new Vector2(ToolController.RailWidth + 12, 80) };
        var players = new Label { Position = new Vector2(ToolController.RailWidth + 12, 104), Modulate = new Color(1, 1, 1, 0.85f) };
        _client.Message += m => { if (m.BodyCase == Protocol.V0.ServerMessage.BodyOneofCase.Presence) { players.Text = PresenceText(m.Presence); if (_panels != null) _panels.Model.Players = m.Presence.Players.Select(p => p.Name).ToList(); } };
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
        if (!_smoke && !Scripted) AddChild(signIn);

        _panels = new PanelView(_client) { Tools = _tools, Camera = _camera, City = _view, Map = _map, Transitions = _shots == null && !Scripted };
        AddChild(_panels);
        var pm = _panels.Model;
        pm.Me = () => _client.Connection.Player is { Length: > 0 } me ? me : "player";
        pm.OnServer = () => _client.Connection.Welcome?.SignInRequired == true;
        pm.Admin = () => _client.Connection.Admin;
        if (global::Godot.FileAccess.FileExists(ReportFile) && long.TryParse(global::Godot.FileAccess.GetFileAsString(ReportFile).Trim(), out var seen)) pm.ShownReport = _savedReport = seen;
        _client.Message += pm.Handle;
        pm.JumpTo += (x, y) => _camera.CentreOn(x + 0.5f, y + 0.5f);
        pm.Flash += r => _view.Flash(r.X0, r.Y0, r.X1, r.Y1);
        pm.PickBuilding += (id, x, y) => { _tools.Pick(id); if (x >= 0) _camera.CentreOn(x + 0.5f, y + 0.5f); };
        pm.Toast += _tools.Toast;
        pm.CopyArrived += (name, save) =>
        {
            // A de-linked player's copy goes beside the game's other files, to load on a server of their own.
            global::Godot.DirAccess.MakeDirRecursiveAbsolute("user://copies");
            using var f = global::Godot.FileAccess.Open($"user://copies/{name}.toycity", global::Godot.FileAccess.ModeFlags.Write);
            f?.StoreBuffer(save);
        };
        pm.UnlocksChanged += _tools.RebuildOptions;
        _tools.Queried += (x, y) => pm.Query(x, y);
        _tools.Act = pm.Act;
        _camera.Panned += () => _panels.FollowCam = false;
        // An emergency scene is played in the city itself.
        var scene3d = new SceneWorld(pm.Emergency, _view, _camera);
        AddChild(scene3d);
        _panels.Scene3D = scene3d;
        // The service's units on the streets, commanded directly while its screen is up (Beta 4).
        AddChild(_dispatch3d = new DispatchWorld(pm.Emergency, _view, _camera));
        _agents = new AgentsView(_client.City, pm, _view);
        AddChild(_agents);
        _streets = new StreetLabels(_client.City, _view);
        AddChild(_streets);
        AddChild(new BorderView(_client.City, _view));
        AddChild(new RiversView(_client.City, _view));
        AddChild(_trees = new TreesView(_client.City, _view));
        AddChild(_farms = new FarmsView(_client.City, _view));
        AddChild(_roads = new RoadsView(_client.City, _view));
        AddChild(_strips = new StripsView(_client.City, _view));
        AddChild(_underground = new UndergroundView(_client.City, _view));
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
        _radar = new RadarPanel(_client.City, a => _client.Connection.Act(a));
        AddChild(_radar);
        _panels.RadarPressed += _radar.Toggle;
        // The path designer (RC part 4), from the build menu's Your designs.
        _designer = new PathDesignerPanel(_client.City, a => _client.Connection.Act(a));
        AddChild(_designer);
        _tools.Opens += what => { if (what == "path-designer" && !_designer.Visible) _designer.Toggle(); };
        // The asset editor runs in a window of its own, as the company's view does.
        _panels.EditorPressed += () => OS.CreateProcess(OS.GetExecutablePath(),
            OS.GetCmdlineArgs().TakeWhile(a => a != "--").Concat(new[] { "--", "--editor" }).ToArray());
        ApplyGraphics();
        // The region map, over everything while it's open; a city of it can open in a window of its own.
        AddChild(new RegionScreen(pm.Region, () => pm.Version));
        pm.Region.OwnWindow += (col, row) => OS.CreateProcess(OS.GetExecutablePath(),
            OS.GetCmdlineArgs().TakeWhile(a => a != "--").Concat(new[] { "--", "--server", server, "--tile", $"{col},{row}" }).ToArray());
        // A city of the region that another host runs: start again pointed there, as a reconnect is.
        _client.Message += m =>
        {
            if (m.Redirect is not { } rd) return;
            _tools.Toast("That city is on another host. Moving there…", "info");
            OS.CreateProcess(OS.GetExecutablePath(),
                OS.GetCmdlineArgs().TakeWhile(a => a != "--").Concat(new[] { "--", "--server", rd.Address, "--tile", $"{rd.Col},{rd.Row}" }).ToArray());
            GetTree().Quit();
        };
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
            $"{pl.Name}{(pl.Name == _client!.Connection.Player ? " (you)" : "")} · {(pl.Spectator ? "watching" : string.Join(", ", pl.Seats.Select(Seat).Concat(pl.DeputyFor.Count > 0 ? new[] { "deputy for " + string.Join("/", pl.DeputyFor) } : Array.Empty<string>())))}{(pl.Online ? "" : " · away")}"));
    }

    public override void _Process(double delta)
    {
        if (_editorOnly) return;
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
            if (_centred && c.Stats.CityName != _cityShown) _centred = false;  // another city of the region
            if (!_centred)
            {
                _camera!.MapWidth = c.Width;
                _camera.MapHeight = c.Height;
                _camera.GroundAt = (x, y) => c.InBounds((int)x, (int)y) ? _view!.Surface(x, y) : 0;
                (_free!.MapWidth, _free.MapHeight, _free.GroundAt) = (c.Width, c.Height, _camera.GroundAt);
                // Start over the town, not the middle of the map.
                var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
                if (built.Count > 0) _camera.CentreOn(built.Average(b => b.X + b.Width / 2f), built.Average(b => b.Y + b.Height / 2f));
                else _camera.CentreOn(c.Width / 2f, c.Height / 2f);
                _centred = true;
                _cityShown = c.Stats.CityName;
            }
            var cell = _camera!.Pick(GetViewport().GetMousePosition(), _view!);
            _view!.Hover(cell);
            // Only the city's screen says what's under the pointer; a side game's has its own.
            _hover!.Text = cell is { } h && _tools!.Enabled ? Describe(c, h.X, h.Y) : "";
            _map!.Marker = _camera.IsFree ? new System.Numerics.Vector2(_free!.Target.X, _free.Target.Z) : _camera.View.Target;
            UpdateDetail();
            UpdateLight();
        }
        if (_gateFile != null)
        {
            if (_gate == null) GateStart();
            Gate(delta);
        }
        else if (_mountainFile != null) Mountains(delta);
        else if (_gpuDir != null) { if (!_gpuDone) GpuTest(delta); }
        else if (_togetherFile != null)
        {
            if (_together == null) TogetherStart();
            Together(delta);
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
        bool under = pm.ShowUnderground || _tools!.State is { Kind: ToolKind.Bulldoze, Underground: true } or { Kind: ToolKind.Utility, Utility: Protocol.V0.UtilityKind.UtilitySubway };
        int companyMap = pm.Company.Active ? pm.Company.MapVersion : -1;
        if (under != _under || pm.OverlayVersion != _overlayVersion || companyMap != _companyMap || ((under || companyMap >= 0) && cellsChanged))
        {
            (_under, _overlayVersion, _companyMap) = (under, pm.OverlayVersion, companyMap);
            Image? image = null;
            // The company's view dims the city and marks its lots; otherwise the pipes, or the panel's layer.
            if (companyMap >= 0) image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, pm.Company.MapPixels());
            else if (under) image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, MapLayer.Underground(c));
            else if (pm.Overlay is { } o && pm.ActiveOverlay != 0 && o.Values.Length == c.Width * c.Height)
            {
                string scheme = PanelModel.OverlayKinds.First(k => k.Kind == pm.ActiveOverlay).Scheme;
                image = Image.CreateFromData(c.Width, c.Height, false, Image.Format.Rgba8, MapLayer.Pixels(o.Values.Span, scheme));
            }
            _view.SetOverlay(image);
        }
        // The underground view (Beta 3.2): the buildings, roads and traffic go, the ground turns to a cutaway, and the
        // mains, sewers and tunnels show beneath it.
        _view.Underground = under;
        _roads!.Visible = _strips!.Visible = _trees!.Visible = _farms!.Visible = !under;
        _agents!.Visible = !under && _graphics.CityLife;
        bool built = c.Buildings.Count != _underBuildings;
        _underBuildings = c.Buildings.Count;
        _underground!.Show(under, cellsChanged || built);
        _trees!.Far = _camera!.IsFree ? _free!.Distance > 60 : _camera.View.Zoom <= 2;
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
          (0, 1, ViewMode.Classic), (0, 4, ViewMode.Classic), (0, 3, ViewMode.Classic),  // the emergency services' screen: the board, a scene in the 3D city (close up), its report
          (0, 1, ViewMode.Classic),  // the region map
          (0, 4, ViewMode.Classic), (0, 3, ViewMode.Classic) };  // the free camera: smoothed buildings up close, then from farther off
    // The views that set something up first, by their place in the list.
    const int ZoneShot = 8, RoadShot = 9, WhyShot = 10, CompanyShot = 11, BoardShot = 12, SceneShot = 13, ReportShot = 14, RegionShot = 15, FreeShot = 16;

    /// <summary>
    /// Whether a pick away from the camera's target is something in front of it on the hills: on the line of sight
    /// in plan, between the camera and the target. A rotation gone wrong picks off that line, or behind.
    /// </summary>
    bool InFront((int X, int Y) p, System.Numerics.Vector2 t, Vector2 centre)
    {
        var size = GetViewport().GetVisibleRect().Size;
        var (_, d) = _camera!.View.Ray(centre.X, centre.Y, size.X, size.Y);
        var h = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(d.X, d.Z));
        var off = new System.Numerics.Vector2(p.X + 0.5f - t.X, p.Y + 0.5f - t.Y);
        float along = System.Numerics.Vector2.Dot(off, h), across = MathF.Abs(off.X * h.Y - off.Y * h.X);
        return along < 0 && across <= 8;
    }

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
                if (_camera.Pick(centre, _view!) is not { } p || (Math.Abs(p.X - t.X) > 8 || Math.Abs(p.Y - t.Y) > 8) && !InFront(p, t, centre))
                {
                    GD.PrintErr($"SMOKE FAILED: rotation {r} picked {_camera.Pick(centre, _view!)?.ToString() ?? "nothing"} at the centre, aimed at ({t.X:0}, {t.Y:0})");
                    GetTree().Quit(1);
                    return;
                }
            }
            v.Rotation = 0;
            // The plant layer (Beta 4.1): a city with woods draws their trees.
            int wooded = c.Plants.Count(p => (p & 15) > 0);
            if (wooded > 0 && _trees!.Crowns == 0)
            {
                // The trees are laid out the frame after the city arrives; give them a moment.
                if (_smokeTime < 30) return;
                GD.PrintErr($"SMOKE FAILED: {wooded} wooded cells but no trees drawn");
                GetTree().Quit(1);
                return;
            }
            GD.Print($"SMOKE OK {c.Width}x{c.Height} buildings={c.Buildings.Count} tick={c.Stats.Tick} picking=4/4 trees={_trees!.Crowns} on {wooded} cells farmsteads={_farms!.Farmsteads} storm-cells={c.Stats.Sky?.Cells.Count ?? 0}");
            if (_shots == null || DisplayServer.GetName() == "headless") { GetTree().Quit(0); return; }
            DirAccess.MakeDirRecursiveAbsolute(_shots);
            _shot = 0;
            // Storms for the radar's shot, set going round the city so its own weather is untouched.
            foreach (var (x, y, h, sp, st) in new[] { (-22.0, -14.0, 20, 45, 2), (18.0, 20.0, 340, 35, 3), (-8.0, 26.0, 0, 30, 1) })
                _client!.Connection.Act(new Protocol.V0.Action { PlaceStorm = new Protocol.V0.PlaceStorm
                {
                    X = (int)(x / ToyCity.ClientModel.Graphics.Radar.CellKm + c.Width / 2.0), Y = (int)(y / ToyCity.ClientModel.Graphics.Radar.CellKm + c.Height / 2.0),
                    HeadingDeg = h, SpeedKmh = sp, Strength = st,
                } });
            // A path design (RC part 4) for the road tool's shot to lay and the designer's shot to show: a boulevard
            // with trees, bike lanes and a median.
            _client!.Connection.Act(new Protocol.V0.Action { DesignPath = ShotDesign() });
        }
        // One view per few frames: set it, let it draw, save it.
        if (_wait-- > 0) return;
        if (_regionShot)
        {
            // Last of all, the region map, with the land west of this city picked.
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/view-{_shot + 1}-region.png");
            GetTree().Quit(0);
            return;
        }
        if (_shot == ShotViews.Length && _graphicsPanel!.Visible)
        {
            // Then the graphics options over the free camera's view.
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/view-{_shot}-graphics.png");
            _graphicsPanel.Visible = false;
            _radar!.Toggle();
            _wait = 10;
            return;
        }
        if (_shot == ShotViews.Length && _radar!.Visible)
        {
            // Then the weather radar with the storms set going at the start.
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/view-{_shot}-radar.png");
            _radar.Visible = false;
            // Then the path designer, redesigning the boulevard the road tool's shot laid.
            if (c.Profiles.LastOrDefault(p => p.Custom) is not { } design)
            {
                GD.PrintErr("SMOKE FAILED: the path design never reached the client");
                GetTree().Quit(1);
                return;
            }
            int designAt = c.Profiles.ToList().IndexOf(design) + 1, built = c.Paths.Count(p => p == designAt);
            _designer!.Toggle();
            _designer.Model.Edit(design);
            _designer.Model.Select(2);
            GD.Print($"PATH DESIGNER OK {design.Id} '{design.Name}' {_designer.Describe()}, {design.SpeedKmh} km/h, {design.CapacityPerHour} an hour, built on {built} cells");
            if (built == 0) { GD.PrintErr("SMOKE FAILED: the road tool's shot didn't lay the design"); GetTree().Quit(1); return; }
            _wait = 10;
            return;
        }
        if (_shot == ShotViews.Length && _designer!.Visible)
        {
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/view-{_shot}-path-designer.png");
            _designer.Visible = false;
            // Then the design as built, up close, from the classic camera.
            if (_camera.IsFree) _camera.ToggleFree();
            if (_designRun is { } run) { _camera.CentreOn(run.X + 0.5f, run.Y + 0.5f); _panels!.Model.Query(run.X + 3, run.Y); }
            (v.Rotation, v.Zoom, v.Mode) = (1, 4, ViewMode.Classic);
            _designShot = true;
            _wait = 15;
            return;
        }
        if (_designShot)
        {
            GetViewport().GetTexture().GetImage().SavePng($"{_shots}/view-{_shot}-path-built.png");
            _designShot = false;
            _panels!.Model.Click("rg-open");
            if (_panels.Model.Region.State is { } rs) _panels.Model.Click($"rg-pick:{Math.Max(0, rs.HereCol - 1)}:{rs.HereRow}");
            _regionShot = true;
            _wait = 20;
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
        int at = _shot;
        (v.Rotation, v.Zoom, v.Mode) = ShotViews[_shot++];
        int tx = (int)v.Target.X, ty = (int)v.Target.Y;
        _wait = 5;
        if (at >= FreeShot)
        {
            if (at == FreeShot)
            {
                _panels!.Model.Click("co-city");
                _panels.Model.Click("em-city");
                _panels.Model.Click("rg-close");
                var town = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
                if (town.Count > 0) _camera.CentreOn(town.Average(b => b.X + b.Width / 2f), town.Average(b => b.Y + b.Height / 2f));
                _camera.ToggleFree();
                (_free!.Pitch, _free.Distance, _free.Yaw) = (22, 10, 30);
                // The free camera's views show smooth shapes and smooth terrain whatever the preset, so every run's shots have them.
                _graphics = _graphics with { Smoothing = ToyCity.ClientModel.Graphics.Smoothing.Wide, FullTerrain = true };
                ApplyGraphics();
            }
            else (_free!.Pitch, _free.Distance, _free.Yaw) = (40, 45, 60);
            _wait = 20;
        }
        else if (at == ZoneShot) { _tools!.SetTool(ToolKind.Zone); _tools.Hold((tx + 4, ty + 3), (tx - 4, ty - 3)); }
        else if (at == RoadShot)
        {
            // A curved road laid south of the town, its strip drawn smoothly over the cells and the ground graded
            // under it, and a diagonal being drawn, its preview priced with the grading.
            // It goes on the nearest open land below the town, so nothing is in its way.
            bool Open(int x, int y) => c.InBounds(x, y) && c.Paths[c.Idx(x, y)] == 0 && c.BuildingAt[c.Idx(x, y)] == 0 && !c.IsWater(c.Idx(x, y));
            int ay = Enumerable.Range(ty + 4, 40).FirstOrDefault(y => Enumerable.Range(tx - 9, 19).All(x => Enumerable.Range(y - 1, 11).All(yy => Open(x, yy))), ty + 7);
            // Both in the path design made at the start (RC part 4): the curve, and a straight run below it.
            string design = c.Profiles.LastOrDefault(p => p.Custom)?.Id ?? "road";
            _client!.Connection.Act(new Protocol.V0.Action { Path = new Protocol.V0.PlacePath { X0 = tx - 8, Y0 = ay, X1 = tx + 8, Y1 = ay, ProfileId = design, Curved = true, Cx = tx, Cy = ay + 6 } });
            _client.Connection.Act(new Protocol.V0.Action { Path = new Protocol.V0.PlacePath { X0 = tx - 8, Y0 = ay + 8, X1 = tx + 8, Y1 = ay + 8, ProfileId = design } });
            _designRun = (tx, ay + 8);
            _camera!.CentreOn(tx, (ty + ay) / 2f);
            _tools!.SetTool(ToolKind.Path);
            _tools.State.Diagonal = true;
            _tools.Hold((tx + 7, ty - 5), (tx - 7, ty + 3));
            _wait = 20;
        }
        else if (at is BoardShot or SceneShot or ReportShot)
        {
            var pm = _panels!.Model;
            // The city held still, so the made-up scene isn't replaced by the service's next day.
            if (at == BoardShot) { pm.Click("co-city"); pm.Click("em-open"); _client!.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } }); }
            // A made-up house fire, as the service would send it, to show a scene and then its report.
            else
            {
                pm.Emergency.Handle(SampleScene(pm.Me(), at == ReportShot, c, tx - 4, ty - 4));
                // Two SWAT officers picked, as a player would with a drag of the box.
                if (at == SceneShot) { pm.Emergency.SelectBox(new[] { 1_000_008, 1_000_009 }, false); pm.Emergency.Click("em-order:Breach"); }
            }
            _wait = 10;
        }
        else if (at == RegionShot)
        {
            _panels!.Model.Click("em-city");
            _panels.Model.Click("rg-open");
            _panels.Model.Click("rg-pick:2:1");
            _wait = 15;
        }
        else if (at == CompanyShot)
        {
            _panels!.Model.Click("overlay:0");
            _panels.Model.Click("co-open");
            _panels.Model.Click("co-tab:lots");
            _wait = 10;
        }
        else if (at == WhyShot)
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

    /// <summary>The shots' path design: a boulevard with street trees, bike lanes and a planted median.</summary>
    static Protocol.V0.DesignPath ShotDesign()
    {
        var d = new Protocol.V0.DesignPath { Name = "Tree-lined boulevard" };
        foreach (var (t, w) in new[] { ("walk", 2.5), ("planter", 1.5), ("bike", 1.8), ("car", 3.5), ("median", 2.0), ("car", 3.5), ("bike", 1.8), ("planter", 1.5), ("walk", 2.5) })
            d.Strips.Add(new Protocol.V0.PathStrip { Type = t, WidthM = w });
        return d;
    }

    /// <summary>
    /// A house fire with an armed suspect for the screenshots, on the city's own streets at the given cell: the
    /// service run by this player, with a scene under way, or, once it's over, the scene gone and its report card in.
    /// </summary>
    static Protocol.V0.SideGameView SampleScene(string me, bool over, CityMirror c, int ox, int oy)
    {
        const int W = 36;
        var tiles = new byte[W * W];
        var fire = new byte[W * W];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, cx = ox + x / 4, cy = oy + y / 4;
                bool building = c.InBounds(cx, cy) && c.BuildingAtCell(cx, cy) != null;
                tiles[i] = (byte)(building ? 2 : 0);
                if (!over && building && x >= 18 && y < 18) fire[i] = (byte)(40 + (x * 7 + y * 13) % 60);
            }
        var scene = new Emergency.Wire.SceneView { Id = 900, CallId = 900, Kind = "fire", OriginX = ox, OriginY = oy, Width = W, Height = W, Step = 4 * 75, Burning = over ? 0 : 38,
            Tiles = Google.Protobuf.ByteString.CopyFrom(tiles), Fire = Google.Protobuf.ByteString.CopyFrom(fire) };
        scene.Events.Add("Engine 1 arrived.");
        scene.Events.Add("Firefighter 2 is hosing the house on Pebble Lane.");
        int id = 1_000_000;
        foreach (var (x, y) in new[] { (19, 13), (20, 11), (18, 15) })
            scene.People.Add(new Emergency.Wire.PersonView { Id = ++id, Role = Emergency.Wire.Role.Firefighter, X = x * 16 + 8, Y = y * 16 + 8, Health = 100, UnitId = 901, State = "ok" });
        scene.People.Add(new Emergency.Wire.PersonView { Id = ++id, Role = Emergency.Wire.Role.Casualty, X = 23 * 16, Y = 17 * 16, Health = 45, Down = true, State = "hurt" });
        foreach (var (x, y) in new[] { (10, 18), (12, 19), (30, 17) })
            scene.People.Add(new Emergency.Wire.PersonView { Id = ++id, Role = Emergency.Wire.Role.Bystander, X = x * 16, Y = y * 16, Health = 100, State = "ok" });
        // A SWAT team stacked up by a building, and a shot from the suspect inside it.
        foreach (var (x, y) in new[] { (8, 24), (9, 24), (8, 25), (9, 25) })
            scene.People.Add(new Emergency.Wire.PersonView { Id = ++id, Role = Emergency.Wire.Role.Officer, X = x * 16 + 8, Y = y * 16 + 8, Health = 100, UnitId = 903, State = "ok", Armed = true });
        scene.People.Add(new Emergency.Wire.PersonView { Id = ++id, Role = Emergency.Wire.Role.Suspect, X = 5 * 16 + 8, Y = 27 * 16 + 8, Health = 100, State = "ok", Armed = true });
        scene.People.Add(new Emergency.Wire.PersonView { Id = ++id, Role = Emergency.Wire.Role.Hostage, X = 4 * 16 + 8, Y = 28 * 16 + 8, Health = 80, State = "ok" });
        scene.Shots.Add(new Emergency.Wire.ShotView { FromX = 5 * 16 + 8, FromY = 27 * 16 + 8, ToX = 8 * 16 + 8, ToY = 24 * 16 + 8, Step = scene.Step - 2, Hit = false });
        scene.Vehicles.Add(new Emergency.Wire.VehicleView { Id = 903, Kind = Emergency.Wire.UnitKind.Swat, X = 12 * 16, Y = 22 * 16 });
        scene.Vehicles.Add(new Emergency.Wire.VehicleView { Id = 901, Kind = Emergency.Wire.UnitKind.Engine, X = 18 * 16, Y = 17 * 16 + 8 });
        scene.Vehicles.Add(new Emergency.Wire.VehicleView { Id = 902, Kind = Emergency.Wire.UnitKind.Ambulance, X = 6 * 16, Y = 18 * 16 });
        var v = new Emergency.Wire.View { Name = "City Emergency Services", Operator = "manual", Owner = me, Style = Emergency.Wire.CommandStyle.SceneCommand, Cash = 18_400, Respect = 12, ServiceQuality = 64 };
        v.Units.Add(new Emergency.Wire.UnitView { Id = 901, Name = "Engine 1", Kind = Emergency.Wire.UnitKind.Engine, State = over ? "ready" : "at scene", CallId = 900, Crew = 3 });
        v.Units.Add(new Emergency.Wire.UnitView { Id = 903, Name = "SWAT 1", Kind = Emergency.Wire.UnitKind.Swat, State = over ? "ready" : "at scene", CallId = 900, Crew = 4 });
        v.Units.Add(new Emergency.Wire.UnitView { Id = 902, Name = "Ambulance 1", Kind = Emergency.Wire.UnitKind.Ambulance, State = over ? "ready" : "at scene", CallId = 900, Crew = 2 });
        v.Calls.Add(new Emergency.Wire.CallView { Id = 900, Kind = "fire", Text = "House fire on Pebble Lane", X = 44, Y = 44, Severity = 3, Status = over ? "done" : "scene", SceneId = 900, Outcome = over ? "put out" : "" });
        if (over) v.Cards.Add(new Emergency.Wire.ReportCard { SceneId = 900, Where = "House fire on Pebble Lane", Score = 87, Outcome = "put out", ResponseMinutes = 6, Saved = 1, PropertyLostPercent = 22, RulesOfEngagement = 100, HostagesFreed = 1 });
        else v.Scenes.Add(scene);
        return new Protocol.V0.SideGameView { Game = ToyCity.ClientModel.Panels.EmergencyModel.Game, Seat = "operator", Payload = Google.Protobuf.MessageExtensions.ToByteString(v) };
    }

    int _benchZoom = -1, _benchFrame;
    double _benchClock;
    ulong _benchTick;
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
        // Frames are timed by the wall clock: Godot caps a frame's delta at 8 physics steps (133 ms), so on a slow
        // machine delta would report 133 ms for a frame that took a second, and the 8 seconds would run for minutes.
        ulong now = Time.GetTicksUsec();
        double ms = _benchTick == 0 ? delta * 1000 : (now - _benchTick) / 1000.0;
        _benchTick = now;
        _benchFrame++;
        if (_benchFrame <= 30) { _benchClock = 0; return; }
        _times.Add(ms);
        _benchClock += ms / 1000;
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
        GD.Print($"BENCH models: {BuildingModels.Made} made in {BuildingModels.MakingSeconds:F1} s");
        GD.Print($"BENCH wrote {_bench}");
        GetTree().Quit(0);
    }

    public override void _ExitTree() => _client?.Dispose();
}
