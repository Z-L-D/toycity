using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Gate;

/// <summary>Something the gate run waits for before its next step, and how long it may take.</summary>
public sealed record GateWait(string What, Func<bool> Done, double Seconds);

/// <summary>A gate step that didn't come out as it should.</summary>
public sealed class GateFailed(string message) : Exception(message);

/// <summary>
/// The Beta 1 gate: the Alpha 3 loop played end to end through the client's own tools and panels, as a player
/// would. Found a benchmark city from the New city dialog; put up bus stops and a depot with the tools; take the
/// bus company, lease the lots, draw a line and buy buses in the company's view; then hand the city seat to the
/// computer on each of stasis, caretaker and autopilot, checking what each does and that riders and fares come in;
/// and take the city back to its "while you were away" report. The steps are an iterator of waits, so the Godot
/// client can run it a frame at a time while it measures, and a test can run it against the server directly.
/// </summary>
public sealed class GateRun
{
    readonly CityClient _client;
    readonly PanelModel _panel;
    readonly Func<ToolState, Pointer, Preview?> _use;
    CityMirror City => _client.City;
    CompanyModel Co => _panel.Company;

    /// <summary>What the run is doing now, for the frame times to be filed under.</summary>
    public string Phase { get; private set; } = "starting";
    /// <summary>Each check made, in order, and what was seen.</summary>
    public readonly List<(string Check, bool Ok, string Seen)> Checks = new();
    /// <summary>Where the stops went, for the camera.</summary>
    public (int X, int Y) Focus { get; private set; }

    /// <param name="use">Uses a tool at a pointer as a click (or a drag's release) would, returning what it planned.</param>
    public GateRun(CityClient client, PanelModel panel, Func<ToolState, Pointer, Preview?> use) => (_client, _panel, _use) = (client, panel, use);

    void Check(string check, bool ok, string seen)
    {
        Checks.Add((check, ok, seen));
        if (!ok) throw new GateFailed($"{check}: {seen}");
    }

    // The most drivers at the depot and riders at the line's stops seen on any frame since the line opened: each
    // moves as homes rematch their commutes through the month, so the checks look at the whole run.
    Func<int>? _drivers, _stopRiders;
    int _mostDrivers, _mostStopRiders;

    GateWait Wait(string what, Func<bool> done, double seconds) => new(what, () =>
    {
        _mostDrivers = Math.Max(_mostDrivers, _drivers?.Invoke() ?? 0);
        _mostStopRiders = Math.Max(_mostStopRiders, _stopRiders?.Invoke() ?? 0);
        return done();
    }, seconds);

