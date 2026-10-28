using System.Text;
using Google.Protobuf;
using ToyCity.Protocol.V0;
using ToyCity.Transport.Wire;
using static ToyCity.ClientModel.Panels.Bb;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Panels;

public enum CompanyTool { Look, Lease, Line }

/// <summary>
/// The bus company's own view, as the web client has it. Switching to it swaps the city's top bar, tools and panel
/// for the company's: its cash and buses, tools for leasing lots and drawing lines, and tabs for lines, lots,
/// money, standing and the log. The map stays, dimmed, with the company's lots and routes on top. Like
/// <see cref="PanelModel"/> it is text with links, and has no engine types.
/// </summary>
public sealed class CompanyModel
{
    public const string Game = "transport";
    public static readonly (string Id, string Name)[] Tabs = { ("lines", "Lines"), ("lots", "Lots"), ("money", "Money"), ("standing", "Standing"), ("log", "Log") };

    readonly CityMirror _city;
    readonly Func<ClientMessage, uint> _send;
    readonly System.Action _changed;

    public CompanyModel(CityMirror city, Func<ClientMessage, uint> send, System.Action changed) => (_city, _send, _changed) = (city, send, changed);

    public View? View { get; private set; }
    /// <summary>True while the company's view is up instead of the city's.</summary>
    public bool Active { get; private set; }
    public CompanyTool Tool { get; private set; }
    public string Tab { get; private set; } = "lines";
    public bool ShowCatchment { get; private set; } = true;
    /// <summary>The lot picked with the look tool, by building id.</summary>
    public int? Selected { get; private set; }
    /// <summary>The line being drawn: its stops (lease ids) in order.</summary>
    public (int LineId, string Name, List<int> Stops, int Fare)? Draft { get; private set; }
    /// <summary>Goes up when the company's lots or lines change, so the map redraws them.</summary>
    public int MapVersion { get; private set; }

    public event Action<string, string>? Toast;
    public event Action<int, int>? JumpTo;
    /// <summary>Asks for the company's view in a window of its own.</summary>
    public event System.Action? OwnWindow;

    public bool Mine => View?.Operator == "manual";

