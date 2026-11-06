using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using Mat = ToyCity.ClientModel.Voxels.Assets.Material;

namespace ToyCity.Godot;

/// <summary>
/// The asset editor (RC part 1): builds voxel assets from the library's snap pieces and props, as Fortnite's
/// building does on a grid of 4 m modules and 3 m storeys, and by hand voxel by voxel as MagicaVoxel does. It runs
/// on its own (the game menu's "Asset editor", or --editor on the command line), needs no server, and saves .vox
/// files MagicaVoxel opens. "Use for a building" puts the asset in user://vox for the game to draw that building
/// type with. With --shots DIR it builds the example house, saves pictures of it into DIR and quits.
/// </summary>
public partial class AssetEditor : Node3D
{
    enum Tool { Piece, Add, Erase, Paint, Fill, Box, Pick, Shape }

    const string AssetsDir = "user://assets";
    AssetDoc _doc = AssetDoc.New("New asset", 1, 1, 1, 1);
    readonly string? _shots;
    Tool _tool = Tool.Piece;
    Piece? _piece = Pieces.Find("wall");
    (VoxModel Model, int Detail, string Name)? _saved;  // a saved asset chosen from "My assets" to place
    Mat _mat = Mat.Brick;
    int _rot, _storey;
    byte _colour = AssetPalette.Brick;
    int _meshed = -1;

