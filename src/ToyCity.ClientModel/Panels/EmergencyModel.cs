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
        "Every call waits for you: you pick who goes and run every scene. Calls left waiting get worse, then are lost.",
        "The computer sends units; you run every scene they reach.",
        "The computer handles every call. Take the ones you want to run yourself.",
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
    public SceneView? Scene => View?.Scenes.FirstOrDefault(s => s.Id == SceneId);
    public bool Running { get; private set; } = true;
    public Order Current { get; private set; } = Order.Move;
    public List<int> Selected { get; } = new();
    /// <summary>The path being drawn, tile by tile, while the pointer is down.</summary>
    public List<int>? Drawing { get; private set; }
    /// <summary>Goes up whenever the scene should be drawn again.</summary>
    public int SceneVersion { get; private set; }

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
        if (Mine && View.Scenes.All(s => s.Id != SceneId))
        {
            var fresh = View.Scenes.FirstOrDefault(s => before?.Scenes.All(b => b.Id != s.Id) ?? true);
            SceneId = fresh?.Id ?? 0;
            if (fresh != null) SceneOpened?.Invoke();
            else { Selected.Clear(); Drawing = null; }
        }
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
        if (Mine && Running && Scene != null && Drawing == null) Command(new Command { Step = new SceneStep { SceneId = SceneId, Steps = 2 } });
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
            case "em-tab": Tab = a[1]; SceneId = 0; break;
            case "em-claim": _send(new ClientMessage { Action = new PAction { Seat = new SetSeat { Seat = Game, Owner = Me(), Level = SeatLevel.SeatManual, SpendCap = 25000 } } }); return;
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
            case "em-watch": SceneId = I(1); Selected.Clear(); SceneVersion++; break;
            case "em-board": SceneId = 0; break;
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

    HashSet<int> Picks(int call)
    {
        if (_picks.TryGetValue(call, out var set)) return set;
        var c = View?.Calls.FirstOrDefault(x => x.Id == call);
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
        if (!Mine) sb.Append("   ").Append(Link("em-claim", "Run it"));
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
        if (v.Stations.Count == 0)
            return Dim($"The service has no stations. Lease a fire station, police station, clinic or hospital to {Esc(v.Name)}: query one on the map.") + "\n"
                + (v.OpenStations.Count > 0 ? Small(Dim("Free stations: " + string.Join("; ", v.OpenStations.Select(o => $"{o.Kind.Replace('-', ' ')} at {o.X},{o.Y}")))) + "\n" : "");
        var sb = new StringBuilder();
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
        var sb = new StringBuilder(Small(Dim(Mine ? "How much you run yourself." : "Run the service to change these.")) + "\n");
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
        var s = Scene!;
        var call = View!.Calls.FirstOrDefault(c => c.Id == s.CallId);
        int secs = s.Step / 4;
        var sb = new StringBuilder();
        sb.Append(Link("em-board", "← Board")).Append($"  [b]{Esc(call?.Text ?? s.Kind)}[/b]  ").Append(Dim($"{secs / 60}:{secs % 60:00} · {s.Burning} alight")).Append('\n');
        if (Mine)
        {
            sb.Append(Link("em-run", Running ? "⏸ Hold" : "▶ Run")).Append("  ").Append(Link("em-leave", "Hand to crews")).Append('\n');
            sb.Append(string.Join("  ", Enum.GetValues<Order>().Select(o => o == Current ? $"[b]{o}[/b]" : Link($"em-order:{o}", o.ToString())))).Append('\n');
            string how = Current switch { Order.Move => "right-click where to go", Order.Path => "drag a path from the selected one", _ => "click the target" };
            sb.Append(Small(Dim($"Click to select (shift adds), then {how}."))).Append('\n');
            if (Selected.Count > 0) sb.Append(Small("Selected: " + string.Join(", ", Selected.Select(Describe)))).Append('\n');
        }
        else sb.Append(Small(Dim("Watching."))).Append('\n');
        foreach (var e in s.Events.Reverse()) sb.Append(Small(Esc(e))).Append('\n');
        if (s.Arriving.Count > 0) sb.Append(Small(Dim("On the way: " + string.Join(", ", s.Arriving.Select(a => $"{Unit(a.UnitId)?.Name ?? "?"} in {Math.Max(0, (a.AtStep - s.Step + 3) / 4)}s"))))).Append('\n');
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
        if (s == null || !Mine) return;
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
        if (Scene is not { } s || !Mine || Selected.Count == 0) return;
        var m = new MoveOrder { SceneId = s.Id, X = x, Y = y };
        m.Ids.AddRange(Selected);
        Command(new Command { Move = m });
    }
}
