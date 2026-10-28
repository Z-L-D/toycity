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
    VBoxContainer? _top;
    bool _enabled = true;

    /// <summary>Off while the bus company's view is up: no toolbar, previews or clicks.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _top!.Visible = _hints!.Visible = value;
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
    Label? _cost, _hints;
    VBoxContainer? _toasts;
    HBoxContainer? _options;
    readonly Dictionary<ToolKind, Button> _buttons = new();
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
        var top = _top = new VBoxContainer();
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.Minsize, 46);  // under the top bar
        top.GrowHorizontal = Control.GrowDirection.Both;
        hud.AddChild(top);
        var bar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        top.AddChild(bar);
        var group = new ButtonGroup();
        foreach (var (kind, name, key) in Tools)
        {
            var b = new Button { Text = $"{name} ({key})", ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += () => SetTool(kind);
            bar.AddChild(b);
            _buttons[kind] = b;
        }
        _options = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        top.AddChild(_options);

        _cost = new Label { Visible = false };
        Outline(_cost);
        hud.AddChild(_cost);
        _hints = new Label { Modulate = new Color(1, 1, 1, 0.85f) };
        Outline(_hints);
        _hints.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 12);
        _hints.GrowVertical = Control.GrowDirection.Begin;
        hud.AddChild(_hints);
        _toasts = new VBoxContainer();
        _toasts.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 48);
        _toasts.GrowVertical = Control.GrowDirection.Begin;
        _toasts.GrowHorizontal = Control.GrowDirection.Both;
        hud.AddChild(_toasts);

        _client.Message += OnMessage;
        SetTool(ToolKind.Query);
    }

    static void Outline(Label l)
    {
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 4);
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
        if (_options == null) return;
        foreach (var child in _options.GetChildren()) child.QueueFree();
        var c = _client.City;
        switch (State.Kind)
        {
            case ToolKind.Zone:
                Choice(new[] { "Residential", "Commercial", "Industrial" }, (int)State.Zone - 1, k => State.Zone = (ZoneKind)(k + 1));
                Choice(Planner.DensityNames, (int)State.Density, k => State.Density = (Density)k);
                Choice(new[] { "Paint areas", "Whole blocks (G)" }, State.BlockZoning ? 1 : 0, k => State.BlockZoning = k == 1);
                if (State.BlockZoning) Choice(new[] { "Leave leftovers", "Leftovers to parks", "Zone leftovers" }, (int)State.Leftover, k => State.Leftover = (Leftover)k);
                break;
            case ToolKind.Path:
                Dropdown(c.Profiles.Select(p => (p.Id, $"{p.Name} ({Planner.Money(p.CostPerCell)}/cell)")).ToList(), State.ProfileId, id => State.ProfileId = id);
                Choice(new[] { "Straight", "Curved (C)" }, State.Curve ? 1 : 0, k => { State.Curve = k == 1; State.CurveEnds = null; });
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
                _options!.AddChild(new Label { Text = "   By themselves:" });
                Choice(new[] { "Off", "Rare", "Normal" }, (int)c.Stats.DisasterSetting,
                    k => _client.Connection.Act(new PAction { Disasters = new SetDisasters { Setting = (DisasterSetting)k } }));
                break;
            case ToolKind.Bulldoze:
                Choice(new[] { "Above ground", "Water pipes only (U)" }, State.Underground ? 1 : 0, k => State.Underground = k == 1);
                break;
        }
        UpdateHints();
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    void Choice(string[] names, int selected, Action<int> pick)
    {
        var group = new ButtonGroup();
        for (int k = 0; k < names.Length; k++)
        {
            int kk = k;
            var b = new Button { Text = names[k], ToggleMode = true, ButtonGroup = group, ButtonPressed = k == selected, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += () => { pick(kk); BuildOptions(); };
            _options!.AddChild(b);
        }
    }

    void Dropdown(List<(string Id, string Label)> items, string selected, Action<string> pick) =>
        Dropdown(items.Select(i => (i.Id, i.Label, false)).ToList(), selected, pick);

    void Dropdown(List<(string Id, string Label, bool Disabled)> items, string selected, Action<string> pick)
    {
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None };
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
        var common = new[] { (", .", "rotate"), ("Wheel", "zoom"), ("T / Y", "top-down / side"), ("Right-drag", "pan"), ("Space", "pause"), ("P / M / U", "panel / commuters / pipes") };
        _hints.Text = string.Join("\n", per.Concat(common).Select(h => $"{h.Item1,-11} {h.Item2}"));
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
            _cost.Modulate = _preview.Ok && c.Stats.Funds >= 0 ? Colors.White : new Color(1, 0.55f, 0.5f);
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
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        Outline(l);
        l.Modulate = severity switch { "warning" => new Color(1, 0.7f, 0.6f), "good" => new Color(0.7f, 1, 0.7f), _ => Colors.White };
        _toasts.AddChild(l);
        while (_toasts.GetChildCount() > 4) _toasts.GetChild(0).Free();
        GetTree().CreateTimer(4.0).Timeout += () => { if (IsInstanceValid(l)) l.QueueFree(); };
    }
}
