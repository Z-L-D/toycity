using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.Godot;

/// <summary>
/// The lot editor (RC part 3), as SimCity 4's Lot Editor is: put a building on a lot one to four cells each way, its
/// front to the street, then dress the lot round it with ground (drives, paths, paving, beds, a pond), car parks,
/// fences and hedges, the plant layer's plants and the library's props. Click to place, drag out a patch, a car park
/// or a fence, and pick anything up to move, turn or delete it. "Use for a building" builds the whole lot into one
/// model the game draws that building type with. It opens from the asset editor's top bar, or with --lot-editor; with
/// --shots DIR it dresses the example lots, takes their pictures, checks the files and quits.
/// </summary>
public partial class LotEditor : Node3D
{
    enum Tool { Select, Building, Ground, Parking, Fence, Plant, Prop }

    const string LotsDir = "user://lots", AssetsDir = "user://assets";
    LotDoc _doc = new("New lot", 1, 1);
    readonly string? _shots;
    Tool _tool = Tool.Building;
    readonly Dictionary<Tool, string> _style = new()
    {
        [Tool.Building] = "example:house", [Tool.Ground] = "concrete", [Tool.Parking] = "some", [Tool.Fence] = "picket",
        [Tool.Plant] = "broadleaf", [Tool.Prop] = "bench",
    };
    int _turn;
    int _meshed = -1;
    RealSet.Result? _built;

