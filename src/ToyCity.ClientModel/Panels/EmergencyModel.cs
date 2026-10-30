using System.Text;
using Google.Protobuf;
using ToyCity.Emergency.Wire;
using ToyCity.Protocol.V0;
using static ToyCity.ClientModel.Panels.Bb;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Panels;

/// <summary>
/// The emergency services' dispatch board, as the web client has it: calls, units, reports, settings and the log as
/// text with links, and the scene a player runs, drawn by the front end from <see cref="Scene"/> and driven through
/// the pointer methods here. While a scene of the player's is open, <see cref="Tick"/> runs it on twice a second.
/// </summary>
public sealed class EmergencyModel
{
    public const string Game = "emergency";
    public static readonly (string Id, string Name)[] Tabs = { ("calls", "Calls"), ("units", "Units"), ("reports", "Reports"), ("settings", "Settings"), ("log", "Log") };
    public enum Order { Move, Path, Hose, Treat, Warn, Arrest, Taser, Shoot }
    static readonly string[] Styles = { "Full command", "Scene command", "Focus" };
    static readonly string[] StyleHelp =
    {
        "You do everything: every call waits for you to pick who goes, and you run every scene. Calls left waiting get worse, then are lost.",
        "The computer picks who goes to each call; you run every scene they reach.",
        "The computer picks who goes and settles each call as it would without you. You step into only the scenes you choose (Take scene on the call).",
    };
    static readonly string[] UnitIcon = { "🚒", "🚓", "🚑" };
    static readonly string[] UnitWord = { "engine", "patrol car", "ambulance" };
    public static readonly string[] RoleName = { "firefighter", "paramedic", "officer", "bystander", "casualty", "suspect" };

    readonly Func<ClientMessage, uint> _send;
    readonly System.Action _changed;
    readonly Dictionary<int, HashSet<int>> _picks = new();
    double _clock;

    public EmergencyModel(Func<ClientMessage, uint> send, System.Action changed) => (_send, _changed) = (send, changed);

    public Func<string> Me { get; set; } = () => "player";
    public View? View { get; private set; }
    public bool Public { get; private set; }
    public bool Mine => View?.Operator == "manual" && View.Owner == Me();
    public string Tab { get; private set; } = "calls";
    public int SceneId { get; private set; }
    public SceneView? Scene => View?.Scenes.FirstOrDefault(s => s.Id == SceneId) ?? (Ended?.Id == SceneId ? Ended : null);

    /// <summary>
    /// The emergency services' own screen: its top bar, its tools down the left and the board on the right, in place
    /// of the city's, as the bus company has. A player who only runs the service plays it there; a mayor who also
    /// runs it switches in and out.
    /// </summary>
    public bool Active { get; private set; }
    public void SetActive(bool on) { Active = on; _changed(); }

    /// <summary>
    /// The scene on show after it ended: kept as it last was, under its report card, until the player goes back to
    /// the calls. A scene that just closes looks like the board broke.
    /// </summary>
    public SceneView? Ended { get; private set; }
    string? _endedCall;
    /// <summary>Whether the ended scene's report card is over it; closing it shows the scene as it finished.</summary>
    public bool ReportOpen { get; private set; }
    /// <summary>Whether the scene is over and its orders are done with.</summary>
    public bool SceneOver => Ended != null && Ended.Id == SceneId;
    /// <summary>The card for the ended scene, once the service has written it (the public never sees it).</summary>
    public ReportCard? EndedCard => Ended == null ? null : View?.Cards.FirstOrDefault(c => c.SceneId == Ended.Id);
    /// <summary>A scene of the player's that opened while another's report was up, waiting its turn.</summary>
    public int NextScene { get; private set; }
    /// <summary>Whether the scene is put aside to show the city map around it.</summary>
    public bool SceneHidden { get; private set; }
    /// <summary>Shows a rectangle of cells on the city map: where the scene is.</summary>
    public event Action<int, int, int, int>? Flash;
    public bool Running { get; private set; } = true;
    public Order Current { get; private set; } = Order.Move;
    public List<int> Selected { get; } = new();
    /// <summary>The path being drawn, tile by tile, while the pointer is down.</summary>
    public List<int>? Drawing { get; private set; }
    /// <summary>Goes up whenever the scene should be drawn again.</summary>
    public int SceneVersion { get; private set; }

