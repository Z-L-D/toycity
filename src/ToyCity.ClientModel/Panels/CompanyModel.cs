using System.Text;
using Google.Protobuf;
using ToyCity.Protocol.V0;
using ToyCity.Transport.Wire;
using static ToyCity.ClientModel.Panels.Bb;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Panels;

public enum CompanyTool { Look, Lease, Line, Signal }

/// <summary>
/// The bus company's own view, as the web client has it. Switching to it swaps the city's top bar, tools and panel
/// for the company's: its cash and buses, tools for leasing lots and drawing lines, and tabs for lines, lots,
/// money, standing and the log. The map stays, dimmed, with the company's lots and routes on top. Like
/// <see cref="PanelModel"/> it is text with links, and has no engine types.
/// </summary>
public sealed class CompanyModel
{
    /// <summary>The city's first transit company; a rival the mayor invites in has an id starting "transport-".</summary>
    public const string Game = "transport";
    static bool IsTransport(string game) => game == Game || game.StartsWith("transport-", StringComparison.Ordinal);
    public static readonly (string Id, string Name)[] Tabs = { ("lines", "Lines"), ("lots", "Lots"), ("money", "Money"), ("standing", "Standing"), ("log", "Log") };

    readonly CityMirror _city;
    readonly Func<ClientMessage, uint> _send;
    readonly System.Action _changed;

    public CompanyModel(CityMirror city, Func<ClientMessage, uint> send, System.Action changed) => (_city, _send, _changed) = (city, send, changed);

    /// <summary>Who this client plays as, named "you" in the city's reasons.</summary>
    public Func<string> Me { get; set; } = () => "player";

    public View? View { get; private set; }
    /// <summary>Every transit company's view, by game id, and the one shown.</summary>
    readonly SortedDictionary<string, (View View, bool Public)> _views = new(StringComparer.Ordinal);
    public string GameId { get; private set; } = Game;
    public IReadOnlyDictionary<string, (View View, bool Public)> Companies => _views;
    /// <summary>The player picked the company to show: don't switch to their own when it arrives.</summary>
    bool _picked;
    /// <summary>True while the company's view is up instead of the city's.</summary>
    public bool Active { get; private set; }
    public CompanyTool Tool { get; private set; }
    public string Tab { get; private set; } = "lines";
    public bool ShowCatchment { get; private set; } = true;
    /// <summary>The lot picked with the look tool, by building id.</summary>
    public int? Selected { get; private set; }
    /// <summary>The line being drawn: its stops (lease ids) in order, and what runs on it (-1 until its first stop).</summary>
    public (int LineId, string Name, List<int> Stops, int Fare, int Mode, bool Freight, bool ToEdge)? Draft { get; private set; }

    static readonly string[] StopKinds = { "bus-stop", "tram-stop", "rail-station" };
    static readonly string[] ModeIcon = { "🚌", "🚋", "🚆" };
    static int ModeOfStop(string kind) => Array.IndexOf(StopKinds, kind);
    static bool IsStop(string kind) => ModeOfStop(kind) >= 0 || kind == "freight-depot";
    static string FreightIcon(int mode) => mode == 2 ? "🚂" : "🚚";
    static string Icon(LineView l) => l.Freight ? FreightIcon((int)l.Mode) : ModeIcon[Math.Clamp((int)l.Mode, 0, 2)];
    static string LotName(string kind) => kind switch
    {
        "bus-depot" => "Depot", "tram-stop" => "Tram stop", "tram-depot" => "Tram depot", "rail-station" => "Station", "rail-depot" => "Rail yard", "freight-depot" => "Freight depot", _ => "Stop",
    };
    /// <summary>Goes up when the company's lots or lines change, so the map redraws them.</summary>
    public int MapVersion { get; private set; }

    public event Action<string, string>? Toast;
    public event Action<int, int>? JumpTo;
    /// <summary>Asks for the company's view in a window of its own.</summary>
    public event System.Action? OwnWindow;

    public bool Mine => View?.Operator == "manual" && View.Owner == Me();
    /// <summary>Another player runs the company, so this client gets the public view: no books, standing or log.</summary>
    public bool Public { get; private set; }

    public void Handle(SideGameView v)
    {
        if (!IsTransport(v.Game)) return;
        _views[v.Game] = (View.Parser.ParseFrom(v.Payload), v.Seat == "public");
        // Show the company this player runs, when they run one.
        string? mine = _views.FirstOrDefault(x => x.Value.View.Operator == "manual" && x.Value.View.Owner == Me()).Key;
        if (!_views.ContainsKey(GameId) || (mine != null && !_picked)) GameId = mine ?? (_views.ContainsKey(Game) ? Game : v.Game);
        Show();
    }

    void Show()
    {
        if (!_views.TryGetValue(GameId, out var cur)) return;
        (View, Public) = cur;
        if (_pendingDone != null && _pendingDone(cur.View)) (Pending, _pendingDone) = (null, null);
        MapVersion++;
        _changed();
    }

    /// <summary>Every company's running routes, for the city map.</summary>
    public IEnumerable<LineView> AllLines => _views.Values.SelectMany(x => x.View.Lines);

    public void SetActive(bool on)
    {
        Active = on;
        if (!on) { Draft = null; Tool = CompanyTool.Look; }
        MapVersion++;
        _changed();
    }

