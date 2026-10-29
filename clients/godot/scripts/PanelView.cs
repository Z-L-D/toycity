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
    Button? _approval, _alerts, _seat, _dispatch;
    SceneCanvas? _scene;
    int _sceneShown = -1;
    readonly Button[] _speeds = new Button[4];
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
    HBoxContainer? _cityBar, _coBar;
    HFlowContainer? _cityTabs, _coTabs;
    readonly Dictionary<string, Button> _coTabButtons = new();
    Label? _coText, _coDate;
    Button? _coSeat;
    PanelContainer? _coTools;
    RichTextLabel? _coToolsText;
    bool _wasCompany;
    /// <summary>Whether the camera keeps the followed commuter centred; panning hands the camera back.</summary>
    public bool FollowCam;

    public PanelView(CityClient client)
    {
        _client = client;
        Model = new PanelModel(client.City, client.Connection.Send);
    }

    static void Outline(Control c)
    {
        c.AddThemeColorOverride("font_outline_color", Colors.Black);
        c.AddThemeConstantOverride("outline_size", 4);
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

    public override void _Ready()
    {
        Layer = 3;
        // The top bar.
        var bar = new PanelContainer();
        bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        AddChild(bar);
        var bars = new VBoxContainer();
        bar.AddChild(bars);
        var row = _cityBar = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        bars.AddChild(row);
        _name = new Label();
        _name.AddThemeFontSizeOverride("font_size", 18);
        row.AddChild(_name);
        row.AddChild(_date = new Label());
        row.AddChild(_funds = new Label());
        row.AddChild(_pop = new Label());
        _rci = new DemandBars(_client.City) { CustomMinimumSize = new Vector2(42, 30), TooltipText = "Demand: click for where it comes from" };
        _rci.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Model.Open("demand"); };
        row.AddChild(_rci);
        _approval = Btn("Approval", () => Model.Open("people"), row);
        _alerts = Btn("Alerts", () => Model.Open("alerts"), row);
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        string[] speedNames = { "⏸", "▶", "▶▶", "▶▶▶" };
        for (int k = 0; k < 4; k++)
        {
            int kk = k;
            _speeds[k] = Btn(speedNames[k], () => _client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = kk } }), row);
            _speeds[k].ToggleMode = true;
        }
        _seat = Btn("Mayor", () => Model.OpenDialog("seat"), row);
        _dispatch = Btn("🚒 Dispatch", () => Model.OpenDialog("dispatch"), row);
        _dispatch.Visible = false;
        Btn("New", () => Model.OpenDialog("new"), row);
        Btn("Save", () => Model.OpenDialog("save"), row);
        Btn("Load", () => Model.OpenDialog("load"), row);
        Btn("Graphics", () => GraphicsPressed?.Invoke(), row);
        Btn("Panel (P)", () => _side!.Visible = !_side.Visible, row);

        // The bus company's top bar, in its place while the company's view is up.
        var co = _coBar = new HBoxContainer { Visible = false };
        co.AddThemeConstantOverride("separation", 14);
        bars.AddChild(co);
        co.AddChild(new Label { Text = "🚌" });
        co.AddChild(_coText = new Label());
        co.AddChild(_coDate = new Label());
        co.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _coSeat = Btn("", () => Model.Click(Model.Company.Mine ? "co-handoff" : "co-claim"), co);
        Btn("⧉ Own window", () => Model.Click("co-window"), co);
        Btn("🏙 Back to the city", () => Model.Click("co-city"), co);

        // The side panel.
        _side = new PanelContainer();
        _side.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        _side.OffsetLeft = -420;
        _side.OffsetRight = -8;
        _side.OffsetTop = 120;
        _side.OffsetBottom = -224;
        AddChild(_side);
        var col = new VBoxContainer();
        _side.AddChild(col);
        var tabs = _cityTabs = new HFlowContainer();
        col.AddChild(tabs);
        _coTabs = new HFlowContainer { Visible = false };
        col.AddChild(_coTabs);
        var coGroup = new ButtonGroup();
        foreach (var (id, name) in CompanyModel.Tabs)
        {
            var b = Btn(name, () => Model.Click($"co-tab:{id}"), _coTabs);
            b.ToggleMode = true;
            b.ButtonGroup = coGroup;
            _coTabButtons[id] = b;
        }
        var group = new ButtonGroup();
        foreach (var (id, name) in PanelModel.Tabs)
        {
            var b = Btn(name, () => Model.Open(id), tabs);
            b.ToggleMode = true;
            b.ButtonGroup = group;
            _tabs[id] = b;
        }
        _input = new LineEdit { Visible = false, ClearButtonEnabled = true };
        _input.TextChanged += t => Model.SetInput(t);
        col.AddChild(_input);
        _text = Text();
        _text.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        col.AddChild(_text);

        // The company's tools, down the left.
        _coTools = new PanelContainer { Visible = false };
        _coTools.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        _coTools.OffsetLeft = 8;
        _coTools.OffsetRight = 248;
        _coTools.OffsetTop = 52;
        _coTools.OffsetBottom = -60;
        AddChild(_coTools);
        _coToolsText = Text();
        _coTools.AddChild(_coToolsText);

        // Dialogs, in the middle of the screen.
        var centre = new CenterContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);
        _dialog = centre;
        var box = new PanelContainer();
        centre.AddChild(box);
        var dcol = new VBoxContainer();
        box.AddChild(dcol);
        _dialogInput = new LineEdit { Visible = false, MaxLength = 40 };
        _dialogInput.TextChanged += t => Model.SetInput(t);
        dcol.AddChild(_dialogInput);
        var drow = new HBoxContainer();
        dcol.AddChild(drow);
        _dialogText = Text();
        _dialogText.CustomMinimumSize = new Vector2(560, 440);
        drow.AddChild(_dialogText);
        // An emergency scene the player runs or watches, beside the dispatch board.
        _scene = new SceneCanvas(Model.Emergency) { Visible = false };
        drow.AddChild(_scene);

        // Banners under the toolbar.
        (_away, _awayText) = Banner(124);
        Btn("Take it back", Model.TakeBack, (Container)_away.GetChild(0));
        (_follow, _followText) = Banner(164);
        _followCentre = Btn("Centre", () => FollowCam = true, (Container)_follow.GetChild(0));
        Btn("Stop following", Model.StopFollowing, (Container)_follow.GetChild(0));

        // The news ticker.
        _ticker = new Button { Flat = true, FocusMode = Control.FocusModeEnum.None, Visible = false, ClipText = true };
        _ticker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 6);
        _ticker.GrowHorizontal = Control.GrowDirection.Both;
        _ticker.GrowVertical = Control.GrowDirection.Begin;
        Outline(_ticker);
        _ticker.Pressed += () =>
        {
            var hs = Model.Headlines;
            if (_tickerIndex >= 0 && _tickerIndex < hs.Count && hs[_tickerIndex].X >= 0) Model.Click($"jump:{hs[_tickerIndex].X}:{hs[_tickerIndex].Y}");
            else Model.Open("people");
        };
        AddChild(_ticker);
    }

    RichTextLabel Text()
    {
        var t = new RichTextLabel { BbcodeEnabled = true, ScrollActive = true, SelectionEnabled = false, FocusMode = Control.FocusModeEnum.None };
        t.MetaClicked += meta => Model.Click(meta.AsString());
        return t;
    }

    (PanelContainer, Label) Banner(float y)
    {
        var p = new PanelContainer { Visible = false };
        p.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.Minsize, (int)y);
        p.GrowHorizontal = Control.GrowDirection.Both;
        AddChild(p);
        var row = new HBoxContainer();
        p.AddChild(row);
        var l = new Label();
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
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        var c = _client.City;
        Model.Tick(delta);
        bool company = Model.Company.Active;
        if (company != _wasCompany)
        {
            _wasCompany = company;
            _cityBar!.Visible = _cityTabs!.Visible = !company;
            _coBar!.Visible = _coTabs!.Visible = _coTools!.Visible = company;
            if (Tools != null) Tools.Enabled = !company;
            _shown = -1;
        }
        if (c.Loaded) TopBar(c);
        if (company) CompanyBar(c);
        Redraw();
        bool scene = Model.Dialog == "dispatch" && Model.Emergency.Scene != null;
        _scene!.Visible = scene;
        if (scene && Model.Emergency.SceneVersion != _sceneShown) { _sceneShown = Model.Emergency.SceneVersion; _scene.QueueRedraw(); }
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
        _funds.Modulate = s.Funds < 0 ? new Color(1, 0.55f, 0.5f) : Colors.White;
        _pop!.Text = $"Pop {Bb.Num(s.Population)}";
        for (int k = 0; k < 4; k++) _speeds[k].SetPressedNoSignal(s.Speed == k);
        if (Model.Civic is { } civic)
        {
            _approval!.Text = $"{(civic.Approval >= 50 ? "👍" : "👎")} {civic.Approval}%";
            _approval.Modulate = civic.Approval < 40 ? new Color(1, 0.6f, 0.55f) : Colors.White;
            _tabs["people"].Text = Model.OpenPrompts > 0 ? $"People ({Model.OpenPrompts})" : "People";
        }
        var (count, worst) = Model.AlertBadge;
        _alerts!.Text = $"Alerts {count}";
        _alerts.Modulate = worst switch { "danger" => new Color(1, 0.5f, 0.45f), "warning" => new Color(1, 0.85f, 0.45f), _ => Colors.White };
        _seat!.Text = Model.SeatLabel;
        _dispatch!.Visible = Model.Emergency.View != null;
        _dispatch.Text = Model.Emergency.Waiting > 0 ? $"🚒 Dispatch ({Model.Emergency.Waiting})" : "🚒 Dispatch";
        _rci!.QueueRedraw();
    }

    void CompanyBar(CityMirror c)
    {
        var s = c.Stats;
        _coText!.Text = Model.Company.TopText;
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
        string key = dialog ? Model.Dialog! : Model.Company.Active ? "co-" + Model.Company.Tab : Model.Tab;
        bool same = dialog ? key == _shownDialog : key == _shownTab;
        double scroll = same ? target.GetVScrollBar().Value : 0;
        target.Text = Model.Render();
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