    void Speed(int speed) => _client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = speed } });

    GateWait Days(string what, int days)
    {
        long until = City.Stats.Tick + days;
        return Wait(what, () => City.Stats.Tick >= until, days * 2 + 30);
    }

    /// <summary>Founds the benchmark city from the New city dialog and waits for it to arrive.</summary>
    public IEnumerable<GateWait> Found()
    {
        Phase = "founding";
        yield return new GateWait("the first city", () => City.Loaded, 60);
        _panel.OpenDialog("new");
        _panel.SetInput("Gate City");
        _panel.Click("bench");
        _panel.Click("new-ok");
        yield return new GateWait("the benchmark city", () => City.Loaded && City.Width == 256 && City.Stats.CityName == "Gate City" && City.Buildings.Count > 3000, 180);
        Speed(0);
        Check("benchmark city founded", City.Buildings.Count > 3000, $"{City.Width} × {City.Height}, {City.Buildings.Count:N0} buildings");
    }

    /// <summary>The Alpha 3 loop on the city <see cref="Found"/> made.</summary>
    public IEnumerable<GateWait> Play()
    {
        var c = City;
        Phase = "building stops";
        Speed(0);
        var site = FindSite() ?? throw new GateFailed("no street near the middle with room for three stops and a depot");
        var (sx, sy) = site;
        Focus = (sx + 12, sy);

        // Three stops along one street, a block apart, with the bus-stop tool.
        var stopTool = new ToolState { Kind = ToolKind.Building, BuildingTypeId = "bus-stop" };
        var stopCells = new[] { (sx + 2, sy), (sx + 12, sy), (sx + 22, sy) };
        foreach (var (x, y) in stopCells)
        {
            var p = _use(stopTool, new Pointer(x, y, 0.5f, 0.1f));
            Check($"bus stop at {x},{y}", p is { Ok: true }, p?.Text ?? "no plan");
        }
        yield return new GateWait("the stops", () => stopCells.All(s => c.StopsAt(s.Item1, s.Item2).Any()), 30);

        // The depot on the block south of the street, cleared first with the bulldozer.
        var clear = _use(new ToolState { Kind = ToolKind.Bulldoze }, new Pointer(sx + 4, sy + 4, 0.5f, 0.5f, (sx + 1, sy + 1)));
        Check("bulldoze the depot's block", clear is { Ok: true }, clear?.Text ?? "no plan");
        yield return new GateWait("the block cleared", () => Enumerable.Range(0, 16).All(k => c.BuildingAtCell(sx + 1 + k % 4, sy + 1 + k / 4) == null), 30);
        var depot = _use(new ToolState { Kind = ToolKind.Building, BuildingTypeId = "bus-depot" }, new Pointer(sx + 2, sy + 1));
        Check("bus depot", depot is { Ok: true }, depot?.Text ?? "no plan");
        yield return new GateWait("the depot", () => Enumerable.Range(0, 16).Any(k => c.BuildingAtCell(sx + 1 + k % 4, sy + 1 + k / 4) is { TypeId: "bus-depot" }), 30);

        // The bus company, in its own view: take it, lease the lots, draw a line, buy buses.
        Phase = "company";
        Speed(3);  // the company's view comes with each day
        yield return new GateWait("the company's view", () => Co.View?.OpenLots.Count(l => l.Kind is "bus-stop" or "bus-depot") >= 4, 30);
        _panel.Open("buses");
        _panel.Click("co-open");
        Check("company view open", Co.Active, "");
        if (!Co.Mine) _panel.Click("co-claim");
        yield return new GateWait("the company claimed", () => Co.Mine, 30);
        _panel.Click("co-tool:Lease");
        var mine = Co.View!.OpenLots.Where(l => l.Kind == "bus-stop" && stopCells.Contains((l.X, l.Y)) || l.Kind == "bus-depot").ToList();
        foreach (var l in mine) Co.MapClick(l.X, l.Y);
        yield return new GateWait("the leases", () => Co.View!.Lots.Count(l => l.Kind == "bus-stop") >= 3 && Co.View.Lots.Any(l => l.Kind == "bus-depot"), 30);
        _panel.Click("co-tool:Line");
        var stops = stopCells.Select(s => Co.View!.Lots.First(l => l.Kind == "bus-stop" && (l.X, l.Y) == s)).ToList();
        foreach (var s in stops) Co.MapClick(s.X, s.Y);
        Check("line drawn through the stops", Co.Draft is { } d && d.Stops.SequenceEqual(stops.Select(s => s.LeaseId)), string.Join(", ", Co.Draft?.Stops ?? new()));
        _panel.SetInput("Gate Line");
        _panel.Click("co-save");
        yield return new GateWait("the line", () => Co.View!.Lines.Any(l => l.Name == "Gate Line"), 30);
        int lineId = Co.View!.Lines.First(l => l.Name == "Gate Line").Id;
        _panel.Click($"co-buses:{lineId}:4");
        yield return new GateWait("the buses", () => Co.View!.Lines.First(l => l.Id == lineId) is { Buses: 4, HeadwayMinutes: > 0 }, 30);
        var line = Co.View!.Lines.First(l => l.Id == lineId);
        Check("line running", line.Problem == "" && line.HeadwayMinutes > 0, $"{line.Buses} buses every {line.HeadwayMinutes} min, a round trip {line.RoundTripMinutes} min");
        _panel.Click("co-city");
        Check("back to the city", !Co.Active, "");
        var depotLot = Co.View!.Lots.First(l => l.Kind == "bus-depot");
        _drivers = () => c.Buildings.GetValueOrDefault(depotLot.BuildingId)?.Workers ?? 0;
        _stopRiders = () => stopCells.Sum(s => c.StopsAt(s.Item1, s.Item2).Sum(b => b.Riders));

        // The city seat to the computer, on each level in turn.
        long away = c.Stats.Tick;
        foreach (var level in new[] { SeatLevel.SeatStasis, SeatLevel.SeatCaretaker, SeatLevel.SeatAutopilot })
        {
            string name = level.ToString()[4..].ToLowerInvariant();
            Phase = name;
            _panel.OpenDialog("seat");
            _panel.Click($"level:{(int)level}");
            _panel.Click("seat-apply");
            yield return Wait($"the seat on {name}", () => _panel.Mayor?.Level == level, 30);
            long funds = c.Stats.Funds;
            int pop = c.Stats.Population;
            if (level == SeatLevel.SeatStasis)
            {
                yield return Days("stasis", 10);
                Check("stasis holds funds and population", c.Stats.Funds == funds && c.Stats.Population == pop,
                    $"funds ${funds:N0} → ${c.Stats.Funds:N0}, population {pop:N0} → {c.Stats.Population:N0}");
                continue;
            }
            // A month's turn, so the leases are billed.
            int month = c.Stats.Month;
            yield return Wait($"a new month on {name}", () => c.Stats.Month != month, 70);
            yield return Days(name, 2);
            var lease = c.Stats.Budget.FirstOrDefault(b => b.Label.StartsWith("Leases:"));
            Check($"{name} bills the leases", lease is { Amount: > 0 } && Co.View!.LastMonth.Rent > 0, $"{lease?.Label ?? "no lease line"} ${lease?.Amount ?? 0:N0}, the company paid ${Co.View!.LastMonth.Rent:N0}");
            if (level == SeatLevel.SeatAutopilot)
                Check("autopilot keeps the city running", c.Stats.Population > 0 && _panel.Mayor?.Level == level, $"population {pop:N0} → {c.Stats.Population:N0}, funds ${funds:N0} → ${c.Stats.Funds:N0}");
        }

        // City to company and company to city.
        Phase = "results";
        line = Co.View!.Lines.First(l => l.Id == lineId);
        Check("commuters ride the line", line.Riders > 0, $"{line.Riders:N0} riders yesterday, {line.LoadPercent}% of seats at the peak");
        Check("fares come in", Co.View.LastMonth.Fares > 0 || Co.View.ThisMonth.Fares > 0, $"${Co.View.LastMonth.Fares:N0} last month, ${Co.View.ThisMonth.Fares:N0} this month");
        var depotNow = c.Buildings.GetValueOrDefault(Co.View.Lots.First(l => l.Kind == "bus-depot").BuildingId);
        Check("drivers work at the depot", _mostDrivers > 0, $"up to {_mostDrivers} drivers{(depotNow is { Powered: false } ? " (the depot has no power)" : "")}"
            + (_mostDrivers > 0 ? "" : $"; now {depotNow?.Workers ?? 0} of {depotNow?.Jobs ?? 0} jobs filled, {depotNow?.ConstructionDaysLeft ?? 0} building days left, {c.Stats.Tick - away} days since the line opened"));
        Check("riders through the city's stops", _mostStopRiders > 0, $"up to {_mostStopRiders:N0} a day at the three stops");

        // The city back in hand, and the report on the time away.
        Phase = "taking back";
        Speed(0);
        _panel.Click("takeback");
        yield return new GateWait("the away report", () => _panel.Dialog == "report", 30);
        var report = _panel.Mayor?.Report;
        Check("the away report covers the time away", report != null && report.FromTick <= away + 1 && report.ToTick >= c.Stats.Tick - 1,
            report == null ? "no report" : $"days {report.FromTick}–{report.ToTick} (left on day {away}), {report.Changes.Count} changes, {report.Notes.Count} notes");
        Phase = "done";
    }

    /// <summary>
    /// A street near the middle running at least 24 cells east from a crossing, with a built block south of its
    /// start for the depot: the crossing's cell.
    /// </summary>
    (int X, int Y)? FindSite()
    {
        var c = City;
        int cx = c.Width / 2 / 5 * 5, cy = c.Height / 2 / 5 * 5;
        var candidates = new List<(int X, int Y)>();
        for (int y = 5; y + 6 < c.Height; y += 5)
            for (int x = 5; x + 26 < c.Width; x += 5) candidates.Add((x, y));
        foreach (var (x, y) in candidates.OrderBy(p => Math.Abs(p.X + 12 - cx) + Math.Abs(p.Y - cy)))
        {
            if (!Enumerable.Range(0, 25).All(k => c.Paths[c.Idx(x + k, y)] != 0)) continue;
            if (!Enumerable.Range(0, 16).All(k => !c.IsWater(c.Idx(x + 1 + k % 4, y + 1 + k / 4)) && c.Paths[c.Idx(x + 1 + k % 4, y + 1 + k / 4)] == 0)) continue;
            if (c.BuildingAtCell(x + 1, y + 1) is not { } b || c.TypeById.GetValueOrDefault(b.TypeId)?.Category != "zoned") continue;
            return (x, y);
        }
        return null;
    }
}