    void SetTool(CompanyTool t)
    {
        Tool = t;
        if (t == CompanyTool.Line && Draft == null) Draft = (0, "", new List<int>(), 150, -1, false, false);
        if (t != CompanyTool.Line) Draft = null;
        if (t == CompanyTool.Line) Tab = "lines";
        _changed();
    }

    void Command(Command c) =>
        _send(new ClientMessage { Action = new PAction { SideGame = new SideGameCommand { Game = GameId, Seat = "operator", Payload = c.ToByteString() } } });

    void Act(PAction a) => _send(new ClientMessage { Action = a });

    /// <summary>
    /// What the player asked for and is waiting on (taking the company, handing it back, a lease), shown at once: the
    /// company only hears at the start of the city's next day, which at the slowest speed is seconds away.
    /// </summary>
    public string? Pending { get; private set; }
    Func<View, bool>? _pendingDone;

    void Wait(string what, Func<View, bool> done)
    {
        (Pending, _pendingDone) = (what, done);
        _changed();
    }

    /// <summary>The server refused something: stop waiting on it.</summary>
    public void Refused()
    {
        if (Pending == null) return;
        (Pending, _pendingDone) = (null, null);
        _changed();
    }

    void Lease(int buildingId)
    {
        if (Pending != null) return;
        Act(new PAction { Lease = new LeaseLot { Game = GameId, BuildingId = buildingId } });
        Wait("Leasing the lot…", v => v.OpenLots.All(o => o.BuildingId != buildingId));
    }

    LotView? Lot(int leaseId) => View?.Lots.FirstOrDefault(l => l.LeaseId == leaseId);
    IEnumerable<LotView> AllLots => View == null ? Enumerable.Empty<LotView>() : View.Lots.Concat(View.OpenLots);
    int Catchment => _city.TypeById.TryGetValue("bus-stop", out var t) && t.Catchment > 0 ? t.Catchment : 8;

