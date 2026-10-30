using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Gate;

/// <summary>
/// The Beta 2 gate: four players on one dedicated server playing a live session through the client's own panels
/// and tools, as people would. Mayor May runs the city (the Godot window, or a test's client), Tess the bus company,
/// Dan the emergency services and Sam watches. May builds stops, a depot and stations and leases the stations to
/// the emergency services; Tess leases the stops and depot, draws a line and buys buses; Dan answers the city's
/// calls himself and runs the scenes they open; May offers Tess cheaper rent on the depot and Tess accepts; Sam
/// sees the routes but not the books, and is refused when he tries to set a tax. At the end Tess de-links: she
/// leaves with a copy of the city, and the city carries on without her.
///
/// The steps are an iterator of waits, as in <see cref="GateRun"/>. The host polls the mayor's client and calls
/// <see cref="Poll"/> every frame, which polls the other three and lets Dan's scripted hands do their work.
/// </summary>
public sealed class TogetherRun : IDisposable
{
    /// <summary>One of the four players: their client, their panels and what the server told them alone.</summary>
    public sealed class Player
    {
        public readonly string Name;
        public readonly CityClient Client;
        public readonly PanelModel Panel;
        public readonly List<string> Notices = new();
        public Presence? Presence;
        public (string Name, byte[] Save)? Copy;
        public CityMirror City => Client.City;
        public CompanyModel Co => Panel.Company;
        public EmergencyModel Em => Panel.Emergency;

        public Player(string name, CityClient client, PanelModel panel)
        {
            (Name, Client, Panel) = (name, client, panel);
            client.Message += m =>
            {
                if (m.BodyCase == ServerMessage.BodyOneofCase.Delta) Notices.AddRange(m.Delta.Notices.Select(n => n.Text));
                if (m.BodyCase == ServerMessage.BodyOneofCase.Presence) Presence = m.Presence;
            };
            panel.CopyArrived += (n, s) => Copy = (n, s);
        }

        public SeatState? Seat(string id) => Panel.Civic?.Seats.FirstOrDefault(s => s.Id == id);
    }

    public const string Password = "secret1";
    public const string Transport = "transport";

    readonly Func<ToolState, Pointer, Preview?> _use;
    readonly List<Player> _others = new();
    public readonly Player May;
    public Player Tess { get; private set; } = null!;
    public Player Dan { get; private set; } = null!;
    public Player Sam { get; private set; } = null!;
    public IEnumerable<Player> Everyone => new[] { May }.Concat(_others);

    /// <summary>What the run is doing now.</summary>
    public string Phase { get; private set; } = "starting";
    /// <summary>Each check made, in order, and what was seen.</summary>
    public readonly List<(string Check, bool Ok, string Seen)> Checks = new();
    /// <summary>Where the stops and stations went, for the camera.</summary>
    public (int X, int Y) Focus { get; private set; }
    /// <summary>
    /// A moment worth a picture, from the mayor's screen: the host may return a wait that ends once it has its
    /// picture (the dialogs stay as they are until then), or null.
    /// </summary>
    public Func<string, GateWait?> Moment { get; set; } = _ => null;

    readonly Uri _server;

    /// <param name="mayor">The mayor's client, signing in as <paramref name="mayorName"/> with <see cref="Password"/> (the server's first account).</param>
    /// <param name="use">Uses a tool at a pointer for the mayor, as a click (or a drag's release) would, returning what it planned.</param>
    public TogetherRun(Uri server, string mayorName, CityClient mayor, PanelModel mayorPanel, Func<ToolState, Pointer, Preview?> use)
    {
        _server = server;
        _use = use;
        May = new Player(mayorName, mayor, mayorPanel);
    }

    void Check(string check, bool ok, string seen)
    {
        Checks.Add((check, ok, seen));
        if (!ok) throw new GateFailed($"{check}: {seen}");
    }

    // Dan's hands: confirm the computer's picks for each call, run each scene for a while, then hand it to the crews.
    bool _danPlays;
    readonly HashSet<int> _sent = new(), _handed = new();
    int _sceneOpen, _firstCall;
    double _sceneClock;
    public int CallsSent => _sent.Count;
    public int ScenesRun => _handed.Count;
    /// <summary>How long Dan runs a scene before handing it to the crews, in seconds.</summary>
    public const double SceneSeconds = 1.5;