    public void Handle(SideGameView v)
    {
        if (v.Game != Game) return;
        View = View.Parser.ParseFrom(v.Payload);
        MapVersion++;
        _changed();
    }

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
        if (t == CompanyTool.Line && Draft == null) Draft = (0, "", new List<int>(), 150);
        if (t != CompanyTool.Line) Draft = null;
        if (t == CompanyTool.Line) Tab = "lines";
        _changed();
    }

    void Command(Command c) =>
        _send(new ClientMessage { Action = new PAction { SideGame = new SideGameCommand { Game = Game, Seat = "operator", Payload = c.ToByteString() } } });

    void Act(PAction a) => _send(new ClientMessage { Action = a });

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
        if (Tool == CompanyTool.Line && Draft is { } d)
        {
            if (lot == null || lot.LeaseId == 0 || lot.Kind != "bus-stop")
            {
                Toast?.Invoke("Click a bus stop the company leases (solid yellow). Lease free ones (dashed) with the Lease tool.", "warning");
                return;
            }
            if (d.Stops.Count > 0 && d.Stops[^1] == lot.LeaseId) d.Stops.RemoveAt(d.Stops.Count - 1);
            else if (d.Stops.Contains(lot.LeaseId)) Toast?.Invoke("A line visits each stop once.", "warning");
            else d.Stops.Add(lot.LeaseId);
            _changed();
            return;
        }
        if (Tool == CompanyTool.Lease)
        {
            if (lot == null) { Toast?.Invoke("Click a free bus stop or depot (dashed outline).", "warning"); return; }
            if (lot.LeaseId > 0) { Toast?.Invoke("The company already leases that.", "info"); return; }
            if (!Mine) { Toast?.Invoke("Run the company yourself to lease lots.", "warning"); return; }
            Act(new PAction { Lease = new LeaseLot { Game = Game, BuildingId = lot.BuildingId } });
            return;
        }
        Selected = lot?.BuildingId;
        if (lot != null) Tab = "lots";
        else Toast?.Invoke("Click a bus stop or depot to see it.", "info");
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
            foreach (var l in View.Lots.Where(l => l.Kind == "bus-stop"))
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
            case "co-claim": Act(new PAction { Seat = new SetSeat { Seat = Game, Owner = "player", Level = SeatLevel.SeatManual, SpendCap = 25000 } }); return;
            case "co-handoff": Act(new PAction { Seat = new SetSeat { Seat = Game, Owner = "", Level = SeatLevel.SeatAutopilot, SpendCap = 25000 } }); return;
            case "co-new": SetTool(CompanyTool.Line); return;
            case "co-edit":
                if (View?.Lines.FirstOrDefault(l => l.Id == I(1)) is { } line)
                {
                    Draft = (line.Id, line.Name, line.StopLeaseIds.ToList(), line.FareCents);
                    Tool = CompanyTool.Line;
                    Tab = "lines";
                    _changed();
                }
                return;
            case "co-close": Command(new Command { DeleteLine = new DeleteLine { LineId = I(1) } }); return;
            case "co-buses": Command(new Command { Buses = new SetBuses { LineId = I(1), Buses = Math.Max(0, I(2)) } }); return;
            case "co-buy": Command(new Command { Standing = new BuyStanding { Kind = a[1], District = I(2) } }); return;
            case "co-lease": Act(new PAction { Lease = new LeaseLot { Game = Game, BuildingId = I(1) } }); return;
            case "co-release": Act(new PAction { EndLease = new EndLease { LeaseId = I(1), ByOperator = true } }); return;
            case "co-jump": JumpTo?.Invoke(I(1), I(2)); return;
            case "co-fare" when Draft is { } d: Draft = d with { Fare = Math.Clamp(d.Fare + I(1), 0, 1000) }; _changed(); return;
            case "co-cancel": SetTool(CompanyTool.Look); return;
            case "co-save" when Draft is { } d:
            {
                var save = new SaveLine { LineId = d.LineId, Name = d.Name.Trim(), FareCents = d.Fare };
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
    // This client plays as "player": name it as "you" in the city's reasons.
    static string Yours(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace("player won't allow it", "you haven't allowed it"), @"\bplayer\b", "you");

    /// <summary>The city's Buses tab: a summary and the way into the company's view.</summary>
    public string RenderCityTab()
    {
        var v = View;
        var sb = new StringBuilder(H3(v == null ? "Buses" : Esc(v.Name)));
        if (v == null) return sb.Append(Dim("No bus company in this city.")).ToString();
        int riders = v.Lines.Sum(l => l.Riders);
        sb.Append(Small(Dim($"{(Mine ? "You run it." : $"The computer runs it ({Esc(v.Operator)}).")} {Many(v.Lines.Count, "line")}, {v.Buses} buses, {Num(riders)} riders yesterday, {v.Lots.Count} lots leased."))).Append("\n\n");
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
        sb.Append(Small(Dim(Tool switch
        {
            CompanyTool.Lease => "Click a free stop or depot (dashed) to lease it on the city's standard terms.",
            CompanyTool.Line => "Click the company's stops (solid yellow) in the order the line visits them. Click the last one again to take it off.",
            _ => "Click a stop or depot to see who it reaches and how it does.",
        }))).Append('\n');
        sb.Append(H4("Map")).Append(Link("co-catch", ShowCatchment ? "☑ Walking reach" : "☐ Walking reach")).Append('\n');
        sb.Append(Small(Dim("People ride if they live in reach of one of the company's stops and work in reach of another, and the bus beats driving or cycling."))).Append('\n');
        sb.Append(H4("Key")).Append(Small($"{Col("#ffcc4d", "■")} the company's lots\n{Col("#ffffff", "□")} free to lease\n— a running line, ┅ one that isn't")).Append('\n');
        return sb.ToString();
    }

    public string Render()
    {
        var v = View;
        if (v == null) return Dim("No bus company in this city.");
        string paused = _city.Stats.Speed == 0 ? Small(Col(Warn, "Paused: the company hears about new lots, lines and purchases when the day turns.")) + "\n" : "";
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

    string Lines(View v)
    {
        var sb = new StringBuilder();
        if (Draft is { } d)
        {
            sb.Append($"[b]{(d.LineId != 0 ? "Editing a line" : "New line")}[/b]\n");
            sb.Append(Small("Click the company's stops on the map in the order the line visits them. It runs both ways.")).Append('\n');
            sb.Append(Small(d.Stops.Count > 0 ? string.Join(" → ", d.Stops.Select(StopName)) : Dim("No stops yet."))).Append('\n');
            sb.Append(Dim("Name it in the box above.")).Append('\n');
            sb.Append($"Fare  {Link("co-fare:-25", "−0.25")}  [b]{Fare(d.Fare)}[/b]  {Link("co-fare:25", "+0.25")}\n");
            sb.Append(d.Stops.Count >= 2 ? $"[b]{Link("co-save", "Save line")}[/b]" : Dim("Save line")).Append("   ").Append(Link("co-cancel", "Cancel")).Append("\n\n");
        }
        int depots = v.Lots.Count(l => l.Kind == "bus-depot"), stops = v.Lots.Count(l => l.Kind == "bus-stop");
        if (v.Lines.Count == 0 && Draft == null)
            sb.Append(Small(Col(Warn, $"To start: lease a depot and two or more bus stops with Lease lots, then Draw a line through the stops and give it buses. {(depots > 0 ? "✓" : "○")} depot · {(stops >= 2 ? "✓" : "○")} two stops{(!Mine ? " · Run the company yourself first (top bar)." : "")}"))).Append('\n');
        foreach (var l in v.Lines)
        {
            var reach = l.StopLeaseIds.Select(Lot).Where(s => s != null).Select(s => Reach(s!)).ToList();
            int homes = reach.Sum(r => r.Homes), jobs = reach.Sum(r => r.Jobs);
            sb.Append($"\n{Col(l.Color, "█")} [b]{Esc(l.Name)}[/b]  {Small(Dim($"{l.StopLeaseIds.Count} stops · {Fare(l.FareCents)}{(l.FareCapCents >= 0 && l.FareCapCents < l.FareCents ? $" (capped at {Fare(l.FareCapCents)})" : "")}"))}\n");
            if (!string.IsNullOrEmpty(l.Problem)) sb.Append(Small(Col(Neg, $"Not running: {Esc(l.Problem)}"))).Append('\n');
            else
            {
                sb.Append(Small($"A bus every {l.HeadwayMinutes} min ({l.RoundTripMinutes} min round trip) · [b]{Num(l.Riders)}[/b] riders yesterday · {l.LoadPercent}% full at the peak")).Append('\n');
                if (l.Riders == 0)
                    sb.Append(Small(Col(Warn, $"No riders yet. Riders live near one of its stops and work near another ({Many(homes, "home")} and {Many(jobs, "job")} in reach of its stops). People re-think their commute about once a month, so a new line takes a month to fill. More stops where homes and jobs are, a lower fare or more buses all help."))).Append('\n');
            }
            var boards = l.StopLeaseIds.Select((s, i) => (Lot(s) is { } lot ? Link($"co-jump:{lot.X}:{lot.Y}", StopName(s)) : "?") + $" {(i < l.Boardings.Count ? l.Boardings[i] : 0)}");
            sb.Append(Small(Dim("Boardings: ") + string.Join(" → ", boards))).Append('\n');
            var lm = l.LastMonth ?? new MonthMoney();
            sb.Append(Small($"Last month: fares {Money(lm.Fares)}, costs {Money(lm.Wages + lm.Upkeep + lm.Rent)}, [b]{PosNeg(lm.Profit, Money(lm.Profit))}[/b]")).Append('\n');
            if (Mine)
                sb.Append($"Buses  {(l.Buses > 0 ? Link($"co-buses:{l.Id}:{l.Buses - 1}", "−") : Dim("−"))}  [b]{l.Buses}[/b]  {Link($"co-buses:{l.Id}:{l.Buses + 1}", "+")}"
                    + $"{(l.Broken > 0 ? Small(Col(Neg, $" ({l.Broken} in the workshop)")) : "")}  {Small(Dim($"{Money(v.BusPrice)} each"))}     {Link($"co-edit:{l.Id}", "Edit")}   {Link($"co-close:{l.Id}", "Close")}\n");
            else sb.Append(Small(Dim($"{l.Buses} buses"))).Append('\n');
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
            string title = $"{(l.Kind == "bus-depot" ? "Depot" : "Stop")} at {l.X},{l.Y}";
            sb.Append($"\n{(l.BuildingId == Selected ? Col(Blue, "▶ ") : "")}[b]{Link($"co-jump:{l.X}:{l.Y}", title)}[/b]  {Small(l.LeaseId > 0 ? Col(Pos, "leased") : Dim("free to lease"))}\n");
            sb.Append(Small(Dim($"{Esc(l.Terms)}{(district != null ? $" · {Esc(district.Name)}, rating {district.Rating}" : "")}"))).Append('\n');
            if (l.Kind == "bus-stop")
                sb.Append(Small($"In walking reach: {Many(r.Homes, "home")} ({Many(r.People, "person", "people")}), {Many(r.Sites, "workplace")} ({Many(r.Jobs, "job")}){(l.LeaseId > 0 ? $" · {Many(boards, "boarding")} yesterday" : "")}")).Append('\n');
            if (l.ClosedDays > 0) sb.Append(Small(Col(Neg, $"Closed for {l.ClosedDays} more days (fire or disaster nearby)."))).Append('\n');
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
        var rows = new List<(string, string)> { ("Fares", Col(Pos, Money(m.Fares))) };
        if (m.Subsidy != 0) rows.Add(("From the city", Col(Pos, Money(m.Subsidy))));
        rows.Add(("Drivers' wages", Col(Neg, "-" + Money(m.Wages))));
        rows.Add(("Bus upkeep", Col(Neg, "-" + Money(m.Upkeep))));
        rows.Add(("Rent and fare tax", Col(Neg, "-" + Money(m.Rent))));
        if (m.Standing != 0) rows.Add(("Standing (ads, statues, rights)", Col(Neg, "-" + Money(m.Standing))));
        rows.Add(("[b]Profit[/b]", $"[b]{PosNeg(m.Profit, Money(m.Profit))}[/b]"));
        return Table(rows);
    }

    string MoneyText(View v)
    {
        var sb = new StringBuilder($"Cash [b]{PosNeg(v.Cash, Money(v.Cash))}[/b]. A bus costs {Money(v.BusPrice)} and sells for half that.\n");
        sb.Append(H4("Last month")).Append(Books(v.LastMonth));
        sb.Append(H4("This month so far")).Append(Books(v.ThisMonth));
        sb.Append(H4("By line, last month"));
        sb.Append(v.Lines.Count == 0 ? Dim("No lines.") + "\n" : Table(v.Lines.Select(l => (Esc(l.Name), PosNeg(l.LastMonth?.Profit ?? 0, Money(l.LastMonth?.Profit ?? 0))))));
        return sb.ToString();
    }

    string Standing(View v)
    {
        var sb = new StringBuilder();
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