    /// <summary>Homes and workplaces within walking reach of a stop, from the city's map.</summary>
    public (int Homes, int People, int Sites, int Jobs) Reach(LotView l)
    {
        var stop = _city.Buildings.GetValueOrDefault(l.BuildingId);
        int r = stop != null && _city.TypeById.TryGetValue(stop.TypeId, out var t) && t.Catchment > 0 ? t.Catchment : Catchment;
        int sw = stop?.Width ?? 1, sh = stop?.Height ?? 1;
        int homes = 0, people = 0, sites = 0, jobs = 0;
        foreach (var b in _city.Buildings.Values)
        {
            int dx = Math.Max(0, Math.Max(b.X - (l.X + sw - 1), l.X - (b.X + b.Width - 1)));
            int dy = Math.Max(0, Math.Max(b.Y - (l.Y + sh - 1), l.Y - (b.Y + b.Height - 1)));
            if (dx + dy > r) continue;
            if (b.Residents > 0) { homes++; people += b.Residents; }
            if (b.Jobs > 0 && b.Id != l.BuildingId) { sites++; jobs += b.Jobs; }
        }
        return (homes, people, sites, jobs);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The map

    /// <summary>A click on the map in the company's view.</summary>
    public void MapClick(int x, int y)
    {
        if (View == null) return;
        var lots = AllLots.ToList();
        var near = new[] { _city.BuildingAtCell(x, y) }.Concat(_city.StopsAt(x, y)).Concat(_city.StopsFacing(x, y)).Where(b => b != null).Select(b => b!.Id);
        var lot = near.Select(id => lots.FirstOrDefault(l => l.BuildingId == id)).FirstOrDefault(l => l != null);
        if (Tool == CompanyTool.Signal)
        {
            if (!Mine) { Toast?.Invoke("Run the company yourself to put up signals.", "warning"); return; }
            int cell = y * _city.Width + x;
            bool on = View.Lines.Any(l => l.Mode == Mode.Rail && l.RouteCells.Contains(cell));
            int now = (int)(View.Signals.FirstOrDefault(sg => sg.Cell == cell)?.Kind ?? Signal.NoSignal);
            if (!on && now == 0) { Toast?.Invoke("Signals go on the company's own rail lines: click a cell one of its trains runs over.", "warning"); return; }
            // Each click goes round: a block signal, a path signal, none.
            Command(new Command { Signal = new PlaceSignal { Cell = cell, Kind = (Signal)((now + 1) % 3) } });
            return;
        }
        if (Tool == CompanyTool.Line && Draft is { } d)
        {
            if (lot == null || lot.LeaseId == 0 || !IsStop(lot.Kind))
            {
                Toast?.Invoke("Click a stop, station or freight depot the company leases (solid yellow). Lease free ones (dashed) with the Lease tool.", "warning");
                return;
            }
            bool freight = lot.Kind == "freight-depot";
            // The first stop picks what runs: a line is all bus stops, all tram stops, all stations, or all freight depots.
            if (d.Stops.Count > 0 && (freight != d.Freight || (!freight && ModeOfStop(lot.Kind) != d.Mode)))
            {
                Toast?.Invoke(d.Freight ? "This is a freight line: click the company's freight depots." : $"This is a {new[] { "bus", "tram", "rail" }[d.Mode]} line: click its own kind of stop.", "warning");
                return;
            }
            if (d.Stops.Count == 0 && d.LineId == 0) Draft = d = d with { Freight = freight, Mode = freight ? (d.Mode == 2 ? 2 : 0) : ModeOfStop(lot.Kind) };
            if (d.Stops.Count > 0 && d.Stops[^1] == lot.LeaseId) d.Stops.RemoveAt(d.Stops.Count - 1);
            else if (d.Stops.Contains(lot.LeaseId)) Toast?.Invoke("A line visits each stop once.", "warning");
            else d.Stops.Add(lot.LeaseId);
            _changed();
            return;
        }
        if (Tool == CompanyTool.Lease)
        {
            if (lot == null) { Toast?.Invoke("Click a free stop, station or depot (dashed outline).", "warning"); return; }
            if (lot.LeaseId > 0) { Toast?.Invoke("The company already leases that.", "info"); return; }
            if (!Mine) { Toast?.Invoke("Run the company yourself to lease lots.", "warning"); return; }
            Lease(lot.BuildingId);
            return;
        }
        Selected = lot?.BuildingId;
        if (lot != null) Tab = "lots";
        else Toast?.Invoke("Click a stop, station or depot to see it.", "info");
        MapVersion++;
        _changed();
    }

    /// <summary>
    /// The company's map as one RGBA pixel per cell, laid over the ground: the city dimmed, each leased stop's
    /// walking reach, the company's lots in yellow (red-brown while closed), free lots in white, the picked one in blue.
    /// </summary>
    public byte[] MapPixels()
    {
        int w = _city.Width, h = _city.Height;
        var px = new byte[w * h * 4];
        void Put(int x, int y, byte r, byte g, byte b, byte a)
        {
            if (!_city.InBounds(x, y)) return;
            int i = (y * w + x) * 4;
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, a);
        }
        for (int i = 0; i < w * h; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (12, 16, 26, 150);
        if (View == null) return px;
        if (ShowCatchment)
            foreach (var l in View.Lots.Where(l => ModeOfStop(l.Kind) >= 0))
            {
                var stop = _city.Buildings.GetValueOrDefault(l.BuildingId);
                int r = stop != null && _city.TypeById.TryGetValue(stop.TypeId, out var t) && t.Catchment > 0 ? t.Catchment : Catchment;
                for (int y = l.Y - r; y <= l.Y + r; y++)
                    for (int x = l.X - r; x <= l.X + r; x++)
                        if (Math.Abs(x - l.X) + Math.Abs(y - l.Y) <= r) Put(x, y, 120, 104, 50, 110);
            }
        foreach (var l in AllLots)
        {
            var b = _city.Buildings.GetValueOrDefault(l.BuildingId);
            int bw = b != null && !_city.IsRoadside(b) ? b.Width : 1, bh = b != null && !_city.IsRoadside(b) ? b.Height : 1;
            var (r, g, bl, a) = l.BuildingId == Selected ? ((byte)90, (byte)209, (byte)255, (byte)240)
                : l.LeaseId > 0 ? l.ClosedDays > 0 ? ((byte)138, (byte)74, (byte)68, (byte)235) : ((byte)255, (byte)204, (byte)77, (byte)235)
                : ((byte)255, (byte)255, (byte)255, (byte)120);
            for (int y = l.Y; y < l.Y + bh; y++)
                for (int x = l.X; x < l.X + bw; x++) Put(x, y, r, g, bl, a);
        }
        // Track the signals hold: green with room to spare, amber nearly full, red asked for more than it can take.
        foreach (var c in View.Reserved)
        {
            var (r, g, b) = c.Percent > 100 ? ((byte)230, (byte)80, (byte)70) : c.Percent > 70 ? ((byte)240, (byte)180, (byte)60) : ((byte)90, (byte)200, (byte)120);
            Put(c.Cell % w, c.Cell / w, r, g, b, 200);
        }
        foreach (var sg in View.Signals)
            if (sg.Kind == Signal.Path) Put(sg.Cell % w, sg.Cell / w, 90, 209, 255, 255);
            else Put(sg.Cell % w, sg.Cell / w, 255, 107, 94, 255);
        return px;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Links

    public void Click(string meta)
    {
        var a = meta.Split(':');
        int I(int k) => k < a.Length && int.TryParse(a[k], out var v) ? v : -1;
        switch (a[0])
        {
            case "co-open": SetActive(true); return;
            case "co-city": SetActive(false); return;
            case "co-window": OwnWindow?.Invoke(); return;
            case "co-tab": Tab = a[1]; _changed(); return;
            case "co-tool": SetTool(Enum.Parse<CompanyTool>(a[1])); return;
            case "co-catch": ShowCatchment = !ShowCatchment; MapVersion++; _changed(); return;
            case "co-claim":
                if (Pending != null) return;
                Act(new PAction { Seat = new SetSeat { Seat = GameId, Owner = Me(), Level = SeatLevel.SeatManual, SpendCap = 25000 } });
                Wait("Taking over the company…", v => v.Operator == "manual" && v.Owner == Me());
                return;
            case "co-handoff":
                if (Pending != null) return;
                Act(new PAction { Seat = new SetSeat { Seat = GameId, Owner = "", Level = SeatLevel.SeatAutopilot, SpendCap = 25000 } });
                Wait("Handing the company to the computer…", v => !(v.Operator == "manual" && v.Owner == Me()));
                return;
            case "co-game" when a.Length > 1 && _views.ContainsKey(a[1]):
                GameId = a[1]; _picked = true; Draft = null; Tool = CompanyTool.Look; Selected = null;
                Show();
                return;
            case "co-bid": Command(new Command { Bid = new Bid { District = I(1), Amount = a.Length > 2 && long.TryParse(a[2], out var amt) ? amt : 0 } }); return;
            case "co-fmode" when Draft is { } fd && fd.LineId == 0: Draft = fd with { Mode = I(1) == 2 ? 2 : 0 }; _changed(); return;
            case "co-edge" when Draft is { } ed: Draft = ed with { ToEdge = !ed.ToEdge }; _changed(); return;
            case "co-new": SetTool(CompanyTool.Line); return;
            case "co-edit":
                if (View?.Lines.FirstOrDefault(l => l.Id == I(1)) is { } line)
                {
                    Draft = (line.Id, line.Name, line.StopLeaseIds.ToList(), line.FareCents, (int)line.Mode, line.Freight, line.ToEdge);
                    Tool = CompanyTool.Line;
                    Tab = "lines";
                    _changed();
                }
                return;
            case "co-close": Command(new Command { DeleteLine = new DeleteLine { LineId = I(1) } }); return;
            case "co-buses": Command(new Command { Buses = new SetBuses { LineId = I(1), Buses = Math.Max(0, I(2)) } }); return;
            case "co-tt": Command(new Command { Timetable = new SetTimetable { LineId = I(1), HeadwayMinutes = Math.Clamp(I(2), 0, 120) } }); return;
            case "co-svc": Command(new Command { Service = new SetService { LineId = I(1), EveryDays = Math.Clamp(I(2), 0, 180) } }); return;
            case "co-renew": Command(new Command { Renew = new Renew { LineId = I(1) } }); return;
            case "co-buy": Command(new Command { Standing = new BuyStanding { Kind = a[1], District = I(2) } }); return;
            case "co-lease": Lease(I(1)); return;
            case "co-release":
            {
                if (Pending != null) return;
                int id = I(1);
                Act(new PAction { EndLease = new EndLease { LeaseId = id, ByOperator = true } });
                Wait("Giving up the lot…", v => v.Lots.All(l => l.LeaseId != id));
                return;
            }
            case "co-jump": JumpTo?.Invoke(I(1), I(2)); return;
            case "co-fare" when Draft is { } d: Draft = d with { Fare = Math.Clamp(d.Fare + I(1), 0, 1000) }; _changed(); return;
            case "co-cancel": SetTool(CompanyTool.Look); return;
            case "co-save" when Draft is { } d:
            {
                var save = new SaveLine { LineId = d.LineId, Name = d.Name.Trim(), FareCents = d.Freight ? 0 : d.Fare, Mode = (Mode)Math.Max(0, d.Mode), Freight = d.Freight, ToEdge = d.Freight && d.ToEdge };
                save.StopLeaseIds.AddRange(d.Stops);
                Command(new Command { SaveLine = save });
                SetTool(CompanyTool.Look);
                return;
            }
        }
    }

    /// <summary>What the text box is for: the name of the line being drawn.</summary>
    public string? InputPrompt => Active && Draft != null && Tab == "lines" ? $"Line name (Route {(View?.Lines.Count ?? 0) + 1})" : null;
    public string Input => Draft?.Name ?? "";

    public void SetInput(string text)
    {
        if (Draft is { } d && d.Name != text) Draft = d with { Name = text };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Text

    static string Fare(int c) => c == 0 ? "free" : $"${c / 100.0:0.00}";
    static string Many(int n, string one, string? many = null) => $"{Num(n)} {(n == 1 ? one : many ?? one + "s")}";
    // Name this client's player as "you" in the city's reasons.
    string Yours(string s)
    {
        string me = System.Text.RegularExpressions.Regex.Escape(Me());
        return System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(s, $@"\b{me} won't allow it", "you haven't allowed it"), $@"\b{me}\b", "you");
    }

    /// <summary>The city's Buses tab: a summary and the way into the company's view.</summary>
    public string RenderCityTab()
    {
        var v = View;
        var sb = new StringBuilder(v == null ? H3("Buses") : "");
        if (v == null) return sb.Append(Dim("No bus company in this city.")).ToString();
        foreach (var (id, (cv, _)) in _views)
        {
            int riders = cv.Lines.Sum(l => l.Riders);
            string runs = cv.Operator == "manual" ? (cv.Owner == Me() ? "You run it." : $"{Esc(cv.Owner)} runs it.") : $"The computer runs it ({Esc(cv.Operator)}).";
            sb.Append(H3(Esc(cv.Name) + (_views.Count > 1 && id == GameId ? " " + Small(Dim("(shown)")) : "")));
            sb.Append(Small(Dim($"{runs} {Many(cv.Lines.Count, "line")}, {Num(riders)} riders yesterday, {cv.Lots.Count} lots leased."))).Append('\n');
            if (_views.Count > 1 && id != GameId) sb.Append(Link($"co-game:{id}", "Show this company")).Append('\n');
            sb.Append('\n');
        }
        sb.Append($"[b]{Link("co-open", "🚌 Switch to the company")}[/b]     {Link("co-window", "⧉ Own window")}\n\n");
        sb.Append(Small(Dim("The company has its own view: its lots and routes on the map, and tools for leasing lots and drawing lines. You can run it in a second window while this one shows the city."))).Append('\n');
        sb.Append(Small(Dim("Its routes show on the city map under Layers → Bus routes."))).Append('\n');
        return sb.ToString();
    }

    /// <summary>The company's tools, down the left side.</summary>
    public string RenderTools()
    {
        string tool(CompanyTool t, string label) => Tool == t ? $"[b]{Col(Gold, label)}[/b]" : Link($"co-tool:{t}", label);
        var sb = new StringBuilder(H4("Tools"));
        sb.Append(tool(CompanyTool.Look, "🔍 Look")).Append('\n');
        if (Mine) sb.Append(tool(CompanyTool.Lease, "📝 Lease lots")).Append('\n').Append(tool(CompanyTool.Line, "✏️ Draw a line")).Append('\n');
        if (Mine && View?.Lines.Any(l => l.Mode == Mode.Rail) == true) sb.Append(tool(CompanyTool.Signal, "🚦 Signals")).Append('\n');
        if (_views.Count > 1)
            foreach (var (id, (cv, _)) in _views.Where(x => x.Key != GameId)) sb.Append(Link($"co-game:{id}", $"⇄ {Esc(cv.Name)}")).Append('\n');
        sb.Append(Small(Dim(Tool switch
        {
            CompanyTool.Lease => "Click a free stop, station or depot (dashed) to lease it on the city's standard terms.",
            CompanyTool.Line => "Click the company's stops (solid yellow) in the order the line visits them. Click the last one again to take it off. The first stop decides what runs: buses, trams, trains or freight.",
            CompanyTool.Signal => $"Click a cell on one of the company's rail lines: a block signal ({Money(View?.SignalPrice ?? 250)}), click again for a path signal, again to take it down. Without signals only one train can use a line.",
            _ => "Click a stop, station or depot to see who it reaches and how it does.",
        }))).Append('\n');
        sb.Append(H4("Map")).Append(Link("co-catch", ShowCatchment ? "☑ Walking reach" : "☐ Walking reach")).Append('\n');
        sb.Append(Small(Dim("People ride if they live in reach of one of the company's stops and work in reach of another, and the ride beats driving or cycling."))).Append('\n');
        sb.Append(H4("Key")).Append(Small($"{Col("#ffcc4d", "■")} the company's lots\n{Col("#ffffff", "□")} free to lease\n— a running line, ┅ one that isn't"
            + (View is { } kv && (kv.Signals.Count > 0 || kv.Reserved.Count > 0) ? $"\n{Col("#ff6b5e", "■")} block signal, {Col("#5ad1ff", "■")} path signal\ntrack held: {Col("#5ac878", "green")} has room, {Col("#f0b43c", "amber")} nearly full, {Col("#e65046", "red")} over-asked" : ""))).Append('\n');
        return sb.ToString();
    }

    public string Render()
    {
        var v = View;
        if (v == null) return Dim("No bus company in this city.");
        string paused = _city.Stats.Speed == 0 ? Small(Col(Warn, "Paused: the company hears about new lots, lines and purchases when the day turns.")) + "\n" : "";
        if (Pending != null) paused = Col(Warn, $"⏳ {Esc(Pending)}") + " " + Small(Dim("It happens when the city's next day starts (not while paused).")) + "\n" + paused;
        if (Public && Tab is "money" or "standing" or "log")
            return Dim($"{Esc(v.Owner)} runs this company. Its books, its standing with the city and its log are theirs to see.");
        return paused + Tab switch
        {
            "lots" => Lots(v),
            "money" => MoneyText(v),
            "standing" => Standing(v),
            "log" => Log(v),
            _ => Lines(v),
        };
    }

    string StopName(int leaseId) => Lot(leaseId) is { } l ? $"{l.X},{l.Y}" : "?";

    static ModeView? SpecOf(View v, int mode, bool freight) =>
        freight ? v.FreightModes.FirstOrDefault(f => (int)f.Mode == mode) : mode >= 0 && mode < v.Modes.Count ? v.Modes[mode] : null;

    string Word(View v, int mode, int n = 1, bool freight = false) =>
        SpecOf(v, mode, freight) is { } sp ? (n == 1 ? sp.Word : sp.Plural) : n == 1 ? "bus" : "buses";

    string Lines(View v)
    {
        var sb = new StringBuilder();
        if (Draft is { } d)
        {
            sb.Append($"[b]{(d.LineId != 0 ? "Editing a line" : "New line")}{(d.Mode >= 0 ? " " + (d.Freight ? FreightIcon(d.Mode) : ModeIcon[d.Mode]) : "")}[/b]\n");
            sb.Append(Small("Click the company's stops on the map in the order the line visits them. It runs both ways. Bus stops make a bus line, tram stops a tram line, stations a train line, freight depots a freight line.")).Append('\n');
            sb.Append(Small((d.Stops.Count > 0 ? string.Join(" → ", d.Stops.Select(StopName)) : Dim("No stops yet.")) + (d.Freight && d.ToEdge ? " → map edge" : ""))).Append('\n');
            sb.Append(Dim("Name it in the box above.")).Append('\n');
            if (d.Freight)
            {
                if (d.LineId == 0) sb.Append($"{(d.Mode != 2 ? "[b]🚚 Trucks[/b]" : Link("co-fmode:0", "🚚 Trucks"))}   {(d.Mode == 2 ? "[b]🚂 Freight trains[/b]" : Link("co-fmode:2", "🚂 Freight trains"))}\n");
                sb.Append(Link("co-edge", d.ToEdge ? "☑ On to the map edge" : "☐ On to the map edge")).Append('\n');
                sb.Append(Small(Dim($"Goods made by factories near a depot go to the shops near the line's other depots{(d.ToEdge ? ", and the rest out of the city" : "")}. Each ton pays more the longer the run."))).Append('\n');
            }
            else sb.Append($"Fare  {Link("co-fare:-25", "−0.25")}  [b]{Fare(d.Fare)}[/b]  {Link("co-fare:25", "+0.25")}\n");
            int need = d.Freight && d.ToEdge ? 1 : 2;
            sb.Append(d.Stops.Count >= need ? $"[b]{Link("co-save", "Save line")}[/b]" : Dim("Save line")).Append("   ").Append(Link("co-cancel", "Cancel")).Append("\n\n");
        }
        bool depot = v.Lots.Any(l => l.Kind.EndsWith("-depot", StringComparison.Ordinal));
        int stops = v.Lots.Count(l => IsStop(l.Kind));
        if (v.Lines.Count == 0 && Draft == null)
            sb.Append(Small(Col(Warn, $"To start: lease a depot and two or more stops of the same kind with Lease lots (bus stops and a bus depot, tram stops and a tram depot, stations and a rail yard, or two freight depots for freight), then Draw a line through the stops and give it vehicles. {(depot ? "✓" : "○")} depot · {(stops >= 2 ? "✓" : "○")} two stops{(!Mine ? " · Run the company yourself first (top bar)." : "")}"))).Append('\n');
        foreach (var l in v.Lines)
        {
            int m = (int)l.Mode;
            bool fr = l.Freight;
            var reach = l.StopLeaseIds.Select(Lot).Where(s => s != null).Select(s => Reach(s!)).ToList();
            int homes = reach.Sum(r => r.Homes), jobs = reach.Sum(r => r.Jobs);
            sb.Append($"\n{Col(l.Color, "█")} {Icon(l)} [b]{Esc(l.Name)}[/b]  {Small(Dim(fr ? $"{Many(l.StopLeaseIds.Count, "depot")}{(l.ToEdge ? " + edge" : "")} · freight" : $"{l.StopLeaseIds.Count} stops · {Fare(l.FareCents)}{(l.FareCapCents >= 0 && l.FareCapCents < l.FareCents ? $" (capped at {Fare(l.FareCapCents)})" : "")}"))}\n");
            if (!string.IsNullOrEmpty(l.Problem)) sb.Append(Small(Col(Neg, $"Not running: {Esc(l.Problem)}"))).Append('\n');
            else if (fr)
            {
                sb.Append(Small($"A {Word(v, m, 1, true)} every {l.HeadwayMinutes} min ({l.RoundTripMinutes} min round trip) · [b]{Num(l.Tons)}[/b] tons carried today")).Append('\n');
                if (l.Tons == 0)
                    sb.Append(Small(Col(Warn, $"Nothing to carry today. Goods come from factories near its depots and go to shops near another of its depots{(l.ToEdge ? " or out to the edge" : "")}."))).Append('\n');
            }
            else
            {
                sb.Append(Small($"A {Word(v, m)} every {l.HeadwayMinutes} min ({l.RoundTripMinutes} min round trip) · [b]{Num(l.Riders)}[/b] riders yesterday · {l.LoadPercent}% full at the peak")).Append('\n');
                if (l.Riders == 0)
                    sb.Append(Small(Col(Warn, $"No riders yet. Riders live near one of its stops and work near another ({Many(homes, "home")} and {Many(jobs, "job")} in reach of its stops). People re-think their commute about once a month, so a new line takes a month to fill. More stops where homes and jobs are, a lower fare or more {Word(v, m, 2)} all help."))).Append('\n');
            }
            if (l.SignalLimited)
                sb.Append(Small(Col(Warn, $"The signals hold it to a train every {l.HeadwayMinutes} min: the track ahead is still held by the last one. Put up signals (🚦) along the line so more trains can share it; path signals keep trains on different lines out of each other's way at junctions."))).Append('\n');
            var boards = l.StopLeaseIds.Select((s, i) => (Lot(s) is { } lot ? Link($"co-jump:{lot.X}:{lot.Y}", StopName(s)) : "?") + (fr ? "" : $" {(i < l.Boardings.Count ? l.Boardings[i] : 0)}"));
            sb.Append(Small(Dim(fr ? "Depots: " : "Boardings: ") + string.Join(" → ", boards) + (l.ToEdge ? " → edge" : ""))).Append('\n');
            var lm = l.LastMonth ?? new MonthMoney();
            sb.Append(Small($"Last month: {(fr ? "freight" : "fares")} {Money(lm.Fares)}, costs {Money(lm.Wages + lm.Upkeep + lm.Rent)}, [b]{PosNeg(lm.Profit, Money(lm.Profit))}[/b]")).Append('\n');
            if (l.Vehicles.Count > 0)
            {
                int Count(string st) => l.Vehicles.Count(x => x.State == st);
                int oldest = l.Vehicles.Max(x => x.AgeDays);
                string fleet = $"{Count("running")} running" + (Count("spare") > 0 ? $", {Count("spare")} spare" : "") + (Count("workshop") > 0 ? $", {Count("workshop")} being serviced" : "")
                    + (Count("broken") > 0 ? ", " + Col(Neg, $"{Count("broken")} broken down") : "");
                string cond = Col(l.Condition < 40 ? Neg : l.Condition < 70 ? Warn : Pos, $"{l.Condition}%");
                sb.Append(Small($"Fleet: {fleet} · condition [b]{cond}[/b] · oldest {oldest / 360}y {oldest % 360 / 30}m{(l.TimetableMinutes > 0 ? $" · on time {l.OnTimePercent}% last month" : "")}")).Append('\n');
            }
            if (Mine)
            {
                string price = SpecOf(v, m, fr) is { } sp ? Money(sp.Price) : Money(v.BusPrice);
                string plural = Word(v, m, 2, fr);
                sb.Append($"{char.ToUpperInvariant(plural[0])}{plural[1..]}  {(l.Buses > 0 ? Link($"co-buses:{l.Id}:{l.Buses - 1}", "−") : Dim("−"))}  [b]{l.Buses}[/b]  {Link($"co-buses:{l.Id}:{l.Buses + 1}", "+")}"
                    + $"  {Small(Dim($"{price} each"))}   {(l.Buses > 0 ? Link($"co-renew:{l.Id}", "Replace oldest") : Dim("Replace oldest"))}   {Link($"co-edit:{l.Id}", "Edit")}   {Link($"co-close:{l.Id}", "Close")}\n");
                int tt = l.TimetableMinutes, svc = l.ServiceDays;
                sb.Append(Small($"Timetable  {Link($"co-tt:{l.Id}:{(tt <= 5 ? 0 : tt - 5)}", "−")}  [b]{(tt > 0 ? $"every {tt} min ({l.Needed} needed)" : $"all {plural}")}[/b]  {Link($"co-tt:{l.Id}:{Math.Min(120, tt > 0 ? tt + 5 : 5)}", "+")}"
                    + $"     Service  {Link($"co-svc:{l.Id}:{(svc <= 7 ? 0 : svc - 7)}", "−")}  [b]{(svc > 0 ? $"every {svc} days" : Col(Neg, "never"))}[/b]  {Link($"co-svc:{l.Id}:{Math.Min(180, svc > 0 ? svc + 7 : 7)}", "+")}")).Append('\n');
            }
            else sb.Append(Small(Dim($"{l.Buses} {Word(v, m, l.Buses, fr)}"))).Append('\n');
        }
        if (v.Lines.Count == 0 && Draft == null) sb.Append(Dim("No lines yet.")).Append('\n');
        if (Mine && Draft == null) sb.Append('\n').Append(Link("co-new", "✏️ New line…")).Append('\n');
        return sb.ToString();
    }

    string Lots(View v)
    {
        var sb = new StringBuilder();
        void Card(LotView l)
        {
            var r = Reach(l);
            int boards = v.Lines.Sum(line => line.StopLeaseIds.IndexOf(l.LeaseId) is var i and >= 0 && i < line.Boardings.Count ? line.Boardings[i] : 0);
            var district = v.Districts.FirstOrDefault(d => d.Id == l.District);
            string title = $"{LotName(l.Kind)} at {l.X},{l.Y}";
            sb.Append($"\n{(l.BuildingId == Selected ? Col(Blue, "▶ ") : "")}[b]{Link($"co-jump:{l.X}:{l.Y}", title)}[/b]  {Small(l.LeaseId > 0 ? Col(Pos, "leased") : Dim("free to lease"))}\n");
            sb.Append(Small(Dim($"{Esc(l.Terms)}{(district != null ? $" · {Esc(district.Name)}, rating {district.Rating}" : "")}"))).Append('\n');
            if (ModeOfStop(l.Kind) >= 0)
                sb.Append(Small($"In walking reach: {Many(r.Homes, "home")} ({Many(r.People, "person", "people")}), {Many(r.Sites, "workplace")} ({Many(r.Jobs, "job")}){(l.LeaseId > 0 ? $" · {Many(boards, "boarding")} yesterday" : "")}")).Append('\n');
            if (l.Kind == "freight-depot" && l.LeaseId > 0)
                sb.Append(Small($"Goods today: {Num(l.TonsReady)} tons ready from factories in reach, {Num(l.TonsWanted)} tons wanted by shops")).Append('\n');
            if (l.ClosedDays > 0) sb.Append(Small(Col(Neg, $"Closed for {l.ClosedDays} more days (fire or disaster nearby)."))).Append('\n');
            if (l.Barred) sb.Append(Small(Col(Neg, "Another company holds exclusive rights in this district: no line stops here."))).Append('\n');
            if (l.BadStanding) sb.Append(Small(Col(Neg, "Bad standing with the city."))).Append('\n');
            if (Mine) sb.Append(l.LeaseId > 0 ? Link($"co-release:{l.LeaseId}", "Give up the lease") : $"[b]{Link($"co-lease:{l.BuildingId}", "Lease it")}[/b]").Append('\n');
        }
        IEnumerable<LotView> Sorted(IEnumerable<LotView> list) => list.OrderByDescending(l => l.BuildingId == Selected);
        sb.Append(H4("The company's lots"));
        if (v.Lots.Count == 0) sb.Append(Small(Dim("None leased yet."))).Append('\n');
        foreach (var l in Sorted(v.Lots)) Card(l);
        sb.Append(H4("Free to lease"));
        if (v.OpenLots.Count == 0) sb.Append(Small(Dim("No free stops or depots. The mayor builds them (Place building → Transit)."))).Append('\n');
        foreach (var l in Sorted(v.OpenLots)) Card(l);
        return sb.ToString();
    }

    static string Books(MonthMoney? m)
    {
        m ??= new MonthMoney();
        var rows = new List<(string, string)> { ("Fares and freight", Col(Pos, Money(m.Fares))) };
        if (m.Subsidy != 0) rows.Add(("From the city", Col(Pos, Money(m.Subsidy))));
        rows.Add(("Drivers' wages", Col(Neg, "-" + Money(m.Wages))));
        rows.Add(("Upkeep, servicing and repairs", Col(Neg, "-" + Money(m.Upkeep))));
        rows.Add(("Rent and fare tax", Col(Neg, "-" + Money(m.Rent))));
        if (m.Standing != 0) rows.Add(("Standing (ads, statues, rights)", Col(Neg, "-" + Money(m.Standing))));
        rows.Add(("[b]Profit[/b]", $"[b]{PosNeg(m.Profit, Money(m.Profit))}[/b]"));
        return Table(rows);
    }

    string MoneyText(View v)
    {
        var sb = new StringBuilder($"Cash [b]{PosNeg(v.Cash, Money(v.Cash))}[/b].\n");
        sb.Append(Small(Dim(string.Join("  ·  ", v.Modes.Concat(v.FreightModes).Select(md => $"{char.ToUpperInvariant(md.Word[0])}{md.Word[1..]}: {Money(md.Price)}, {md.Seats} {(md.Freight ? "tons" : "seats")}, {md.Owned} of {md.Capacity} depot places, {Money(md.ServiceCost)} a service, lasts {md.LifeYears} years"))))).Append('\n');
        sb.Append(H4("Last month")).Append(Books(v.LastMonth));
        sb.Append(H4("This month so far")).Append(Books(v.ThisMonth));
        sb.Append(H4("By line, last month"));
        sb.Append(v.Lines.Count == 0 ? Dim("No lines.") + "\n" : Table(v.Lines.Select(l => (Esc(l.Name), PosNeg(l.LastMonth?.Profit ?? 0, Money(l.LastMonth?.Profit ?? 0))))));
        return sb.ToString();
    }

    string Standing(View v)
    {
        var sb = new StringBuilder();
        if (v.Tenders.Count > 0) sb.Append(H4("Out to tender"));
        foreach (var t in v.Tenders)
        {
            sb.Append($"[b]{Esc(t.Name)} franchise[/b]  {Small($"{t.Months} months{(t.Exclusive ? ", exclusive" : "")} · closes in {Many(t.DaysLeft, "day")}")}\n");
            sb.Append(Small(Dim($"The city wants at least {Money(t.MinBid)}; bids are sealed and the highest wins when it closes, paying its bid then. {(t.Exclusive ? "The winner alone stops in the district." : "The winner alone may lease new transit lots in the district.")} The company has {Many(t.Lots, "lot")} there."))
                + (t.YourBid > 0 ? Small($" [b]Your bid: {Money(t.YourBid)}[/b]") : "")).Append('\n');
            if (Mine)
                sb.Append(Small("Bid  " + string.Join("   ", new[] { t.MinBid, t.MinBid * 3 / 2, t.MinBid * 2, t.MinBid * 3 }
                    .Select(amt => Link($"co-bid:{t.District}:{amt}", t.YourBid == amt ? $"[b]{Money(amt)}[/b]" : Money(amt)))))).Append('\n');
        }
        if (v.Franchises.Count > 0)
        {
            sb.Append(H4("Franchises in force"));
            foreach (var f in v.Franchises)
                sb.Append(Small($"{Esc(f.Name)}: {(f.Mine ? Col(Pos, "[b]yours[/b]") : $"held by {Esc(_views.TryGetValue(f.Holder, out var h) ? h.View.Name : f.Holder)}")}{(f.Exclusive ? ", exclusive" : "")}, {(f.DaysLeft + 29) / 30} months left")).Append('\n');
        }
        if (v.Districts.Count == 0) sb.Append(Small(Dim("No lots yet, so no standing anywhere."))).Append('\n');
        foreach (var d in v.Districts)
        {
            string extras = (d.Statue ? "🗿 statue · " : "") + (d.ExclusiveDays > 0 ? $"exclusive rights, {(d.ExclusiveDays + 29) / 30} months left · " : "");
            sb.Append($"\n[b]{Esc(d.Name)}[/b]  {Small($"{extras}rating [b]{Col(d.Rating < 0 ? Neg : Pos, d.Rating.ToString())}[/b]")}\n{SignedBar(d.Rating, 1000, 10)}\n");
            if (Mine)
                sb.Append(Small(string.Join("   ", v.Offers.Select(o => o.Allowed ? Link($"co-buy:{o.Kind}:{d.Id}", $"{Esc(o.Name)} · {Money(o.Cost)}") : Dim($"{Esc(o.Name)} · {Money(o.Cost)}"))))).Append('\n');
        }
        var blocked = v.Offers.Where(o => !o.Allowed).Select(o => Yours(o.WhyNot)).ToList();
        sb.Append('\n').Append(Small(Dim("Good service, riders and jobs raise a rating; bad standing and breakdowns lower it. Below -400 the district turns down requests for more lots."
            + (blocked.Count > 0 ? $" {Esc(string.Join(" ", blocked))} Players set these in the city view under Mayor → House rules." : "")))).Append('\n');
        return sb.ToString();
    }

    static string Log(View v) => v.Log.Count == 0 ? Small(Dim("Nothing yet.")) : string.Join("\n", v.Log.Reverse().Select(s => "• " + Esc(s)));

    /// <summary>The company's top-bar figures.</summary>
    public string TopText => View is { } v
        ? $"{v.Name}   {Money(v.Cash)}   {v.Buses} of {v.BusCapacity} buses   service {v.ServiceQuality}/100"
        : "No bus company";
}