    /// <summary>
    /// What the player asked for and is waiting on (taking the service, handing it back, a lease), shown on the board
    /// at once. The city takes it now, but the service only hears at the start of the city's next day, which at the
    /// slowest speed is up to ten seconds away; a click with no sign of it looks like a miss.
    /// </summary>
    public string? Pending { get; private set; }
    Func<View, bool>? _pendingDone;

    void Wait(string what, Func<View, bool> done) => (Pending, _pendingDone) = (what, done);

    /// <summary>The server refused something: stop waiting on it.</summary>
    public void Refused()
    {
        if (Pending == null) return;
        (Pending, _pendingDone) = (null, null);
        _changed();
    }

    public event Action<string, string>? Toast;
    public event Action<int, int>? JumpTo;
    /// <summary>A scene of the player's opened: the front end shows the board.</summary>
    public event System.Action? SceneOpened;

    public void Handle(SideGameView v)
    {
        if (v.Game != Game) return;
        var before = View;
        View = View.Parser.ParseFrom(v.Payload);
        Public = v.Seat == "public";
        if (_pendingDone != null && _pendingDone(View)) (Pending, _pendingDone) = (null, null);
        // The scene on show ended: keep it, as it last was, with its report over it.
        if (SceneId != 0 && !SceneOver && View.Scenes.All(s => s.Id != SceneId) && before?.Scenes.FirstOrDefault(s => s.Id == SceneId) is { } gone)
        {
            (Ended, ReportOpen, SceneHidden) = (gone, true, false);
            _endedCall = before.Calls.FirstOrDefault(c => c.Id == gone.CallId)?.Text;
            Selected.Clear();
            Drawing = null;
        }
        if (Mine && (SceneId == 0 || SceneOver))
        {
            var fresh = View.Scenes.FirstOrDefault(s => before?.Scenes.All(b => b.Id != s.Id) ?? true);
            // A new scene waits while a report is up; otherwise it opens by itself.
            if (fresh != null && SceneOver) NextScene = fresh.Id;
            else if (fresh != null) { SceneId = fresh.Id; SceneHidden = false; SceneOpened?.Invoke(); }
        }
        if (NextScene != 0 && View.Scenes.All(s => s.Id != NextScene)) NextScene = 0;
        if (before != null)
            foreach (var card in View.Cards.Where(c => before.Cards.All(b => b.SceneId != c.SceneId)))
                Toast?.Invoke($"Scene report: {card.Where}, {card.Score} ({card.Outcome})", card.Score < 40 ? "warning" : "info");
        SceneVersion++;
        _changed();
    }

    /// <summary>Calls waiting for someone to send units, for the top bar.</summary>
    public int Waiting => View?.Calls.Count(c => c.Status is "waiting" or "proposed") ?? 0;

    void Command(Command c) =>
        _send(new ClientMessage { Action = new PAction { SideGame = new SideGameCommand { Game = Game, Seat = "operator", Payload = c.ToByteString() } } });

    /// <summary>Runs the player's open scene on: two steps every half second.</summary>
    public void Tick(double delta)
    {
        _clock += delta;
        if (_clock < 0.5) return;
        _clock = 0;
        if (Mine && Running && Scene != null && !SceneOver && Drawing == null) Command(new Command { Step = new SceneStep { SceneId = SceneId, Steps = 2 } });
    }

    UnitView? Unit(int id) => View?.Units.FirstOrDefault(u => u.Id == id);

    // ---------------------------------------------------------------------------------------------------------
    // Links