    Camera3D _cam = null!;
    float _yaw = -40, _pitch = 38, _dist = 34;
    Vector3 _target;
    MeshInstance3D _mesh = null!, _ghost = null!, _lines = null!, _grid = null!;
    readonly StandardMaterial3D _solid = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.85f };
    readonly StandardMaterial3D _ghostMat = new()
    {
        VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1, 1, 1, 0.55f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
    readonly StandardMaterial3D _lineMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true };
    (string, int, int, int, int, int, int)? _ghostKey;

    // What the pointer is over, worked out each frame.
    VoxHit? _hit;
    (int X, int Y, int Z)? _place;  // where a piece or prop's corner goes
    bool _dragging;
    (int X, int Y, int Z)? _boxFrom;
    int _dragAxis = -1, _dragAt;
    (int, int, int)? _lastDrag;
    Vector2 _lastMouse;
    bool _orbiting, _panning;

    // The UI.
    LineEdit _name = null!;
    Label _status = null!, _colourName = null!, _title = null!;
    OptionButton _detail = null!, _type = null!;
    readonly Dictionary<Tool, Button> _toolButtons = new();
    readonly Dictionary<Mat, Button> _matButtons = new();
    readonly List<(Piece, Button)> _pieceButtons = new();
    VBoxContainer _mine = null!;
    readonly ButtonGroup _libraryGroup = new();
    Panel _swatch = null!;
    readonly List<(string Id, string Name, int W, int H)> _types = new();
    FileDialog? _dialog;
    int _shotStep;
    double _shotWait;

    MeshInstance3D _roof = null!;
    OptionButton _roofStyle = null!, _roofColour = null!;
    HSlider _pitchSlider = null!, _overhang = null!;
    Label _pitchLabel = null!, _overhangLabel = null!;
    CheckBox _turnRidge = null!;
    bool _showingRoof;
    static readonly (byte, string)[] RoofColours =
    {
        (AssetPalette.RoofTile, "Clay tiles"), (AssetPalette.Slate, "Slate"), (AssetPalette.Asphalt, "Shingles"),
        (AssetPalette.MetalDark, "Metal"), (AssetPalette.WoodDark, "Wood shakes"), (AssetPalette.Grass, "Green roof"),
    };

    // The shape tool: a profile drawn in 2D, placed as a lathe, an extrude or a sweep.
    ProfileCanvas _canvas = null!;
    PanelContainer _shapePanel = null!;
    OptionButton _shapeKind = null!, _shapeAxis = null!, _preset = null!;
    HSlider _depth = null!;
    Label _depthLabel = null!, _shapeHint = null!;
    CheckBox _closed = null!, _showVoxels = null!;
    readonly List<System.Numerics.Vector3> _path = new();
    double _shapeTurn;
    (System.Numerics.Vector3, int, double, int, int)? _shapeGhostKey;
    int _profileVersion;

    public AssetEditor(string? shots) { _shots = shots; }

    /// <summary>The roof panel's settings onto the asset.</summary>
    void RoofChanged()
    {
        if (_showingRoof) return;
        var style = (RoofStyle)_roofStyle.GetSelectedId();
        _doc.Roof = style == RoofStyle.None ? null
            : new RoofSpec(style, _pitchSlider.Value, (int)_overhang.Value, (byte)_roofColour.GetSelectedId(), _turnRidge.ButtonPressed);
        Refresh();
    }

    /// <summary>The asset's roof onto the roof panel, without it counting as a change.</summary>
    void ShowRoof()
    {
        _showingRoof = true;
        var r = _doc.Roof;
        _roofStyle.Select(_roofStyle.GetItemIndex((int)(r?.Style ?? RoofStyle.None)));
        if (r != null)
        {
            _pitchSlider.Value = r.Pitch;
            _overhang.Value = r.OverhangCm;
            int ci = _roofColour.GetItemIndex(r.Colour);
            if (ci >= 0) _roofColour.Select(ci);
            _turnRidge.ButtonPressed = r.TurnRidge;
        }
        _pitchLabel.Text = $"Pitch {_pitchSlider.Value:0}°";
        _overhangLabel.Text = $"Overhang {_overhang.Value:0} cm";
        foreach (var c in new Control[] { _pitchSlider, _overhang, _roofColour, _turnRidge }) c.Modulate = r == null ? new Color(1, 1, 1, 0.4f) : Colors.White;
        _showingRoof = false;
    }

    float Vs => 0.5f / _doc.Detail;   // metres a voxel

    public override void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(AssetsDir);
        if (_shots != null) DirAccess.MakeDirRecursiveAbsolute(_shots);
        var env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color, BackgroundColor = new Color("#2a3140"),
            AmbientLightSource = global::Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("#9fb0c8"), AmbientLightEnergy = 0.3f,
            TonemapMode = global::Godot.Environment.ToneMapper.Agx, SsaoEnabled = true,
        };
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52, -35, 0), LightEnergy = 0.85f, ShadowEnabled = true });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30, 150, 0), LightEnergy = 0.15f });
        AddChild(_cam = new Camera3D { Fov = 50, Current = true });
        AddChild(_mesh = new MeshInstance3D { MaterialOverride = _solid });
        AddChild(_roof = new MeshInstance3D { MaterialOverride = _solid });
        AddChild(_ghost = new MeshInstance3D { MaterialOverride = _ghostMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(_lines = new MeshInstance3D { MaterialOverride = _lineMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(_grid = new MeshInstance3D { MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // A floor under the model, to stand it on and to catch its shadow.
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(400, 400) }, Position = new Vector3(0, -0.01f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#4b5466"), Roughness = 1 },
        });
        _types.AddRange(BuildingTypes().OrderBy(t => t.Name));
        BuildUi();
        if (_shots == null) New(AssetDoc.DefaultDetail);
        else _doc = DemoAsset.House(AssetDoc.DefaultDetail);
        Frame();
        Refresh();
    }

    /// <summary>The game's building types, read from content/buildings.json above the project, as the server reads them.</summary>
    internal static IEnumerable<(string Id, string Name, int W, int H)> BuildingTypes()
    {
        var dir = new DirectoryInfo(ProjectSettings.GlobalizePath("res://"));
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "content", "buildings.json"))) dir = dir.Parent;
        if (dir == null) yield break;
        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(dir.FullName, "content", "buildings.json"))); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { yield break; }
        foreach (string list in new[] { "zoned", "civic" })
            if (doc.RootElement.TryGetProperty(list, out var types))
                foreach (var t in types.EnumerateArray())
                {
                    int Get(string k) => t.TryGetProperty(k, out var v) && v.TryGetInt32(out int n) ? n : 0;
                    yield return (t.GetProperty("id").GetString() ?? "", t.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "", Get("w"), Get("h"));
                }
    }

    // ---- the UI --------------------------------------------------------------------------------------------------

    static PanelContainer Glass(Control parent)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", UiTheme.GlassBox());
        parent.AddChild(p);
        return p;
    }

    Button Btn(Container row, string text, string tip, Action go, bool toggle = false)
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

        // Across the top: the asset, its files, and whole-model changes.
        var top = Glass(root);
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide, Control.LayoutPresetMode.Minsize, 10);
        top.OffsetLeft = 10; top.OffsetRight = -10;
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);
        top.AddChild(bar);
        _title = new Label { Text = "Asset editor" };
        _title.AddThemeFontSizeOverride("font_size", 16);
        bar.AddChild(_title);
        bar.AddChild(new VSeparator());
        _name = new LineEdit { PlaceholderText = "Name", CustomMinimumSize = new Vector2(170, 0) };
        _name.TextChanged += t => _doc.Name = t;
        bar.AddChild(_name);
        _detail = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Voxel size for a new asset" };
        _detail.AddItem("0.5 m voxels", 1);
        _detail.AddItem("0.25 m voxels", 2);
        _detail.AddItem("0.125 m voxels", 4);
        _detail.AddItem("6.25 cm voxels (houses)", 8);
        _detail.Select(_detail.GetItemIndex(AssetDoc.DefaultDetail));
        bar.AddChild(_detail);
        Btn(bar, "New", "Start a new asset at the voxel size beside", () => New(_detail.GetSelectedId()));
        Btn(bar, "Open", "Open one of your saved assets", () => Pick(FileDialog.FileModeEnum.OpenFile, FileDialog.AccessEnum.Userdata, AssetsDir, p => Open(p)));
        Btn(bar, "Save", "Save it to your assets (Ctrl+S)", Save);
        Btn(bar, "Import .vox", "Open a MagicaVoxel file from anywhere", () => Pick(FileDialog.FileModeEnum.OpenFile, FileDialog.AccessEnum.Filesystem, null, p => Open(p)));
        Btn(bar, "Export .vox", "Save a MagicaVoxel file anywhere", () => Pick(FileDialog.FileModeEnum.SaveFile, FileDialog.AccessEnum.Filesystem, null, p =>
        {
            VoxFile.Write(_doc.Model, p);
            Say($"Exported {System.IO.Path.GetFileName(p)}");
        }));
        bar.AddChild(new VSeparator());
        Btn(bar, "Undo", "Ctrl+Z", () => { _doc.Undo(); Refresh(); });
        Btn(bar, "Redo", "Ctrl+Y", () => { _doc.Redo(); Refresh(); });
        Btn(bar, "Mirror", "Flip the whole model east to west (M)", () => { _doc.MirrorX(); Refresh(); });
        Btn(bar, "Symmetry", "Copy the west half onto the east, flipped", () => { _doc.SymmetriseX(); Refresh(); });
        Btn(bar, "Clear", "Empty the model", () => { _doc.Clear(); Refresh(); });
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _type = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "The building type the asset will be drawn as", CustomMinimumSize = new Vector2(190, 0) };
        foreach (var t in _types) _type.AddItem(t.W > 0 ? $"{t.Name} ({t.W}×{t.H})" : $"{t.Name} (grows)");
        if (_types.Count == 0) _type.AddItem("(no building types found)");
        bar.AddChild(_type);
        Btn(bar, "Use for a building", "Draw every building of that type with this asset", UseForBuilding);

        // Down the left: the tools, the materials, and the library.
        var left = Glass(root);
        left.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide, Control.LayoutPresetMode.Minsize, 10);
        left.OffsetTop = 62; left.OffsetBottom = -40;
        left.CustomMinimumSize = new Vector2(250, 0);
        var lcol = new VBoxContainer();
        lcol.AddThemeConstantOverride("separation", 6);
        left.AddChild(lcol);
        lcol.AddChild(UiTheme.Heading("Tools"));
        var tools = new GridContainer { Columns = 4 };
        lcol.AddChild(tools);
        var toolGroup = new ButtonGroup();
        foreach (var (t, label, tip) in new[]
        {
            (Tool.Piece, "Place", "Place the library piece chosen below (1)"), (Tool.Add, "Add", "Add voxels in the colour chosen (2)"),
            (Tool.Erase, "Erase", "Take voxels away (3)"), (Tool.Paint, "Paint", "Recolour voxels (4)"),
            (Tool.Fill, "Fill", "Recolour a joined patch of one colour (5)"), (Tool.Box, "Box", "Drag out a box; with Shift, empty it (6)"),
            (Tool.Pick, "Pick", "Take the colour of a voxel (7, or Alt+click)"),
            (Tool.Shape, "Shape", "Draw a profile, then place it as a lathe, an extrude or a sweep: smooth in the game, voxels when wrecked (8)"),
        })
        {
            var b = Btn(tools, label, tip, () => SetTool(t), toggle: true);
            b.ButtonGroup = toolGroup;
            _toolButtons[t] = b;
        }
        lcol.AddChild(UiTheme.Heading("Material"));
        var mats = new GridContainer { Columns = 3 };
        lcol.AddChild(mats);
        var matGroup = new ButtonGroup();
        foreach (var m in Enum.GetValues<Mat>())
        {
            var b = Btn(mats, m.ToString(), $"Build walls, floors and stairs in {m.ToString().ToLowerInvariant()}", () => { _mat = m; _ghostKey = null; }, toggle: true);
            b.ButtonGroup = matGroup;
            b.ButtonPressed = m == _mat;
            var (main, _) = AssetPalette.Of(m);
            var c = Swatch(main);
            b.Icon = SwatchIcon(c);
            _matButtons[m] = b;
        }
        // The auto-roof: a smooth polygon roof over the top of the walls, made again whenever they change.
        lcol.AddChild(UiTheme.Heading("Auto-roof"));
        var roofRow = new HBoxContainer();
        lcol.AddChild(roofRow);
        _roofStyle = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "A roof over everything the top storey's walls enclose" };
        foreach (var st in Enum.GetValues<RoofStyle>()) _roofStyle.AddItem(st == RoofStyle.None ? "No roof" : st.ToString(), (int)st);
        _roofStyle.ItemSelected += _ => RoofChanged();
        roofRow.AddChild(_roofStyle);
        _roofColour = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "What the roof is covered in" };
        foreach (var (c, label) in RoofColours) _roofColour.AddItem(label, c);
        _roofColour.ItemSelected += _ => RoofChanged();
        roofRow.AddChild(_roofColour);
        var pitchRow = new HBoxContainer();
        lcol.AddChild(pitchRow);
        _pitchLabel = new Label { CustomMinimumSize = new Vector2(90, 0) };
        pitchRow.AddChild(_pitchLabel);
        _pitchSlider = new HSlider { MinValue = 10, MaxValue = 60, Step = 1, Value = 38, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        _pitchSlider.ValueChanged += _ => RoofChanged();
        pitchRow.AddChild(_pitchSlider);
        var overRow = new HBoxContainer();
        lcol.AddChild(overRow);
        _overhangLabel = new Label { CustomMinimumSize = new Vector2(90, 0) };
        overRow.AddChild(_overhangLabel);
        _overhang = new HSlider { MinValue = 0, MaxValue = 100, Step = 5, Value = 40, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        _overhang.ValueChanged += _ => RoofChanged();
        overRow.AddChild(_overhang);
        _turnRidge = new CheckBox { Text = "Turn the ridge", FocusMode = Control.FocusModeEnum.None };
        _turnRidge.Toggled += _ => RoofChanged();
        lcol.AddChild(_turnRidge);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        lcol.AddChild(scroll);
        var lib = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(lib);
        foreach (string cat in Pieces.Categories)
        {
            lib.AddChild(UiTheme.Heading(cat));
            var grid = new GridContainer { Columns = 3 };
            grid.AddThemeConstantOverride("h_separation", 4);
            grid.AddThemeConstantOverride("v_separation", 4);
            lib.AddChild(grid);
            foreach (var p in Pieces.All.Where(p => p.Category == cat))
            {
                var piece = p;
                var b = new Button
                {
                    ToggleMode = true, ButtonGroup = _libraryGroup, FocusMode = Control.FocusModeEnum.None, TooltipText = p.Name,
                    Icon = Thumb(p.Build(Mat.Brick, 1), 64), CustomMinimumSize = new Vector2(72, 72), ExpandIcon = false,
                    IconAlignment = HorizontalAlignment.Center,
                };
                UiTheme.Flat(b, 3, 3, 6);
                b.Pressed += () => { _piece = piece; _saved = null; _ghostKey = null; SetTool(Tool.Piece); };
                b.ButtonPressed = p == _piece;
                grid.AddChild(b);
                _pieceButtons.Add((p, b));
            }
        }
        lib.AddChild(UiTheme.Heading("My assets"));
        _mine = new VBoxContainer();
        lib.AddChild(_mine);
        ListMine();

        // Down the right: the palette.
        var right = Glass(root);
        right.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide, Control.LayoutPresetMode.Minsize, 10);
        right.OffsetTop = 62; right.OffsetBottom = -40;
        right.GrowHorizontal = Control.GrowDirection.Begin;
        var rcol = new VBoxContainer();
        rcol.AddThemeConstantOverride("separation", 6);
        right.AddChild(rcol);
        rcol.AddChild(UiTheme.Heading("Colour"));
        var now = new HBoxContainer();
        rcol.AddChild(now);
        _swatch = new Panel { CustomMinimumSize = new Vector2(28, 28) };
        now.AddChild(_swatch);
        _colourName = new Label();
        now.AddChild(_colourName);
        rcol.AddChild(UiTheme.Heading("Named"));
        var named = new GridContainer { Columns = 8 };
        named.AddThemeConstantOverride("h_separation", 2);
        named.AddThemeConstantOverride("v_separation", 2);
        rcol.AddChild(named);
        foreach (var (i, nm, _) in AssetPalette.Named) named.AddChild(SwatchButton(i, nm, 22));
        rcol.AddChild(UiTheme.Heading("Ramp"));
        var ramp = new GridContainer { Columns = 16 };
        ramp.AddThemeConstantOverride("h_separation", 1);
        ramp.AddThemeConstantOverride("v_separation", 1);
        rcol.AddChild(ramp);
        for (int i = 32; i < 256; i++) ramp.AddChild(SwatchButton((byte)i, AssetPalette.NameOf((byte)i), 11));
        SetColour(_colour);

        // Along the bottom: what's under the pointer, and the keys.
        var foot = Glass(root);
        foot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide, Control.LayoutPresetMode.Minsize, 10);
        foot.OffsetLeft = 10; foot.OffsetRight = -10;
        foot.GrowVertical = Control.GrowDirection.Begin;
        _status = new Label { Text = "" };
        foot.AddChild(_status);
        BuildShapePanel(root);
        SetTool(Tool.Piece);
    }

    Color Swatch(byte i)
    {
        var (r, g, b, _) = VoxModel.Unpack(_doc.Model.Palette[i]);
        return Color.Color8(r, g, b);
    }

    static Texture2D SwatchIcon(Color c)
    {
        var img = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8);
        img.Fill(c);
        return ImageTexture.CreateFromImage(img);
    }

    static Texture2D Thumb(VoxModel m, int size) =>
        ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.Rgba8, Thumbnail.Render(m, size)));

    Button SwatchButton(byte i, string name, int size)
    {
        var b = new Button { CustomMinimumSize = new Vector2(size, size), TooltipText = name, FocusMode = Control.FocusModeEnum.None };
        var c = Swatch(i);
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed" })
            b.AddThemeStyleboxOverride(st, UiTheme.Box(c, st.Contains("hover") ? Colors.White : null, 3, 0, 0));
        b.Pressed += () => { SetColour(i); if (_tool is Tool.Piece or Tool.Pick) SetTool(Tool.Add); };
        return b;
    }

    void SetColour(byte i)
    {
        _colour = i;
        _swatch.AddThemeStyleboxOverride("panel", UiTheme.Box(Swatch(i), Colors.White, 4, 0, 0));
        _colourName.Text = AssetPalette.NameOf(i);
    }

    void SetTool(Tool t)
    {
        _tool = t;
        if (_shapePanel != null) _shapePanel.Visible = t == Tool.Shape;
        _path.Clear();
        _toolButtons[t].ButtonPressed = true;
        _boxFrom = null;
        _ghostKey = null;
    }

    void ListMine()
    {
        foreach (var c in _mine.GetChildren()) c.QueueFree();
        var dir = DirAccess.Open(AssetsDir);
        var files = dir?.GetFiles().Where(f => f.EndsWith(".vox")).OrderBy(f => f).ToList() ?? new List<string>();
        if (files.Count == 0) { _mine.AddChild(new Label { Text = "Saved assets show here,\nto place in others.", Modulate = UiTheme.Muted }); return; }
        foreach (string f in files)
        {
            string path = $"{AssetsDir}/{f}";
            var b = UiTheme.Flat(new Button { Text = f[..^4], ToggleMode = true, ButtonGroup = _libraryGroup, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left }, 6, 3, 6);
            b.TooltipText = "Place this asset (double-click the name in Open to edit it)";
            b.Pressed += () =>
            {
                string abs = ProjectSettings.GlobalizePath(path);
                try
                {
                    var m = VoxFile.Read(abs);
                    var (name, detail) = AssetDoc.ReadSidecar(abs) ?? (f[..^4], 1);
                    _saved = (m, detail, name);
                    _piece = null;
                    _ghostKey = null;
                    SetTool(Tool.Piece);
                }
                catch (Exception e) when (e is IOException or InvalidDataException) { Say($"{f}: {e.Message}"); }
            };
            _mine.AddChild(b);
        }
    }

    void Pick(FileDialog.FileModeEnum mode, FileDialog.AccessEnum access, string? dir, Action<string> done)
    {
        _dialog?.QueueFree();
        _dialog = new FileDialog { FileMode = mode, Access = access, Filters = new[] { "*.vox ; MagicaVoxel models" }, UseNativeDialog = false };
        if (dir != null) _dialog.CurrentDir = dir;
        if (mode == FileDialog.FileModeEnum.SaveFile) _dialog.CurrentFile = Slug(_doc.Name) + ".vox";
        _dialog.FileSelected += p => done(ProjectSettings.GlobalizePath(p));
        AddChild(_dialog);
        _dialog.PopupCentered(new Vector2I(760, 520));
    }

    // ---- files ---------------------------------------------------------------------------------------------------

    static string Slug(string name)
    {
        var s = new string(name.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length > 0 ? s : "asset";
    }

    void New(int detail)
    {
        _doc = AssetDoc.New("New asset", AssetDoc.Details.Contains(detail) ? detail : AssetDoc.DefaultDetail, 4, 4, 3);
        _storey = 0;
        _name.Text = _doc.Name;
        Frame();
        Refresh();
    }

    void Open(string path)
    {
        try
        {
            _doc = AssetDoc.Load(path);
            _name.Text = _doc.Name;
            _storey = 0;
            Frame();
            Refresh();
            Say($"Opened {System.IO.Path.GetFileName(path)} ({_doc.Model.SizeX}×{_doc.Model.SizeY}×{_doc.Model.SizeZ}, {Vs:0.###} m voxels)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { Say($"Couldn't open it: {e.Message}"); }
    }

    void Save()
    {
        string path = ProjectSettings.GlobalizePath($"{AssetsDir}/{Slug(_doc.Name)}.vox");
        _doc.Save(path);
        ListMine();
        Say($"Saved to your assets as {System.IO.Path.GetFileName(path)}");
    }

    void UseForBuilding()
    {
        if (_types.Count == 0 || _type.Selected < 0) { Say("No building types to use it for"); return; }
        var t = _types[_type.Selected];
        // The gate's shot run mustn't change what the other runs draw, so it writes somewhere else.
        string folder = _shots != null ? System.IO.Path.Combine(_shots, "vox") : ProjectSettings.GlobalizePath("user://vox");
        string path = _doc.UseForBuilding(folder, t.Id);
        var (w, d) = AssetDoc.CellsOf(VoxFile.Read(path), _doc.Detail);
        string lot = t.W > 0 ? $"its lot is {t.W}×{t.H}" : "fitted to each lot it grows on";
        Say($"Every {t.Name} is now drawn with \"{_doc.Name}\" ({w}×{d} cells, {lot}). The game shows it the next time it starts.");
    }

    void Say(string text) { _status.Text = text; GD.Print(text); _sayClock = 4; }

    // ---- the shape tool ------------------------------------------------------------------------------------------

    void BuildShapePanel(Control root)
    {
        _shapePanel = Glass(root);
        _shapePanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 10);
        _shapePanel.OffsetLeft = 272;
        _shapePanel.OffsetBottom = -48;
        _shapePanel.GrowVertical = Control.GrowDirection.Begin;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        _shapePanel.AddChild(col);
        col.AddChild(UiTheme.Heading("Shape"));
        var row = new HBoxContainer();
        col.AddChild(row);
        _shapeKind = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Lathe spins the profile round the axis; extrude pulls it straight; sweep draws it along a path you click out" };
        _shapeKind.AddItem("Lathe", (int)ShapeKind.Lathe);
        _shapeKind.AddItem("Extrude", (int)ShapeKind.Extrude);
        _shapeKind.AddItem("Sweep", (int)ShapeKind.Sweep);
        _shapeKind.ItemSelected += _ => ProfileSettingsChanged();
        row.AddChild(_shapeKind);
        _shapeAxis = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Draw the outline as seen from the front, from the side, or from above" };
        _shapeAxis.AddItem("Front", (int)ExtrudeAxis.Back);
        _shapeAxis.AddItem("Side", (int)ExtrudeAxis.Across);
        _shapeAxis.AddItem("Plan", (int)ExtrudeAxis.Up);
        _shapeAxis.ItemSelected += _ => ProfileSettingsChanged();
        row.AddChild(_shapeAxis);
        _preset = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Start from a ready profile" };
        _preset.AddItem("Presets…");
        foreach (string n in ShapePresets.Names) _preset.AddItem(n);
        _preset.ItemSelected += i =>
        {
            if (i <= 0) return;
            if (ShapePresets.Make(ShapePresets.Names[i - 1], _colour) is { } p) ShowProfile(p);
            _preset.Select(0);
        };
        row.AddChild(_preset);
        _canvas = new ProfileCanvas { CustomMinimumSize = new Vector2(330, 300), MouseFilter = Control.MouseFilterEnum.Stop, ClipContents = true };
        _canvas.Changed += () => { _profileVersion++; _shapeGhostKey = null; };
        col.AddChild(_canvas);
        var depthRow = new HBoxContainer();
        col.AddChild(depthRow);
        _depthLabel = new Label { CustomMinimumSize = new Vector2(96, 0) };
        depthRow.AddChild(_depthLabel);
        _depth = new HSlider { MinValue = 0.125, MaxValue = 16, Step = 0.125, Value = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        _depth.ValueChanged += _ => ProfileSettingsChanged();
        depthRow.AddChild(_depth);
        var opts = new HBoxContainer();
        col.AddChild(opts);
        _closed = new CheckBox { Text = "Closed", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "A closed outline makes a solid; an open one a thin shell, like a bowl or an awning" };
        _closed.Toggled += _ => ProfileSettingsChanged();
        opts.AddChild(_closed);
        Btn(opts, "Clear", "Start the profile again", () => { _canvas.Points.Clear(); _canvas.QueueRedraw(); _profileVersion++; _shapeGhostKey = null; });
        Btn(opts, "Finish path", "Place the sweep along the path clicked out (Enter)", FinishPath);
        _showVoxels = new CheckBox { Text = "Show voxels", FocusMode = Control.FocusModeEnum.None, TooltipText = "See the voxels kept for when the building is wrecked" };
        _showVoxels.Toggled += _ => { _meshed = -1; Refresh(); };
        opts.AddChild(_showVoxels);
        _shapeHint = new Label { Modulate = UiTheme.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) };
        col.AddChild(_shapeHint);
        _shapePanel.Visible = false;
        ShowProfile(ShapePresets.Column(6, 0.35, _colour));
    }

    void ShowProfile(Shape s)
    {
        _canvas.Show(s);
        _shapeKind.Select(_shapeKind.GetItemIndex((int)s.Kind));
        if (s.Kind == ShapeKind.Extrude) { _shapeAxis.Select(_shapeAxis.GetItemIndex((int)s.Axis)); _depth.SetValueNoSignal(s.Depth); }
        _closed.SetPressedNoSignal(s.Closed);
        ProfileSettingsChanged();
    }

    void ProfileSettingsChanged()
    {
        var kind = (ShapeKind)_shapeKind.GetSelectedId();
        _canvas.Kind = kind;
        _canvas.Axis = (ExtrudeAxis)_shapeAxis.GetSelectedId();
        _canvas.Closed = _closed.ButtonPressed;
        _canvas.QueueRedraw();
        _shapeAxis.Visible = kind == ShapeKind.Extrude;
        _depth.Visible = _depthLabel.Visible = kind == ShapeKind.Extrude;
        _depthLabel.Text = $"Depth {_depth.Value:0.###} m";
        _shapeHint.Text = kind switch
        {
            ShapeKind.Lathe => "Draw half the outline right of the axis. Click in the view to place it; R turns it.",
            ShapeKind.Extrude => "Draw the outline, then click in the view to place it. Depth is how far it's pulled back.",
            _ => "Draw the cross-section, then click points in the view for its path; Enter places it, Esc starts again.",
        };
        _profileVersion++;
        _shapeGhostKey = null;
    }

    /// <summary>Where the pointer would place a shape, in metres: the middle of the voxel in front of what it's over, at its foot.</summary>
    System.Numerics.Vector3? ShapeAt()
    {
        if (_hit is not { } h) return null;
        var f = h.Front;
        return new System.Numerics.Vector3((f.X + 0.5f) * Vs, (f.Y + 0.5f) * Vs, f.Z * Vs);
    }

    /// <summary>The shape the panel describes, placed at a point; null while its profile is too short.</summary>
    Shape? CurrentShape(System.Numerics.Vector3 at, List<System.Numerics.Vector3>? path = null)
    {
        var kind = (ShapeKind)_shapeKind.GetSelectedId();
        var pts = _canvas.Points.ToList();
        if (pts.Count < 2 || _closed.ButtonPressed && kind != ShapeKind.Lathe && pts.Count < 3) return null;
        var s = new Shape
        {
            Kind = kind, Profile = pts, Closed = _closed.ButtonPressed, Axis = (ExtrudeAxis)_shapeAxis.GetSelectedId(),
            Depth = _depth.Value, Colour = _colour, Turn = _shapeTurn, X = at.X, Y = at.Y, Z = at.Z,
        };
        if (kind == ShapeKind.Sweep)
        {
            if (path == null || path.Count < 2) return null;
            // A sweep's path is kept from its first point.
            s.X = path[0].X; s.Y = path[0].Y; s.Z = path[0].Z; s.Turn = 0;
            s.Path = path.Select(p => p - path[0]).ToList();
        }
        return s;
    }

    void FinishPath()
    {
        if (_path.Count >= 2 && CurrentShape(_path[0], _path) is { } sh)
        {
            int n = _doc.AddShape(sh);
            Say($"Placed a sweep along {_path.Count} points: {n} voxels, drawn smooth");
            Refresh();
        }
        else Say("Click at least two points for the path first");
        _path.Clear();
        _shapeGhostKey = null;
    }

    /// <summary>The shape that would be placed, ghosted where the pointer is; for a sweep, along the path so far.</summary>
    void ShowShapeGhost()
    {
        var at = ShapeAt();
        _ghost.Position = Vector3.Zero;
        if (at is not { } p) { if (_path.Count == 0) _ghost.Visible = false; return; }
        var key = (p, _profileVersion, _shapeTurn, _path.Count, (int)_colour);
        if (_shapeGhostKey != key)
        {
            _shapeGhostKey = key;
            Shape? s = (ShapeKind)_shapeKind.GetSelectedId() == ShapeKind.Sweep
                ? CurrentShape(p, _path.Count > 0 ? _path.Append(p).ToList() : null)
                : CurrentShape(p);
            _ghost.Mesh = s != null ? BuildingModels.ToMesh(s.Mesh(1f, 1, _doc.Model.Palette)) : null;
            _ghost.Position = Vector3.Zero;
        }
        _ghost.Visible = _ghost.Mesh != null;
    }

    /// <summary>For the shots: draws a profile into the panel as a player would, point by point.</summary>
    internal void UseProfile(Shape s) { SetTool(Tool.Shape); ShowProfile(s); }
    double _sayClock;

    // ---- the view ------------------------------------------------------------------------------------------------

    /// <summary>Points the camera at the middle of the model.</summary>
    void Frame()
    {
        var m = _doc.Model;
        _target = new Vector3(m.SizeX * Vs / 2, m.SizeZ * Vs / 4, m.SizeY * Vs / 2);
        _dist = Mathf.Max(m.SizeX, m.SizeY) * Vs * 1.7f + 4;
        _meshed = -1;
    }

    void Refresh()
    {
        _title.Text = $"{(_doc.Dirty ? "• " : "")}Asset editor · {Vs:0.###} m voxels · {_doc.Model.SizeX}×{_doc.Model.SizeY}×{_doc.Model.SizeZ}";
        if (_meshed == _doc.Version) return;
        _meshed = _doc.Version;
        // Shapes are drawn as the game draws them, smooth over the rest; "Show voxels" shows what's kept for wrecking.
        if (_doc.Shapes.Count > 0 && !_showVoxels.ButtonPressed)
        {
            var mesh = VoxMesher.Mesh(Draw.Without(_doc.Model, _doc.Detail, _doc.Shapes), Vs);
            foreach (var sh in _doc.Shapes) mesh.Append(sh.Mesh(1f, 1, _doc.Model.Palette));
            _mesh.Mesh = BuildingModels.ToMesh(mesh);
        }
        else _mesh.Mesh = BuildingModels.ToMesh(VoxMesher.Mesh(_doc.Model, Vs));
        _roof.Mesh = BuildingModels.ToMesh(_doc.RoofMesh(Vs));
        ShowRoof();
        DrawGrid();
    }

    void DrawGrid()
    {
        var m = _doc.Model;
        int M = _doc.ModuleVoxels;
        float h = _storey * _doc.StoreyVoxels * Vs + 0.02f, w = m.SizeX * Vs, d = m.SizeY * Vs;
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        var line = new Color(0.75f, 0.85f, 1f, 0.55f);
        for (int x = 0; x <= m.SizeX; x += M) { im.SurfaceSetColor(line); im.SurfaceAddVertex(new Vector3(x * Vs, h, 0)); im.SurfaceAddVertex(new Vector3(x * Vs, h, d)); }
        for (int y = 0; y <= m.SizeY; y += M) { im.SurfaceSetColor(line); im.SurfaceAddVertex(new Vector3(0, h, y * Vs)); im.SurfaceAddVertex(new Vector3(w, h, y * Vs)); }
        // The model's box, faint.
        var box = new Color(1, 1, 1, 0.12f);
        float top = m.SizeZ * Vs;
        foreach (var (a, b) in BoxEdges(Vector3.Zero, new Vector3(w, top, d))) { im.SurfaceSetColor(box); im.SurfaceAddVertex(a); im.SurfaceAddVertex(b); }
        im.SurfaceEnd();
        _grid.Mesh = im;
    }

    static IEnumerable<(Vector3, Vector3)> BoxEdges(Vector3 a, Vector3 b)
    {
        var c = new[]
        {
            new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, a.Y, a.Z), new Vector3(b.X, a.Y, b.Z), new Vector3(a.X, a.Y, b.Z),
            new Vector3(a.X, b.Y, a.Z), new Vector3(b.X, b.Y, a.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(a.X, b.Y, b.Z),
        };
        for (int i = 0; i < 4; i++)
        {
            yield return (c[i], c[(i + 1) % 4]);
            yield return (c[i + 4], c[(i + 1) % 4 + 4]);
            yield return (c[i], c[i + 4]);
        }
    }

    /// <summary>The model to place with the Place tool, at this asset's detail.</summary>
    VoxModel? Stamp()
    {
        // Built once for each choice, not every frame: a fine tree is a lot of voxels to make.
        var key = (_saved?.Model, _piece, _mat, _doc.Detail);
        if (_stampKey != key)
        {
            _stampKey = key;
            _stamp = _saved is { } s ? AssetDoc.Rescale(s.Model, s.Detail, _doc.Detail) : _piece?.Build(_mat, _doc.Detail);
        }
        return _stamp;
    }

    (VoxModel?, Piece?, Mat, int)? _stampKey;
    VoxModel? _stamp;

    bool StampSnaps => _saved == null && _piece?.Snap == true;

    public override void _Process(double delta)
    {
        var cam = Basis.FromEuler(new Vector3(Mathf.DegToRad(-_pitch), Mathf.DegToRad(_yaw), 0));
        _cam.Position = _target + cam * new Vector3(0, 0, _dist);
        _cam.LookAt(_target, Vector3.Up);
        if (_sayClock > 0 && (_sayClock -= delta) <= 0) _status.Text = "";
        if (_shots != null) { Shots(delta); }
        Hover(_forcedMouse ?? GetViewport().GetMousePosition());
        Refresh();
        if (_sayClock <= 0) _status.Text = Hint();
    }

    string Hint()
    {
        string at = _hit is { Ground: false } h ? $"voxel {h.X}, {h.Y}, {h.Z} · {AssetPalette.NameOf(_doc[h.X, h.Y, h.Z])}" : "";
        string what = _tool switch
        {
            Tool.Piece => $"{(_saved?.Name ?? _piece?.Name ?? "")} · storey {_storey + 1} · R to turn, PgUp/PgDn storey",
            Tool.Box => _boxFrom == null ? "Drag out a box; Shift empties it" : "Let go to fill the box",
            _ => _tool.ToString(),
        };
        return $"{what}    {at}    ·  right-drag to turn the view, middle-drag to move it, wheel to zoom, Ctrl+Z undo";
    }

    (Vector3 O, Vector3 D) Ray(Vector2 mouse)
    {
        var o = _cam.ProjectRayOrigin(mouse) / Vs;
        var d = _cam.ProjectRayNormal(mouse);
        // Godot's x east, y up, z south to the model's x east, y south, z up.
        return (new Vector3(o.X, o.Z, o.Y), new Vector3(d.X, d.Z, d.Y));
    }

    void Hover(Vector2 mouse)
    {
        var (o, d) = Ray(mouse);
        _hit = VoxRay.Cast(_doc.Model, o.X, o.Y, o.Z, d.X, d.Y, d.Z);
        _place = null;
        if (_tool == Tool.Piece && Stamp() is { } stamp)
        {
            if (StampSnaps)
            {
                float zp = _storey * _doc.StoreyVoxels;
                if (Mathf.Abs(d.Z) > 1e-4f && (zp - o.Z) / d.Z is var t and > 0)
                {
                    int M = _doc.ModuleVoxels;
                    int gx = Mathf.FloorToInt((o.X + d.X * t) / M), gy = Mathf.FloorToInt((o.Y + d.Y * t) / M);
                    if (gx >= 0 && gy >= 0 && gx < _doc.Model.SizeX / M && gy < _doc.Model.SizeY / M) _place = (gx, gy, _storey);
                }
            }
            else if (_hit is { } h)
            {
                var (tx, ty) = AssetDoc.Turned(stamp, _rot);
                var f = h.Front;
                _place = (f.X - tx / 2, f.Y - ty / 2, f.Z);
            }
        }
        ShowGhost(stamp: _tool == Tool.Piece ? Stamp() : null);
        if (_tool == Tool.Shape) ShowShapeGhost();
        if (_dragging && _tool is Tool.Add or Tool.Erase or Tool.Paint) DragTo();
    }

    void ShowGhost(VoxModel? stamp)
    {
        var im = new ImmediateMesh();
        bool any = false;
        void Box(Vector3 a, Vector3 b, Color c)
        {
            if (!any) { im.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; }
            foreach (var (p, q) in BoxEdges(a, b)) { im.SurfaceSetColor(c); im.SurfaceAddVertex(p); im.SurfaceAddVertex(q); }
        }
        Vector3 W(int x, int y, int z) => new(x * Vs, z * Vs, y * Vs);
        if (stamp != null && _place is { } pl)
        {
            int ox = pl.X, oy = pl.Y, oz = pl.Z;
            if (StampSnaps) (ox, oy, oz) = (pl.X * _doc.ModuleVoxels, pl.Y * _doc.ModuleVoxels, pl.Z * _doc.StoreyVoxels);
            var key = (_saved?.Name ?? _piece!.Id, (int)_mat, _rot, ox, oy, oz, _doc.Detail);
            if (_ghostKey != key)
            {
                _ghostKey = key;
                var (tx, ty) = AssetDoc.Turned(stamp, _rot);
                var turned = new VoxModel(tx, ty, stamp.SizeZ);
                Array.Copy(stamp.Palette, turned.Palette, 256);
                for (int z = 0; z < stamp.SizeZ; z++)
                    for (int y = 0; y < stamp.SizeY; y++)
                        for (int x = 0; x < stamp.SizeX; x++)
                            if (stamp[x, y, z] is var c and > 0) { var (a, b) = AssetDoc.Turn(x, y, stamp.SizeX, stamp.SizeY, _rot); turned[a, b, z] = c; }
                _ghost.Mesh = BuildingModels.ToMesh(VoxMesher.Mesh(turned, Vs));
                _ghost.Position = W(ox, oy, oz);
            }
            _ghost.Visible = true;
            var (sx, sy) = AssetDoc.Turned(stamp, _rot);
            Box(W(ox, oy, oz), W(ox + sx, oy + sy, oz + stamp.SizeZ), new Color(1, 0.85f, 0.3f, 0.9f));
        }
        else _ghost.Visible = false;
        if (_tool is Tool.Add or Tool.Box && _hit is { } h)
        {
            var f = h.Front;
            if (_boxFrom is { } from)
                Box(W(Math.Min(from.X, f.X), Math.Min(from.Y, f.Y), Math.Min(from.Z, f.Z)), W(Math.Max(from.X, f.X) + 1, Math.Max(from.Y, f.Y) + 1, Math.Max(from.Z, f.Z) + 1), new Color(0.5f, 0.9f, 1));
            else Box(W(f.X, f.Y, f.Z), W(f.X + 1, f.Y + 1, f.Z + 1), new Color(0.5f, 0.9f, 1));
        }
        else if (_tool is Tool.Erase or Tool.Paint or Tool.Fill or Tool.Pick && _hit is { Ground: false } g)
            Box(W(g.X, g.Y, g.Z), W(g.X + 1, g.Y + 1, g.Z + 1), _tool == Tool.Erase ? new Color(1, 0.4f, 0.4f) : Colors.White);
        if (any) im.SurfaceEnd();
        _lines.Mesh = im;
    }

    // ---- input ---------------------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _dist = Mathf.Max(2, _dist * 0.9f);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _dist = Mathf.Min(400, _dist * 1.1f);
            else if (mb.ButtonIndex == MouseButton.Right) _orbiting = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.Middle) _panning = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                Hover(mb.Position);
                if (mb.Pressed) Press(mb.AltPressed, mb.ShiftPressed);
                else Release(mb.ShiftPressed);
            }
            _lastMouse = mb.Position;
        }
        else if (e is InputEventMouseMotion mm)
        {
            var dm = mm.Position - _lastMouse;
            _lastMouse = mm.Position;
            if (_orbiting) { _yaw -= dm.X * 0.3f; _pitch = Mathf.Clamp(_pitch + dm.Y * 0.3f, -10, 89); }
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
                case Key.Z when ctrl && k.ShiftPressed: case Key.Y when ctrl: _doc.Redo(); break;
                case Key.Z when ctrl: _doc.Undo(); break;
                case Key.S when ctrl: Save(); break;
                case Key.R when _tool == Tool.Shape: _shapeTurn += (k.ShiftPressed ? -1 : 1) * Math.PI / 2; _shapeGhostKey = null; break;
                case Key.R: _rot = (_rot + (k.ShiftPressed ? 3 : 1)) & 3; _ghostKey = null; break;
                case Key.Pageup: _storey = Math.Min(_doc.Model.SizeZ / _doc.StoreyVoxels - 1, _storey + 1); DrawGrid(); break;
                case Key.Pagedown: _storey = Math.Max(0, _storey - 1); DrawGrid(); break;
                case Key.M: _doc.MirrorX(); break;
                case Key.F: Frame(); break;
                case >= Key.Key1 and <= Key.Key8: SetTool((Tool)(k.Keycode - Key.Key1)); break;
                case Key.Enter or Key.KpEnter when _tool == Tool.Shape: FinishPath(); break;
                case Key.Escape when _tool == Tool.Shape: _path.Clear(); _shapeGhostKey = null; break;
                default: return;
            }
            Refresh();
        }
    }

    void Press(bool alt, bool shift)
    {
        if (alt || _tool == Tool.Pick)
        {
            if (_hit is { Ground: false } p) SetColour(_doc[p.X, p.Y, p.Z]);
            return;
        }
        switch (_tool)
        {
            case Tool.Piece when _place is { } pl:
                if (_saved is { } s) _doc.PlaceModel(s.Model, s.Detail, pl.X, pl.Y, pl.Z, _rot);
                else if (_piece is { Snap: true }) _doc.PlacePiece(_piece, _mat, pl.X, pl.Y, pl.Z, _rot);
                else if (_piece != null) _doc.PlaceProp(_piece, pl.X, pl.Y, pl.Z, _rot);
                break;
            case Tool.Fill when _hit is { Ground: false } f: _doc.FillColour(f.X, f.Y, f.Z, _colour); break;
            case Tool.Shape when ShapeAt() is { } at:
                if (_shapeKind.GetSelectedId() == (int)ShapeKind.Sweep) { _path.Add(at); _shapeGhostKey = null; }
                else if (CurrentShape(at) is { } sh) { int n = _doc.AddShape(sh); Say($"Placed a {sh.Kind.ToString().ToLowerInvariant()}: {n} voxels, drawn smooth"); }
                else Say("Draw the profile first: at least two points, three for a closed one");
                break;
            case Tool.Box when _hit is { } b: _boxFrom = shift && !b.Ground ? (b.X, b.Y, b.Z) : b.Front; break;
            case Tool.Add or Tool.Erase or Tool.Paint when _hit is { } h:
                _doc.Begin();
                _dragging = true;
                _lastDrag = null;
                // A drag stays on the plane it started on, so adding doesn't climb towards the camera.
                (_dragAxis, _dragAt) = h.Nz != 0 ? (2, _tool == Tool.Add ? h.Front.Z : h.Z) : h.Ny != 0 ? (1, _tool == Tool.Add ? h.Front.Y : h.Y) : (0, _tool == Tool.Add ? h.Front.X : h.X);
                DragTo();
                break;
        }
        Refresh();
    }

    void DragTo()
    {
        if (_hit is not { } h) return;
        var at = _tool == Tool.Add ? h.Front : (h.X, h.Y, h.Z);
        if (_tool != Tool.Add && h.Ground) return;
        int axis = _dragAxis switch { 0 => at.X, 1 => at.Y, _ => at.Z };
        if (axis != _dragAt || _lastDrag == at) return;
        _lastDrag = at;
        if (_tool == Tool.Add) _doc.Set(at.X, at.Y, at.Z, _colour);
        else if (_tool == Tool.Erase) _doc.Set(at.X, at.Y, at.Z, 0);
        else _doc.Paint(at.X, at.Y, at.Z, _colour);
        _meshed = -1;
    }

    void Release(bool shift)
    {
        if (_dragging) { _doc.End(); _dragging = false; }
        if (_tool == Tool.Box && _boxFrom is { } a && _hit is { } h)
        {
            var b = shift && !h.Ground ? (h.X, h.Y, h.Z) : h.Front;
            _doc.Box(a.X, a.Y, a.Z, b.Item1, b.Item2, b.Item3, shift ? (byte)0 : _colour);
        }
        _boxFrom = null;
        Refresh();
    }

    // ---- the gate run's pictures ---------------------------------------------------------------------------------

    void Shots(double delta)
    {
        if ((_shotWait -= delta) > 0) return;
        void Save(string name)
        {
            string path = System.IO.Path.Combine(_shots!, name);
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"EDITOR SHOT {name}");
        }
        var m = _doc.Model;
        switch (_shotStep++)
        {
            case 0: _forcedMouse = new Vector2(-4000, -4000); _yaw = -38; _pitch = 30; _dist = m.SizeX * Vs * 1.45f; _target = new Vector3(m.SizeX * Vs / 2, 3, m.SizeY * Vs / 2); break;
            case 1: Save("editor-1-house.png"); _yaw = 145; _pitch = 24; break;
            case 2: Save("editor-2-house-back.png"); _yaw = -62; _pitch = 16; _dist = 11; _target = new Vector3(m.SizeX * Vs * 0.25f, 6.5f, m.SizeY * Vs * 0.5f); break;
            case 3: Save("editor-3-roof-close.png"); break;
            case 4:
            {
                // Placing a piece: the second storey of a new wing, with the ghost of the next wall.
                _doc = AssetDoc.New("Wing", AssetDoc.DefaultDetail, 4, 4, 3);
                for (int gx = 0; gx < 2; gx++) _doc.PlacePiece(Pieces.Find("floor")!, Mat.Concrete, gx + 1, 1, 0, 0);
                _doc.PlacePiece(Pieces.Find("wall-wide-window")!, Mat.Concrete, 1, 1, 0, 0);
                _doc.PlacePiece(Pieces.Find("wall-door")!, Mat.Concrete, 2, 1, 0, 0);
                _doc.PlacePiece(Pieces.Find("wall")!, Mat.Concrete, 1, 1, 0, 3);
                _piece = Pieces.Find("wall-window");
                _matButtons[Mat.Concrete].ButtonPressed = true;
                _mat = Mat.Concrete;
                foreach (var (p, b) in _pieceButtons) b.ButtonPressed = p == _piece;
                _rot = 1;
                _storey = 0;
                Frame();
                _yaw = -30; _pitch = 40; _dist = 17;
                break;
            }
            case 5:
            {
                // Point the pointer at frame (2, 1) on the ground storey for the ghost.
                Warp(new Vector3((2.5f * _doc.ModuleVoxels) * Vs, 0, (1.5f * _doc.ModuleVoxels) * Vs));
                break;
            }
            case 6: Save("editor-4-placing.png"); break;
            case 7:
            {
                // An L-shaped house under a hipped auto-roof, with the roof panel showing its settings.
                _doc = DemoAsset.LHouse(AssetDoc.DefaultDetail);
                SetTool(Tool.Paint);
                _forcedMouse = null;
                Frame();
                _yaw = -35; _pitch = 32; _dist = 25; _target = new Vector3(_doc.Model.SizeX * Vs * 0.4f, 3, _doc.Model.SizeY * Vs * 0.4f);
                break;
            }
            case 8: Save("editor-5-hip-roof-l.png"); break;
            case 9:
            {
                // The shape tool: a rotunda of lathes, an extruded arch and a swept rail, with the panel showing the dome's profile.
                _doc = DemoAsset.Rotunda(4);
                SetColour(Draw.Ramp(5, 7));
                UseProfile(ShapePresets.Dome(4.9, _colour));
                _forcedMouse = new Vector2(-4000, -4000);
                _meshed = -1;
                Frame();
                _yaw = -28; _pitch = 22; _dist = 26; _target = new Vector3(8, 4.5f, 8);
                break;
            }
            case 10: Save("editor-6-shapes.png"); _showVoxels.ButtonPressed = true; _dist = 21; _yaw = -40; _pitch = 18; _target = new Vector3(8, 6.5f, 8); break;
            case 11: Save("editor-7-shapes-voxels.png"); _showVoxels.ButtonPressed = false; break;
            case 12: Save("editor-8-shapes-close.png"); break;
            case 13:
            {
                // Files: save, open, and use for a building, checked.
                var house = DemoAsset.House(AssetDoc.DefaultDetail);
                string dir = System.IO.Path.Combine(_shots!, "assets");
                Directory.CreateDirectory(dir);
                house.Save(System.IO.Path.Combine(dir, "brick-house.vox"));
                var back = AssetDoc.Load(System.IO.Path.Combine(dir, "brick-house.vox"));
                bool same = back.Model.Voxels.AsSpan().SequenceEqual(house.Model.Voxels) && back.Detail == AssetDoc.DefaultDetail && back.Name == "Brick house";
                // Shapes save beside the voxels and come back the same.
                var rot = DemoAsset.Rotunda(4);
                rot.Save(System.IO.Path.Combine(dir, "rotunda.vox"));
                var rotBack = AssetDoc.Load(System.IO.Path.Combine(dir, "rotunda.vox"));
                bool shapes = rotBack.Shapes.Count == rot.Shapes.Count && rotBack.Shapes.Zip(rot.Shapes).All(p => p.First.ToJson().ToJsonString() == p.Second.ToJson().ToJsonString());
                GD.Print($"EDITOR SHAPES {(shapes ? "OK" : "DIFFER")} {rotBack.Shapes.Count} shapes");
                same &= shapes;
                _doc = back;
                string used = _doc.UseForBuilding(System.IO.Path.Combine(_shots!, "vox"), "res-low-1");
                GD.Print($"EDITOR FILES {(same ? "OK" : "DIFFER")} {System.IO.Path.GetFileName(used)} {_doc.Model.Count} voxels");
                GD.Print(same ? "EDITOR OK" : "EDITOR FAILED");
                GetTree().Quit(same ? 0 : 1);
                break;
            }
        }
        _shotWait = 0.6;
    }

    /// <summary>Moves the pointer onto a point in the world, for the shot of placing a piece.</summary>
    void Warp(Vector3 world)
    {
        var at = _cam.UnprojectPosition(world);
        GetViewport().WarpMouse(at);
        Hover(at);
        _forcedMouse = at;
    }

    Vector2? _forcedMouse;
}