    Camera3D _cam = null!;
    float _yaw = -35, _pitch = 42, _dist = 30;
    Vector3 _target;
    MeshInstance3D _mesh = null!, _roof = null!, _lines = null!, _grid = null!, _street = null!;
    Label3D _streetLabel = null!;
    readonly StandardMaterial3D _solid = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.85f };
    readonly StandardMaterial3D _lineMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true };

    // The pointer, on the lot's ground, in metres from its north-west corner.
    (double X, double Y)? _at, _from;
    int? _picked;
    bool _orbiting, _panning;
    Vector2 _lastMouse;
    Vector2? _forcedMouse;

    LineEdit _name = null!;
    Label _status = null!, _title = null!, _hint = null!;
    OptionButton _size = null!, _type = null!;
    readonly Dictionary<Tool, Button> _toolButtons = new();
    VBoxContainer _styles = null!;
    readonly List<(string Id, string Name, int W, int H)> _types = new();
    FileDialog? _dialog;
    double _sayClock;
    int _shotStep;
    double _shotWait;

    static readonly (int W, int D)[] Sizes = { (1, 1), (2, 1), (1, 2), (2, 2), (3, 2), (3, 3), (4, 4) };

    public LotEditor(string? shots) { _shots = shots; }

    double V => 0.5 / (_built?.Detail ?? 4);

    public override void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(LotsDir);
        if (_shots != null) DirAccess.MakeDirRecursiveAbsolute(_shots);
        var env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color, BackgroundColor = new Color("#2a3140"),
            AmbientLightSource = global::Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("#9fb0c8"), AmbientLightEnergy = 0.3f,
            TonemapMode = global::Godot.Environment.ToneMapper.Agx, SsaoEnabled = true,
        };
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, -35, 0), LightEnergy = 0.9f, ShadowEnabled = true, DirectionalShadowMaxDistance = 120 });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30, 150, 0), LightEnergy = 0.15f });
        AddChild(_cam = new Camera3D { Fov = 50, Current = true });
        AddChild(_mesh = new MeshInstance3D { MaterialOverride = _solid });
        AddChild(_roof = new MeshInstance3D { MaterialOverride = _solid });
        AddChild(_street = new MeshInstance3D { MaterialOverride = _solid });
        AddChild(_lines = new MeshInstance3D { MaterialOverride = _lineMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(_grid = new MeshInstance3D { MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(_streetLabel = new Label3D { Text = "street", FontSize = 96, PixelSize = 0.01f, Modulate = new Color(1, 1, 1, 0.7f), RotationDegrees = new Vector3(-90, 0, 0), Shaded = false });
        // Land round the lot, a little lower so the lot's own ground shows over it.
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(400, 400) }, Position = new Vector3(0, -0.03f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#4f6a3c"), Roughness = 1 },
        });
        _types.AddRange(AssetEditor.BuildingTypes().OrderBy(t => t.Name));
        BuildUi();
        if (_shots != null) _doc = DemoAsset.HouseLot();
        else { _doc.SetBuilding(new LotBuilding("example:house", 4, 1.5)); _doc.ForgetHistory(); }
        Opened();
    }

    // ---- the UI --------------------------------------------------------------------------------------------------

    static PanelContainer Glass(Control parent)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", UiTheme.GlassBox());
        parent.AddChild(p);
        return p;
    }

    static Button Btn(Container row, string text, string tip, Action go, bool toggle = false)
    {
        var b = UiTheme.Flat(new Button { Text = text, TooltipText = tip, ToggleMode = toggle, FocusMode = Control.FocusModeEnum.None }, 8, 4, 6);
        b.Pressed += go;
        row.AddChild(b);
        return b;
    }

    void BuildUi()
    {
        var hud = new CanvasLayer();
        AddChild(hud);
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        hud.AddChild(root);

        var top = Glass(root);
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide, Control.LayoutPresetMode.Minsize, 10);
        top.OffsetLeft = 10; top.OffsetRight = -10;
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);
        top.AddChild(bar);
        _title = new Label { Text = "Lot editor" };
        _title.AddThemeFontSizeOverride("font_size", 16);
        bar.AddChild(_title);
        bar.AddChild(new VSeparator());
        _name = new LineEdit { PlaceholderText = "Name", CustomMinimumSize = new Vector2(170, 0) };
        _name.TextChanged += t => _doc.Name = t;
        bar.AddChild(_name);
        _size = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "The lot's size in cells (16 m each), across × deep" };
        foreach (var (w, d) in Sizes) _size.AddItem($"{w}×{d} cells");
        _size.ItemSelected += i => { _doc.Resize(Sizes[i].W, Sizes[i].D); Frame(); };
        bar.AddChild(_size);
        Btn(bar, "New", "Start a new lot of the size beside", () =>
        {
            var (w, d) = Sizes[Math.Max(0, _size.Selected)];
            _doc = new LotDoc("New lot", w, d);
            Opened();
        });
        Btn(bar, "Example", "Open the example lot: the brick house with its garden", () => { _doc = DemoAsset.HouseLot(); Opened(); });
        Btn(bar, "Open", "Open one of your saved lots", () => Pick(FileDialog.FileModeEnum.OpenFile, LotsDir, "*.lot.json ; Lots", Open));
        Btn(bar, "Save", "Save it to your lots (Ctrl+S)", Save);
        bar.AddChild(new VSeparator());
        Btn(bar, "Undo", "Ctrl+Z", () => { _doc.Undo(); _picked = null; });
        Btn(bar, "Redo", "Ctrl+Y", () => { _doc.Redo(); _picked = null; });
        Btn(bar, "Clear", "Take everything off the lot", () => { _doc.Clear(); _picked = null; });
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Btn(bar, "Asset editor", "Back to the asset editor", () => Swap(new AssetEditor(null)));

        var left = Glass(root);
        left.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide, Control.LayoutPresetMode.Minsize, 10);
        left.OffsetTop = 62; left.OffsetBottom = -40;
        left.CustomMinimumSize = new Vector2(250, 0);
        var lcol = new VBoxContainer();
        lcol.AddThemeConstantOverride("separation", 6);
        left.AddChild(lcol);
        lcol.AddChild(UiTheme.Heading("Tools"));
        var tools = new GridContainer { Columns = 2 };
        lcol.AddChild(tools);
        var group = new ButtonGroup();
        foreach (var (t, label, tip) in new[]
        {
            (Tool.Select, "Pick up", "Click a thing to pick it up; drag to move it, R turns it, Delete takes it away (1)"),
            (Tool.Building, "Building", "Put the building chosen below on the lot (2)"),
            (Tool.Ground, "Ground", "Drag out a patch of ground: a drive, a path, paving, a bed (3)"),
            (Tool.Parking, "Car park", "Drag out a car park; its bays run along the longer side (4)"),
            (Tool.Fence, "Fence", "Drag a fence, a wall or a hedge from one end to the other (5)"),
            (Tool.Plant, "Plants", "Click to plant a tree, a shrub, reeds or flowers (6)"),
            (Tool.Prop, "Props", "Click to place a library prop; R turns it (7)"),
        })
        {
            var b = Btn(tools, label, tip, () => SetTool(t), toggle: true);
            b.ButtonGroup = group;
            b.CustomMinimumSize = new Vector2(112, 0);
            _toolButtons[t] = b;
        }
        lcol.AddChild(new HSeparator());
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        lcol.AddChild(scroll);
        _styles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_styles);
        lcol.AddChild(new HSeparator());
        lcol.AddChild(UiTheme.Heading("Use it"));
        _type = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "The building type the lot will be drawn as", FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(230, 0) };
        foreach (var t in _types) _type.AddItem(t.W > 0 ? $"{t.Name} ({t.W}×{t.H})" : $"{t.Name} (grows)");
        if (_types.Count == 0) _type.AddItem("(no building types found)");
        _type.ItemSelected += i => { if (i < _types.Count) _doc.Type = _types[(int)i].Id; };
        lcol.AddChild(_type);
        var use = new HBoxContainer();
        lcol.AddChild(use);
        Btn(use, "Use for a building", "Draw every building of that type on a lot this size with this lot", UseForBuilding);
        Btn(use, "Save as asset", "Build the lot into a voxel asset, to open and finish by hand in the asset editor", SaveAsAsset);
        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(230, 0) };
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.Modulate = new Color(1, 1, 1, 0.75f);
        lcol.AddChild(_hint);

        var bottom = Glass(root);
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide, Control.LayoutPresetMode.Minsize, 10);
        bottom.OffsetLeft = 10; bottom.OffsetRight = -10;
        _status = new Label();
        bottom.AddChild(_status);
        SetTool(Tool.Building);
    }

    void SetTool(Tool t)
    {
        _tool = t;
        _from = null;
        if (t != Tool.Select) _picked = null;
        _toolButtons[t].ButtonPressed = true;
        foreach (var c in _styles.GetChildren()) c.QueueFree();
        var group = new ButtonGroup();
        void Choice(string id, string name, string? tip = null)
        {
            var b = UiTheme.Flat(new Button { Text = name, TooltipText = tip ?? "", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left, ButtonGroup = group }, 8, 3, 6);
            b.ButtonPressed = _style.TryGetValue(t, out var s) && s == id;
            b.Pressed += () => { _style[t] = id; _from = null; };
            _styles.AddChild(b);
        }
        void Head(string text) => _styles.AddChild(UiTheme.Heading(text));
        switch (t)
        {
            case Tool.Select:
                Head("Picked up");
                _styles.AddChild(new Label { Text = "Click a thing on the lot.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
                Btn(_styles, "Turn (R)", "Turn it a quarter", () => { if (_picked is { } p) _doc.TurnAt(p); });
                Btn(_styles, "Delete (Del)", "Take it off the lot", DeletePicked);
                break;
            case Tool.Building:
                Head("Examples");
                Choice("example:house", "Brick house", "The asset editor's example house, without its garden");
                var mine = DirAccess.Open(AssetsDir)?.GetFiles().Where(f => f.EndsWith(".vox")).OrderBy(f => f).ToList() ?? new List<string>();
                if (mine.Count > 0) Head("My assets");
                foreach (var f in mine) Choice(ProjectSettings.GlobalizePath($"{AssetsDir}/{f}"), System.IO.Path.GetFileNameWithoutExtension(f));
                Head("The set's models");
                // Zoned types grow on any lot, so they're offered a cell square; the rest at their own size, if it fits.
                foreach (var ty in _types.Where(ty => RealSet.Has(ty.Id) && ty.W <= _doc.W && ty.H <= _doc.D))
                    for (int v = 0; v < (ty.W == 0 ? RealSet.Variants : 1); v++)
                        Choice($"set:{ty.Id}:{Math.Max(1, ty.W)}x{Math.Max(1, ty.H)}:{v}", ty.W == 0 ? $"{ty.Name} {(char)('A' + v)}" : ty.Name, "The realistic set's model with its own ground taken up");
                break;
            case Tool.Ground: Head("Ground"); foreach (var (id, n) in LotDoc.Grounds) Choice(id, n); break;
            case Tool.Parking: Head("Car park"); foreach (var (id, n) in LotDoc.Parkings) Choice(id, n); break;
            case Tool.Fence: Head("Fences, walls and hedges"); foreach (var (id, n) in LotDoc.Fences) Choice(id, n); break;
            case Tool.Plant: Head("Plants"); foreach (var (id, n) in LotDoc.Plants) Choice(id, n); break;
            case Tool.Prop: Head("Props"); foreach (var p in LotDoc.Props) Choice(p.Id, p.Name); break;
        }
        _styles.AddChild(new HSeparator());
        _styles.AddChild(UiTheme.Heading("The rest of the lot"));
        var ground = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "What the lot is where nothing else is" };
        foreach (var (id, n) in LotDoc.Grounds) ground.AddItem(n);
        ground.Select(Array.FindIndex(LotDoc.Grounds, g => g.Id == _doc.Ground));
        ground.ItemSelected += i => _doc.SetGround(LotDoc.Grounds[i].Id);
        _styles.AddChild(ground);
        _hint.Text = t switch
        {
            Tool.Select => "Drag to move. R turns, Delete takes it off.",
            Tool.Building => "Click where it goes. R turns it first. A lot has one building; placing another moves it.",
            Tool.Ground or Tool.Parking => "Drag from one corner to the other. Snaps to half metres.",
            Tool.Fence => "Drag from one end to the other. Hold Shift to keep it straight along the lot.",
            _ => "Click to place. R turns a prop.",
        };
    }

    void Swap(Node next)
    {
        var parent = GetParent();
        parent.AddChild(next);
        QueueFree();
    }

    void Pick(FileDialog.FileModeEnum mode, string dir, string filter, Action<string> done)
    {
        _dialog?.QueueFree();
        _dialog = new FileDialog { FileMode = mode, Access = FileDialog.AccessEnum.Userdata, Filters = new[] { filter }, UseNativeDialog = false, CurrentDir = dir };
        _dialog.FileSelected += p => done(ProjectSettings.GlobalizePath(p));
        AddChild(_dialog);
        _dialog.PopupCentered(new Vector2I(760, 520));
    }

    // ---- files ---------------------------------------------------------------------------------------------------

    static string Slug(string name)
    {
        var s = new string(name.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length > 0 ? s : "lot";
    }

    void Opened()
    {
        _name.Text = _doc.Name;
        _picked = null;
        int si = Array.IndexOf(Sizes, (_doc.W, _doc.D));
        if (si >= 0) _size.Select(si);
        int ti = _types.FindIndex(t => t.Id == _doc.Type);
        if (ti >= 0) _type.Select(ti);
        _meshed = -1;
        SetTool(_tool);
        Frame();
        Refresh();
    }

    void Open(string path)
    {
        try { _doc = LotDoc.Load(path); Opened(); Say($"Opened {System.IO.Path.GetFileName(path)}"); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { Say($"Couldn't open it: {e.Message}"); }
    }

    void Save()
    {
        string path = ProjectSettings.GlobalizePath($"{LotsDir}/{Slug(_doc.Name)}.lot.json");
        _doc.Save(path);
        Say($"Saved to your lots as {System.IO.Path.GetFileName(path)}");
    }

    void UseForBuilding()
    {
        if (_types.Count == 0 || _type.Selected < 0) { Say("No building types to use it for"); return; }
        var t = _types[_type.Selected];
        _doc.Type = t.Id;
        string folder = _shots != null ? System.IO.Path.Combine(_shots, "vox") : ProjectSettings.GlobalizePath("user://vox");
        string path = _doc.UseForBuilding(folder, t.Id);
        string fits = t.W == _doc.W && t.H == _doc.D ? "" : $" Its lot is {t.W}×{t.H}, so it's used only where one grows {_doc.W}×{_doc.D}.";
        Say($"Every {t.Name} on a {_doc.W}×{_doc.D} lot is now drawn as \"{_doc.Name}\" ({System.IO.Path.GetFileName(path)}).{fits} The game shows it the next time it starts.");
    }

    void SaveAsAsset()
    {
        DirAccess.MakeDirRecursiveAbsolute(AssetsDir);
        string path = ProjectSettings.GlobalizePath($"{AssetsDir}/{Slug(_doc.Name)}.vox");
        _doc.ToAsset().Save(path);
        Say($"Saved as an asset, {System.IO.Path.GetFileName(path)}: open it in the asset editor to finish it by hand");
    }

    void Say(string text) { _status.Text = text; GD.Print(text); _sayClock = 5; }

    // ---- the view ------------------------------------------------------------------------------------------------

    void Frame()
    {
        _target = new Vector3((float)_doc.Width / 2, 2, (float)_doc.Depth / 2 + 1);
        _dist = (float)Math.Max(_doc.Width, _doc.Depth) * 1.55f + 6;
    }

    void Refresh()
    {
        if (_meshed != _doc.Version)
        {
            _meshed = _doc.Version;
            var r = _built = _doc.Compose();
            float v = 0.5f / r.Detail;
            var mesh = VoxMesher.Mesh(r.Shapes.Count > 0 ? Draw.Without(r.Model, r.Detail, r.Shapes) : r.Model, v);
            foreach (var sh in r.Shapes) mesh.Append(sh.Mesh(1f, 1, r.Model.Palette));
            _mesh.Mesh = BuildingModels.ToMesh(mesh);
            _roof.Mesh = BuildingModels.ToMesh(r.Roof is { } spec ? AutoRoof.Build(r.Model, r.Detail, spec, v) : new MeshData());
            DrawStreet();
            DrawGrid();
        }
        _title.Text = $"{(_doc.Dirty ? "• " : "")}Lot editor · {_doc.W}×{_doc.D} cells ({_doc.Width:0} × {_doc.Depth:0} m) · {V:0.###} m voxels · {_doc.Items.Count} things";
        DrawLines();
    }

    /// <summary>The street along the lot's front (south), with a pavement, so the lot is seen the way it'll stand.</summary>
    void DrawStreet()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Quad(float x0, float z0, float x1, float z1, float y, Color c)
        {
            st.SetColor(c); st.SetNormal(Vector3.Up);
            st.AddVertex(new Vector3(x0, y, z0)); st.AddVertex(new Vector3(x1, y, z0)); st.AddVertex(new Vector3(x1, y, z1));
            st.AddVertex(new Vector3(x0, y, z0)); st.AddVertex(new Vector3(x1, y, z1)); st.AddVertex(new Vector3(x0, y, z1));
        }
        float w = (float)_doc.Width, d = (float)_doc.Depth;
        Quad(-12, d + 0.0f, w + 12, d + 2.5f, 0.02f, new Color("#a9a59b"));
        Quad(-12, d + 2.5f, w + 12, d + 10.5f, 0.0f, new Color("#3a3b3d"));
        for (float x = -10; x < w + 10; x += 6) Quad(x, d + 6.4f, x + 3, d + 6.6f, 0.01f, new Color("#e8e2c8"));
        Quad(-12, d + 10.5f, w + 12, d + 13, 0.02f, new Color("#a9a59b"));
        st.Index();
        _street.Mesh = st.Commit();
        _streetLabel.Position = new Vector3(w / 2, 0.05f, d + 8.6f);
    }

    /// <summary>The lot's edge and its cells, and a faint metre grid.</summary>
    void DrawGrid()
    {
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        void L(Vector3 a, Vector3 b, Color c) { im.SurfaceSetColor(c); im.SurfaceAddVertex(a); im.SurfaceAddVertex(b); }
        float w = (float)_doc.Width, d = (float)_doc.Depth, y = 0.0f;
        var metre = new Color(1, 1, 1, 0.07f);
        for (int x = 1; x < w; x++) if (x % 16 != 0) L(new Vector3(x, y + 0.14f, 0), new Vector3(x, y + 0.14f, d), metre);
        for (int z = 1; z < d; z++) if (z % 16 != 0) L(new Vector3(0, y + 0.14f, z), new Vector3(w, y + 0.14f, z), metre);
        var cell = new Color(1, 0.82f, 0.3f, 0.8f);
        for (int x = 0; x <= w; x += 16) L(new Vector3(x, 0.15f, 0), new Vector3(x, 0.15f, d), cell);
        for (int z = 0; z <= d; z += 16) L(new Vector3(0, 0.15f, z), new Vector3(w, 0.15f, z), cell);
        im.SurfaceEnd();
        _grid.Mesh = im;
    }

    /// <summary>What's picked up, and what the tool would place where the pointer is.</summary>
    void DrawLines()
    {
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        int n = 0;
        void L(Vector3 a, Vector3 b, Color c) { im.SurfaceSetColor(c); im.SurfaceAddVertex(a); im.SurfaceAddVertex(b); n++; }
        void Box((double X0, double Y0, double X1, double Y1) r, float h, Color c)
        {
            var a = new Vector3((float)r.X0, 0.05f, (float)r.Y0);
            var b = new Vector3((float)r.X1, h, (float)r.Y1);
            var p = new[] { new Vector3(a.X, 0, a.Z), new Vector3(b.X, 0, a.Z), new Vector3(b.X, 0, b.Z), new Vector3(a.X, 0, b.Z) };
            for (int i = 0; i < 4; i++)
            {
                L(p[i] + Vector3.Up * 0.05f, p[(i + 1) % 4] + Vector3.Up * 0.05f, c);
                L(p[i] + Vector3.Up * h, p[(i + 1) % 4] + Vector3.Up * h, c);
                L(p[i] + Vector3.Up * 0.05f, p[i] + Vector3.Up * h, c);
            }
        }
        float HeightOf(int i) => i < 0 ? (float)(_doc.Building is { } bb ? _doc.Model(bb.Source)?.Height ?? 6 : 6)
            : _doc.Items[i].Kind switch { LotKind.Plant => _doc.Items[i].Style is "broadleaf" or "conifer" ? 8 : 1.5f, LotKind.Fence => 1.6f, LotKind.Prop => 1.6f, _ => 0.3f };
        var pick = new Color(1, 0.85f, 0.25f);
        if (_picked is { } p && (p < 0 ? _doc.Building != null : p < _doc.Items.Count))
        {
            var r = _doc.BoundsOf(p);
            if (_tool == Tool.Select && _from is { } f && _at is { } at)
            {
                var (dx, dy) = (Snap(at.X - f.X, 0.25), Snap(at.Y - f.Y, 0.25));
                Box(r, HeightOf(p), new Color(pick, 0.35f));
                r = (r.X0 + dx, r.Y0 + dy, r.X1 + dx, r.Y1 + dy);
            }
            Box(r, HeightOf(p), pick);
        }
        var ghost = new Color(0.55f, 0.85f, 1f);
        if (Ghost() is { } g) Box(g.Bounds, g.Height, ghost);
        if (_tool == Tool.Fence && _from is { } a && _at is { } e)
        {
            var (x1, y1) = FenceEnd(a, e);
            L(new Vector3((float)a.X, 0.1f, (float)a.Y), new Vector3((float)x1, 0.1f, (float)y1), ghost);
            L(new Vector3((float)a.X, 1.2f, (float)a.Y), new Vector3((float)x1, 1.2f, (float)y1), ghost);
        }
        if (n == 0) L(Vector3.Zero, Vector3.Zero, ghost);
        im.SurfaceEnd();
        _lines.Mesh = im;
    }

    static double Snap(double v, double step) => Math.Round(v / step) * step;

    (double, double) FenceEnd((double X, double Y) a, (double X, double Y) e)
    {
        var (x, y) = (Snap(e.X, 0.25), Snap(e.Y, 0.25));
        if (Input.IsKeyPressed(Key.Shift)) { if (Math.Abs(x - a.X) > Math.Abs(y - a.Y)) y = a.Y; else x = a.X; }
        return (x, y);
    }

    /// <summary>The outline of what the tool would place now, if anything.</summary>
    ((double X0, double Y0, double X1, double Y1) Bounds, float Height)? Ghost()
    {
        if (_at is not { } at) return null;
        string style = _style.GetValueOrDefault(_tool, "");
        switch (_tool)
        {
            case Tool.Building when _doc.Model(style) is { } m:
                var (w, d) = (_turn & 1) == 0 ? (m.Width, m.Depth) : (m.Depth, m.Width);
                var (bx, by) = BuildingCorner(at, w, d);
                return ((bx, by, bx + w, by + d), (float)m.Height);
            case Tool.Ground or Tool.Parking:
                var a = _from ?? at;
                var (x0, y0, x1, y1) = (Snap(a.X, 0.5), Snap(a.Y, 0.5), Snap(at.X, 0.5), Snap(at.Y, 0.5));
                return ((Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1) + (_from == null ? 0.5 : 0), Math.Max(y0, y1) + (_from == null ? 0.5 : 0)), 0.3f);
            case Tool.Plant:
                double r = style is "broadleaf" ? 2.6 : style is "conifer" ? 2.3 : style is "orchard" ? 1.5 : 0.7;
                return ((at.X - r, at.Y - r, at.X + r, at.Y + r), style is "broadleaf" or "conifer" ? 8 : 1.5f);
            case Tool.Prop:
                var (pw, pd) = LotDoc.PropSize(style, _turn);
                var (px, py) = (Snap(at.X, 0.25), Snap(at.Y, 0.25));
                return ((px - pw / 2, py - pd / 2, px + pw / 2, py + pd / 2), 1.6f);
        }
        return null;
    }

    (double X, double Y) BuildingCorner((double X, double Y) at, double w, double d) =>
        (Math.Clamp(Snap(at.X - w / 2, 0.5), 0, Math.Max(0, _doc.Width - w)), Math.Clamp(Snap(at.Y - d / 2, 0.5), 0, Math.Max(0, _doc.Depth - d)));

    public override void _Process(double delta)
    {
        var cam = Basis.FromEuler(new Vector3(Mathf.DegToRad(-_pitch), Mathf.DegToRad(_yaw), 0));
        _cam.Position = _target + cam * new Vector3(0, 0, _dist);
        _cam.LookAt(_target, Vector3.Up);
        if (_sayClock > 0 && (_sayClock -= delta) <= 0) _status.Text = "";
        if (_shots != null) Shots(delta);
        _at = Ground(_forcedMouse ?? GetViewport().GetMousePosition());
        Refresh();
        if (_sayClock <= 0) _status.Text = Hint();
    }

    string Hint()
    {
        string where = _at is { } a ? $"{a.X:0.0}, {a.Y:0.0} m" : "";
        string picked = _picked is { } p ? p < 0 ? "the building" : p < _doc.Items.Count ? $"{_doc.Items[p].Kind.ToString().ToLowerInvariant()} ({_doc.Items[p].Style})" : "" : "";
        string what = _tool switch
        {
            Tool.Select => picked.Length > 0 ? $"Picked up {picked} · drag to move, R turns, Delete takes it off" : "Click a thing to pick it up",
            Tool.Building => "Click to put the building down · R turns it",
            Tool.Ground or Tool.Parking => _from == null ? "Drag out the patch" : "Let go to lay it",
            Tool.Fence => _from == null ? "Drag the fence from one end to the other" : "Let go to put it up · Shift keeps it straight",
            Tool.Prop => "Click to place · R turns it",
            _ => "Click to plant",
        };
        return $"{what}    {where}    ·  right-drag to turn the view, middle-drag to move it, wheel to zoom, Ctrl+Z undo";
    }

    /// <summary>Where a point on the screen falls on the ground, in lot metres.</summary>
    (double X, double Y)? Ground(Vector2 mouse)
    {
        var o = _cam.ProjectRayOrigin(mouse);
        var d = _cam.ProjectRayNormal(mouse);
        if (d.Y > -1e-4f) return null;
        float t = -o.Y / d.Y;
        var p = o + d * t;
        if (p.X < -2 || p.Z < -2 || p.X > _doc.Width + 2 || p.Z > _doc.Depth + 2) return null;
        return (Math.Clamp(p.X, 0, _doc.Width), Math.Clamp(p.Z, 0, _doc.Depth));
    }

    // ---- input ---------------------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _dist = Mathf.Max(3, _dist * 0.9f);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _dist = Mathf.Min(300, _dist * 1.1f);
            else if (mb.ButtonIndex == MouseButton.Right) _orbiting = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.Middle) _panning = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                _at = Ground(mb.Position);
                if (mb.Pressed) Press(); else Release();
            }
            _lastMouse = mb.Position;
        }
        else if (e is InputEventMouseMotion mm)
        {
            var dm = mm.Position - _lastMouse;
            _lastMouse = mm.Position;
            if (_orbiting) { _yaw -= dm.X * 0.3f; _pitch = Mathf.Clamp(_pitch + dm.Y * 0.3f, 5, 89); }
            if (_panning)
            {
                var b = _cam.GlobalTransform.Basis;
                _target += (-b.X * dm.X + b.Y * dm.Y) * _dist * 0.0015f;
            }
        }
        else if (e is InputEventKey { Pressed: true } k)
        {
            bool ctrl = k.CtrlPressed || k.MetaPressed;
            switch (k.Keycode)
            {
                case Key.Z when ctrl && k.ShiftPressed: case Key.Y when ctrl: _doc.Redo(); _picked = null; break;
                case Key.Z when ctrl: _doc.Undo(); _picked = null; break;
                case Key.S when ctrl: Save(); break;
                case Key.R when _tool == Tool.Select && _picked is { } p: _doc.TurnAt(p); break;
                case Key.R: _turn = (_turn + (k.ShiftPressed ? 3 : 1)) & 3; break;
                case Key.Delete or Key.Backspace when _picked != null: DeletePicked(); break;
                case Key.Escape: _from = null; _picked = null; break;
                case Key.F: Frame(); break;
                case >= Key.Key1 and <= Key.Key7: SetTool((Tool)(k.Keycode - Key.Key1)); break;
                default: return;
            }
        }
    }

    void DeletePicked()
    {
        if (_picked is not { } p) return;
        if (p < 0) _doc.SetBuilding(null);
        else if (p < _doc.Items.Count) _doc.Remove(p);
        _picked = null;
    }

    void Press()
    {
        if (_at is not { } at) return;
        string style = _style.GetValueOrDefault(_tool, "");
        switch (_tool)
        {
            case Tool.Select:
                _picked = _doc.At(at.X, at.Y);
                _from = _picked != null ? at : null;
                break;
            case Tool.Building when Ghost() is { } g:
                _doc.SetBuilding(new LotBuilding(style, g.Bounds.X0, g.Bounds.Y0, _turn));
                break;
            case Tool.Ground or Tool.Parking or Tool.Fence:
                _from = _tool == Tool.Fence ? (Snap(at.X, 0.25), Snap(at.Y, 0.25)) : at;
                break;
            case Tool.Plant:
                _doc.Add(new LotItem(LotKind.Plant, style, Snap(at.X, 0.25), Snap(at.Y, 0.25)));
                break;
            case Tool.Prop:
                _doc.Add(new LotItem(LotKind.Prop, style, Snap(at.X, 0.25), Snap(at.Y, 0.25), Turn: _turn));
                break;
        }
    }

    void Release()
    {
        if (_from is not { } a) return;
        _from = null;
        if (_at is not { } at) return;
        string style = _style.GetValueOrDefault(_tool, "");
        switch (_tool)
        {
            case Tool.Select when _picked is { } p:
                var (dx, dy) = (Snap(at.X - a.X, 0.25), Snap(at.Y - a.Y, 0.25));
                if (dx != 0 || dy != 0) _doc.Move(p, dx, dy);
                break;
            case Tool.Ground or Tool.Parking:
                double x0 = Snap(Math.Min(a.X, at.X), 0.5), y0 = Snap(Math.Min(a.Y, at.Y), 0.5);
                double x1 = Math.Max(x0 + 0.5, Snap(Math.Max(a.X, at.X), 0.5)), y1 = Math.Max(y0 + 0.5, Snap(Math.Max(a.Y, at.Y), 0.5));
                _doc.Add(new LotItem(_tool == Tool.Ground ? LotKind.Ground : LotKind.Parking, style, x0, y0, x1, y1));
                break;
            case Tool.Fence:
                var (fx, fy) = FenceEnd(a, at);
                if (Math.Abs(fx - a.X) + Math.Abs(fy - a.Y) >= 0.5) _doc.Add(new LotItem(LotKind.Fence, style, a.X, a.Y, fx, fy));
                break;
        }
    }

    // ---- the gate run's pictures ---------------------------------------------------------------------------------

    void Shots(double delta)
    {
        if ((_shotWait -= delta) > 0) return;
        void Shot(string name)
        {
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_shots!, name));
            GD.Print($"LOT SHOT {name}");
        }
        float w = (float)_doc.Width, d = (float)_doc.Depth;
        switch (_shotStep++)
        {
            case 0: _yaw = -32; _pitch = 36; _dist = 30; _target = new Vector3(8, 2.5f, 9); _forcedMouse = new Vector2(-4000, -4000); break;
            case 1: Shot("lot-1-house.png"); _yaw = 18; _pitch = 16; _dist = 17; _target = new Vector3(8, 3, 13); break;
            case 2: Shot("lot-2-house-from-the-street.png"); _yaw = 0; _pitch = 89; _dist = 33; _target = new Vector3(8, 0, 10f); break;
            case 3:
                Shot("lot-3-plan.png");
                // Picking up the car and rolling it down the drive: the outline where it was and where it's going.
                SetTool(Tool.Select);
                _picked = _doc.At(14, 11);
                _from = (14, 11);
                _yaw = -40; _pitch = 40; _dist = 24; _target = new Vector3(10, 2, 9);
                break;
            case 4: Warp(new Vector3(14, 0, 14)); break;
            case 5:
                Shot("lot-4-moving-the-car.png");
                Release();
                GD.Print($"LOT MOVED car to {_doc.Items[_picked!.Value].X0:0.##}, {_doc.Items[_picked.Value].Y0:0.##}");
                _picked = null;
                _forcedMouse = new Vector2(-4000, -4000);
                _doc = DemoAsset.ShopLot();
                Opened();
                _yaw = -28; _pitch = 34; _dist = 40; _target = new Vector3(16, 2, 9);
                break;
            case 6:
                Shot("lot-5-shop.png");
                // Laying a car park: the outline of the one being dragged out.
                SetTool(Tool.Parking);
                _from = (2, 1);
                Warp(new Vector3(14, 0, 11));
                _yaw = 20; _pitch = 30; _dist = 30; _target = new Vector3(16, 3, 10);
                break;
            case 7: Warp(new Vector3(14, 0, 11)); break;
            case 8:
            {
                Shot("lot-6-car-park-close.png");
                _from = null;
                // Files: save and open a lot, use it for a building, and save it as an asset.
                var lot = DemoAsset.HouseLot();
                string dir = System.IO.Path.Combine(_shots!, "lots");
                Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "brick-house-lot.lot.json");
                lot.Save(file);
                var back = LotDoc.Load(file);
                var a = lot.Compose();
                var b = back.Compose();
                bool same = back.ToJson() == lot.ToJson() && a.Model.Voxels.AsSpan().SequenceEqual(b.Model.Voxels);
                string used = back.UseForBuilding(System.IO.Path.Combine(_shots!, "vox"), "r-l-m");
                var asset = AssetDoc.Load(used);
                bool whole = asset.Model.SizeX == a.Model.SizeX && asset.Model.SizeY == a.Model.SizeY && asset.Roof != null && asset.Shapes.Count == a.Shapes.Count;
                GD.Print($"LOT FILES {(same ? "OK" : "DIFFER")} {(whole ? "WHOLE" : "CUT")} {System.IO.Path.GetFileName(used)} {asset.Model.SizeX}x{asset.Model.SizeY}x{asset.Model.SizeZ} voxels at {0.5 / asset.Detail:0.###} m, " +
                    $"{lot.Items.Count} things, {a.Shapes.Count} shapes, roof {asset.Roof?.Style}");
                bool ok = same && whole;
                GD.Print(ok ? "LOT EDITOR OK" : "LOT EDITOR FAILED");
                GetTree().Quit(ok ? 0 : 1);
                break;
            }
        }
        _shotWait = 0.6;
    }

    /// <summary>Moves the pointer onto a point on the ground, for the shots of dragging.</summary>
    void Warp(Vector3 world)
    {
        var at = _cam.UnprojectPosition(world);
        _forcedMouse = at;
        _at = Ground(at);
    }
}