    public void Click(string meta)
    {
        var a = meta.Split(':');
        int I(int k) => k < a.Length && int.TryParse(a[k], out var v) ? v : -1;
        switch (a[0])
        {
            case "em-tab": Tab = a[1]; LeaveScene(); break;
            case "em-open": Active = true; break;
            case "em-city": Active = false; break;
            case "em-report": ReportOpen = !ReportOpen; break;
            case "em-locate":
            {
                if (Scene is not { } sc) return;
                // Four tiles to a cell: the scene's patch of the city, centred on the map.
                int cw = Math.Max(1, sc.Width / 4), ch = Math.Max(1, sc.Height / 4);
                JumpTo?.Invoke(sc.OriginX + cw / 2, sc.OriginY + ch / 2);
                Flash?.Invoke(sc.OriginX, sc.OriginY, sc.OriginX + cw - 1, sc.OriginY + ch - 1);
                SceneHidden = true;
                break;
            }
            case "em-scene": SceneHidden = false; break;
            case "em-next": { int next = NextScene; LeaveScene(); SceneId = next; SceneOpened?.Invoke(); break; }
            case "em-claim":
                if (Pending != null) return;
                _send(new ClientMessage { Action = new PAction { Seat = new SetSeat { Seat = Game, Owner = Me(), Level = SeatLevel.SeatManual, SpendCap = 25000 } } });
                Wait("Taking over the service…", v => v.Operator == "manual" && v.Owner == Me());
                break;
            case "em-release":
                if (Pending != null) return;
                _send(new ClientMessage { Action = new PAction { Seat = new SetSeat { Seat = Game, Owner = "", Level = SeatLevel.SeatCaretaker, SpendCap = 25000 } } });
                Wait("Handing the service back to the computer…", v => !(v.Operator == "manual" && v.Owner == Me()));
                break;
            case "em-lease":
            {
                if (Pending != null) return;
                int bid = I(1);
                _send(new ClientMessage { Action = new PAction { Lease = new LeaseLot { Game = Game, BuildingId = bid } } });
                Wait("Leasing the station…", v => v.OpenStations.All(o => o.BuildingId != bid));
                break;
            }
            case "em-go": if (View?.Calls.FirstOrDefault(c => c.Id == I(1)) is { } call) JumpTo?.Invoke(call.X, call.Y); return;
            case "em-pick":
            {
                var set = Picks(I(1));
                if (!set.Remove(I(2))) set.Add(I(2));
                break;
            }
            case "em-send":
            {
                var set = Picks(I(1));
                if (set.Count == 0) { Toast?.Invoke("Tick the units to send first.", "warning"); return; }
                var d = new Dispatch { CallId = I(1) };
                d.UnitIds.AddRange(set.Order());
                Command(new Command { Dispatch = d });
                _picks.Remove(I(1));
                return;
            }
            case "em-take": Command(new Command { Take = new TakeScene { CallId = I(1) } }); return;
            case "em-watch": LeaveScene(); SceneId = I(1); break;
            case "em-board": LeaveScene(); break;
            case "em-leave": Command(new Command { Leave = new LeaveScene { SceneId = SceneId } }); return;
            case "em-run": Running = !Running; break;
            case "em-order": Current = Enum.Parse<Order>(a[1]); break;
            case "em-buy": Command(new Command { Buy = new BuyUnit { LeaseId = I(1), Kind = (UnitKind)I(2) } }); return;
            case "em-sell": Command(new Command { Sell = new SellUnit { UnitId = I(1) } }); return;
            case "em-style": Settings(s => s.Style = (CommandStyle)I(1)); return;
            case "em-switch": Settings(s =>
            {
                if (a[1] == "pick") s.PickResponders = !s.PickResponders;
                if (a[1] == "heads") s.CrewsUseHeads = !s.CrewsUseHeads;
                if (a[1] == "watch") s.DriversWatchOut = !s.DriversWatchOut;
            }); return;
            default: return;
        }
        _changed();
    }

    /// <summary>Back to the board: the scene, its report and anything picked in it are put away.</summary>
    void LeaveScene()
    {
        (SceneId, Ended, ReportOpen, SceneHidden, NextScene) = (0, null, false, false, 0);
        Selected.Clear();
        Drawing = null;
        SceneVersion++;
    }

