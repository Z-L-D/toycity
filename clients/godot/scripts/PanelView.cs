using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// The city's own screens, drawn from <see cref="PanelModel"/>: the top bar (date, money, people, demand, approval,
/// alerts, speed, who runs the city, and new, save and load), the side panel with its tabs, dialogs over the map,
/// the news ticker along the bottom, and banners while the computer has the city or a commuter is followed.
/// The panels are text with links, so this side only lays them out and passes clicks back.
/// </summary>
public partial class PanelView : CanvasLayer
{
    readonly CityClient _client;
    public readonly PanelModel Model;
    static readonly string[] MonthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    Label? _name, _date, _funds, _pop;
    Button? _approval, _alerts, _seat, _dispatch, _company;
    SceneCanvas? _scene;
    CenterContainer? _sceneBox;
    PanelContainer? _report, _tabsBox;
    RichTextLabel? _reportText;
    int _sceneShown = -1;
    Button[] _speeds = Array.Empty<Button>(), _coSpeeds = Array.Empty<Button>(), _emSpeeds = Array.Empty<Button>();
    DemandBars? _rci;
    PanelContainer? _side;
    readonly Dictionary<string, Button> _tabs = new();
    LineEdit? _input, _dialogInput;
    RichTextLabel? _text, _dialogText;
    Control? _dialog;
    PanelContainer? _away, _follow;
    Label? _awayText, _followText;
    Button? _followCentre, _ticker;
    int _shown = -1;
    string _shownTab = "", _shownDialog = "";
    int _tickerIndex = -1;
    long _tickerSeq = -1;
    double _tickerClock;

    /// <summary>The map tools, whose own keys come first: U with the bulldozer means pipes only.</summary>
    public ToolController? Tools;
    // For clicks on the map in the bus company's view, which has no tools of the city's.
    public ClassicCamera? Camera;
    public CityView? City;
    public MapView? Map;
    HBoxContainer? _cityBar, _coBar, _emBar;
    GridContainer? _cityTabs, _coTabs;
    Label? _emName, _emText, _emDate, _emPending;
    Button? _emSeat;
    PanelContainer? _emTools;
    VBoxContainer? _emToolList;
    readonly Dictionary<string, Button> _coTabButtons = new();
    Label? _coText, _coDate;
    Button? _coSeat;
    PanelContainer? _coTools;
    RichTextLabel? _coToolsText;
    string _wasScreen = "city";
    /// <summary>Whether the camera keeps the followed commuter centred; panning hands the camera back.</summary>
    public bool FollowCam;

    public PanelView(CityClient client)
    {
        _client = client;
        Model = new PanelModel(client.City, client.Connection.Send);
    }

    /// <summary>The Graphics button: Main opens the options.</summary>
    public event System.Action? GraphicsPressed;

