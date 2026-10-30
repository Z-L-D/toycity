using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.Godot;

/// <summary>
/// The map tools: a toolbar across the top with each tool's options under it, the preview drawn over the map with
/// a cost tag at the cursor, key hints in the lower left, and the server's warnings as toasts. What a tool would do
/// comes from <see cref="Planner"/>, which the tests check against the server; this class only draws and sends it.
/// Placement snaps by default; holding Alt places freely.
/// </summary>
public partial class ToolController : Node
{
    readonly CityClient _client;
    readonly CityView _view;
    readonly ClassicCamera _camera;
    readonly MapView _map;
    public readonly ToolState State = new();
    Control? _column;
    bool _enabled = true;

    /// <summary>Off while the bus company's view is up: no toolbar, previews or clicks.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _column!.Visible = _hints!.Visible = value;
            if (value) return;
            _drag = null;
            State.CurveEnds = null;
            _preview = null;
            Cursor = null;
            ((ImmediateMesh)_overlay!.Mesh).ClearSurfaces();
            _cost!.Visible = false;
        }
    }

    /// <summary>The query tool picked a cell.</summary>
    public event Action<int, int>? Queried;
    /// <summary>Sends an action, or keeps it for a what-if plan; null sends it straight to the city.</summary>
    public Func<PAction, string, bool>? Act;
    (int X, int Y)? _drag;
    (int X, int Y)? _held;  // a pointer held by Hold, for screenshots: the mouse is ignored while it is set

    /// <summary>Holds the pointer at a cell, dragging from another, so a screenshot can show a tool's preview.</summary>
    public void Hold((int X, int Y)? at, (int X, int Y)? from = null) { _held = at; _drag = from; }
    Preview? _preview;
    MeshInstance3D? _overlay;
    Label? _cost;
    VBoxContainer? _toasts, _hints;
    VBoxContainer? _options;
    readonly Dictionary<ToolKind, Button> _buttons = new();
    readonly Dictionary<ToolKind, VBoxContainer> _optionBoxes = new();

    /// <summary>The screen's frame, as the web client's: the top bar, the tool column on the left, the panel on the right.</summary>
    public const int TopHeight = 46, ColumnWidth = 168, SideWidth = 360;

    /// <summary>A control covering the map between the bars, for things placed over the map.</summary>
    public static Control MapArea()
    {
        var a = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        a.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        (a.OffsetLeft, a.OffsetTop, a.OffsetRight, a.OffsetBottom) = (ColumnWidth, TopHeight, -SideWidth, 0);
        return a;
    }
    int _lastSpeed = 1;
    /// <summary>The cursor's place on the map last frame, or null off it.</summary>
    public (int X, int Y, float Fx, float Fy)? Cursor { get; private set; }

    public ToolController(CityClient client, CityView view, ClassicCamera camera, MapView map)
    {
        (_client, _view, _camera, _map) = (client, view, camera, map);
    }

    static readonly (ToolKind Kind, string Name, Key Key)[] Tools =
    {
        (ToolKind.Query, "Query", Key.Q), (ToolKind.Bulldoze, "Bulldoze", Key.B), (ToolKind.Path, "Roads", Key.R),
        (ToolKind.Zone, "Zone", Key.Z), (ToolKind.Dezone, "De-zone", Key.X), (ToolKind.Utility, "Utilities", Key.L),
        (ToolKind.Building, "Buildings", Key.N), (ToolKind.Terraform, "Terraform", Key.F), (ToolKind.Disaster, "Disasters", Key.H),
    };

    /// <summary>The tool column's sections, as the web client groups them.</summary>
    static readonly (string Heading, ToolKind[] Kinds)[] Sections =
    {
        ("Look", new[] { ToolKind.Query, ToolKind.Bulldoze }), ("Paths", new[] { ToolKind.Path }), ("Zones", new[] { ToolKind.Zone, ToolKind.Dezone }),
        ("Utilities", new[] { ToolKind.Utility }), ("Civic", new[] { ToolKind.Building }), ("Land", new[] { ToolKind.Terraform }),
        ("Disasters", new[] { ToolKind.Disaster }),
    };

    public override void _Ready()
    {
        _overlay = new MeshInstance3D
        {
            Mesh = new ImmediateMesh(),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        _view.AddChild(_overlay);

        var hud = new CanvasLayer { Layer = 2 };
        AddChild(hud);
        // The tool column down the left: each section's tools, and the chosen tool's options under its button.
        var column = new PanelContainer();
        column.AddThemeStyleboxOverride("panel", UiTheme.Docked(Side.Right, 8));
        column.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        (column.OffsetLeft, column.OffsetTop, column.OffsetRight, column.OffsetBottom) = (0, TopHeight, ColumnWidth, 0);
        hud.AddChild(column);
        _column = column;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);
        var group = new ButtonGroup();
        foreach (var (heading, kinds) in Sections)
        {
            var h = UiTheme.Heading(heading);
            if (list.GetChildCount() > 0) h.CustomMinimumSize = new Vector2(0, 22);
            h.VerticalAlignment = VerticalAlignment.Bottom;
            list.AddChild(h);
            foreach (var kind in kinds)
            {
                var (_, name, key) = Tools.First(t => t.Kind == kind);
                var b = new Button { Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left };
                // The key, small and grey at the button's right, as the web's tool buttons show it.
                var k = new Label { Text = key.ToString(), MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center,
                    LabelSettings = new LabelSettings { FontSize = 10, FontColor = UiTheme.Muted } };
                k.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);
                k.OffsetLeft = -22;
                b.AddChild(k);
                b.Pressed += () => SetTool(kind);
                list.AddChild(b);
                _buttons[kind] = b;
                var opts = new VBoxContainer { Visible = false };
                opts.AddThemeConstantOverride("separation", 3);
                list.AddChild(opts);
                _optionBoxes[kind] = opts;
            }
        }

        var area = MapArea();
        hud.AddChild(area);
        _cost = new Label { Visible = false };
        _cost.AddThemeStyleboxOverride("normal", UiTheme.ChipBox(UiTheme.Line));
        hud.AddChild(_cost);
        // Key hints in the map's lower left, a dark tag each.
        _hints = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hints.AddThemeConstantOverride("separation", 3);
        _hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 10);
        _hints.GrowVertical = Control.GrowDirection.Begin;
        area.AddChild(_hints);
        _toasts = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _toasts.AddThemeConstantOverride("separation", 4);
        _toasts.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 48);
        _toasts.GrowVertical = Control.GrowDirection.Begin;
        _toasts.GrowHorizontal = Control.GrowDirection.Both;
        area.AddChild(_toasts);

        _client.Message += OnMessage;
        SetTool(ToolKind.Query);
    }

    public void SetTool(ToolKind kind)
    {
        State.Kind = kind;
        State.CurveEnds = null;
        _drag = null;
        if (_buttons.TryGetValue(kind, out var b)) b.SetPressedNoSignal(true);
        BuildOptions();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Options under the toolbar

    /// <summary>Chooses a building for the place tool, as if picked from its list.</summary>
    public void Pick(string typeId)
    {
        if (!_client.City.TypeById.TryGetValue(typeId, out var t)) return;
        State.BuildingTypeId = typeId;
        SetTool(ToolKind.Building);
        Toast($"Placing {t.Name}: click the map to build it.", "info");
    }

    /// <summary>Redraws the options, for when what they list changed, such as the buildings unlocked.</summary>
    public void RebuildOptions() => BuildOptions();

    void BuildOptions()
    {
        if (_optionBoxes.Count == 0) return;
        foreach (var (kind, box) in _optionBoxes)
        {
            foreach (var child in box.GetChildren()) child.QueueFree();
            box.Visible = false;
        }
        if (!_optionBoxes.TryGetValue(State.Kind, out _options)) return;
        var c = _client.City;
        switch (State.Kind)
        {
            case ToolKind.Zone:
                Choice(new[] { "Residential", "Commercial", "Industrial" }, (int)State.Zone - 1, k => State.Zone = (ZoneKind)(k + 1), ZoneColours);
                Choice(Planner.DensityNames.Select(n => n[..1]).ToArray(), (int)State.Density, k => State.Density = (Density)k, tips: Planner.DensityNames);
                Choice(new[] { "Area", "Block" }, State.BlockZoning ? 1 : 0, k => State.BlockZoning = k == 1, tips: new[] { "Paint zones over an area", "Zone whole blocks (G)" });
                if (State.BlockZoning) Choice(new[] { "Leave leftovers", "Leftovers to parks", "Zone leftovers" }, (int)State.Leftover, k => State.Leftover = (Leftover)k);
                break;
            case ToolKind.Path:
                Dropdown(c.Profiles.Select(p => (p.Id, $"{p.Name} ({Planner.Money(p.CostPerCell)}/cell)")).ToList(), State.ProfileId, id => State.ProfileId = id);
                Choice(new[] { "Straight", "Curve" }, State.Curve ? 1 : 0, k => { State.Curve = k == 1; State.CurveEnds = null; }, tips: new[] { "Straight roads", "Curved roads (C)" });
                break;
            case ToolKind.Utility:
                Choice(new[] { "Power line", "Water pipe" }, (int)State.Utility, k => State.Utility = (UtilityKind)k);
                break;
            case ToolKind.Building:
            {
                // Landmarks and reward lots stay locked until earned, and a one-per-city building once it's built.
                var items = new List<(string, string, bool)>();
                foreach (var t in c.Types.Where(t => t.Placeable).OrderBy(t => t.Category).ThenBy(t => t.Cost))
                {
                    bool locked = !c.Unlocked.Contains(t.Id) && t.UnlockText != "";
                    bool built = t.Unique && c.Buildings.Values.Any(b => b.TypeId == t.Id);
                    string note = locked ? $" 🔒 {t.UnlockText}" : built ? " (built)" : $" ({(t.Cost > 0 ? Planner.Money(t.Cost) : "free")})";
                    items.Add((t.Id, $"{Cap(t.Category)}: {t.Name}{note}", locked || built));
                }
                Dropdown(items, State.BuildingTypeId, id => State.BuildingTypeId = id);
                break;
            }
            case ToolKind.Terraform:
                Choice(Planner.TerraformNames, (int)State.Terraform, k => State.Terraform = (TerraformMode)k);
                break;
            case ToolKind.Disaster:
                Choice(Planner.DisasterNames, (int)State.Disaster, k => State.Disaster = (DisasterKind)k);
                _options!.AddChild(new Label { Text = "By themselves", LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Muted } });
                Choice(new[] { "Off", "Rare", "Normal" }, (int)c.Stats.DisasterSetting,
                    k => _client.Connection.Act(new PAction { Disasters = new SetDisasters { Setting = (DisasterSetting)k } }));
                break;
            case ToolKind.Bulldoze:
                Choice(new[] { "Above ground", "Pipes only (U)" }, State.Underground ? 1 : 0, k => State.Underground = k == 1);
                break;
        }
        _options.Visible = _options.GetChildCount() > 0;
        UpdateHints();
    }

    static readonly Color[] ZoneColours = { new(0.3f, 0.69f, 0.31f), new(0.29f, 0.5f, 0.88f), new(0.88f, 0.69f, 0.19f) };
    static readonly Dictionary<Color, Texture2D> Swatches = new();

    /// <summary>A small square of colour for a button's icon, as the web's tool buttons have.</summary>
    static Texture2D Swatch(Color c)
    {
        if (Swatches.TryGetValue(c, out var t)) return t;
        var img = Image.CreateEmpty(10, 10, false, Image.Format.Rgba8);
        img.Fill(c);
        return Swatches[c] = ImageTexture.CreateFromImage(img);
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// A set of choices as a row of small buttons, or one per line when the names are long or carry a colour.
    /// </summary>
    void Choice(string[] names, int selected, Action<int> pick, Color[]? colours = null, string[]? tips = null)
    {
        var group = new ButtonGroup();
        bool row = colours == null && names.Length <= 3 && names.All(n => n.Length <= 8);
        Container box = row ? new HBoxContainer() : new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        _options!.AddChild(box);
        for (int k = 0; k < names.Length; k++)
        {
            int kk = k;
            var b = UiTheme.Small(new Button
            {
                Text = names[k], ToggleMode = true, ButtonGroup = group, ButtonPressed = k == selected, FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = row ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                TooltipText = tips?[k] ?? "", ClipText = true,
            }, row ? 2 : 8, 3, 13);
            if (colours != null) b.Icon = Swatch(colours[k]);
            b.Pressed += () => { pick(kk); BuildOptions(); };
            box.AddChild(b);
        }
    }

    void Dropdown(List<(string Id, string Label)> items, string selected, Action<string> pick) =>
        Dropdown(items.Select(i => (i.Id, i.Label, false)).ToList(), selected, pick);

    void Dropdown(List<(string Id, string Label, bool Disabled)> items, string selected, Action<string> pick)
    {
        var o = UiTheme.Small(new OptionButton { FocusMode = Control.FocusModeEnum.None, ClipText = true, FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 8, 3, 13);
        for (int k = 0; k < items.Count; k++)
        {
            o.AddItem(items[k].Label, k);
            o.SetItemDisabled(k, items[k].Disabled);
            if (items[k].Id == selected) o.Selected = k;
        }
        if (items.FirstOrDefault(i => !i.Disabled) is { Id: not null } first && items.All(i => i.Id != selected || i.Disabled))
        {
            pick(first.Id);
            o.Selected = items.IndexOf(first);
        }
        o.ItemSelected += k => { pick(items[(int)k].Id); UpdateHints(); };
        _options!.AddChild(o);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Key hints, lower left

    void UpdateHints()
    {
        if (_hints == null) return;
        string density = Planner.DensityNames[(int)State.Density].ToLowerInvariant();
        var per = State.Kind switch
        {
            ToolKind.Query => new[] { ("Click", "select") },
            ToolKind.Bulldoze => new[] { ("Drag", State.Underground ? "remove pipes in area" : "clear area"), ("U", "pipes only"), ("Esc", "cancel") },
            ToolKind.Path when State.Curve => State.CurveEnds == null
                ? new[] { ("Drag", "set the two ends"), ("C", "straight roads"), ("Esc", "cancel") }
                : new[] { ("Move", "bend the curve"), ("Click", "build it"), ("Esc", "start again") },
            ToolKind.Path => new[] { ("Drag", "draw a straight road"), ("Alt", "free: follow the cursor"), ("Alt+Shift", "vertical leg first"), ("C", "curved roads"), ("Esc", "cancel") },
            ToolKind.Zone when State.BlockZoning => new[] { ("Click", $"zone a whole block, {density} density"), ("G", "paint areas"), ("V", "cycle density"), ("Z", "next zone"), ("Esc", "cancel") },
            ToolKind.Zone => new[] { ("Drag", $"paint {density} density zone"), ("G", "zone whole blocks"), ("V", "cycle density"), ("Z", "next zone"), ("Esc", "cancel") },
            ToolKind.Dezone => new[] { ("Drag", "remove zoning"), ("Esc", "cancel") },
            ToolKind.Utility => new[] { ("Drag", "draw a straight line"), ("Alt", "free: follow the cursor"), ("L", "power or water"), ("Esc", "cancel") },
            ToolKind.Building => new[] { ("Click", "place"), ("Alt", "free: exactly at the cursor"), ("Esc", "cancel") },
            ToolKind.Terraform => new[] { ("Drag", $"{Planner.TerraformNames[(int)State.Terraform].ToLowerInvariant()} area"), ("Esc", "cancel") },
            _ => new[] { ("Click", $"start a {Planner.DisasterNames[(int)State.Disaster].ToLowerInvariant()} here"), ("Esc", "cancel") },
        };
        var common = new[] { (", .", "rotate"), ("Wheel", "zoom"), ("Right-drag", "pan"), ("T / Y", "top-down / side"), ("Space", "pause"), ("P / M / U", "panel / commuters / pipes") };
        foreach (var child in _hints.GetChildren()) child.QueueFree();
        foreach (var (key, what) in per.Concat(common))
        {
            // A dark tag with the key in a key cap, as the web client's hints.
            var tag = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            tag.AddThemeStyleboxOverride("panel", UiTheme.ChipBox());
            var r = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            r.AddThemeConstantOverride("separation", 6);
            tag.AddChild(r);
            var cap = new Label { Text = key, LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Text } };
            var capBox = UiTheme.Box(UiTheme.Panel2, UiTheme.HoverLine, 4, 4, 0);
            capBox.BorderWidthBottom = 2;
            cap.AddThemeStyleboxOverride("normal", capBox);
            r.AddChild(cap);
            r.AddChild(new Label { Text = what, LabelSettings = new LabelSettings { FontSize = 12, FontColor = UiTheme.Text } });
            _hints.AddChild(tag);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Input

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_enabled) return;
        var c = _client.City;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.Keycode == Key.Escape)
            {
                if (_drag != null || State.CurveEnds != null) { _drag = null; State.CurveEnds = null; UpdateHints(); }
                else SetTool(ToolKind.Query);
            }
            else if (k.Keycode == Key.Space)
            {
                int now = c.Stats.Speed;
                if (now > 0) _lastSpeed = now;
                _client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = now > 0 ? 0 : Math.Max(1, _lastSpeed) } });
            }
            else if (k.Keycode == Key.Z && State.Kind == ToolKind.Zone) { State.Zone = (ZoneKind)((int)State.Zone % 3 + 1); BuildOptions(); }
            else if (k.Keycode == Key.L && State.Kind == ToolKind.Utility) { State.Utility = 1 - State.Utility; BuildOptions(); }
            else if (Tools.FirstOrDefault(t => t.Key == k.Keycode) is { Name: not null } tool) SetTool(tool.Kind);
            else if (k.Keycode == Key.V && State.Kind == ToolKind.Zone) { State.Density = (Density)(((int)State.Density + 1) % 3); BuildOptions(); }
            else if (k.Keycode == Key.G && State.Kind == ToolKind.Zone) { State.BlockZoning = !State.BlockZoning; BuildOptions(); }
            else if (k.Keycode == Key.C && State.Kind == ToolKind.Path) { State.Curve = !State.Curve; State.CurveEnds = null; BuildOptions(); }
            else if (k.Keycode == Key.U && State.Kind == ToolKind.Bulldoze) { State.Underground = !State.Underground; BuildOptions(); }
            else return;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) return;
        if (mb.Pressed && _map.CellAt(mb.Position) is { } m)
        {
            _camera.CentreOn(m.X + 0.5f, m.Y + 0.5f);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!c.Loaded || Cursor is not { } at) return;
        GetViewport().SetInputAsHandled();
        if (mb.Pressed)
        {
            bool click = State.Kind is ToolKind.Building or ToolKind.Disaster
                || State.Kind == ToolKind.Zone && State.BlockZoning
                || State.Kind == ToolKind.Path && State.CurveEnds != null;
            if (State.Kind == ToolKind.Query) { Selected = (at.X, at.Y); Queried?.Invoke(at.X, at.Y); return; }
            if (click)
            {
                Commit(Plan(at));
                if (State.CurveEnds != null) { State.CurveEnds = null; UpdateHints(); }
                return;
            }
            _drag = (at.X, at.Y);
        }
        else if (_drag is { } start)
        {
            if (State.Kind == ToolKind.Path && State.Curve)
            {
                // The ends are set; the curve bends with the mouse until the next click.
                var end = Planner.SnapEnd(start, at.X, at.Y, true);
                if (end != start) State.CurveEnds = (start.X, start.Y, end.X, end.Y);
                _drag = null;
                UpdateHints();
                return;
            }
            Commit(Plan(at));
            _drag = null;
        }
    }

    /// <summary>The cell picked with the query tool, for the panels to show.</summary>
    public (int X, int Y)? Selected { get; private set; }

    /// <summary>The preview under the cursor this frame.</summary>
    public Preview? Current => _preview;

    Preview? Plan((int X, int Y, float Fx, float Fy) at) =>
        Planner.Plan(_client.City, State, new Pointer(at.X, at.Y, at.Fx, at.Fy, _drag, Input.IsKeyPressed(Key.Alt), Input.IsKeyPressed(Key.Shift)));

    /// <summary>Uses a tool at a pointer as a click there would, for the gate run: plans it, sends it, returns the plan.</summary>
    public Preview? Use(ToolState tool, Pointer at)
    {
        var p = Planner.Plan(_client.City, tool, at);
        Commit(p);
        return p;
    }

    void Commit(Preview? p)
    {
        if (p == null) return;
        if (!p.Ok || p.Actions.Count == 0)
        {
            Toast(string.IsNullOrEmpty(p.Text) ? "Can't do that here." : p.Text, "warning");
            return;
        }
        foreach (var a in p.Actions)
            if (Act != null) Act(a, p.Label);
            else _client.Connection.Act(a);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Preview and cost tag, each frame

    static Color TintColor(Tint t, ToolState s) => t switch
    {
        Tint.Bad => new Color(1, 0.25f, 0.2f, 0.55f),
        Tint.Zone => s.Kind == ToolKind.Zone ? new[] { Colors.White, new Color(0.3f, 0.69f, 0.31f), new Color(0.29f, 0.5f, 0.88f), new Color(0.88f, 0.69f, 0.19f) }[(int)s.Zone] with { A = 0.5f } : new Color(1, 1, 1, 0.3f),
        Tint.Leftover => new Color(1, 1, 1, 0.18f),
        Tint.Road => new Color(0.75f, 0.75f, 0.8f, 0.6f),
        Tint.Power => new Color(1, 0.85f, 0.29f, 0.65f),
        Tint.Water => new Color(0.37f, 0.78f, 1, 0.65f),
        Tint.Clear => new Color(1, 0.35f, 0.24f, 0.4f),
        Tint.Earth => new Color(0.78f, 0.63f, 0.35f, 0.45f),
        Tint.Stop => new Color(1, 0.84f, 0, 0.8f),
        _ => new Color(0.4f, 1, 0.5f, 0.45f),
    };

    public override void _Process(double delta)
    {
        if (!_enabled) return;
        var c = _client.City;
        Cursor = _held is { } held ? (held.X, held.Y, 0.5f, 0.5f)
            : c.Loaded && GetViewport().GuiGetHoveredControl() == null ? _camera.PickPoint(GetViewport().GetMousePosition(), _view) : null;
        _preview = Cursor is { } at ? Plan(at) : null;
        DrawPreview();
        if (_preview != null && !string.IsNullOrEmpty(_preview.Text) && Cursor != null)
        {
            _cost!.Visible = true;
            _cost.Text = _preview.Text;
            _cost.AddThemeColorOverride("font_color", _preview.Ok && c.Stats.Funds >= 0 ? UiTheme.Text : UiTheme.Bad);
            _cost.Position = GetViewport().GetMousePosition() + new Vector2(18, 18);
        }
        else _cost!.Visible = false;
    }

    void DrawPreview()
    {
        var mesh = (ImmediateMesh)_overlay!.Mesh;
        mesh.ClearSurfaces();
        if (_preview == null || _preview.Cells.Count == 0) return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (x, y, tint) in _preview.Cells)
        {
            if (!_client.City.InBounds(x, y)) continue;
            float h = _view.Top(x, y) + 0.03f;
            float x0 = x, z0 = y, x1 = x + 1, z1 = y + 1;
            if (_preview.Side >= 0)
            {
                // A stop: the kerb strip on its side of the road cell.
                const float k = 0.22f;
                (x0, z0, x1, z1) = _preview.Side switch { 0 => (x0, z0, x1, z0 + k), 1 => (x1 - k, z0, x1, z1), 2 => (x0, z1 - k, x1, z1), _ => (x0, z0, x0 + k, z1) };
            }
            mesh.SurfaceSetColor(TintColor(tint, State));
            foreach (var (vx, vz) in new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z0), (x1, z1), (x0, z1) })
                mesh.SurfaceAddVertex(new Vector3(vx, h, vz));
        }
        mesh.SurfaceEnd();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Toasts: the server's warnings (a refused action says why) and good news

    void OnMessage(ServerMessage m)
    {
        if (m.BodyCase == ServerMessage.BodyOneofCase.Notice) Toast(m.Notice.Text, m.Notice.Severity);
        else if (m.BodyCase == ServerMessage.BodyOneofCase.Error) Toast(m.Error.Message, "warning");
        else if (m.BodyCase == ServerMessage.BodyOneofCase.Delta)
            foreach (var n in m.Delta.Notices.Where(n => n.Severity == "warning")) Toast(n.Text, n.Severity);
    }

    public void Toast(string text, string severity)
    {
        if (_toasts == null || string.IsNullOrEmpty(text)) return;
        // A dark tag with a border in the news's colour, as the web client's toasts.
        var l = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        var edge = severity switch { "warning" => UiTheme.Warn, "error" or "danger" => UiTheme.Bad, "good" => UiTheme.Good, _ => UiTheme.Line };
        var box = UiTheme.Box(UiTheme.Chip with { A = 0.92f }, edge, 6, 12, 6);
        l.AddThemeStyleboxOverride("panel", box);
        l.AddChild(new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center });
        _toasts.AddChild(l);
        while (_toasts.GetChildCount() > 4) _toasts.GetChild(0).Free();
        GetTree().CreateTimer(4.0).Timeout += () => { if (IsInstanceValid(l)) l.QueueFree(); };
    }
}
