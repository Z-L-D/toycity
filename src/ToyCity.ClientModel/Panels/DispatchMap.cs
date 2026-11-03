using ToyCity.Emergency.Wire;

namespace ToyCity.ClientModel.Panels;

/// <summary>
/// Direct dispatch on the city map (Beta 4, part 1), the same in both clients: where each unit is right now, eased
/// along its leg by the day clock; which units the player has picked (a click, shift-clicks, a box, or a numbered
/// group); and the orders a right-click or a button sends. Auto-dispatch still runs underneath: these orders are on
/// top of it, as Zac chose. A unit busy with a call can't take one, so it's left out of the order and the player told.
/// </summary>
public sealed class DispatchMap
{
    /// <summary>Each kind's colour on the map: engines red, patrol cars blue, ambulances white (green stripe), SWAT dark.</summary>
    public static readonly string[] Body = { "#d8342a", "#2f6fe0", "#f4f4f2", "#232a33" };
    public static readonly string[] Stripe = { "#f4f4f2", "#f4f4f2", "#2fae5a", "#9aa4b0" };
    public const string SelectedColour = "#ffe14d";

    readonly Func<View?> _view;
    readonly Action<Command> _send;
    readonly Action<string, string> _toast;
    readonly Dictionary<int, List<int>> _groups = new();

    public DispatchMap(Func<View?> view, Action<Command> send, Action<string, string> toast) => (_view, _send, _toast) = (view, send, toast);

    /// <summary>The city's cells across, to turn cells into places.</summary>
    public Func<int> Width { get; set; } = () => 128;
    public List<int> Selected { get; } = new();
    /// <summary>Set after "Follow": the next unit clicked is the one the selection follows.</summary>
    public bool PickingLeader { get; private set; }
    /// <summary>Goes up whenever the selection changes, for front ends that redraw on change.</summary>
    public int Version { get; private set; }

    public readonly record struct Marker(UnitView Unit, double X, double Y, double Dx, double Dy, bool Moving, bool Selected);

    /// <summary>
    /// Where a unit is at a minute of the day clock, in cells (the centre of a cell is +0.5), and which way it faces.
    /// Driving, it's that far along its leg's cells; standing, at its cell, or its station's door when it's home.
    /// </summary>
    public static (double X, double Y, double Dx, double Dy, bool Moving)? Position(UnitView u, double minute, int width)
    {
        if (width <= 0) return null;
        (double, double) At(int c) => (c % width + 0.5, c / width + 0.5);
        // Ordered since the last day: it leaves at the next (its start is still -1).
        if (u.Leg.Count > 1 && u.LegStart < 0) { var (sx, sy) = At(u.Leg[0]); var (nx, ny) = At(u.Leg[1]); return (sx, sy, nx - sx, ny - sy, true); }
        if (u.Leg.Count > 1 && u.LegMinutes > 0 && minute < u.LegStart + u.LegMinutes)
        {
            double t = Math.Clamp((minute - u.LegStart) / (double)u.LegMinutes, 0, 1) * (u.Leg.Count - 1);
            int k = Math.Min(u.Leg.Count - 2, (int)t);
            var (ax, ay) = At(u.Leg[k]);
            var (bx, by) = At(u.Leg[k + 1]);
            double f = t - k;
            return (ax + (bx - ax) * f, ay + (by - ay) * f, bx - ax, by - ay, true);
        }
        int cell = u.Cell >= 0 ? u.Cell : u.Home;
        if (cell < 0) return null;
        var (x, y) = At(cell);
        return (x, y, 1, 0, false);
    }

    /// <summary>Every unit that is somewhere on the map at this minute.</summary>
    public List<Marker> Markers(double minute)
    {
        var list = new List<Marker>();
        if (_view() is not { } v) return list;
        int w = Width();
        foreach (var u in v.Units)
        {
            if (u.State == "suspended" || Position(u, minute, w) is not { } p) continue;
            list.Add(new Marker(u, p.X, p.Y, p.Dx, p.Dy, p.Moving, Selected.Contains(u.Id)));
        }
        return list;
    }

