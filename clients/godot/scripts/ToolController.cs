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
    Control? _column, _flyout, _toolopts;
    VBoxContainer? _railList, _flyList;
    readonly BuildMenu _menu = new();
    List<BuildCategory> _cats = new();
    bool _enabled = true;

    /// <summary>Off while the bus company's view is up: no toolbar, previews or clicks.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _column!.Visible = _hints!.Visible = value;
            if (!value) _flyout!.Visible = _toolopts!.Visible = false;
            else { BuildRail(); BuildFlyout(); BuildOptions(); }
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
    HBoxContainer? _options;

    /// <summary>
    /// The screen's frame. The side games keep the web client's docked one: a bar across the top, a column down the
    /// left and the panel on the right. The mayor's view (UI A, Beta 4) floats glass over a full-window map: the build
    /// rail down the left (<see cref="RailWidth"/> with its margin) and the L along the bottom (<see cref="BarHeight"/>).
    /// </summary>
    public const int TopHeight = 46, ColumnWidth = 168, SideWidth = 360, RailWidth = 74, BarHeight = 80;

    /// <summary>A control covering the map between the bars, for things placed over the map.</summary>
    public static Control MapArea()
    {
        var a = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        a.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        (a.OffsetLeft, a.OffsetTop, a.OffsetRight, a.OffsetBottom) = (RailWidth, 0, 0, -BarHeight);
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

    /// <summary>The rail's keys in this client, by category.</summary>
    static readonly Dictionary<string, ToolKind> RailTools = new()
    {
        ["zones"] = ToolKind.Zone, ["transport"] = ToolKind.Path, ["utilities"] = ToolKind.Utility, ["civic"] = ToolKind.Building,
        ["land"] = ToolKind.Terraform, ["disasters"] = ToolKind.Disaster, ["bulldoze"] = ToolKind.Bulldoze,
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
        // The build rail down the left (UI A): query, then a button per category, each opening a flyout of tiles.
        var rail = new PanelContainer();
        rail.AddThemeStyleboxOverride("panel", UiTheme.GlassBox(12, 6, 6));
        rail.Position = new Vector2(12, 12);
        hud.AddChild(rail);
        _column = rail;
        _railList = new VBoxContainer();
        _railList.AddThemeConstantOverride("separation", 4);
        rail.AddChild(_railList);
        // The flyout, beside the rail.
        var fly = new PanelContainer { Visible = false, Position = new Vector2(RailWidth, 12), CustomMinimumSize = new Vector2(360, 0) };
        fly.AddThemeStyleboxOverride("panel", UiTheme.GlassBox(12, 12, 10));
        hud.AddChild(fly);
        _flyout = fly;
        _flyList = new VBoxContainer();
        _flyList.AddThemeConstantOverride("separation", 8);
        fly.AddChild(_flyList);
        // The tool in hand and its options, along the top beside the rail.
        var opts = new PanelContainer { Visible = false, Position = new Vector2(RailWidth, 12) };
        opts.AddThemeStyleboxOverride("panel", UiTheme.GlassBox(12, 10, 6));
        hud.AddChild(opts);
        _toolopts = opts;
        _options = new HBoxContainer();
        _options.AddThemeConstantOverride("separation", 8);
        opts.AddChild(_options);

        var area = MapArea();
        hud.AddChild(area);
        _cost = new Label { Visible = false };
        _cost.AddThemeStyleboxOverride("normal", UiTheme.ChipBox(UiTheme.Line));
        hud.AddChild(_cost);
        // Key hints in the map's lower left, a dark tag each.
        _hints = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hints.AddThemeConstantOverride("separation", 3);
        _hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 10);
        _hints.Position += new Vector2(160, 0);
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
        BuildRail();
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
    public void RebuildOptions() { BuildRail(); BuildFlyout(); BuildOptions(); }

    /// <summary>Straight (0), diagonal (1) or curved (2) roads.</summary>
    int PathMode => State.Curve ? 2 : State.Diagonal ? 1 : 0;

    void SetPathMode(int k)
    {
        State.Diagonal = k == 1;
        State.Curve = k == 2;
        State.CurveEnds = null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The build rail and its flyout (UI A, Beta 4): the shared BuildMenu, as the web client draws it.

    void BuildRail()
    {
        if (_railList == null) return;
        _cats = BuildMenu.For(_client.City);
        string? lit = BuildMenu.CategoryOf(State, _cats);
        foreach (var child in _railList.GetChildren()) child.QueueFree();
        var q = RailButton("query", "Query (Q): click anything to see why it is the way it is", "Q", State.Kind == ToolKind.Query);
        q.Pressed += () => { _menu.Close(); SetTool(ToolKind.Query); BuildFlyout(); };
        _railList.AddChild(new HSeparator { Modulate = new Color(1, 1, 1, 0.5f) });
        foreach (var c in _cats)
        {
            string key = RailTools.TryGetValue(c.Id, out var tk) && Tools.FirstOrDefault(t => t.Kind == tk) is { Name: not null } t ? t.Key.ToString() : "";
            var b = RailButton(c.Icon, c.Name + (key.Length > 0 ? $" ({key})" : ""), key, _menu.Open == c.Id || (_menu.Open == null && lit == c.Id && c.Id != "bulldoze") || (c.Id == "bulldoze" && State.Kind == ToolKind.Bulldoze));
            string id = c.Id;
            b.Pressed += () =>
            {
                if (id == "bulldoze") { _menu.Close(); SetTool(ToolKind.Bulldoze); }
                else _menu.Toggle(id);
                BuildRail();
                BuildFlyout();
            };
        }
    }

    Button RailButton(string icon, string tip, string key, bool on)
    {
        var b = UiTheme.IconButton(icon, tip, 22, 9);
        b.ToggleMode = true;
        b.SetPressedNoSignal(on);
        b.CustomMinimumSize = new Vector2(42, 42);
        if (key.Length > 0)
        {
            var k = new Label { Text = key, MouseFilter = Control.MouseFilterEnum.Ignore, LabelSettings = new LabelSettings { FontSize = 9, FontColor = on ? new Color("#dfe7ff") : UiTheme.Muted } };
            k.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            (k.OffsetLeft, k.OffsetTop, k.OffsetRight, k.OffsetBottom) = (-10, -14, -2, 0);
            b.AddChild(k);
        }
        _railList!.AddChild(b);
        return b;
    }

    /// <summary>The flyout: title, what it's for, the pin, the group tabs and the tiles with their prices.</summary>
    void BuildFlyout()
    {
        if (_flyList == null) return;
        foreach (var child in _flyList.GetChildren()) child.QueueFree();
        var shown = _menu.Shown(_cats);
        _flyout!.Visible = shown != null && _enabled;
        _toolopts!.Visible = _enabled && shown == null && _options!.GetChildCount() > 0;
        if (shown is not { } sh) return;
        var (cat, group) = sh;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        _flyList.AddChild(head);
        var badge = new PanelContainer { CustomMinimumSize = new Vector2(32, 32) };
        badge.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.UiAccent, null, 8, 6, 6));
        badge.AddChild(new TextureRect { Texture = UiTheme.Icon(cat.Icon, 20, Colors.White), StretchMode = TextureRect.StretchModeEnum.KeepCentered });
        head.AddChild(badge);
        head.AddChild(new Label { Text = cat.Name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 16, FontColor = Colors.White } });
        var pin = UiTheme.IconButton("pin", _menu.Pinned ? "Pinned open: click to let it close after a pick" : "Pin it open", 16, 6);
        pin.ToggleMode = true;
        pin.SetPressedNoSignal(_menu.Pinned);
        pin.Pressed += () => { _menu.Pin(); BuildFlyout(); };
        head.AddChild(pin);
        var close = UiTheme.IconButton("close", "Close (Esc)", 16, 6);
        close.Pressed += () => { _menu.Close(); BuildRail(); BuildFlyout(); };
        head.AddChild(close);
        _flyList.AddChild(new Label { Text = cat.Description, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(336, 0),
            LabelSettings = new LabelSettings { FontSize = 12, FontColor = new Color("#9aa6bd") } });
        if (cat.Groups.Count > 1)
        {
            var tabs = new HFlowContainer();
            tabs.AddThemeConstantOverride("h_separation", 4);
            tabs.AddThemeConstantOverride("v_separation", 4);
            _flyList.AddChild(tabs);
            for (int k = 0; k < cat.Groups.Count; k++)
            {
                int kk = k;
                var t = UiTheme.Flat(new Button { Text = cat.Groups[k].Name, ToggleMode = true, FocusMode = Control.FocusModeEnum.None }, 10, 3, 14);
                t.AddThemeFontSizeOverride("font_size", 12);
                t.SetPressedNoSignal(k == group);
                t.Pressed += () => { _menu.PickGroup(kk); BuildFlyout(); };
                tabs.AddChild(t);
            }
        }
        var items = cat.Groups[group].Items;
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        foreach (var item in items) grid.AddChild(Tile(item));
        // Tall lists scroll, leaving room for the L at the bottom.
        int rows = (items.Count + 2) / 3;
        float room = GetViewport().GetVisibleRect().Size.Y - 330;
        if (rows * 104 > room)
        {
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(336, Math.Max(200, room)) };
            scroll.AddChild(grid);
            _flyList.AddChild(scroll);
        }
        else _flyList.AddChild(grid);
        _flyout.ResetSize();
    }

    static readonly Color[] TileZone = { new("#4caf50"), new("#4a7fe0"), new("#e0b030") };

    Button Tile(BuildItem item)
    {
        bool on = BuildMenu.IsCurrent(item, State), locked = item.Locked.Length > 0;
        var b = new Button { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(108, 96), TooltipText = item.Name + (locked ? $" ({item.Locked})" : "") };
        var edge = on ? UiTheme.UiAccent : UiTheme.GlassLine;
        var fill = on ? UiTheme.UiAccent with { A = 0.22f } : new Color(1, 1, 1, 0.04f);
        b.AddThemeStyleboxOverride("normal", UiTheme.Box(fill, edge, 10, 4, 8));
        b.AddThemeStyleboxOverride("hover", UiTheme.Box(on ? fill : new Color(1, 1, 1, 0.08f), on ? edge : new Color(1, 1, 1, 0.18f), 10, 4, 8));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Box(fill, UiTheme.UiAccent, 10, 4, 8));
        if (locked) b.Modulate = new Color(1, 1, 1, 0.45f);
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        (col.OffsetTop, col.OffsetBottom) = (8, -6);
        col.AddThemeConstantOverride("separation", 3);
        b.AddChild(col);
        var iconBox = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(38, 38) };
        iconBox.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(1, 1, 1, 0.06f), null, 9, 7, 7));
        var tint = item.Kind == ToolKind.Zone ? TileZone[(int)item.Zone - 1] : UiTheme.Soft;
        iconBox.AddChild(new TextureRect { Texture = UiTheme.Icon(item.Icon, 24, tint), MouseFilter = Control.MouseFilterEnum.Ignore, StretchMode = TextureRect.StretchModeEnum.KeepCentered });
        col.AddChild(iconBox);
        col.AddChild(new Label { Text = item.Name, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(100, 0), LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Text } });
        string price = locked ? $"🔒 {item.Locked}" : item.Price;
        if (price.Length > 0)
            col.AddChild(new Label { Text = price, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true, CustomMinimumSize = new Vector2(100, 0),
                LabelSettings = new LabelSettings { FontSize = 11, FontColor = locked ? new Color("#9aa6bd") : UiTheme.Gold } });
        b.Pressed += () =>
        {
            if (!_menu.Pick(item, State)) { Toast($"{item.Name}: {(item.Locked == "Built" ? "there can only be one, and it's built" : item.Locked)}", "warning"); return; }
            State.CurveEnds = null;
            _drag = null;
            BuildRail();
            BuildOptions();
            BuildFlyout();
        };
        return b;
    }

    // For the gate: the menu worked as a player would.

    /// <summary>The open flyout's category, or null.</summary>
    public string? FlyoutShown => _flyout is { Visible: true } ? _menu.Open : null;
    public bool OptionsShown => _toolopts is { Visible: true };
    public IReadOnlyList<BuildCategory> Menu => _cats;
    /// <summary>The tiles in the flyout as drawn.</summary>
    public int TilesShown => _flyList?.FindChildren("*", "GridContainer", true, false).Sum(g => g.GetChildCount()) ?? 0;

    /// <summary>A rail button's press, then a group tab's.</summary>
    public void OpenMenu(string category, int group, bool pin = false)
    {
        if (_menu.Open != category) _menu.Toggle(category);
        _menu.PickGroup(group);
        if (pin != _menu.Pinned) _menu.Pin();
        BuildRail();
        BuildFlyout();
    }

    /// <summary>A tile's press in the open flyout: false when there's none like it, or it's locked.</summary>
    public bool PickTile(Func<BuildItem, bool> which)
    {
        if (_menu.Shown(_cats) is not { } sh || _flyList == null) return false;
        var buttons = _flyList.FindChildren("*", "GridContainer", true, false).SelectMany(g => g.GetChildren()).OfType<Button>().ToList();
        var items = sh.Category.Groups[sh.Group].Items;
        for (int k = 0; k < Math.Min(buttons.Count, items.Count); k++)
        {
            if (!which(items[k])) continue;
            buttons[k].EmitSignal(BaseButton.SignalName.Pressed);
            return items[k].Locked.Length == 0;
        }
        return false;
    }

    public void CloseMenu() { if (_menu.Pinned) _menu.Pin(); _menu.Close(); BuildRail(); BuildFlyout(); }

    /// <summary>The tool in hand: its icon, name and price, ahead of its options.</summary>
    void Named(string icon, string name, string price = "")
    {
        _options!.AddChild(new TextureRect { Texture = UiTheme.Icon(icon, 18, Colors.White), StretchMode = TextureRect.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(20, 20) });
        _options.AddChild(new Label { Text = name, VerticalAlignment = VerticalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 14, FontColor = Colors.White } });
        if (price.Length > 0) _options.AddChild(new Label { Text = price, VerticalAlignment = VerticalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 12, FontColor = UiTheme.Gold } });
    }

    void BuildOptions()
    {
        if (_options == null) return;
        foreach (var child in _options.GetChildren()) { _options.RemoveChild(child); child.QueueFree(); }
        var c = _client.City;
        switch (State.Kind)
        {
            case ToolKind.Zone:
                Named("zones", new[] { "Residential", "Commercial", "Industrial" }[Math.Clamp((int)State.Zone - 1, 0, 2)]);
                Choice(Planner.DensityNames, (int)State.Density, k => State.Density = (Density)k, tips: Planner.DensityNames.Select(n => $"{n} density (V cycles)").ToArray());
                Choice(new[] { "Area", "Block" }, State.BlockZoning ? 1 : 0, k => State.BlockZoning = k == 1, tips: new[] { "Paint zones over an area", "Zone whole blocks (G)" });
                if (State.BlockZoning) Choice(new[] { "Leave leftovers", "Leftovers to parks", "Zone leftovers" }, (int)State.Leftover, k => State.Leftover = (Leftover)k);
                break;
            case ToolKind.Dezone: Named("bulldoze", "Dezone"); break;
            case ToolKind.Path:
            {
                var p = c.Profiles.FirstOrDefault(q => q.Id == State.ProfileId);
                Named(p != null && (p.Rail || p.Tram.Length > 0) ? "tram" : "road", p?.Name ?? "Path", p != null ? $"{Planner.Money(p.CostPerCell)} / cell" : "");
                // Beta 3.2: what it's paved with, and so how steep it climbs.
                if (Planner.SurfaceChoices(c, State.ProfileId) is { Count: > 1 } surfaces)
                    Dropdown(surfaces, State.Surface, id => State.Surface = id);
                Choice(new[] { "Cut through", "Tunnel (O)" }, State.Tunnel ? 1 : 0, k => State.Tunnel = k == 1,
                    tips: new[] { "Grade through hills, however deep the cut", "Go under a hill where the road would run 8 m or more deep" });
                Choice(new[] { "Straight", "Diagonal", "Curve" }, PathMode, SetPathMode, tips: new[] { "Straight roads", "Smooth diagonals at any angle (C)", "Curved roads (C)" });
                break;
            }
            case ToolKind.Utility:
                Named(new[] { "power", "water", "subway" }[(int)State.Utility], new[] { "Power line", "Water and sewer pipe", "Subway tunnel" }[(int)State.Utility], new[] { "$5 / cell", "$3 / cell", "$150 / cell" }[(int)State.Utility]);
                break;
            case ToolKind.Building:
            {
                var t = c.TypeById.GetValueOrDefault(State.BuildingTypeId);
                Named(t == null ? "civic" : BuildMenu.For(c).SelectMany(m => m.Groups).SelectMany(g => g.Items).FirstOrDefault(i => i.Type == t.Id)?.Icon ?? "civic", t?.Name ?? "Building", t is { Cost: > 0 } ? Planner.Money(t.Cost) : "");
                break;
            }
            case ToolKind.Terraform:
                Named("land", "Terraform");
                Choice(Planner.TerraformNames, (int)State.Terraform, k => State.Terraform = (TerraformMode)k);
                break;
            case ToolKind.River:
                Named("water", "River brush");
                Choice(new[] { "16 m", "32 m", "48 m", "64 m", "80 m" }, State.RiverWidth - 1, k => State.RiverWidth = k + 1, tips: Enumerable.Range(1, 5).Select(k => $"{k} cell{(k > 1 ? "s" : "")} wide").ToArray());
                Choice(new[] { "2 m", "4 m", "6 m", "8 m" }, State.RiverDepth - 1, k => State.RiverDepth = k + 1, tips: Enumerable.Range(1, 4).Select(k => $"{k} step{(k > 1 ? "s" : "")} deep").ToArray());
                break;
            case ToolKind.Dam:
                Named("water", "Dam");
                Choice(Enumerable.Range(1, 8).Select(k => $"{2 * k} m").ToArray(), State.DamRise - 1, k => State.DamRise = k + 1, tips: Enumerable.Range(1, 8).Select(k => $"Holds the river {2 * k} m over its level").ToArray());
                break;
            case ToolKind.Disaster:
                Named("disasters", "Disaster");
                Choice(Planner.DisasterNames, (int)State.Disaster, k => State.Disaster = (DisasterKind)k);
                _options.AddChild(new Label { Text = "By themselves", VerticalAlignment = VerticalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Muted } });
                Choice(new[] { "Off", "Rare", "Normal" }, (int)c.Stats.DisasterSetting,
                    k => _client.Connection.Act(new PAction { Disasters = new SetDisasters { Setting = (DisasterSetting)k } }));
                break;
            case ToolKind.Bulldoze:
                Named("bulldoze", "Bulldoze");
                Choice(new[] { "Above ground", "Pipes only (U)" }, State.Underground ? 1 : 0, k => State.Underground = k == 1);
                break;
        }
        _toolopts!.Visible = _enabled && _options.GetChildCount() > 0 && _menu.Open == null;
        _toolopts.ResetSize();
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
        // A segmented row: the options side by side in a faint well, the chosen one blue.
        var well = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        well.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(1, 1, 1, 0.05f), null, 8, 2, 2));
        _options!.AddChild(well);
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        well.AddChild(box);
        for (int k = 0; k < names.Length; k++)
        {
            int kk = k;
            var b = UiTheme.Flat(new Button { Text = names[k], ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = tips?[k] ?? "" }, 9, 3, 6);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.SetPressedNoSignal(k == selected);
            if (colours != null) b.Icon = Swatch(colours[k]);
            b.Pressed += () => { pick(kk); Callable.From(BuildOptions).CallDeferred(); };
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
            ToolKind.Path when State.Diagonal => new[] { ("Drag", "draw a road at any angle"), ("C", "curved roads"), ("Esc", "cancel") },
            ToolKind.Path => new[] { ("Drag", "draw a straight road"), ("Alt", "free: follow the cursor"), ("Alt+Shift", "vertical leg first"), ("C", "curved roads"), ("O", State.Tunnel ? "cut through hills" : "tunnel under hills"), ("Esc", "cancel") },
            ToolKind.Zone when State.BlockZoning => new[] { ("Click", $"zone a whole block, {density} density"), ("G", "paint areas"), ("V", "cycle density"), ("Z", "next zone"), ("Esc", "cancel") },
            ToolKind.Zone => new[] { ("Drag", $"paint {density} density zone"), ("G", "zone whole blocks"), ("V", "cycle density"), ("Z", "next zone"), ("Esc", "cancel") },
            ToolKind.Dezone => new[] { ("Drag", "remove zoning"), ("Esc", "cancel") },
            ToolKind.Utility => new[] { ("Drag", "draw a straight line"), ("Alt", "free: follow the cursor"), ("L", "power or water"), ("Esc", "cancel") },
            ToolKind.Building => new[] { ("Click", "place"), ("Alt", "free: exactly at the cursor"), ("Esc", "cancel") },
            ToolKind.Terraform => new[] { ("Drag", $"{Planner.TerraformNames[(int)State.Terraform].ToLowerInvariant()} area"), ("Esc", "cancel") },
            ToolKind.River => new[] { ("Drag", "dig a river from where its water starts"), ("Esc", "cancel") },
            ToolKind.Dam => new[] { ("Drag", "wall a river, bank to bank"), ("Esc", "cancel") },
            _ => new[] { ("Click", $"start a {Planner.DisasterNames[(int)State.Disaster].ToLowerInvariant()} here"), ("Esc", "cancel") },
        };
        var common = new[] { (", .", "rotate"), ("Wheel", "zoom"), ("Right-drag", "pan"), ("T / Y", "top-down / side"), ("Space", "pause"), ("P / M / U", "panel / commuters / pipes") };
        foreach (var child in _hints.GetChildren()) child.QueueFree();
        foreach (var (key, what) in per.Concat(common))
        {
            // A dark tag with the key in a key cap, as the web client's hints.
            var tag = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
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
                if (_menu.Open != null) { _menu.Close(); BuildRail(); BuildFlyout(); }
                else if (_drag != null || State.CurveEnds != null) { _drag = null; State.CurveEnds = null; UpdateHints(); }
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
            else if (k.Keycode == Key.C && State.Kind == ToolKind.Path) { SetPathMode((PathMode + 1) % 3); BuildOptions(); }
            else if (k.Keycode == Key.O && State.Kind == ToolKind.Path) { State.Tunnel = !State.Tunnel; BuildOptions(); }
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
        // A click on the map closes the flyout, unless it's pinned.
        if (mb.Pressed && _menu.Open != null && !_menu.Pinned) { _menu.Close(); BuildRail(); BuildFlyout(); }
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