    HashSet<int> Picks(int call)
    {
        var c = View?.Calls.FirstOrDefault(x => x.Id == call);
        // An empty pick made while the call waited gives way to the computer's proposal when it comes.
        if (_picks.TryGetValue(call, out var set) && (set.Count > 0 || c?.Status != "proposed")) return set;
        return _picks[call] = c?.Status == "proposed" ? c.UnitIds.ToHashSet() : new HashSet<int>();
    }

    void Settings(Action<Settings> change)
    {
        if (View == null) return;
        var s = new Settings { Style = View.Style, PickResponders = View.PickResponders, CrewsUseHeads = View.CrewsUseHeads, DriversWatchOut = View.DriversWatchOut };
        change(s);
        Command(new Command { Settings = s });
    }

    // ---------------------------------------------------------------------------------------------------------
    // The board

    public string Render()
    {
        var v = View;
        var sb = new StringBuilder(H3($"🚒 {Esc(v?.Name ?? "Emergency services")}"));
        if (v == null) return sb.Append(Dim("No emergency services in this city.")).Append('\n').ToString();
        sb.Append(Public ? "" : $"{Money(v.Cash)}   ").Append($"respect {v.Respect}   service {v.ServiceQuality}   ")
            .Append(Dim(v.Operator == "manual" ? $"run by {Esc(v.Owner)}" : v.Operator));
        sb.Append("   ").Append(Pending != null ? "" : Mine ? Link("em-release", "Hand back to the computer") : Link("em-claim", "Run it yourself"));
        if (Pending != null) sb.Append('\n').Append(Col(Warn, $"⏳ {Esc(Pending)}")).Append(' ').Append(Small(Dim("It happens when the city's next day starts (not while paused).")));
        sb.Append('\n').Append(string.Join("  ", Tabs.Select(t => t.Id == Tab && Scene == null ? $"[b]{t.Name}[/b]" : Link($"em-tab:{t.Id}", t.Name)))).Append("\n\n");
        if (Scene != null) return sb.Append(RenderScene()).ToString();
        sb.Append(Tab switch { "units" => RenderUnits(), "reports" => RenderReports(), "settings" => RenderSettings(), "log" => RenderLog(), _ => RenderCalls() });
        return sb.ToString();
    }