    /// <summary>The unit nearest a point on the map, within a cell and a half (the front end passes cells).</summary>
    public int? UnitAt(double x, double y, double minute, double reach = 1.5)
    {
        var best = Markers(minute).Select(m => (m.Unit.Id, D: Math.Abs(m.X - x) + Math.Abs(m.Y - y))).Where(m => m.D <= reach).OrderBy(m => m.D).FirstOrDefault();
        return best.D <= reach && best.Id != 0 ? best.Id : null;
    }

    void Changed() => Version++;

    /// <summary>A left click on the map: on a unit it's picked (shift adds or takes it off), on nothing the pick clears.</summary>
    public void Click(int? unit, bool add)
    {
        if (PickingLeader)
        {
            PickingLeader = false;
            if (unit is { } lead) Follow(lead);
            Changed();
            return;
        }
        if (unit is { } id)
        {
            if (!add) { Selected.Clear(); Selected.Add(id); }
            else if (!Selected.Remove(id)) Selected.Add(id);
        }
        else if (!add) Selected.Clear();
        Changed();
    }

    /// <summary>A box dragged over the map: the units in it (shift adds them to the pick).</summary>
    public void Box(IEnumerable<int> ids, bool add)
    {
        PickingLeader = false;
        if (!add) Selected.Clear();
        foreach (var id in ids) if (!Selected.Contains(id)) Selected.Add(id);
        Changed();
    }

    /// <summary>Ctrl and a number: the pick becomes that group.</summary>
    public void SetGroup(int n)
    {
        if (Selected.Count == 0) { _groups.Remove(n); return; }
        _groups[n] = Selected.ToList();
        _toast($"Group {n}: {Selected.Count} unit{(Selected.Count == 1 ? "" : "s")}.", "info");
    }

    public bool HasGroup(int n) => _groups.ContainsKey(n);

    /// <summary>A number: picks that group again. False when there's no such group.</summary>
    public bool RecallGroup(int n)
    {
        if (!_groups.TryGetValue(n, out var ids)) return false;
        var live = _view()?.Units.Select(u => u.Id).ToHashSet() ?? new HashSet<int>();
        Selected.Clear();
        Selected.AddRange(ids.Where(live.Contains));
        Changed();
        return true;
    }

    UnitView? Unit(int id) => _view()?.Units.FirstOrDefault(u => u.Id == id);
    static bool Free(UnitView u) => u.State is "ready" or "proposed";

    /// <summary>The picked units that can take an order; the busy ones are named to the player.</summary>
    List<int> Orderable()
    {
        var units = Selected.Select(Unit).OfType<UnitView>().ToList();
        var busy = units.Where(u => !Free(u)).ToList();
        if (units.Count == 0) { _toast("Pick some units first: click one, shift-click more, or drag a box.", "warning"); return new(); }
        if (busy.Count == units.Count) { _toast(busy.Count == 1 ? $"{busy[0].Name} is busy with a call." : "They're all busy with calls.", "warning"); return new(); }
        if (busy.Count > 0) _toast($"{string.Join(", ", busy.Select(u => u.Name))} {(busy.Count == 1 ? "is" : "are")} busy with a call, so left out.", "info");
        return units.Where(Free).Select(u => u.Id).ToList();
    }

    void Order(string kind, List<int> ids, int x = 0, int y = 0, int call = 0, int follow = 0, bool queue = false)
    {
        if (ids.Count == 0) return;
        var o = new UnitOrder { Kind = kind, X = x, Y = y, CallId = call, FollowId = follow, Queue = queue };
        o.UnitIds.AddRange(ids);
        _send(new Command { Order = o });
    }

    /// <summary>The open call at or next to a cell, if any.</summary>
    public CallView? CallAt(int x, int y) =>
        _view()?.Calls.Where(c => c.Status is not ("done" or "lost") && Math.Abs(c.X - x) <= 1 && Math.Abs(c.Y - y) <= 1)
            .OrderBy(c => Math.Abs(c.X - x) + Math.Abs(c.Y - y)).FirstOrDefault();