    Button Btn(string text, System.Action pressed, Container parent)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
    }

    static Label Stat(Container parent, string text = "", int size = UiTheme.FontSize, Color? colour = null, bool bold = false)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (size != UiTheme.FontSize) l.AddThemeFontSizeOverride("font_size", size);
        if (colour is { } c) l.AddThemeColorOverride("font_color", c);
        if (bold) l.AddThemeConstantOverride("outline_size", 1);
        if (bold) l.AddThemeColorOverride("font_outline_color", colour ?? UiTheme.Text);
        parent.AddChild(l);
        return l;
    }

    /// <summary>The four speed buttons, small and close together, as the web's.</summary>
    Button[] Speeds(Container parent)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        parent.AddChild(box);
        string[] names = { "⏸", "▶", "▶▶", "▶▶▶" };
        string[] tips = { "Pause (Space)", "Slow", "Normal", "Fast" };
        var list = new Button[4];
        for (int k = 0; k < 4; k++)
        {
            int kk = k;
            list[k] = UiTheme.Small(Btn(names[k], () => _client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = kk } }), box), 8, 2, 11);
            list[k].ToggleMode = true;
            list[k].TooltipText = tips[k];
        }
        return list;
    }

    /// <summary>A bar across the top, one per screen: the city's, the bus company's, the emergency services'.</summary>
    HBoxContainer TopRow(VBoxContainer bars, bool visible)
    {
        var row = new HBoxContainer { Visible = visible, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Begin };
        row.AddThemeConstantOverride("separation", 14);
        bars.AddChild(row);
        // Buttons keep their own height in the middle of the bar, as the web's do.
        row.ChildEnteredTree += n => { if (n is Control c) c.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; };
        return row;
    }

    /// <summary>A column down the left for a side game's tools, where the city's tools are.</summary>
    PanelContainer LeftColumn()
    {
        var p = new PanelContainer { Visible = false };
        p.AddThemeStyleboxOverride("panel", UiTheme.Docked(Side.Right, 8));
        p.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        (p.OffsetLeft, p.OffsetTop, p.OffsetRight, p.OffsetBottom) = (0, ToolController.TopHeight, ToolController.ColumnWidth, 0);
        AddChild(p);
        return p;
    }

    public override void _Ready()
    {
        Layer = 3;
        // The top bar: one row per screen, only one shown.
        var bar = new PanelContainer();
        var barStyle = UiTheme.Docked(Side.Bottom, 12);
        (barStyle.ContentMarginTop, barStyle.ContentMarginBottom) = (0, 0);
        bar.AddThemeStyleboxOverride("panel", barStyle);
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        (bar.OffsetTop, bar.OffsetBottom) = (0, ToolController.TopHeight);
        AddChild(bar);
        var bars = new VBoxContainer();
        bar.AddChild(bars);

        var row = _cityBar = TopRow(bars, true);
        Stat(row, "ToyCity", 14, UiTheme.Accent, bold: true);
        _name = Stat(row);
        _date = Stat(row);
        _funds = Stat(row);
        _funds.TooltipText = "Treasury";
        _pop = Stat(row);
        _approval = Btn("Approval", () => Model.Open("people"), row);
        _approval.Flat = true;
        _approval.TooltipText = "Mayor approval: click for what people think";
        _speeds = Speeds(row);
        _rci = new DemandBars(_client.City) { CustomMinimumSize = new Vector2(56, 34), TooltipText = "Demand: bars up mean the city wants more of that zone", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _rci.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Model.Open("demand"); };
        row.AddChild(_rci);
        _alerts = Btn("⚠ 0", () => Model.Open("alerts"), row);
        _alerts.TooltipText = "Alerts: problems in the city, click one to go there";
        _seat = Btn("Mayor", () => Model.OpenDialog("seat"), row);
        _seat.TooltipText = "Who runs the city: hand it to the computer while you're away";
        // The side games' own screens, as the web client's top bar has them.
        _company = Btn("🚌 Bus company", () => Model.Click("co-open"), row);
        _company.TooltipText = "Switch to the bus company's own screen";
        _company.Visible = false;
        _dispatch = Btn("🚒 Dispatch", () => Model.Click("em-open"), row);
        _dispatch.TooltipText = "Switch to the emergency services' own screen";
        _dispatch.Visible = false;
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Btn("🗺 Region", () => Model.Click("rg-open"), row).TooltipText = "The region map: the cities around this one, and open land to found more";
        Btn("New", () => Model.OpenDialog("new"), row);
        Btn("Save", () => Model.OpenDialog("save"), row);
        Btn("Load", () => Model.OpenDialog("load"), row);
        Btn("Graphics", () => GraphicsPressed?.Invoke(), row);

        // The bus company's top bar, in the city's place while its screen is up.
        var co = _coBar = TopRow(bars, false);
        _coText = Stat(co, "", 14, UiTheme.Accent, bold: true);
        _coDate = Stat(co);
        _coSpeeds = Speeds(co);
        _coSeat = Btn("", () => Model.Click(Model.Company.Mine ? "co-handoff" : "co-claim"), co);
        co.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Btn("⧉ Own window", () => Model.Click("co-window"), co);
        Btn("🏙 Back to the city", () => Model.Click("co-city"), co);

        // The emergency services' top bar.
        var em = _emBar = TopRow(bars, false);
        _emName = Stat(em, "", 14, UiTheme.Accent, bold: true);
        _emText = Stat(em);
        _emDate = Stat(em);
        _emSpeeds = Speeds(em);
        _emSeat = Btn("", () => { if (Model.Emergency.SeatButton is { } sb) Model.Click(sb.Meta); }, em);
        _emPending = Stat(em, "", UiTheme.FontSize, UiTheme.Warn);
        em.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Btn("🏙 Back to the city", () => Model.Click("em-city"), em);

        // The side panel, down the right.
        _side = new PanelContainer();
        _side.AddThemeStyleboxOverride("panel", UiTheme.Docked(Side.Left, 0));
        _side.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        (_side.OffsetLeft, _side.OffsetRight, _side.OffsetTop, _side.OffsetBottom) = (-ToolController.SideWidth, 0, ToolController.TopHeight, 0);
        AddChild(_side);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        _side.AddChild(col);
        var tabsBox = new PanelContainer();
        var tabsStyle = UiTheme.Box(UiTheme.Panel, null, 0, 6, 6);
        (tabsStyle.BorderColor, tabsStyle.BorderWidthBottom) = (UiTheme.Line, 1);
        tabsBox.AddThemeStyleboxOverride("panel", tabsStyle);
        col.AddChild(tabsBox);
        var tabsCol = new VBoxContainer();
        tabsBox.AddChild(tabsCol);
        var tabs = _cityTabs = new GridContainer { Columns = 5 };
        tabs.AddThemeConstantOverride("h_separation", 3);
        tabs.AddThemeConstantOverride("v_separation", 3);
        tabsCol.AddChild(tabs);
        _coTabs = new GridContainer { Columns = 5, Visible = false };
        _coTabs.AddThemeConstantOverride("h_separation", 3);
        tabsCol.AddChild(_coTabs);
        _tabsBox = tabsBox;
        var coGroup = new ButtonGroup();
        foreach (var (id, name) in CompanyModel.Tabs)
        {
            var b = UiTheme.Small(Btn(name, () => Model.Click($"co-tab:{id}"), _coTabs), 0, 3);
            b.ToggleMode = true;
            b.ButtonGroup = coGroup;
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _coTabButtons[id] = b;
        }
        var group = new ButtonGroup();
        foreach (var (id, name) in PanelModel.Tabs)
        {
            var b = UiTheme.Small(Btn(name, () => Model.Open(id), tabs), 0, 3);
            b.ToggleMode = true;
            b.ButtonGroup = group;
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _tabs[id] = b;
        }
        var body = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right" }) body.AddThemeConstantOverride($"margin_{side}", 12);
        foreach (var side in new[] { "top", "bottom" }) body.AddThemeConstantOverride($"margin_{side}", 10);
        col.AddChild(body);
        var bodyCol = new VBoxContainer();
        body.AddChild(bodyCol);
        _input = new LineEdit { Visible = false, ClearButtonEnabled = true };
        _input.TextChanged += t => Model.SetInput(t);
        bodyCol.AddChild(_input);
        _text = Text();
        _text.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        bodyCol.AddChild(_text);

        // The company's tools, down the left where the city's are.
        _coTools = LeftColumn();
        _coToolsText = Text();
        _coTools.AddChild(_coToolsText);
        // The emergency services' tools: the board's pages, or the scene's orders.
        _emTools = LeftColumn();
        var emScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _emTools.AddChild(emScroll);
        _emToolList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _emToolList.AddThemeConstantOverride("separation", 4);
        emScroll.AddChild(_emToolList);

        // Over the map, between the bars: the scene a player runs, banners and the news.
        var area = ToolController.MapArea();
        AddChild(area);
        _sceneBox = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _sceneBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        area.AddChild(_sceneBox);
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, UiTheme.Line, 6, 4, 4));
        _sceneBox.AddChild(frame);
        _scene = new SceneCanvas(Model.Emergency);
        frame.AddChild(_scene);
        // The ended scene's report card, over the scene.
        _report = new PanelContainer { Visible = false };
        _report.AddThemeStyleboxOverride("panel", UiTheme.Floating());
        _sceneBox.AddChild(_report);
        var rcol = new VBoxContainer();
        rcol.AddThemeConstantOverride("separation", 12);
        _report.AddChild(rcol);
        _reportText = Text();
        _reportText.FitContent = true;
        _reportText.ScrollActive = false;
        _reportText.CustomMinimumSize = new Vector2(400, 0);
        rcol.AddChild(_reportText);
        var rrow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        rrow.AddThemeConstantOverride("separation", 8);
        rcol.AddChild(rrow);
        Btn("Close report", () => Model.Click("em-report"), rrow).TooltipText = "See the scene as it finished";
        Btn("Return to calls", () => Model.Click("em-board"), rrow);

        // Dialogs, in the middle of the screen over a dimmed map.
        var layer = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(layer);
        _dialog = layer;
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(backdrop);
        // A click outside the dialog closes it, as Escape does.
        backdrop.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true } mb || mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            Model.CloseDialog();
            backdrop.AcceptEvent();
        };
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(centre);
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", UiTheme.Floating());
        centre.AddChild(box);
        var dcol = new VBoxContainer();
        box.AddChild(dcol);
        var close = UiTheme.Small(new Button { Text = "✕", Flat = false, FocusMode = Control.FocusModeEnum.None, TooltipText = "Close (Esc)", SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd }, 8, 2);
        close.Pressed += Model.CloseDialog;
        dcol.AddChild(close);
        _dialogInput = new LineEdit { Visible = false, MaxLength = 40 };
        _dialogInput.TextChanged += t => Model.SetInput(t);
        dcol.AddChild(_dialogInput);
        _dialogText = Text();
        _dialogText.CustomMinimumSize = new Vector2(560, 440);
        dcol.AddChild(_dialogText);

        // Banners at the top of the map.
        (_away, _awayText) = Banner(area, 10, UiTheme.Warn);
        Btn("Take it back", Model.TakeBack, (Container)_away.GetChild(0));
        (_follow, _followText) = Banner(area, 54, UiTheme.Line);
        _followCentre = Btn("Centre", () => FollowCam = true, (Container)_follow.GetChild(0));
        Btn("Stop following", Model.StopFollowing, (Container)_follow.GetChild(0));

        // The news ticker, along the bottom of the map.
        _ticker = new Button { FocusMode = Control.FocusModeEnum.None, Visible = false, ClipText = true };
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed" })
            _ticker.AddThemeStyleboxOverride(st, UiTheme.Box(UiTheme.Chip with { A = 0.9f }, st.StartsWith("hover") ? UiTheme.HoverLine : UiTheme.Line, 14, 12, 4));
        _ticker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 10);
        _ticker.GrowHorizontal = Control.GrowDirection.Both;
        _ticker.GrowVertical = Control.GrowDirection.Begin;
        _ticker.Pressed += () =>
        {
            var hs = Model.Headlines;
            if (_tickerIndex >= 0 && _tickerIndex < hs.Count && hs[_tickerIndex].X >= 0) Model.Click($"jump:{hs[_tickerIndex].X}:{hs[_tickerIndex].Y}");
            else Model.Open("people");
        };
        area.AddChild(_ticker);
    }

    RichTextLabel Text()
    {
        var t = new RichTextLabel { BbcodeEnabled = true, ScrollActive = true, SelectionEnabled = false, FocusMode = Control.FocusModeEnum.None };
        t.MetaClicked += meta => Model.Click(meta.AsString());
        return t;
    }

    (PanelContainer, Label) Banner(Control area, float y, Color edge)
    {
        var p = new PanelContainer { Visible = false };
        p.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Chip with { A = 0.92f }, edge, 8, 12, 6));
        p.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.Minsize, (int)y);
        p.GrowHorizontal = Control.GrowDirection.Both;
        area.AddChild(p);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        p.AddChild(row);
        var l = new Label { VerticalAlignment = VerticalAlignment.Center };
        row.AddChild(l);
        return (p, l);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb && Model.Company.Active)
        {
            if (Map?.CellAt(mb.Position) is { } m) Camera!.CentreOn(m.X + 0.5f, m.Y + 0.5f);
            else if (Camera?.Pick(mb.Position, City!) is { } cell) Model.Company.MapClick(cell.X, cell.Y);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        switch (k.Keycode)
        {
            case Key.P: _side!.Visible = !_side.Visible; break;
            case Key.M: Model.ShowAgents = !Model.ShowAgents; break;
            case Key.U when Tools?.State.Kind != ToyCity.ClientModel.Tools.ToolKind.Bulldoze: Model.ShowUnderground = !Model.ShowUnderground; break;
            case Key.Escape when Model.Dialog != null: Model.CloseDialog(); break;
            case Key.Escape when Model.Company.Active: Model.Click("co-tool:Look"); break;
            case Key.Escape when Model.Emergency.Active && Model.Emergency.SceneOver: Model.Click("em-board"); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        var c = _client.City;
        Model.Tick(delta);
        bool company = Model.Company.Active, dispatch = !company && Model.Emergency.Active;
        // Which screen is up: the city's, or a side game's own, each with its own top bar and left column.
        string screen = company ? "company" : dispatch ? "dispatch" : "city";
        if (screen != _wasScreen)
        {
            _wasScreen = screen;
            _cityBar!.Visible = _cityTabs!.Visible = screen == "city";
            _coBar!.Visible = _coTabs!.Visible = _coTools!.Visible = company;
            _emBar!.Visible = _emTools!.Visible = dispatch;
            _tabsBox!.Visible = !dispatch;
            if (Tools != null) Tools.Enabled = screen == "city";
            _shown = -1;
        }
        if (c.Loaded) TopBar(c);
        if (company) CompanyBar(c);
        if (dispatch) DispatchBar(c);
        Redraw();
        if (Map != null) Map.RightInset = _side!.Visible ? ToolController.SideWidth : 0;
        // The scene a player runs, fitted to the map's room, with its report over it once it's over.
        var em = Model.Emergency;
        bool scene = dispatch && em.Scene != null && !em.SceneHidden;
        _sceneBox!.Visible = scene;
        _report!.Visible = scene && em.SceneOver && em.ReportOpen;
        if (scene)
        {
            var room = _sceneBox.Size;
            _scene!.Tile = Mathf.Clamp(Mathf.Floor(Mathf.Min(room.X, room.Y) - 40) / 36, 8, 22);
            if (em.SceneVersion != _sceneShown) { _sceneShown = em.SceneVersion; _scene.QueueRedraw(); }
        }
        // Banners.
        _away!.Visible = Model.AwayText != null;
        if (Model.AwayText is { } away) _awayText!.Text = away;
        _follow!.Visible = Model.Followed != null;
        _followCentre!.Visible = !FollowCam;
        Ticker(delta);
    }

    void TopBar(CityMirror c)
    {
        var s = c.Stats;
        _name!.Text = s.CityName;
        _date!.Text = $"{MonthNames[Math.Clamp(s.Month - 1, 0, 11)]} {s.Day}, {s.Year}";
        _funds!.Text = Bb.Money(s.Funds);
        _funds.AddThemeColorOverride("font_color", s.Funds < 0 ? UiTheme.Bad : UiTheme.Text);
        _name.AddThemeColorOverride("font_color", UiTheme.Text);
        _pop!.Text = $"👥 {Bb.Num(s.Population)}";
        foreach (var set in new[] { _speeds, _coSpeeds, _emSpeeds })
            for (int k = 0; k < set.Length; k++) set[k].SetPressedNoSignal(s.Speed == k);
        if (Model.Civic is { } civic)
        {
            _approval!.Text = $"{(civic.Approval >= 50 ? "👍" : "👎")} {civic.Approval}%";
            _approval.Modulate = civic.Approval < 40 ? new Color(1, 0.6f, 0.55f) : Colors.White;
            _tabs["people"].Text = Model.OpenPrompts > 0 ? $"People ({Model.OpenPrompts})" : "People";
        }
        var (count, worst) = Model.AlertBadge;
        _alerts!.Text = $"⚠ {count}";
        _alerts.Modulate = worst switch { "danger" => new Color(1, 0.5f, 0.45f), "warning" => new Color(1, 0.85f, 0.45f), _ => Colors.White };
        _seat!.Text = Model.SeatLabel;
        _dispatch!.Visible = Model.Emergency.View != null;
        _dispatch.Text = Model.Emergency.Waiting > 0 ? $"🚒 Dispatch ({Model.Emergency.Waiting})" : "🚒 Dispatch";
        _company!.Visible = Model.Company.View != null;
        if (Model.Company.View is { } co) _company.Text = $"🚌 {co.Name}";
        _rci!.QueueRedraw();
    }

    void DispatchBar(CityMirror c)
    {
        var em = Model.Emergency;
        var s = c.Stats;
        _emName!.Text = $"🚒 {em.View?.Name ?? "Emergency services"}";
        _emText!.Text = em.TopStats;
        _emDate!.Text = $"{MonthNames[Math.Clamp(s.Month - 1, 0, 11)]} {s.Day}, {s.Year}";
        _emSeat!.Visible = em.SeatButton != null;
        if (em.SeatButton is { } sb) _emSeat.Text = sb.Text;
        _emPending!.Visible = em.Pending != null;
        _emPending.Text = em.Pending is { } p ? $"⏳ {p}" : "";
        if (em.Waiting > 0 && em.Scene == null) _emName.Text += $"  ({em.Waiting} waiting)";
    }

    /// <summary>The emergency services' column: buttons for the board's pages, or the scene's orders.</summary>
    void DispatchTools()
    {
        foreach (var child in _emToolList!.GetChildren()) child.QueueFree();
        var em = Model.Emergency;
        bool first = true;
        foreach (var (heading, items) in em.ToolSections())
        {
            var h = UiTheme.Heading(heading);
            if (!first) h.CustomMinimumSize = new Vector2(0, 22);
            h.VerticalAlignment = VerticalAlignment.Bottom;
            first = false;
            _emToolList.AddChild(h);
            var group = heading == "Scene" ? null : new ButtonGroup();
            foreach (var (meta, text, on) in items)
            {
                var b = new Button { Text = text, ToggleMode = true, ButtonPressed = on, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left, ClipText = true };
                b.Pressed += () => Model.Click(meta);
                _emToolList.AddChild(b);
            }
        }
        if (em.Scene != null && em.Mine && !em.SceneOver)
            _emToolList.AddChild(new Label { Text = em.OrderHint, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Muted } });
        if (em.View != null && em.Scene == null && !em.Mine && em.Pending == null)
            _emToolList.AddChild(new Label { Text = "The computer runs the service. Run it yourself (top bar) to send units and command scenes.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart, LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Muted } });
    }

    void CompanyBar(CityMirror c)
    {
        var s = c.Stats;
        _coText!.Text = $"🚌 {Model.Company.TopText}";
        _coDate!.Text = $"{MonthNames[Math.Clamp(s.Month - 1, 0, 11)]} {s.Day}, {s.Year}";
        var v = Model.Company.View;
        _coSeat!.Text = v == null ? "" : Model.Company.Mine ? "You run it · hand to the computer" : $"The computer runs it ({v.Operator}) · run it yourself";
    }

    /// <summary>Rewrites the panel and dialog when the model changed, keeping the scroll place within a tab.</summary>
    void Redraw()
    {
        if (Model.Version == _shown) return;
        _shown = Model.Version;
        _tabs[Model.Tab].SetPressedNoSignal(true);
        if (Model.Company.Active)
        {
            _coTabButtons[Model.Company.Tab].SetPressedNoSignal(true);
            _coToolsText!.Text = Model.Company.RenderTools();
        }
        bool dialog = Model.Dialog != null;
        _dialog!.Visible = dialog;
        var target = dialog ? _dialogText! : _text!;
        var em = Model.Emergency;
        bool dispatch = !dialog && em.Active && !Model.Company.Active;
        string key = dialog ? Model.Dialog! : Model.Company.Active ? "co-" + Model.Company.Tab : dispatch ? $"em-{em.Tab}-{em.SceneId}" : Model.Tab;
        bool same = dialog ? key == _shownDialog : key == _shownTab;
        double scroll = same ? target.GetVScrollBar().Value : 0;
        target.Text = dispatch ? em.RenderBody() : Model.Render();
        if (em.Active)
        {
            DispatchTools();
            if (em.SceneOver) _reportText!.Text = em.RenderReport();
        }
        if (scroll > 0) Callable.From(() => target.GetVScrollBar().Value = scroll).CallDeferred();
        if (dialog) _shownDialog = key; else { _shownTab = key; _shownDialog = ""; }
        // The text box: the guide's search, or a name for a new city or a save.
        var input = dialog ? _dialogInput! : _input!;
        (dialog ? _input! : _dialogInput!).Visible = false;
        input.Visible = Model.InputPrompt != null;
        input.PlaceholderText = Model.InputPrompt ?? "";
        if (input.Visible && input.Text != Model.Input) input.Text = Model.Input;
        if (Model.FollowedTrip != null) _followText!.Text = $"Following {Model.FollowedName}{(Model.FollowedJob != "" ? $" ({Model.FollowedJob})" : "")}";
    }

    /// <summary>Updates the follow banner with what the followed commuter is doing.</summary>
    public void FollowStatus(string status, double minute) =>
        _followText!.Text = $"{Model.FollowedName} · {status}{(Model.FollowedJob != "" ? $" ({Model.FollowedJob})" : "")} · {Commuters.TimeOfDay(minute)}";

    void Ticker(double delta)
    {
        var hs = Model.Headlines;
        _ticker!.Visible = hs.Count > 0;
        if (hs.Count == 0) return;
        _tickerClock += delta;
        // A new headline shows at once; otherwise the newest few take turns.
        if (hs[0].Seq != _tickerSeq) { _tickerSeq = hs[0].Seq; _tickerIndex = 0; _tickerClock = 0; }
        else if (_tickerClock > 7) { _tickerIndex = (_tickerIndex + 1) % Math.Min(6, hs.Count); _tickerClock = 0; }
        var h = hs[Math.Clamp(_tickerIndex, 0, hs.Count - 1)];
        _ticker.Text = $"📰 {h.Text}";
        _ticker.Modulate = h.Severity switch { "danger" => new Color(1, 0.6f, 0.55f), "warning" => new Color(1, 0.88f, 0.55f), "good" => new Color(0.75f, 1, 0.75f), _ => Colors.White };
    }

    /// <summary>The R, C and I demand bars, as SC4's: up from the middle when the city wants more, with an outline for demand held back by limits such as power.</summary>
    partial class DemandBars : Control
    {
        readonly CityMirror _city;
        public DemandBars(CityMirror city) => _city = city;

        public override void _Draw()
        {
            var s = _city.Stats;
            if (s.Demand.Count < 9) return;
            var raw = s.DemandRaw.Count >= 9 ? s.DemandRaw : s.Demand;
            Color[] colours = { new(0.3f, 0.69f, 0.31f), new(0.29f, 0.5f, 0.88f), new(0.88f, 0.69f, 0.19f) };
            float w = Size.X / 3, mid = Size.Y / 2;
            DrawLine(new Vector2(0, mid), new Vector2(Size.X, mid), new Color(1, 1, 1, 0.4f));
            for (int z = 0; z < 3; z++)
            {
                int v = Math.Max(s.Demand[z * 3], Math.Max(s.Demand[z * 3 + 1], s.Demand[z * 3 + 2]));
                int rv = Math.Max(raw[z * 3], Math.Max(raw[z * 3 + 1], raw[z * 3 + 2]));
                Rect2 Bar(int d) { float h = Math.Abs(d) / 100f * mid; return new Rect2(z * w + 3, d >= 0 ? mid - h : mid, w - 6, h); }
                DrawRect(Bar(v), colours[z] with { A = v >= 0 ? 1 : 0.45f });
                if (rv > v) DrawRect(Bar(rv), colours[z], false, 1);
            }
        }
    }
}