    string RenderCalls()
    {
        var v = View!;
        if (v.Calls.Count == 0) return Dim("No calls. Fires, crimes and injuries in the city come in here.") + "\n";
        var sb = new StringBuilder();
        foreach (var c in v.Calls.OrderBy(c => c.Status == "done").ThenByDescending(c => c.Id))
        {
            string icon = c.Kind switch { "fire" => "🔥", "crime" => "🚨", _ => "🩹" };
            string status = c.Status == "done" ? $"done: {c.Outcome}" : c.Status == "waiting" && c.WaitedDays > 0 ? $"waiting {c.WaitedDays}d" : c.Status;
            sb.Append($"{icon} [b]{Esc(c.Text)}[/b] ").Append(Dim($"sev {c.Severity} · {status}{(c.Minutes > 0 ? $" · {c.Minutes} min" : "")}")).Append(' ').Append(Link($"em-go:{c.Id}", "map")).Append('\n');
            if (c.UnitIds.Count > 0) sb.Append(Small(Dim(string.Join(", ", c.UnitIds.Select(id => Unit(id)?.Name ?? "?"))))).Append('\n');
            if (Mine && c.Status is "waiting" or "proposed")
            {
                var set = Picks(c.Id);
                var choices = v.Units.Where(u => u.State == "ready" || u.State == "proposed" && u.CallId == c.Id).ToList();
                if (choices.Count == 0) sb.Append(Small(Dim("No units free.")));
                foreach (var u in choices) sb.Append(Link($"em-pick:{c.Id}:{u.Id}", $"{(set.Contains(u.Id) ? "☑" : "☐")} {UnitIcon[(int)u.Kind]} {Esc(u.Name)}")).Append("  ");
                if (choices.Count > 0) sb.Append($"[b]{Link($"em-send:{c.Id}", "Send")}[/b]");
                if (v.Style == CommandStyle.Focus) sb.Append("  ").Append(Link($"em-take:{c.Id}", "Take scene"));
                sb.Append('\n');
            }
            if (Mine && c.Status == "rolling" && v.Style == CommandStyle.Focus) sb.Append(Link($"em-take:{c.Id}", "Take scene")).Append('\n');
            if (c.Status == "scene") sb.Append(Link($"em-watch:{c.SceneId}", Mine ? "Run scene" : "Watch")).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    string RenderUnits()
    {
        var v = View!;
        var sb = new StringBuilder();
        if (v.Stations.Count == 0)
            sb.Append(Dim(v.OpenStations.Count > 0
                ? "The service has no stations yet: the city's fire stations, police stations, clinics and hospitals only send units once they're leased to it."
                : $"The service has no stations. Build a fire station, police station, clinic or hospital, then lease it to {Esc(v.Name)}.")).Append("\n\n");
        if (v.OpenStations.Count > 0)
        {
            sb.Append("[b]Not leased yet[/b]\n");
            foreach (var o in v.OpenStations)
            {
                sb.Append($"{Esc(o.Kind.Replace('-', ' '))} at {o.X},{o.Y}  ");
                sb.Append(Mine ? Pending != null ? Dim("⏳ …") : Link($"em-lease:{o.BuildingId}", "Lease it") : Dim("the mayor, or whoever runs the service, can lease it")).Append('\n');
            }
            sb.Append('\n');
        }
        foreach (var s in v.Stations)
        {
            int kind = s.Kind == "fire-station" ? 0 : s.Kind == "police-station" ? 1 : 2;
            sb.Append($"[b]{Esc(s.Name)}[/b]\n");
            foreach (var u in v.Units.Where(u => u.StationLeaseId == s.LeaseId))
            {
                sb.Append($"{UnitIcon[(int)u.Kind]} {Esc(u.Name)} ").Append(Dim($"{u.Crew} crew · {u.State}{(u.BackInDays > 0 ? $" ({u.BackInDays}d)" : "")}"));
                if (Mine && u.State == "ready") sb.Append("  ").Append(Link($"em-sell:{u.Id}", "Sell"));
                sb.Append('\n');
            }
            if (Mine) sb.Append(Link($"em-buy:{s.LeaseId}:{kind}", $"Buy {UnitWord[kind]}")).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    string RenderReports()
    {
        if (Public) return Dim("Scene reports are the service's own business.") + "\n";
        var v = View!;
        if (v.Cards.Count == 0) return Dim("Each scene you run gets a report card here. Lives saved count for most; harm your crews cause costs more than a slow response.") + "\n";
        var sb = new StringBuilder();
        foreach (var c in v.Cards.Reverse())
        {
            sb.Append($"[b]{Esc(c.Where)}[/b]  ").Append(Col(c.Score < 40 ? Neg : Pos, $"[b]{c.Score}[/b]")).Append("  ").Append(Dim(c.Outcome)).Append('\n');
            sb.Append(Small(Dim($"{c.ResponseMinutes} min response · {c.Saved} saved · {c.Lost} lost · {c.HarmedByCrews} hurt by crews · {c.FriendlyFire} friendly fire · {c.UnauthorizedForce} unauthorised force · {c.PropertyLostPercent}% property lost"))).Append('\n');
            foreach (var n in c.Notes) sb.Append(Small(Esc(n))).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    string RenderSettings()
    {
        var v = View!;
        var sb = new StringBuilder(Small(Dim(Mine
            ? "How much you run yourself while you run the service. Hand it back to the computer (top of this panel) and it runs everything again."
            : "These apply while a player runs the service; the computer runs everything otherwise. Run it yourself to change them.")) + "\n");
        string Opt(string meta, bool on, string text) => Mine ? Link(meta, $"{(on ? "☑" : "☐")} {text}") : $"{(on ? "☑" : "☐")} {text}";
        for (int i = 0; i < Styles.Length; i++)
            sb.Append(Opt($"em-style:{i}", (int)v.Style == i, $"[b]{Styles[i]}[/b]")).Append('\n').Append(Small(Dim(StyleHelp[i]))).Append('\n');
        sb.Append('\n').Append(Opt("em-switch:pick", v.PickResponders, "[b]Pick responders[/b]")).Append('\n')
            .Append(Small(Dim("The computer proposes who goes and waits a day for you to change it."))).Append('\n');
        sb.Append(Opt("em-switch:heads", v.CrewsUseHeads, "[b]Crews use their heads[/b]")).Append('\n')
            .Append(Small(Dim("Crews with no orders do the obvious job nearest them. Off: they stand still until told."))).Append('\n');
        sb.Append(Opt("em-switch:watch", v.DriversWatchOut, "[b]Drivers watch out[/b]")).Append('\n')
            .Append(Small(Dim("Vehicles stop for people in the way. Off: they drive on and hit them. Anyone hit is hurt or killed, the vehicle is off the road for 30 days, and the service's respect and standing fall."))).Append('\n');
        return sb.ToString();
    }

    string RenderLog()
    {
        var v = View!;
        return v.Log.Count == 0 ? Dim("Nothing yet.") + "\n" : string.Join("\n", v.Log.Reverse().Select(l => Small(Esc(l)))) + "\n";
    }

    string RenderScene()
    {
        var sb = new StringBuilder(Link("em-board", "← Board")).Append("  ").Append(SceneTitle()).Append('\n');
        if (SceneOver)
        {
            sb.Append(RenderReport()).Append('\n').Append($"[b]{Link("em-board", "Return to calls")}[/b]").Append('\n');
            return sb.ToString();
        }
        if (Mine)
        {
            sb.Append(Link("em-run", Running ? "⏸ Hold" : "▶ Run")).Append("  ").Append(Link("em-leave", "Hand to crews")).Append("  ").Append(Link("em-locate", "📍 Where is this?")).Append('\n');
            sb.Append(string.Join("  ", Enum.GetValues<Order>().Select(o => o == Current ? $"[b]{o}[/b]" : Link($"em-order:{o}", o.ToString())))).Append('\n');
            sb.Append(Small(Dim(OrderHint))).Append('\n');
        }
        else sb.Append(Small(Dim("Watching."))).Append("  ").Append(Link("em-locate", "📍 Where is this?")).Append('\n');
        return sb.Append(SceneNews()).ToString();
    }

    /// <summary>The scene's call and its clock.</summary>
    public string SceneTitle()
    {
        var s = Scene!;
        var call = View!.Calls.FirstOrDefault(c => c.Id == s.CallId);
        int secs = s.Step / 4;
        return $"[b]{Esc(call?.Text ?? (SceneOver ? _endedCall : null) ?? s.Kind)}[/b]  " + Dim(SceneOver ? $"over after {secs / 60}:{secs % 60:00}" : $"{secs / 60}:{secs % 60:00} · {s.Burning} alight");
    }

    /// <summary>How to give the order picked.</summary>
    public string OrderHint => "Click to select (shift adds), then " + Current switch
    {
        Order.Move => "right-click where to go.", Order.Path => "drag a path from the selected one.", _ => "click the target.",
    };

    /// <summary>Who is selected, what just happened at the scene, and who is on the way.</summary>
    public string SceneNews()
    {
        var s = Scene!;
        var sb = new StringBuilder();
        if (Selected.Count > 0) sb.Append(Small("Selected: " + string.Join(", ", Selected.Select(Describe)))).Append('\n');
        foreach (var e in s.Events.Reverse()) sb.Append(Small(Esc(e))).Append('\n');
        if (s.Arriving.Count > 0 && !SceneOver) sb.Append(Small(Dim("On the way: " + string.Join(", ", s.Arriving.Select(a => $"{Unit(a.UnitId)?.Name ?? "?"} in {Math.Max(0, (a.AtStep - s.Step + 3) / 4)}s"))))).Append('\n');
        return sb.ToString();
    }

    /// <summary>The ended scene's report card: the score and what made it.</summary>
    public string RenderReport()
    {
        var sb = new StringBuilder(H3("Scene over"));
        if (EndedCard is not { } c)
            return sb.Append(Dim(Public ? "The service keeps its report cards to itself." : "The crews are writing the report…")).Append('\n').ToString();
        sb.Append($"[b]{Esc(c.Where)}[/b]  ").Append(Col(c.Score < 40 ? Neg : Pos, $"[font_size=22][b]{c.Score}[/b][/font_size]")).Append("  ").Append(Dim(Esc(c.Outcome))).Append("\n\n");
        sb.Append(Table(new[]
        {
            ("Response", $"{c.ResponseMinutes} min"), ("Lives saved", Num(c.Saved)), ("Lives lost", Num(c.Lost)),
            ("Hurt by crews", Num(c.HarmedByCrews)), ("Friendly fire", Num(c.FriendlyFire)), ("Unauthorised force", Num(c.UnauthorizedForce)),
            ("Property lost", $"{c.PropertyLostPercent}%"),
        }.Select(r => (Dim(r.Item1), r.Item2))));
        foreach (var n in c.Notes) sb.Append(Small(Esc(n))).Append('\n');
        if (NextScene != 0) sb.Append('\n').Append(Col(Warn, "Another scene of yours is waiting.")).Append(' ').Append(Link("em-next", "Go to it")).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The service's own screen

    /// <summary>The top bar's text: the service, its money and standing, and who runs it.</summary>
    public string TopText => View is not { } v ? "Emergency services" : $"{v.Name}   {TopStats}";

    /// <summary>The top bar's figures, after the service's name.</summary>
    public string TopStats => View is not { } v ? ""
        : $"{(Public ? "" : $"{Money(v.Cash)}   ")}respect {v.Respect}   service {v.ServiceQuality}   " + (v.Operator == "manual" ? $"run by {v.Owner}" : $"the computer runs it ({v.Operator})");

    /// <summary>The seat button's words, or null while a change is under way.</summary>
    public (string Meta, string Text)? SeatButton => View == null || Pending != null ? null
        : Mine ? ("em-release", "Hand back to the computer") : ("em-claim", "Run it yourself");

    /// <summary>
    /// The tools down the left of the service's screen, as headed groups of buttons: the board's pages, or in a
    /// scene the orders and what to do with the scene.
    /// </summary>
    public List<(string Heading, List<(string Meta, string Text, bool On)> Items)> ToolSections()
    {
        var list = new List<(string, List<(string, string, bool)>)>();
        if (View == null) return list;
        if (Scene == null)
        {
            list.Add(("Board", Tabs.Select(t => ($"em-tab:{t.Id}", t.Id == "calls" && Waiting > 0 ? $"{t.Name} ({Waiting} waiting)" : t.Name, t.Id == Tab)).ToList()));
            return list;
        }
        var scene = new List<(string, string, bool)> { ("em-board", "← Return to calls", false) };
        scene.Add(SceneHidden ? ("em-scene", "Back to the scene", false) : ("em-locate", "📍 Where is this?", false));
        if (SceneOver) scene.Add(("em-report", ReportOpen ? "Close report" : "Show report", ReportOpen));
        else if (Mine) { scene.Add(("em-run", Running ? "⏸ Hold" : "▶ Run", !Running)); scene.Add(("em-leave", "Hand to crews", false)); }
        list.Add(("Scene", scene));
        if (Mine && !SceneOver) list.Add(("Orders", Enum.GetValues<Order>().Select(o => ($"em-order:{o}", o.ToString(), o == Current)).ToList()));
        return list;
    }

    /// <summary>The board's page, or the scene's news, for the panel on the right of the service's screen.</summary>
    public string RenderBody()
    {
        if (View == null) return Dim("No emergency services in this city.") + "\n";
        if (Scene != null) return SceneTitle() + "\n" + (Mine && !SceneOver ? Small(Dim(OrderHint)) + "\n" : "") + "\n" + SceneNews();
        var sb = new StringBuilder();
        if (Pending != null) sb.Append(Col(Warn, $"⏳ {Esc(Pending)}")).Append(' ').Append(Small(Dim("It happens when the city's next day starts (not while paused)."))).Append("\n\n");
        sb.Append(H3(Tabs.First(t => t.Id == Tab).Name));
        sb.Append(Tab switch { "units" => RenderUnits(), "reports" => RenderReports(), "settings" => RenderSettings(), "log" => RenderLog(), _ => RenderCalls() });
        return sb.ToString();
    }

    string Describe(int id)
    {
        var s = Scene!;
        if (s.People.FirstOrDefault(p => p.Id == id) is { } p) return $"{RoleName[(int)p.Role]} ({Unit(p.UnitId)?.Name ?? ""})";
        return Unit(id)?.Name ?? "";
    }

    // ---------------------------------------------------------------------------------------------------------
    // The scene, from the pointer

    /// <summary>The person or vehicle on a tile: crews and vehicles first.</summary>
    public int? HitAt(int x, int y)
    {
        var s = Scene;
        if (s == null) return null;
        bool Near(int px, int py, double r) => Math.Abs(px / 16.0 - 0.5 - x) <= r && Math.Abs(py / 16.0 - 0.5 - y) <= r;
        var p = s.People.Where(q => q.State != "escaped" && Near(q.X, q.Y, 0.6)).OrderBy(q => (int)q.Role > 2).FirstOrDefault();
        if (p != null && (int)p.Role <= 2) return p.Id;
        if (s.Vehicles.FirstOrDefault(q => Near(q.X, q.Y, 1)) is { } v) return v.Id;
        return p?.Id;
    }

    bool CrewOrVehicle(int id)
    {
        var s = Scene!;
        var p = s.People.FirstOrDefault(q => q.Id == id);
        return p != null ? (int)p.Role <= 2 : s.Vehicles.Any(v => v.Id == id);
    }

    public void PointerDown(int x, int y, bool add)
    {
        var s = Scene;
        if (s == null || !Mine || SceneOver) return;
        var hit = HitAt(x, y);
        if (Current == Order.Path && Selected.Count == 1 && (hit == null || hit == Selected[0])) { Drawing = new List<int> { y * s.Width + x }; SceneVersion++; return; }
        bool targets = Current is Order.Treat or Order.Warn or Order.Arrest;
        if (Current is Order.Move or Order.Path || hit is { } h0 && CrewOrVehicle(h0) && !targets)
        {
            if (hit is { } h && CrewOrVehicle(h)) { if (!add) Selected.Clear(); if (!Selected.Contains(h)) Selected.Add(h); }
            else if (!add) Selected.Clear();
            SceneVersion++;
            _changed();
            return;
        }
        if (Selected.Count == 0) return;
        var t = new TaskOrder { SceneId = s.Id, X = x, Y = y, TargetId = hit ?? 0, Task = Current switch
        {
            Order.Hose => TaskKind.Hose, Order.Treat => TaskKind.Treat, Order.Warn => TaskKind.Warn, Order.Arrest => TaskKind.Arrest,
            Order.Taser => TaskKind.Taser, _ => TaskKind.Shoot,
        } };
        t.Ids.AddRange(Selected);
        Command(new Command { Task = t });
    }

    public void PointerMove(int x, int y)
    {
        var s = Scene;
        if (Drawing == null || s == null) return;
        int c = y * s.Width + x, last = Drawing[^1];
        // A fast drag fills in with steps between neighbouring tiles.
        while (last != c)
        {
            int lx = last % s.Width, ly = last / s.Width;
            if (lx != x) lx += Math.Sign(x - lx); else ly += Math.Sign(y - ly);
            last = ly * s.Width + lx;
            Drawing.Add(last);
        }
        SceneVersion++;
    }

    public void PointerUp()
    {
        if (Drawing == null || Scene is not { } s) { Drawing = null; return; }
        var p = new PathOrder { SceneId = s.Id, Id = Selected[0] };
        p.Tiles.AddRange(Drawing);
        Command(new Command { Path = p });
        Drawing = null;
        SceneVersion++;
    }

    public void MoveTo(int x, int y)
    {
        if (Scene is not { } s || !Mine || SceneOver || Selected.Count == 0) return;
        var m = new MoveOrder { SceneId = s.Id, X = x, Y = y };
        m.Ids.AddRange(Selected);
        Command(new Command { Move = m });
    }
}