    /// <summary>
    /// A right click on the map: on a call, the picked units go to it; on a unit, they follow it; anywhere else they
    /// drive to the nearest street there (shift queues it after where they're going).
    /// </summary>
    public void RightClick(int x, int y, int? unit, bool queue)
    {
        if (Selected.Count == 0) return;
        if (unit is { } lead && !Selected.Contains(lead)) { Follow(lead); return; }
        if (CallAt(x, y) is { } call)
        {
            var ids = Orderable();
            Order("scene", ids, call: call.Id);
            return;
        }
        Order("go", Orderable(), x, y, queue: queue);
    }

    public void Hold() => Order("hold", Orderable());
    public void Return() => Order("return", Orderable());

    /// <summary>"Follow": the next unit clicked leads.</summary>
    public void StartFollow()
    {
        if (Selected.Count == 0) { _toast("Pick the units that should follow first.", "warning"); return; }
        PickingLeader = true;
        Changed();
    }

    void Follow(int lead) => Order("follow", Orderable().Where(id => id != lead).ToList(), follow: lead);

    /// <summary>The help line for the map, which changes with what's picked.</summary>
    public string Hint()
    {
        if (PickingLeader) return "Click the unit they should follow.";
        if (Selected.Count == 0) return "Click a unit to pick it (shift adds; drag a box for several; ctrl+1–9 makes a group, 1–9 picks it again).";
        return "Right-click a place to send them (shift queues more places), a call to answer it, or a unit to follow it.";
    }

    /// <summary>A line on the picked units, for the tools: "Engine 1, Patrol 2 (driving)".</summary>
    public string PickedText()
    {
        var units = Selected.Select(Unit).OfType<UnitView>().ToList();
        if (units.Count == 0) return "No units picked.";
        string One(UnitView u) => $"{u.Name} ({Doing(u)})";
        return units.Count <= 4 ? string.Join(", ", units.Select(One)) : $"{units.Count} units: {string.Join(", ", units.Take(3).Select(u => u.Name))} and {units.Count - 3} more";
    }

    /// <summary>What a unit is doing, in a word or two.</summary>
    public string Doing(UnitView u)
    {
        if (u.State is "on call" or "at scene") return u.State;
        if (u.State == "suspended") return "off the road";
        if (u.Order == "held") return "holding";
        if (u.Order == "following") return $"following {Unit(u.FollowId)?.Name ?? "a unit"}";
        if (u.Leg.Count > 0) return u.Order == "posted" ? "driving out" : u.Cell == u.Home ? "going home" : "driving";
        if (u.Order == "posted") return "posted";
        return u.Cell == u.Home || u.Cell < 0 ? "at the station" : "ready";
    }
}

/// <summary>
/// The day clock's minute for moving things, run on smoothly between the city's days. At most speeds the city's day
/// comes round faster than its clock would run (48 minutes of clock in a second or two), so the clock on the top bar
/// waits and jumps; a unit eased by it would sit, then leap. This runs the minute across each day's step over as long
/// as the last day took, so a unit drives the whole way in the time it has.
/// </summary>
public sealed class TickClock
{
    long _minute = long.MinValue;
    DateTime _at;
    double _intervalMs = 2000;

    public long Minute(long clockMinute, int step, int speed, DateTime now) => (long)Exact(clockMinute, step, speed, now);

    public double Exact(long clockMinute, int step, int speed, DateTime now)
    {
        if (clockMinute != _minute)
        {
            if (_minute != long.MinValue && clockMinute > _minute) _intervalMs = Math.Clamp((now - _at).TotalMilliseconds, 100, 120_000);
            (_minute, _at) = (clockMinute, now);
        }
        if (speed <= 0 || step <= 0) return clockMinute;
        double f = Math.Clamp((now - _at).TotalMilliseconds / _intervalMs, 0, 1);
        return clockMinute + f * (step - 0.001);
    }
}
