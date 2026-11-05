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

    Label? _name, _date, _funds, _pop, _fundsChange, _sideTitle;
    PanelContainer? _topBar, _lbar, _modes, _upright, _gameMenu;
    Control? _miniFrame;
    Button? _mayorMode;
    readonly Dictionary<string, Button> _uprightButtons = new();
    int _opened;
    Control? _sideHead;
    OptionButton? _pace;
    /// <summary>Both clocks, the hands running on between ticks (Beta 3.2).</summary>
    readonly GameClock _clock = new();
    Button? _approval, _alerts, _seat, _dispatch, _company;
    /// <summary>The scene in the 3D city, commanded while the dispatch screen shows it.</summary>
    public SceneWorld? Scene3D { get; set; }
    CenterContainer? _sceneBox;
    PanelContainer? _report, _tabsBox;
    RichTextLabel? _reportText;
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
    Label? _coPending;
    PanelContainer? _coTools;
    RichTextLabel? _coToolsText;
    string _wasScreen = "";
    string? _wasShown;
    /// <summary>Whether screen switches fade; off for screenshots and scripted runs, which capture a frame at once.</summary>
    public bool Transitions { get; set; } = true;
    ColorRect? _dip;

    PanelContainer? _working;
    Label? _workingText;

    /// <summary>
    /// The "working…" card in the middle of the screen, from the first frame of a long wait (a new city, a save, a
    /// load, opening or moving to another city of the region) until it's done.
    /// </summary>
    void WorkingCard(double delta)
    {
        string? what = Model.Working ?? (Model.Region.Pending is { Length: > 0 } rp && (rp.StartsWith("Opening") || rp.StartsWith("Moving") || rp.StartsWith("Founding")) ? rp : null);
        if (_working == null)
        {
            var layer = new CanvasLayer { Layer = 9 };
            AddChild(layer);
            var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(centre);
            _working = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _working.AddThemeStyleboxOverride("panel", UiTheme.Floating());
            centre.AddChild(_working);
            _workingText = new Label { HorizontalAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(260, 0) };
            _workingText.AddThemeFontSizeOverride("font_size", 16);
            _working.AddChild(_workingText);
        }
        _working.Visible = what != null;
        if (what == null) return;
        // A turning hourglass, so a long wait still looks alive.
        _workingText!.Text = $"{((int)(Time.GetTicksMsec() / 500) % 2 == 0 ? "⏳" : "⌛")} {what}";
    }

    /// <summary>A quick fade from the background colour, over everything but the sign-in.</summary>
    void Dip()
    {
        if (_dip == null)
        {
            var layer = new CanvasLayer { Layer = 10 };
            AddChild(layer);
            _dip = new ColorRect { Color = UiTheme.Bg, MouseFilter = Control.MouseFilterEnum.Ignore };
            _dip.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(_dip);
        }
        _dip.Modulate = new Color(1, 1, 1, 0.7f);
        _dip.Visible = true;
        var t = CreateTween();
        t.TweenProperty(_dip, "modulate:a", 0f, 0.22).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        t.TweenCallback(Callable.From(() => _dip.Visible = false));
    }
    /// <summary>Whether the camera keeps the followed commuter centred; panning hands the camera back.</summary>
    public bool FollowCam;

    public PanelView(CityClient client)
    {
        _client = client;
        Model = new PanelModel(client.City, client.Connection.Send);
    }

    /// <summary>The Graphics button: Main opens the options.</summary>
    public event System.Action? GraphicsPressed;
    public event System.Action? RadarPressed;
    public event System.Action? EditorPressed;

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
    Button[] Speeds(Container parent, bool glass = false)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        if (glass)
        {
            var well = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            well.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(1, 1, 1, 0.05f), null, 9, 2, 2));
            parent.AddChild(well);
            well.AddChild(box);
            string[] g = { "", "1×", "2×", "3×" };
            string[] gt = { "Pause (Space)", "Slow", "Normal", "Fast" };
            var gl = new Button[4];
            for (int k = 0; k < 4; k++)
            {
                int kk = k;
                gl[k] = UiTheme.Flat(new Button { Text = g[k], ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = gt[k] }, 8, 3, 7);
                if (k == 0) gl[k].Icon = UiTheme.Icon("pause", 14);
                gl[k].AddThemeFontSizeOverride("font_size", 12);
                gl[k].Pressed += () => _client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = kk } });
                box.AddChild(gl[k]);
            }
            return gl;
        }
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

    /// <summary>
    /// The calendar's pace (Beta 3.2): how many calendar days pass in a day of the clock. Growth, building and money go
    /// by the calendar; commuters, calls and the sun by the clock.
    /// </summary>
    OptionButton PaceMenu(Container parent)
    {
        var o = UiTheme.Small(new OptionButton { FocusMode = Control.FocusModeEnum.None }, 8, 2, 11);
        foreach (int pace in GameClock.Paces) o.AddItem(GameClock.PaceName(pace), pace);
        o.TooltipText = "The calendar's pace: how many calendar days pass in one day of the clock.\nGrowth, building and money go by the calendar; commuters, calls and the sun by the clock.";
        o.ItemSelected += k => _client.Connection.Send(new ClientMessage { Pace = new SetPace { DaysPerClockDay = o.GetItemId((int)k) } });
        parent.AddChild(o);
        return o;
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

    /// <summary>A small upper-case label over a number in the L's bar.</summary>
    static VBoxContainer Cell(Container parent, string label)
    {
        var v = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        v.AddThemeConstantOverride("separation", 0);
        parent.AddChild(v);
        v.AddChild(new Label { Text = label.ToUpperInvariant(), LabelSettings = new LabelSettings { FontSize = 9, FontColor = UiTheme.Label2 } });
        return v;
    }

    static Control Sep(Container parent)
    {
        var r = new ColorRect { Color = UiTheme.GlassLine, CustomMinimumSize = new Vector2(1, 34), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        parent.AddChild(r);
        return r;
    }

    static PanelContainer GlassPanel(Control parent)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", UiTheme.GlassBox());
        parent.AddChild(p);
        return p;
    }

    static readonly (string Id, string Icon, string Tip)[] Upright =
    {
        ("layers", "layers", "Layers: what the map shows"), ("underground", "underground", "Underground view (U): pipes, tunnels and subways"), ("traffic", "traffic", "Commuters on the streets (M)"),
        ("why", "why", "Why: what you queried, and why it is the way it is"), ("inspect", "inspect", "Inspect: the raw values of a queried cell"),
        ("demand", "demand", "Demand: what the city wants more of, and why"), ("forecast", "forecast", "Forecast: where the city is heading"), ("people", "people", "People: what they think of you"),
        ("laws", "laws", "Laws: ordinances"), ("guide", "guide", "Guide: how to play"),
    };

    /// <summary>
    /// The mayor's view of the UI overhaul (variation A, Beta 4): the city's name and the modes in a glass pill top
    /// right, and the L in the bottom left, the minimap and the panels' buttons up it and the city's numbers along it.
    /// </summary>
    void BuildL()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // The bar along the bottom.
        _lbar = GlassPanel(root);
        _lbar.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        (_lbar.OffsetLeft, _lbar.OffsetRight, _lbar.OffsetTop, _lbar.OffsetBottom) = (12, -12, -68, -12);
        var lb = _lbar.GetThemeStylebox("panel") as StyleBoxFlat;
        var row = _cityBar = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        _lbar.AddChild(row);
        _date = new Label { CustomMinimumSize = new Vector2(220, 0) };
        _date.AddThemeFontSizeOverride("font_size", 14);
        _date.MouseFilter = Control.MouseFilterEnum.Pass;
        Cell(row, "Date").AddChild(_date);
        _speeds = Speeds(row, glass: true);
        _pace = PaceMenu(row);
        _pace.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        UiTheme.Flat(_pace, 8, 4, 8);
        Sep(row);
        var fundsRow = new HBoxContainer();
        fundsRow.AddThemeConstantOverride("separation", 6);
        Cell(row, "Funds").AddChild(fundsRow);
        _funds = new Label { TooltipText = "Treasury", MouseFilter = Control.MouseFilterEnum.Pass };
        fundsRow.AddChild(_funds);
        _fundsChange = new Label { VerticalAlignment = VerticalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Good } };
        fundsRow.AddChild(_fundsChange);
        _pop = new Label { TooltipText = "Population", MouseFilter = Control.MouseFilterEnum.Pass };
        Cell(row, "People").AddChild(_pop);
        var ap = Cell(row, "Approval");
        _approval = UiTheme.Flat(Btn("Approval", () => Model.Open("people"), ap), 0, 0, 4);
        _approval.TooltipText = "Mayor approval: click for what people think";
        _approval.Alignment = HorizontalAlignment.Left;
        _rci = new DemandBars(_client.City) { CustomMinimumSize = new Vector2(50, 40), TooltipText = "Demand: bars up mean the city wants more of that zone", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _rci.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Model.Open("demand"); };
        row.AddChild(_rci);
        Sep(row);
        // The news, in the bar's free middle.
        _ticker = new Button { FocusMode = Control.FocusModeEnum.None, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Alignment = HorizontalAlignment.Left, Text = "" };
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed" })
            _ticker.AddThemeStyleboxOverride(st, UiTheme.Box(new Color(1, 1, 1, st.StartsWith("hover") ? 0.07f : 0.04f), null, 16, 12, 6));
        _ticker.Pressed += () =>
        {
            var hs = Model.Headlines;
            if (_tickerIndex >= 0 && _tickerIndex < hs.Count && hs[_tickerIndex].X >= 0) Model.Click($"jump:{hs[_tickerIndex].X}:{hs[_tickerIndex].Y}");
            else Model.Open("people");
        };
        row.AddChild(_ticker);
        _alerts = UiTheme.IconButton("alerts", "Alerts: problems in the city, click one to go there");
        _alerts.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _alerts.Pressed += () => Toggle("alerts");
        row.AddChild(_alerts);
        var budget = UiTheme.IconButton("budget", "Budget: taxes, spending and loans");
        budget.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        budget.ToggleMode = true;
        budget.Pressed += () => Toggle("budget");
        row.AddChild(budget);
        _uprightButtons["budget"] = budget;

        // The upright: the minimap and the panels' buttons.
        _upright = GlassPanel(root);
        _upright.GrowVertical = Control.GrowDirection.Begin;
        var ucol = new VBoxContainer();
        ucol.AddThemeConstantOverride("separation", 6);
        _upright.AddChild(ucol);
        _miniFrame = new Control { CustomMinimumSize = new Vector2(184, 184), MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = "The whole city: click or drag to look there" };
        ucol.AddChild(_miniFrame);
        _miniFrame.GuiInput += e =>
        {
            bool look = e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left };
            if (!look || Map?.CellAt(GetViewport().GetMousePosition()) is not { } m) return;
            Camera?.CentreOn(m.X + 0.5f, m.Y + 0.5f);
            FollowCam = false;
            _miniFrame.AcceptEvent();
        };
        _miniFrame.Resized += () => Map?.Refit();
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 2);
        grid.AddThemeConstantOverride("v_separation", 2);
        ucol.AddChild(grid);
        foreach (var (id, icon, tip) in Upright)
        {
            var b = UiTheme.IconButton(icon, tip, 18, 7);
            b.ToggleMode = true;
            string pid = id;
            b.Pressed += () =>
            {
                if (pid == "underground") Model.ShowUnderground = !Model.ShowUnderground;
                else if (pid == "traffic") Model.ShowAgents = !Model.ShowAgents;
                else Toggle(pid);
            };
            grid.AddChild(b);
            _uprightButtons[id] = b;
        }

        // The city and its modes, top right.
        _modes = GlassPanel(root);
        var ms = UiTheme.GlassBox(22, 14, 4);
        ms.ContentMarginRight = 6;
        _modes.AddThemeStyleboxOverride("panel", ms);
        _modes.GrowHorizontal = Control.GrowDirection.Begin;
        var mrow = new HBoxContainer();
        mrow.AddThemeConstantOverride("separation", 4);
        _modes.AddChild(mrow);
        _name = new Label { VerticalAlignment = VerticalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 14, FontColor = Colors.White } };
        mrow.AddChild(_name);
        Sep(mrow).CustomMinimumSize = new Vector2(1, 22);
        Button Mode(string text, string tip, System.Action go)
        {
            var b = UiTheme.Flat(Btn(text, go, mrow), 12, 5, 16);
            b.TooltipText = tip;
            return b;
        }
        _mayorMode = Mode("Mayor", "The mayor's view: build and run the city", () => { });
        _mayorMode.ToggleMode = true;
        _mayorMode.SetPressedNoSignal(true);
        _company = Mode("Transit", "Switch to the bus company's own screen", () => Model.Click("co-open"));
        _company.Visible = false;
        _dispatch = Mode("Dispatch", "Switch to the emergency services' own screen", () => Model.Click("em-open"));
        _dispatch.Visible = false;
        Mode("Region", "The region map: the cities around this one, and open land to found more", () => Model.Click("rg-open"));
        var menu = UiTheme.IconButton("menu", "Game: who runs the city, new, save, load and graphics", 18, 6);
        mrow.AddChild(menu);

        // The game menu under it.
        _gameMenu = GlassPanel(root);
        _gameMenu.Visible = false;
        _gameMenu.GrowHorizontal = Control.GrowDirection.Begin;
        _gameMenu.CustomMinimumSize = new Vector2(200, 0);
        var gcol = new VBoxContainer();
        _gameMenu.AddChild(gcol);
        Button Item(string text, System.Action go) { var b = UiTheme.Flat(Btn(text, () => { _gameMenu!.Visible = false; go(); }, gcol), 10, 5, 8); b.Alignment = HorizontalAlignment.Left; return b; }
        _seat = Item("Mayor", () => Model.OpenDialog("seat"));
        _seat.TooltipText = "Who runs the city: hand it to the computer while you're away";
        Item("New city", () => Model.OpenDialog("new"));
        Item("Save", () => Model.OpenDialog("save"));
        Item("Load", () => Model.OpenDialog("load"));
        Item("Weather radar", () => RadarPressed?.Invoke());
        Item("Asset editor", () => EditorPressed?.Invoke()).TooltipText = "Build voxel assets from snap pieces and props, and use them for buildings";
        Item("Graphics", () => GraphicsPressed?.Invoke());
        menu.Pressed += () => _gameMenu.Visible = !_gameMenu.Visible;
        // Placed once their contents give them a size: top right, the menu under the pill.
        Callable.From(() =>
        {
            _modes.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
            _gameMenu.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
            _gameMenu.OffsetTop += 48;
            _gameMenu.OffsetBottom += 48;
            _upright.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 12);
            _upright.OffsetTop -= 64;
            _upright.OffsetBottom -= 64;
        }).CallDeferred();
    }

    /// <summary>The minimap: in the L's upright in the mayor's view, in the map's lower right on the side games' screens.</summary>
    void PlaceMap()
    {
        if (Map == null || _miniFrame == null) return;
        Node want = _wasScreen == "city" ? _miniFrame : Map.HomeParent ?? this;
        if (Map.GetParent() != want) Map.Reparent(want, false);
        Map.Refit();
    }

    /// <summary>For the gate: whether the floating panel is up, and where.</summary>
    public Rect2? SideRect => _side is { Visible: true } ? _side.GetGlobalRect() : null;
    public Rect2 BarRect => _lbar!.GetGlobalRect();
    public Rect2 UprightRect => _upright!.GetGlobalRect();
    public Rect2 ModesRect => _modes!.GetGlobalRect();
    public void ClosePanel() => _side!.Visible = false;

    /// <summary>An L button: opens its tab in the floating panel, or closes the panel when that tab is up.</summary>
    void Toggle(string tab)
    {
        if (_side!.Visible && Model.Tab == tab) { _side.Visible = false; return; }
        Model.Open(tab);
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

        _topBar = bar;
        BuildL();

        // The bus company's top bar, in the city's place while its screen is up.
        var co = _coBar = TopRow(bars, false);
        _coText = Stat(co, "", 14, UiTheme.Accent, bold: true);
        _coDate = Stat(co);
        _coSpeeds = Speeds(co);
        _coSeat = Btn("", () => Model.Click(Model.Company.Mine ? "co-handoff" : "co-claim"), co);
        _coPending = Stat(co, "", UiTheme.FontSize, UiTheme.Warn);
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
        _side = new PanelContainer { Visible = false };
        _side.AddThemeStyleboxOverride("panel", UiTheme.Docked(Side.Left, 0));
        _side.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        (_side.OffsetLeft, _side.OffsetRight, _side.OffsetTop, _side.OffsetBottom) = (-ToolController.SideWidth, 0, ToolController.TopHeight, 0);
        AddChild(_side);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        _side.AddChild(col);
        // In the mayor's view the panel floats and closes: its tab's name and a close button head it.
        var shead = new HBoxContainer { Visible = false };
        var sheadBox = new MarginContainer();
        foreach (var (side, m) in new[] { ("left", 14), ("right", 6), ("top", 8), ("bottom", 0) }) sheadBox.AddThemeConstantOverride($"margin_{side}", m);
        sheadBox.AddChild(shead);
        col.AddChild(sheadBox);
        _sideHead = sheadBox;
        _sideTitle = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 15, FontColor = Colors.White } };
        shead.AddChild(_sideTitle);
        var sclose = UiTheme.IconButton("close", "Close (P)", 16, 6);
        sclose.Pressed += () => _side!.Visible = false;
        shead.AddChild(sclose);
        shead.Visible = true;
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
        // Direct dispatch's keys on the map: H hold, G go home, F follow, ctrl+number a group, a number picks it.
        if (Model.Emergency.Directing && Model.Emergency.MapKey(OS.GetKeycodeString(k.Keycode), k.CtrlPressed)) { GetViewport().SetInputAsHandled(); return; }
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
        WorkingCard(delta);
        bool company = Model.Company.Active, dispatch = !company && Model.Emergency.Active;
        // Which screen is up: the city's, or a side game's own, each with its own top bar and left column.
        string screen = company ? "company" : dispatch ? "dispatch" : "city";
        // Every switch between the mayor's, the company's, the dispatch and the region screens dips through the
        // background for a moment, so it reads as one window changing rather than a jump.
        string shown = Model.Region.Active ? "region" : screen;
        if (shown != _wasShown)
        {
            if (_wasShown != null && Transitions) Dip();
            _wasShown = shown;
        }
        if (screen != _wasScreen)
        {
            _wasScreen = screen;
            _cityTabs!.Visible = screen == "city";
            bool city = screen == "city";
            _topBar!.Visible = !city;
            _lbar!.Visible = _modes!.Visible = _upright!.Visible = city;
            _gameMenu!.Visible = false;
            _sideHead!.Visible = city;
            // The side games keep the docked panel down the right; the mayor's floats as glass and closes.
            if (city)
            {
                _side!.AddThemeStyleboxOverride("panel", UiTheme.GlassBox(12, 0, 0));
                (_side.OffsetLeft, _side.OffsetRight, _side.OffsetTop, _side.OffsetBottom) = (-392, -12, 64, -84);
            }
            else
            {
                _side!.AddThemeStyleboxOverride("panel", UiTheme.Docked(Side.Left, 0));
                (_side.OffsetLeft, _side.OffsetRight, _side.OffsetTop, _side.OffsetBottom) = (-ToolController.SideWidth, 0, ToolController.TopHeight, 0);
                _side.Visible = true;
            }
            PlaceMap();
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
        // A tab asked for (a query, an alert, an L button) opens the floating panel again.
        if (Model.Opened != _opened) { _opened = Model.Opened; _side.Visible = true; }
        if (_wasScreen == "city")
        {
            _sideTitle!.Text = PanelModel.Tabs.FirstOrDefault(t => t.Id == Model.Tab).Name ?? Model.Tab;
            foreach (var (id, b) in _uprightButtons)
                b.SetPressedNoSignal(id switch { "underground" => Model.ShowUnderground, "traffic" => Model.ShowAgents, _ => _side.Visible && Model.Tab == id });
        }
        // The scene a player runs is in the 3D city itself; its report comes up over it once it's over.
        var em = Model.Emergency;
        bool scene = dispatch && em.Scene != null && !em.SceneHidden;
        if (Scene3D != null) Scene3D.Commanding = scene;
        _sceneBox!.Visible = _report!.Visible = scene && em.SceneOver && em.ReportOpen;
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
        string clock = _clock.Text(s, DateTime.UtcNow);
        _date!.Text = clock + (s.Weather.Length > 0 ? $" · {s.Weather}" : "");
        _date.TooltipText = $"Day {s.Day} of {MonthNames[Math.Clamp(s.Month - 1, 0, 11)]}, {s.Year}. The calendar runs at {GameClock.PaceName(s.Pace == 0 ? 30 : s.Pace).ToLowerInvariant()} of the clock." + (s.Roads is { Text.Length: > 0 } r ? $"\n{r.Text}. Road crews on {r.CrewsPct}% of their budget." : "");
        int paceAt = Array.IndexOf(GameClock.Paces, s.Pace == 0 ? 30 : s.Pace);
        if (paceAt >= 0 && _pace!.Selected != paceAt) _pace.Select(paceAt);
        _funds!.Text = Bb.Money(s.Funds);
        _funds.AddThemeColorOverride("font_color", s.Funds < 0 ? UiTheme.Bad : UiTheme.Text);
        _name.AddThemeColorOverride("font_color", UiTheme.Text);
        _pop!.Text = Bb.Num(s.Population);
        long net = s.LastMonthIncome - s.LastMonthExpenses;
        _fundsChange!.Text = s.LastMonthIncome != 0 || s.LastMonthExpenses != 0 ? $"{(net >= 0 ? "+" : "−")}{Bb.Money(Math.Abs(net))}/mo" : "";
        _fundsChange.LabelSettings.FontColor = net >= 0 ? UiTheme.Good : UiTheme.Bad;
        foreach (var set in new[] { _speeds, _coSpeeds, _emSpeeds })
            for (int k = 0; k < set.Length; k++) set[k].SetPressedNoSignal(s.Speed == k);
        if (Model.Civic is { } civic)
        {
            _approval!.Text = $"{(civic.Approval >= 50 ? "👍" : "👎")} {civic.Approval}%";
            _approval.Modulate = civic.Approval < 40 ? new Color(1, 0.6f, 0.55f) : Colors.White;
            _tabs["people"].Text = Model.OpenPrompts > 0 ? $"People ({Model.OpenPrompts})" : "People";
        }
        var (count, worst) = Model.AlertBadge;
        _alerts!.Text = count > 0 ? count.ToString() : "";
        _alerts.Modulate = worst switch { "danger" => new Color(1, 0.5f, 0.45f), "warning" => new Color(1, 0.85f, 0.45f), _ => Colors.White };
        _seat!.Text = Model.SeatLabel;
        _dispatch!.Visible = Model.Emergency.View != null;
        _dispatch.Text = Model.Emergency.Waiting > 0 ? $"Dispatch ({Model.Emergency.Waiting})" : "Dispatch";
        _company!.Visible = Model.Company.View != null;
        if (Model.Company.View is { } co) _company.TooltipText = $"Switch to {co.Name}'s own screen";
        _rci!.QueueRedraw();
    }

    void DispatchBar(CityMirror c)
    {
        var em = Model.Emergency;
        var s = c.Stats;
        _emName!.Text = $"🚒 {em.View?.Name ?? "Emergency services"}";
        _emText!.Text = em.TopStats;
        // A dispatcher lives by the day clock; the calendar's month is there for the seasons.
        _emDate!.Text = _clock.Text(s, DateTime.UtcNow);
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
        if (em.Directing)
            _emToolList.AddChild(new Label { Text = em.UnitText, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                LabelSettings = new LabelSettings { FontSize = 11, FontColor = UiTheme.Muted } });
        if (em.Scene != null && em.Mine && !em.SceneOver && !em.SceneHidden)
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
        _coDate!.Text = _clock.Text(s, DateTime.UtcNow);
        var v = Model.Company.View;
        _coSeat!.Text = v == null ? "" : Model.Company.Mine ? "You run it · hand to the computer" : $"The computer runs it ({v.Operator}) · run it yourself";
        // While a change waits on the city's next day, it says so in place of the button.
        _coSeat.Visible = v != null && Model.Company.Pending == null;
        _coPending!.Visible = Model.Company.Pending != null;
        _coPending.Text = Model.Company.Pending is { } p ? $"⏳ {p}" : "";
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
        // It keeps its place in the L's bar, empty until there's news.
        if (hs.Count == 0) { _ticker!.Text = ""; return; }
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