    /// <summary>Polls the other three players' clients and runs Dan's hands; the host polls the mayor's.</summary>
    public void Poll(double dt)
    {
        foreach (var p in _others)
        {
            p.Client.Poll();
            p.Panel.Tick(dt);
        }
        if (!_danPlays || Dan.Em.View is not { } v || !Dan.Em.Mine) return;
        foreach (var c in v.Calls.Where(c => c.Status == "proposed" && _sent.Add(c.Id)))
            Dan.Panel.Click($"em-send:{c.Id}");
        // One scene at a time: the one open on his board, or the next one waiting for him.
        if ((Dan.Em.Scene is not { } s || _handed.Contains(s.Id)) && v.Scenes.FirstOrDefault(x => !_handed.Contains(x.Id)) is { } next)
            Dan.Panel.Click($"em-watch:{next.Id}");
        if (Dan.Em.Scene is { } run && !_handed.Contains(run.Id))
        {
            if (_sceneOpen != run.Id) (_sceneOpen, _sceneClock) = (run.Id, 0);
            _sceneClock += dt;
            if (_sceneClock > SceneSeconds && _handed.Add(run.Id)) Dan.Panel.Click("em-leave");
        }
    }

    void Speed(int speed) => May.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = speed } });

    GateWait Days(string what, int days)
    {
        long until = May.City.Stats.Tick + days;
        return new GateWait(what, () => May.City.Stats.Tick >= until, days * 2 + 30);
    }

    Player Join(string name)
    {
        var client = new CityClient(_server, "toycity-gate");
        var panel = new PanelModel(client.City, client.Connection.Send) { Me = () => client.Connection.Player, OnServer = () => true };
        client.Message += panel.Handle;
        var p = new Player(name, client, panel);
        _others.Add(p);
        return p;
    }

    /// <summary>Signs a player in, making the account the first time.</summary>
    IEnumerable<GateWait> SignIn(Player p)
    {
        var c = p.Client.Connection;
        yield return new GateWait($"the welcome for {p.Name}", () => c.SignInNeeded, 30);
        c.SignIn(p.Name, Password, create: true);
        yield return new GateWait($"an answer for {p.Name}", () => c.Player.Length > 0 || c.SignInNeeded, 30);
        if (c.SignInNeeded) c.SignIn(p.Name, Password);
        yield return new GateWait($"{p.Name} in the city", () => c.Player == p.Name && p.City.Loaded && p.Panel.Civic != null, 60);
    }

    public IEnumerable<GateWait> Play()
    {
        // Everyone in: the mayor first, as the server's first account.
        Phase = "signing in";
        yield return new GateWait("the mayor signed in", () => May.Client.Connection.Player == May.Name && May.City.Loaded && May.Panel.Civic != null, 60);
        Check("the first account is the mayor", May.Panel.Mayor?.Owner == May.Name, $"mayor: {May.Panel.Mayor?.Owner}");
        // The town grows while the others arrive.
        Speed(3);
        Tess = Join("Tess");
        Dan = Join("Dan");
        Sam = Join("Sam");
        foreach (var p in _others)
            foreach (var w in SignIn(p)) yield return w;
        yield return new GateWait("four players in the presence", () => Everyone.All(p => p.Presence?.Players.Count(x => x.Online) == 4), 30);
        Check("four players on the server", true, string.Join(", ", May.Presence!.Players.Select(p => p.Name)));
        yield return new GateWait("the town to grow", () => May.City.Stats.Tick >= GrowDays, GrowDays * 2 + 30);
        Speed(0);

        // Tess and Dan take their seats from the seat dialog; Sam watches.
        Phase = "seats";
        Tess.Panel.OpenDialog("seat");
        Tess.Panel.Click($"claim:{Transport}");
        Dan.Panel.OpenDialog("seat");
        Dan.Panel.Click($"claim:{EmergencyModel.Game}");
        yield return new GateWait("the seats claimed", () => May.Seat(Transport)?.Owner == "Tess" && May.Seat(EmergencyModel.Game)?.Owner == "Dan", 30);
        yield return new GateWait("the seats in the presence", () => Sam.Presence!.Players.First(p => p.Name == "Tess").Seats.Contains(Transport)
            && Sam.Presence.Players.First(p => p.Name == "Dan").Seats.Contains(EmergencyModel.Game), 30);
        var samHere = Sam.Presence!.Players.First(p => p.Name == "Sam");
        Check("seats from the seat dialog", true, $"Tess runs {May.Seat(Transport)!.Name}, Dan runs {May.Seat(EmergencyModel.Game)!.Name}");
        Check("Sam watches", samHere.Spectator && samHere.Seats.Count == 0, samHere.Spectator ? "spectator, no seats" : "has a seat");
        May.Panel.OpenDialog("seat");
        if (Moment("four players") is { } m1) yield return m1;
        May.Panel.CloseDialog();

        // The mayor builds stops along a street, a depot and two stations off the east road, with the tools.
        Phase = "building";
        foreach (var w in Build()) yield return w;

        // The stations to the emergency services, from each building's panel.
        Phase = "leasing stations";
        foreach (var b in _stations)
        {
            May.Panel.Query(b.X, b.Y);
            May.Panel.Click($"lease-to:{EmergencyModel.Game}:{b.Id}");
            Check($"lease terms for the {b.TypeId}", May.Panel.Dialog == "terms", May.Panel.Dialog ?? "no dialog");
            May.Panel.Click("terms:ok");
        }
        yield return new GateWait("the stations leased", () => _stations.All(b => May.Panel.Civic!.Leases.Any(l => l.BuildingId == b.Id && l.Game == EmergencyModel.Game)), 30);
        var terms = May.Panel.Civic!.Leases.Where(l => l.Game == EmergencyModel.Game).Select(l => l.Terms).ToList();
        Check("the stations are leased as public services", terms.All(t => t.Mode == LeaseMode.LeasePublicService),
            string.Join("; ", terms.Select(PanelModel.TermsText)));
        Speed(3);  // a side game's view comes with each day
        yield return new GateWait("Dan's units", () => Dan.Em.View is { } v && Dan.Em.Mine && v.Stations.Count == _stations.Count && v.Units.Count >= 4, 30);
        Check("the stations come with units", true, $"{Dan.Em.View!.Stations.Count} stations, {Dan.Em.View.Units.Count} units");
        May.Panel.Query(_stations[0].X, _stations[0].Y);
        if (Moment("station leased") is { } m2) yield return m2;

        // Dan: scene command, picking the responders himself.
        Phase = "dispatch";
        Dan.Panel.OpenDialog("dispatch");
        Dan.Panel.Click("em-tab:settings");
        Dan.Panel.Click("em-style:1");
        yield return new GateWait("scene command", () => Dan.Em.View!.Style == Emergency.Wire.CommandStyle.SceneCommand, 30);
        if (!Dan.Em.View!.PickResponders) Dan.Panel.Click("em-switch:pick");
        yield return new GateWait("Dan picks responders", () => Dan.Em.View!.PickResponders, 30);
        // Two patrols won't keep up with the town's crime: Dan buys two more from the Units tab.
        Dan.Panel.Click("em-tab:units");
        var police = Dan.Em.View!.Stations.First(st => st.Kind == "police-station");
        int patrols = Dan.Em.View.Units.Count(u => u.Kind == Emergency.Wire.UnitKind.Patrol);
        for (int k = 0; k < 2; k++) Dan.Panel.Click($"em-buy:{police.LeaseId}:{(int)Emergency.Wire.UnitKind.Patrol}");
        yield return new GateWait("Dan's new patrols", () => Dan.Em.View!.Units.Count(u => u.Kind == Emergency.Wire.UnitKind.Patrol) == patrols + 2, 30);
        Check("Dan buys two more patrols", true, $"{patrols + 2} patrols, ${Dan.Em.View!.Cash:N0} left");
        Dan.Panel.Click("em-tab:calls");
        _firstCall = Dan.Em.View!.Calls.Select(c => c.Id).DefaultIfEmpty(0).Max() + 1;
        _danPlays = true;

        // Tess: lease the stops and the depot, draw a line through the stops, buy buses.
        Phase = "company";
        foreach (var w in RunTheBuses()) yield return w;
        May.Panel.Click("routes");
        if (Moment("the line") is { } m3) yield return m3;

        // The city runs: commuters ride, calls come in and Dan answers them.
        Phase = "live";
        yield return new GateWait("a call Dan sends units to", () => CallsSent > 0, 150);
        Check("Dan sends units to the city's calls", true, $"{CallsSent} call(s) so far");
        May.Panel.OpenDialog("dispatch");
        if (Moment("dispatch board") is { } m4) yield return m4;
        May.Panel.CloseDialog();
        yield return new GateWait("a scene run and handed to the crews", () => ScenesRun > 0 && Dan.Em.View!.Cards.Count > 0, 240);
        var card = Dan.Em.View!.Cards[0];
        Check("Dan runs a scene and gets a report card", true, $"{card.Where}: {card.Score} ({card.Outcome}), {ScenesRun} scene(s)");
        yield return new GateWait("riders on Tess's line", () => Tess.Co.View!.Lines.First(l => l.Id == _lineId).Riders > 0, 120);
        var line = Tess.Co.View!.Lines.First(l => l.Id == _lineId);
        Check("commuters ride Tess's line", true, $"{line.Riders:N0} riders yesterday, {line.LoadPercent}% of seats at the peak");
        // Since Dan took the calls; the board keeps the last few dozen.
        var calls = Dan.Em.View!.Calls.Where(c => c.Id >= _firstCall).ToList();
        Check("the city's calls since Dan took them", ScenesRun > 0, $"{calls.Count(c => c.Status == "done" && c.UnitIds.Count > 0)} answered, "
            + $"{calls.Count(c => c.Status == "done" && c.UnitIds.Count == 0)} let go, {calls.Count(c => c.Status != "done")} open; {ScenesRun} scene(s) run by Dan");

        // Sam, watching: the routes but not the books, and no say in the city.
        Phase = "watching";
        yield return new GateWait("the line on Sam's map", () => Sam.Co.View?.Lines.Any(l => l.Name == LineName) == true, 30);
        var pub = Sam.Co.View!;
        Check("Sam sees the routes, not the books", Sam.Co.Public && pub.Cash == 0 && pub.Log.Count == 0,
            $"{pub.Lines.Count} line(s), cash shown ${pub.Cash:N0}, {pub.Log.Count} log lines");
        Sam.Client.Connection.Act(new PAction { Tax = new SetTax { Zone = ZoneKind.ZoneResidential, Wealth = Wealth.Low, PercentTenths = 150 } });
        yield return new GateWait("Sam's refusal", () => Sam.Notices.Any(n => n.Contains("Only the mayor")), 30);
        int maySaw = May.Notices.Count(n => n.Contains("Only the mayor"));
        Check("Sam can't set taxes, and only Sam hears so", maySaw == 0, Sam.Notices.First(n => n.Contains("Only the mayor")));

        // A dispute's first rung: May offers Tess cheaper rent on the depot, and Tess accepts.
        Phase = "negotiating";
        var depotLease = May.Panel.Civic!.Leases.First(l => l.BuildingId == _depot!.Id);
        long rent = depotLease.Terms.RentPerMonth;
        May.Panel.Query(_depot!.X, _depot.Y);
        May.Panel.Click($"offer-make:{depotLease.Id}:{(int)OfferKind.OfferTerms}");
        Check("the offer's terms", May.Panel.Dialog == "terms", May.Panel.Dialog ?? "no dialog");
        May.Panel.Click("terms:rent:-100");
        May.Panel.Click("terms:ok");
        yield return new GateWait("the offer to Tess", () => OpenOffer(Tess, depotLease.Id) != null, 30);
        var offer = OpenOffer(Tess, depotLease.Id)!;
        Tess.Panel.Click($"offer-answer:{offer.Id}:1");
        yield return new GateWait("the new rent", () => May.Panel.Civic!.Leases.First(l => l.Id == depotLease.Id).Terms.RentPerMonth == rent - 100, 30);
        Check("Tess accepts May's offer", true, $"depot rent ${rent:N0} → ${rent - 100:N0} a month: {offer.Text}");
        May.Panel.Query(_depot.X, _depot.Y);
        if (Moment("offer accepted") is { } m5) yield return m5;
        if (Moment("free camera") is { } m6) yield return m6;

        // Everyone sees the same city.
        Phase = "same city";
        Speed(0);
        foreach (var w in SameCity("before the de-link")) yield return w;

        // Tess de-links: she leaves with her copy, and the city carries on.
        Phase = "de-link";
        Speed(3);
        Tess.Panel.OpenDialog("seat");
        bool canLeave = Tess.Panel.Render().Contains("[url=delink:Tess]");
        Check("Tess may leave", canLeave, canLeave ? "\"Leave with a copy\" in her seat dialog" : "no leave link in her seat dialog");
        Tess.Panel.Click("delink:Tess");
        Check("asked to confirm", Tess.Panel.Dialog == "confirm", Tess.Panel.Dialog ?? "no dialog");
        Tess.Panel.Click("yes");
        yield return new GateWait("Tess's copy", () => Tess.Copy != null, 60);
        Check("Tess leaves with a copy", Tess.Copy!.Value.Save.Length > 1000 && Tess.Copy.Value.Name.Contains("Tess"), $"\"{Tess.Copy.Value.Name}\", {Tess.Copy.Value.Save.Length / 1024:N0} KB");
        yield return new GateWait("the de-link in the city", () => May.Panel.Civic!.Delinks.Count == 1 && May.Seat(Transport)?.Owner == "", 30);
        var leases = May.Panel.Civic!.Leases.Count(l => l.Game == Transport);
        Check("the company goes back to the computer, its leases ended", leases == 0 && May.Seat(Transport)!.Level == SeatLevel.SeatCaretaker,
            $"{leases} transport leases, the seat on {May.Seat(Transport)!.Level.ToString()[4..].ToLowerInvariant()}");
        Check("Dan keeps his seat", May.Seat(EmergencyModel.Game)?.Owner == "Dan", May.Seat(EmergencyModel.Game)?.Owner ?? "");
        yield return new GateWait("Sam sees Tess leave", () => Sam.Panel.Civic!.Delinks.Count == 1, 30);
        May.Panel.OpenDialog("seat");
        bool says = May.Panel.Render().Contains("Tess left");
        Check("the mayor's seat dialog says so", says, says ? "\"Tess left\" under past de-links" : "no \"Tess left\"");
        if (Moment("Tess left") is { } m7) yield return m7;
        Tess.Client.Connection.Act(new PAction { Seat = new SetSeat { Seat = Transport, Owner = "Tess", Level = SeatLevel.SeatManual, SpendCap = 25000 } });
        yield return new GateWait("Tess's refusal", () => Tess.Notices.Any(n => n.Contains("can share a city again")), 30);
        yield return Days("a day after", 1);
        string owner = May.Seat(Transport)?.Owner ?? "";
        Check("Tess can't take the company straight back", owner == "", owner == "" ? $"refused: {Tess.Notices.Last(n => n.Contains("can share a city again"))}" : $"{owner} has it");

        Phase = "after";
        Speed(0);
        foreach (var w in SameCity("after the de-link")) yield return w;
        May.Panel.CloseDialog();
        Phase = "done";
    }

    static OfferState? OpenOffer(Player p, int leaseId) =>
        p.Panel.Civic?.Leases.FirstOrDefault(l => l.Id == leaseId)?.Offers.FirstOrDefault(o => o.Status == "open");

    /// <summary>Waits for every client to reach the same day, then checks they agree on the city.</summary>
    IEnumerable<GateWait> SameCity(string when)
    {
        yield return new GateWait($"everyone on the same day {when}", () =>
        {
            long day = May.City.Stats.Tick;
            return Everyone.All(p => p.City.Stats.Tick == day);
        }, 30);
        string Of(Player p) => $"day {p.City.Stats.Tick}, ${p.City.Stats.Funds:N0}, {p.City.Stats.Population:N0} people, {p.City.Buildings.Count} buildings";
        var seen = Everyone.Select(Of).Distinct().ToList();
        Check($"all four see the same city {when}", seen.Count == 1, string.Join(" / ", seen));
    }

    readonly List<BuildingState> _stations = new();
    BuildingState? _depot;
    (int X, int Y)[] _stopCells = Array.Empty<(int, int)>();
    int _lineId;
    public const string LineName = "Together Line";
    /// <summary>The town's age before the mayor starts building.</summary>
    public const int GrowDays = 120;

    IEnumerable<GateWait> Build()
    {
        var c = May.City;
        // The starter town: a road grid 30 cells across with the coal plant beside its east road.
        var plant = c.Buildings.Values.FirstOrDefault(b => b.TypeId == "coal-plant") ?? throw new GateFailed("no coal plant: not the starter town");
        int ox = plant.X - 31, oy = plant.Y - 1;
        int street = Enumerable.Range(1, 5).Select(k => oy + 5 * k).First(y => Enumerable.Range(0, 30).All(k => c.Paths[c.Idx(ox + k, y)] != 0));
        _stopCells = new[] { (ox + 3, street), (ox + 13, street), (ox + 23, street) };
        Focus = (ox + 15, street);
        var stop = new ToolState { Kind = ToolKind.Building, BuildingTypeId = "bus-stop" };
        foreach (var (x, y) in _stopCells)
        {
            var p = _use(stop, new Pointer(x, y, 0.5f, 0.1f));
            Check($"bus stop at {x},{y}", p is { Ok: true }, p?.Text ?? "no plan");
        }
        yield return new GateWait("the stops", () => _stopCells.All(s => c.StopsAt(s.Item1, s.Item2).Any()), 30);

        // Off the east road: the first free places the planner allows, top to bottom.
        foreach (var type in new[] { "bus-depot", "fire-station", "police-station" })
        {
            var tool = new ToolState { Kind = ToolKind.Building, BuildingTypeId = type };
            var at = Enumerable.Range(oy + 5, 30).Select(y => new Pointer(ox + 31, y))
                .FirstOrDefault(p => Planner.Plan(c, tool, p) is { Ok: true }, new Pointer(-1, -1));
            Check($"a place for the {type}", at.X >= 0, at.X >= 0 ? $"{at.X},{at.Y}" : "nowhere off the east road");
            var before = c.Buildings.Values.Where(b => b.TypeId == type).Select(b => b.Id).ToHashSet();
            var plan = _use(tool, at);
            Check($"{type} built", plan is { Ok: true }, plan?.Text ?? "no plan");
            BuildingState? New() => c.Buildings.Values.FirstOrDefault(b => b.TypeId == type && !before.Contains(b.Id));
            yield return new GateWait($"the {type}", () => New() != null, 30);
            var built = New()!;
            if (type == "bus-depot") _depot = built;
            else _stations.Add(built);
        }
        if (Moment("stops and stations") is { } m) yield return m;
    }

    IEnumerable<GateWait> RunTheBuses()
    {
        var co = Tess.Co;
        var p = Tess.Panel;
        yield return new GateWait("Tess's company view", () => co.Mine && co.View!.OpenLots.Count(l => l.Kind is "bus-stop" or "bus-depot") >= 4, 30);
        p.Open("buses");
        p.Click("co-open");
        Check("Tess's company view open", co.Active && co.Mine, co.Mine ? $"{co.View!.Name}, ${co.View.Cash:N0} in hand" : "not hers");
        p.Click("co-tool:Lease");
        foreach (var l in co.View!.OpenLots.Where(l => l.Kind == "bus-stop" && _stopCells.Contains((l.X, l.Y)) || l.BuildingId == _depot!.Id).ToList())
            co.MapClick(l.X, l.Y);
        yield return new GateWait("Tess's leases", () => co.View!.Lots.Count(l => l.Kind == "bus-stop") >= 3 && co.View.Lots.Any(l => l.Kind == "bus-depot"), 30);
        p.Click("co-tool:Line");
        var stops = _stopCells.Select(s => co.View!.Lots.First(l => l.Kind == "bus-stop" && (l.X, l.Y) == s)).ToList();
        foreach (var s in stops) co.MapClick(s.X, s.Y);
        Check("Tess draws a line through the stops", co.Draft is { } d && d.Stops.SequenceEqual(stops.Select(s => s.LeaseId)), string.Join(", ", co.Draft?.Stops ?? new()));
        p.SetInput(LineName);
        p.Click("co-save");
        yield return new GateWait("the line", () => co.View!.Lines.Any(l => l.Name == LineName), 30);
        _lineId = co.View!.Lines.First(l => l.Name == LineName).Id;
        p.Click($"co-buses:{_lineId}:4");
        yield return new GateWait("the buses", () => co.View!.Lines.First(l => l.Id == _lineId) is { Buses: 4, HeadwayMinutes: > 0 }, 30);
        var line = co.View!.Lines.First(l => l.Id == _lineId);
        Check("Tess's line running", line.Problem == "", $"{line.Buses} buses every {line.HeadwayMinutes} min, a round trip {line.RoundTripMinutes} min");
        p.Click("co-city");
    }

    public void Dispose()
    {
        foreach (var p in _others) p.Client.Dispose();
    }
}
