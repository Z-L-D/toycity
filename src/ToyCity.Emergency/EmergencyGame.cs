using Google.Protobuf;
using ToyCity.Contract;
using W = ToyCity.Emergency.Wire;

namespace ToyCity.Emergency;

/// <summary>
/// The emergency services: fire engines, police cars and ambulances kept at leased stations, sent to the city's
/// fires, crimes and injuries. The computer dispatches and settles calls by itself when nobody runs the service.
/// A player running it picks a command style: answer every call themselves (Full command), let the computer
/// dispatch and run every scene (Scene command), or let the computer handle everything but the scenes they take
/// (Focus). Each scene played gets a report card, and the cards make the service's respect and its standing with
/// the city. Nothing stops a player doing badly: ignoring calls, sending the wrong crews, driving into people or
/// shooting the wrong person all go ahead, and all have consequences.
/// </summary>
public sealed class EmergencyGame : ISideGame
{
    public const string Id = "emergency";
    public const int StartingCash = 20_000, WagePerCrew = 450, StationStaff = 2;
    public const int SuspendedDays = 30, FailAfterDays = 3;
    /// <summary>A scene nobody has run for a day is finished by the crews, so a player can't hold units for long by walking away.</summary>
    public const int SceneIdleDays = 1;
    public const int GearPrice = 1_500, GearUpkeep = 40;
    /// <summary>What each crooked way brings in: a month's racket in a district, and each call looked away from.</summary>
    public const int RacketPerMonth = 1_500, LookAwayBribe = 400, OvertimePerFire = 500;

    public Manifest Manifest { get; } = new(Id, "City Emergency Services", ContractInfo.Version,
        new[] { "fire-station", "police-station", "clinic", "hospital" },
        "Fire, police and ambulance crews from leased stations, answering the city's fires, crimes and injuries.");

    /// <summary>What each kind of unit costs and carries.</summary>
    static (string Name, int Price, int Resale, int Upkeep, int Crew) Spec(W.UnitKind k) => k switch
    {
        W.UnitKind.Engine => ("Engine", 8_000, 4_000, 300, 4),
        W.UnitKind.Patrol => ("Patrol", 2_500, 1_200, 120, 2),
        W.UnitKind.Swat => ("SWAT", 9_000, 4_500, 350, 4),
        _ => ("Ambulance", 4_000, 2_000, 180, 2),
    };

    /// <summary>The equipment a unit of this kind can carry, with its bit. SWAT teams come armoured.</summary>
    static readonly (string Item, string Name, W.UnitKind Kind, int Bit)[] Gear =
    {
        ("breathing-gear", "breathing gear", W.UnitKind.Engine, 1),
        ("body-armour", "body armour", W.UnitKind.Patrol, 2),
        ("defibrillator", "a defibrillator", W.UnitKind.Ambulance, 4),
    };

    static IEnumerable<string> GearOf(int bits) => Gear.Where(g => (bits & g.Bit) != 0).Select(g => g.Item);

    /// <summary>What a station keeps, how many it holds, and what it starts with.</summary>
    static (W.UnitKind Kind, int Holds, int Starts) StationSpec(string kind) => kind switch
    {
        "fire-station" => (W.UnitKind.Engine, 4, 2),
        "police-station" => (W.UnitKind.Patrol, 6, 2),
        "clinic" => (W.UnitKind.Ambulance, 2, 1),
        _ => (W.UnitKind.Ambulance, 4, 2),
    };

    sealed class Money
    {
        public long Subsidy, Wages, Upkeep, Vehicles;
        public long Profit => Subsidy - Wages - Upkeep - Vehicles;
        public void Write(BinaryWriter w) { w.Write(Subsidy); w.Write(Wages); w.Write(Upkeep); w.Write(Vehicles); }
        public static Money Read(BinaryReader r) => new() { Subsidy = r.ReadInt64(), Wages = r.ReadInt64(), Upkeep = r.ReadInt64(), Vehicles = r.ReadInt64() };
        public W.MonthMoney ToWire() => new() { Subsidy = Subsidy, Wages = Wages, Upkeep = Upkeep, Vehicles = Vehicles, Profit = Profit };
    }

    sealed class Unit
    {
        public int Id, Number, LeaseId, Crew, CallId;
        /// <summary>Equipment bits (<see cref="Gear"/>).</summary>
        public int Gear;
        public W.UnitKind Kind;
        public string State = "ready";
        /// <summary>The tick a unit back from a call is ready again, or a suspended one is back on the road.</summary>
        public long Until;
    }

    sealed class Call
    {
        public int Id, X, Y, Severity, WaitedDays, Minutes, SceneId, District = -1;
        public long IncidentId, Rolled;
        public string Kind = "", Text = "", Status = "waiting", Outcome = "";
        public bool Taken;
        public List<int> Units = new();
    }

    ulong _seed;
    string _name = "City Emergency Services";
    long _cash = StartingCash;
    int _respect, _quality = 50;
    int _nextUnit = 1, _nextCall = 1, _nextScene = 1;
    readonly int[] _numbers = new int[4];
    readonly Settings _settings = new();
    readonly List<Unit> _units = new();
    readonly List<Call> _calls = new();
    readonly List<Scene> _scenes = new();
    readonly List<W.ReportCard> _cards = new();
    readonly List<string> _log = new();
    /// <summary>How the last calls went, 0..100 each: the service quality the city sees.</summary>
    readonly List<int> _results = new();
    /// <summary>Answers for the city not yet sent: scenes that ended between ticks.</summary>
    readonly List<IncidentResponse> _responses = new();
    /// <summary>Stations already given their starting units.</summary>
    readonly SortedSet<int> _equipped = new();
    readonly SortedSet<int> _wanted = new();
    /// <summary>Calls that found no unit of the kind they needed this month, by unit kind: autopilot buys for these.</summary>
    readonly int[] _short = new int[4];
    /// <summary>Crews on duty: three shifts keep every unit staffed around the clock; fewer save wages.</summary>
    int _shifts = 3;
    /// <summary>The first unit's minutes to each call rolled, by the tick it rolled: the response ratings.</summary>
    readonly List<(long Tick, string Kind, int Minutes)> _responseTimes = new();
    // The crooked ways (part 11): each district -1 when off.
    bool _crookedOk;
    string _crookedWhy = "";
    int _slowWalk = -1, _racket = -1, _lookAway = -1, _exposed;
    bool _overtime, _strike;
    /// <summary>What the papers found out, for the next answer the city hears.</summary>
    readonly List<string> _scandals = new();
    Money _this = new(), _last = new();
    long _tick;
    int _day, _month = 1, _year;
    OperatorLevel _operator = OperatorLevel.Caretaker;
    string _owner = "";
    readonly List<LeasedLot> _lots = new();
    readonly List<OpenLot> _open = new();

    public void NewGame(ulong seed) => _seed = seed;

    LeasedLot? Lot(int leaseId) => _lots.FirstOrDefault(l => l.LeaseId == leaseId);
    string UnitName(int id) => _units.FirstOrDefault(u => u.Id == id) is { } u ? $"{Spec(u.Kind).Name} {u.Number}" : "A unit";
    bool Playing => _operator == OperatorLevel.Manual;

    void Log(string text)
    {
        _log.Add($"{Months[(_month + 11) % 12]} {_day}: {text}");
        if (_log.Count > 40) _log.RemoveAt(0);
    }

    static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    // ---------------------------------------------------------------------
    // The day
    // ---------------------------------------------------------------------

    public void Tick(CityToGame input, ICityQueries city, GameToCity output)
    {
        _tick = input.Tick;
        _day = input.Day;
        _month = input.Month;
        _year = input.Year;
        _operator = input.Operator;
        _owner = input.OwnerName ?? "";
        _lots.Clear(); _lots.AddRange(input.Lots);
        _open.Clear(); _open.AddRange(input.OpenLots);
        var crooked = input.Offers.FirstOrDefault(o => o.Kind == "crooked");
        _crookedOk = crooked?.Allowed == true;
        _crookedWhy = crooked == null ? "The city doesn't allow crooked services." : crooked.WhyNot;
        if (!_crookedOk && Crooked) EndCrooked($"Crooked services were switched off: {_crookedWhy}");

        Stations();
        ReturnUnits();
        if (!Playing) foreach (var s in _scenes.ToList()) { s.FinishByCrews(); EndScene(s); }
        foreach (var s in _scenes.Where(s => _tick - s.Touched > SceneIdleDays).ToList())
        {
            Log($"Nobody ran the scene of call {s.CallId} for {SceneIdleDays} days: the crews finished it.");
            s.FinishByCrews();
            EndScene(s);
        }
        if (_operator != OperatorLevel.Stasis)
        {
            HearIncidents(input, city);
            Settle(city);
            Age();
            Dispatch(city);
        }
        if (_strike) _respect = Math.Clamp(_respect - 2, -100, 100);
        if (input.NewMonth) CloseMonth(input, output);
        if (_operator is OperatorLevel.Caretaker or OperatorLevel.Autopilot) PlayItself(input);
        Publish(output);
    }

    /// <summary>New stations get their starting units; units at a station the service no longer leases are sold.</summary>
    void Stations()
    {
        foreach (var lot in _lots)
        {
            if (!_equipped.Add(lot.LeaseId)) continue;
            var spec = StationSpec(lot.Kind);
            for (int i = 0; i < spec.Starts; i++) AddUnit(spec.Kind, lot.LeaseId);
            Log($"The {Describe(lot.Kind)} at {lot.X},{lot.Y} opened with {spec.Starts} {Spec(spec.Kind).Name.ToLowerInvariant()}{(spec.Starts == 1 ? "" : "s")}.");
        }
        _equipped.RemoveWhere(id => Lot(id) == null);
        foreach (var u in _units.Where(u => Lot(u.LeaseId) == null && u.State != "at scene").ToList())
        {
            _units.Remove(u);
            _cash += Spec(u.Kind).Resale;
            foreach (var c in _calls.Where(c => c.Units.Contains(u.Id))) c.Units.Remove(u.Id);
            Log($"Sold {Spec(u.Kind).Name} {u.Number}: its station closed.");
        }
    }

    static string Describe(string kind) => kind.Replace('-', ' ');

    Unit AddUnit(W.UnitKind kind, int leaseId)
    {
        var u = new Unit { Id = _nextUnit++, Number = ++_numbers[(int)kind], Kind = kind, LeaseId = leaseId, Crew = Spec(kind).Crew };
        _units.Add(u);
        return u;
    }

    void ReturnUnits()
    {
        foreach (var u in _units)
        {
            if (u.State is "on call" && u.CallId == 0 && u.Until <= _tick) u.State = _strike ? "on strike" : "ready";
            if (u.State == "suspended" && u.Until <= _tick) { u.State = "ready"; Log($"{UnitName(u.Id)} is back on the road."); }
        }
    }

    void HearIncidents(CityToGame input, ICityQueries city)
    {
        foreach (var inc in input.Incidents)
        {
            // The city's emergencies, and a transit breakdown that hurt people (a call for the ambulance crews).
            bool hurt = inc.Kind == "breakdown" && inc.Severity >= 2;
            if (inc.X < 0 || !(hurt || inc.Source == "city" && inc.Kind is "fire" or "crime" or "injury")) continue;
            if (_calls.Count(c => c.Status != "done") >= 30) break;
            var call = new Call
            {
                Id = _nextCall++, IncidentId = inc.Id, Kind = hurt ? "injury" : inc.Kind, X = inc.X, Y = inc.Y, Severity = inc.Severity,
                Text = hurt ? $"Passengers hurt: {inc.Cause}" : inc.Kind switch
                {
                    "fire" => $"Fire at a {inc.Cause.ToLowerInvariant()}",
                    "crime" => $"Report of {inc.Cause}",
                    _ => $"Injury: {inc.Cause}",
                },
            };
            call.District = city.DistrictOf(inc.Y * city.Width + inc.X);
            _calls.Add(call);
            if (call.Kind == "crime" && call.District == _lookAway)
            {
                // Looked away from, for a consideration.
                _cash += LookAwayBribe;
                _respect = Math.Clamp(_respect - 3, -100, 100);
                Finish(call, "escaped", $"{call.Text}: no officer came.", 0);
                Expose(8, $"{_name} officers were paid to look the other way in district {call.District + 1}.");
            }
        }
    }

    /// <summary>Who dispatches: the computer, unless a player in full command answers every call themselves.</summary>
    bool ComputerDispatches => !Playing || _settings.Style != W.CommandStyle.FullCommand;

    /// <summary>Whether a call rolled now is played at the scene rather than settled by the computer.</summary>
    bool Scenes(Call c) => Playing && (_settings.Style != W.CommandStyle.Focus || c.Taken);

    /// <summary>The computer's picks for a call: the nearest ready units of the kind it needs, then help if there is some.</summary>
    List<Unit> Pick(Call c)
    {
        var (need, count, help) = c.Kind switch
        {
            "fire" => (W.UnitKind.Engine, c.Severity >= 3 ? 2 : 1, c.Severity >= 2),
            "crime" => (W.UnitKind.Patrol, c.Severity >= 3 ? 2 : 1, c.Severity >= 2),
            _ => (W.UnitKind.Ambulance, 1, false),
        };
        var ready = _units.Where(u => u.State == "ready" && OnDuty(u) && Lot(u.LeaseId) != null)
            .OrderBy(u => Distance(u, c)).ThenBy(u => u.Id).ToList();
        var pick = ready.Where(u => u.Kind == need).Take(count).ToList();
        if (pick.Count == 0) return pick;
        if (help && c.Kind != "injury" && ready.FirstOrDefault(u => u.Kind == W.UnitKind.Ambulance) is { } amb) pick.Add(amb);
        // The worst crimes get the SWAT team too, when there is one free.
        if (c.Kind == "crime" && c.Severity >= 3 && ready.FirstOrDefault(u => u.Kind == W.UnitKind.Swat) is { } swat) pick.Add(swat);
        return pick;
    }

    /// <summary>
    /// Whether a unit's crew is on duty today: with fewer than three shifts, each unit is off one day in three or two,
    /// staggered so a station always has someone. Nobody is on duty during a strike.
    /// </summary>
    bool OnDuty(Unit u) => !_strike && (_shifts >= 3 || (int)((_tick + u.Id) % 3) < _shifts);

    int Distance(Unit u, Call c) => Lot(u.LeaseId) is { } lot ? Math.Abs(lot.X - c.X) + Math.Abs(lot.Y - c.Y) : int.MaxValue / 2;

    static W.UnitKind Needed(string kind) => kind switch { "fire" => W.UnitKind.Engine, "crime" => W.UnitKind.Patrol, _ => W.UnitKind.Ambulance };

    void Dispatch(ICityQueries city)
    {
        foreach (var c in _calls.Where(c => c.Status == "proposed").ToList())
            if (c.Rolled < _tick) Roll(c, c.Units.Select(Find).OfType<Unit>().ToList(), city);
        if (!ComputerDispatches) return;
        foreach (var c in _calls.Where(c => c.Status == "waiting").ToList())
        {
            var pick = Pick(c);
            if (pick.Count == 0) { _short[(int)Needed(c.Kind)]++; continue; }
            if (Playing && _settings.PickResponders)
            {
                // The computer's pick waits a day for the player to change it.
                c.Status = "proposed";
                c.Rolled = _tick;
                c.Units = pick.Select(u => u.Id).ToList();
                foreach (var u in pick) { u.State = "proposed"; u.CallId = c.Id; }
                continue;
            }
            Roll(c, pick, city);
        }
    }

    Unit? Find(int id) => _units.FirstOrDefault(u => u.Id == id);

    /// <summary>Units leave for a call: a scene opens for it now, or the computer settles it tomorrow.</summary>
    void Roll(Call c, List<Unit> units, ICityQueries city)
    {
        foreach (var u in _units.Where(u => u.State == "proposed" && u.CallId == c.Id && !units.Contains(u))) { u.State = "ready"; u.CallId = 0; }
        if (units.Count == 0) { c.Status = "waiting"; c.Units.Clear(); return; }
        c.Units = units.Select(u => u.Id).ToList();
        var minutes = units.Select(u => Minutes(u, c, city)).ToList();
        c.Minutes = minutes.Min();
        c.Rolled = _tick;
        _responseTimes.Add((_tick, c.Kind, c.Minutes));
        _responseTimes.RemoveAll(r => r.Tick < _tick - 30);
        if (c.District == _slowWalk) Expose(4, $"{_name} has been slow to answer calls in district {c.District + 1}, and people there have noticed.");
        foreach (var u in units) { u.State = "on call"; u.CallId = c.Id; }
        if (!Scenes(c)) { c.Status = "rolling"; return; }
        OpenScene(c, units, minutes, city);
    }

    void OpenScene(Call c, List<Unit> units, List<int> minutes, ICityQueries city)
    {
        int half = Scene.CellsAcross / 2;
        var area = city.Area(c.X - half, c.Y - half, Scene.CellsAcross, Scene.CellsAcross);
        // Units come in on the side of the scene facing their station.
        var from = Lot(units[0].LeaseId);
        int ex = from == null ? 0 : Math.Clamp((from.X - (c.X - half)) * Scene.TilesPerCell, 0, Scene.Size - 1);
        int ey = from == null ? 0 : Math.Clamp((from.Y - (c.Y - half)) * Scene.TilesPerCell, 0, Scene.Size - 1);
        var s = Scene.Create(_nextScene++, c.Id, c.IncidentId, c.Kind, c.Severity, c.X, c.Y, c.Text, c.Minutes, area, ex, ey,
            city.Hash((int)_tick, c.Id, 0x7363, 7));
        s.Touched = _tick;
        foreach (var u in units) if (u.Gear != 0) s.Gear[u.Id] = u.Gear;
        for (int i = 0; i < units.Count; i++)
        {
            units[i].State = "at scene";
            s.Arriving.Add((units[i].Id, units[i].Kind, units[i].Crew, 8 + 4 * minutes[i]));
        }
        _scenes.Add(s);
        c.Status = "scene";
        c.SceneId = s.Id;
    }

    /// <summary>How long a unit takes to reach a call: along the streets from its station, plus getting out of the door.</summary>
    int Minutes(Unit u, Call c, ICityQueries city)
    {
        var lot = Lot(u.LeaseId);
        if (lot == null) return 30;
        int road = NearestRoad(c, city);
        var route = lot.AccessCell >= 0 && road >= 0 ? city.Route(lot.AccessCell, road) : null;
        int minutes = 2 + (route?.Minutes ?? 10 + (Math.Abs(lot.X - c.X) + Math.Abs(lot.Y - c.Y)) / 2);
        // A slow-walked district waits.
        return c.District == _slowWalk ? minutes + 12 : minutes;
    }

    static int NearestRoad(Call c, ICityQueries city)
    {
        const int r = 4;
        var area = city.Area(c.X - r, c.Y - r, 2 * r + 1, 2 * r + 1);
        int best = -1, bestD = int.MaxValue;
        for (int i = 0; i < area.Length; i++)
        {
            if (area[i] != CellKind.Road) continue;
            int x = c.X - r + i % (2 * r + 1), y = c.Y - r + i / (2 * r + 1);
            int d = Math.Abs(x - c.X) + Math.Abs(y - c.Y);
            if (d < bestD) { bestD = d; best = y * city.Width + x; }
        }
        return best;
    }

    /// <summary>Calls the computer handled yesterday come back today: how they went depends on who went, how fast and how bad it was.</summary>
    void Settle(ICityQueries city)
    {
        foreach (var c in _calls.Where(c => c.Status == "rolling" && c.Rolled < _tick).ToList())
        {
            var units = c.Units.Select(Find).OfType<Unit>().ToList();
            bool right = units.Any(u => u.Kind == Needed(c.Kind));
            int good = right
                ? 92 - 3 * Math.Max(0, c.Minutes - 6) - 12 * (c.Severity - 1) - 10 * c.WaitedDays + (units.Count > 1 ? 5 : 0) + _respect / 10
                : 10;
            bool ok = (int)(city.Hash((int)_tick, c.Id, 0x656d, 1) % 100) < Math.Clamp(good, 5, 97);
            if (_overtime && c.Kind == "fire" && units.Any(u => u.Kind == W.UnitKind.Engine))
            {
                // Crews on the overtime game let fires run, and claim the hours.
                _cash += OvertimePerFire;
                if (ok && (int)(city.Hash((int)_tick, c.Id, 0x6f74, 2) % 100) < 35) ok = false;
                Expose(6, $"Fires in {_name}'s care keep spreading, and the overtime claims are up. The papers smell arson.");
            }
            string outcome = Good(c.Kind, ok);
            string who = units.Count > 0 ? UnitName(units[0].Id) : "Nobody";
            string text = ok ? "" : c.Kind switch
            {
                "fire" => $"{c.Text} spread before {who} could stop it.",
                "crime" => $"{c.Text}: the suspect got away from {who}.",
                _ => $"{c.Text}: {who} couldn't save them.",
            };
            if (!right && units.Count > 0) text = $"{c.Text}: {_name} sent the wrong crew. " + text;
            Finish(c, outcome, text, ok ? 80 : 20);
            _respect = Math.Clamp(_respect + (ok ? 1 : -2), -100, 100);
        }
    }

    static string Good(string kind, bool ok) => kind switch
    {
        "fire" => ok ? "out" : "spread",
        "crime" => ok ? "arrest" : "escaped",
        _ => ok ? "treated" : "died",
    };

    void Finish(Call c, string outcome, string text, int result)
    {
        c.Status = "done";
        c.Outcome = outcome;
        foreach (var u in c.Units.Select(Find).OfType<Unit>().Where(u => u.CallId == c.Id))
        {
            u.CallId = 0;
            if (u.State != "suspended") { u.State = "on call"; u.Until = _tick + 1; }
        }
        _responses.Add(new IncidentResponse(c.IncidentId, outcome, text));
        _results.Add(result);
        if (_results.Count > 20) _results.RemoveAt(0);
        if (text.Length > 0) Log(text);
        // Keep the last few finished calls for the view.
        var done = _calls.Where(x => x.Status == "done").ToList();
        foreach (var old in done.Take(Math.Max(0, done.Count - 12))) _calls.Remove(old);
    }

    /// <summary>A call nobody answers gets worse, and after a few days it is lost.</summary>
    void Age()
    {
        foreach (var c in _calls.Where(c => c.Status is "waiting").ToList())
        {
            c.WaitedDays++;
            if (c.WaitedDays == 2 && c.Severity < 3) c.Severity++;
            if (c.WaitedDays < FailAfterDays) continue;
            Finish(c, Good(c.Kind, false), $"{c.Text}: nobody came. {_name} let the call go unanswered for {c.WaitedDays} days.", 0);
            _respect = Math.Clamp(_respect - 5, -100, 100);
        }
    }

    void EndScene(Scene s)
    {
        _scenes.Remove(s);
        var card = s.Card();
        _cards.Add(card);
        if (_cards.Count > 8) _cards.RemoveAt(0);
        _respect = Math.Clamp(_respect + Math.Clamp((card.Score - 40) / 5, -20, 10), -100, 100);
        var text = new List<string>();
        foreach (var v in s.Vehicles.Where(v => v.HitSomeone))
        {
            if (Find(v.Unit) is not { } u) continue;
            u.State = "suspended";
            u.Until = _tick + SuspendedDays;
            u.CallId = 0;
            text.Add($"{UnitName(u.Id)} drove into someone at the scene. It is off the road for {SuspendedDays} days while that is looked into, and people are angry.");
        }
        if (card.FriendlyFire > 0) text.Add($"{_name} officers hit their own at the scene.");
        if (card.UnauthorizedForce > 0) text.Add($"{_name} is under investigation for force at the scene.");
        if (card.HarmedByCrews > 0 && text.Count == 0) text.Add($"{_name} crews hurt {card.HarmedByCrews} {(card.HarmedByCrews == 1 ? "person" : "people")} at the scene.");
        if (card.Outcome is "spread" or "escaped" or "died") text.Insert(0, card.Outcome switch
        {
            "spread" => $"{s.Where} spread.",
            "escaped" => $"{s.Where}: the suspect got away.",
            _ => $"{s.Where}: someone died at the scene.",
        });
        Log($"Scene report for {s.Where.ToLowerInvariant()}: {card.Score}.");
        if (_calls.FirstOrDefault(c => c.Id == s.CallId) is { } call) Finish(call, card.Outcome, string.Join(" ", text), Math.Clamp(card.Score, 0, 100));
        else _responses.Add(new IncidentResponse(s.IncidentId, card.Outcome, string.Join(" ", text)));
    }

    /// <summary>The first of the month: wages and upkeep, the city's subsidy and bills, and the books close.</summary>
    void CloseMonth(CityToGame input, GameToCity output)
    {
        if (_racket >= 0)
        {
            _cash += RacketPerMonth;
            _respect = Math.Clamp(_respect - 2, -100, 100);
            Log($"Collected ${RacketPerMonth:N0} from the shops of district {_racket + 1}.");
            Expose(15, $"{_name} has been running a protection racket on the shops of district {_racket + 1}.");
        }
        foreach (var u in _units)
        {
            // Wages for the shifts kept; nobody is paid for a month on strike.
            long wages = _strike ? 0 : (long)u.Crew * WagePerCrew * _shifts / 3, upkeep = Spec(u.Kind).Upkeep + GearUpkeep * GearOf(u.Gear).Count();
            _this.Wages += wages; _this.Upkeep += upkeep;
            _cash -= wages + upkeep;
        }
        foreach (var bill in input.Bills.OrderBy(b => b.LeaseId).ThenBy(b => b.Label))
        {
            if (bill.Amount < 0) { _cash -= bill.Amount; _this.Subsidy -= bill.Amount; continue; }
            long pay = Math.Clamp(_cash, 0, bill.Amount);
            if (pay < bill.Amount) Log($"Couldn't pay the city's {bill.Label.ToLowerInvariant()} in full: ${bill.Amount - pay:N0} short.");
            output.Paid[bill.LeaseId] = output.Paid.GetValueOrDefault(bill.LeaseId) + pay;
            _cash -= pay;
        }
        _last = _this;
        _this = new Money();
        Array.Clear(_short);
    }

    /// <summary>
    /// Caretaker keeps the service solvent. Autopilot also grows it: leases a station of each kind it lacks, and buys
    /// units where calls found nobody to send.
    /// </summary>
    void PlayItself(CityToGame input)
    {
        if (_cash < 0 && _units.Where(u => u.State == "ready" && _units.Count(o => o.LeaseId == u.LeaseId) > 1)
                .OrderByDescending(u => _units.Count(o => o.LeaseId == u.LeaseId)).ThenByDescending(u => u.Id).FirstOrDefault() is { } spare)
        {
            _units.Remove(spare);
            _cash += Spec(spare.Kind).Resale;
            Log($"Sold {Spec(spare.Kind).Name} {spare.Number} to pay the bills.");
        }
        if (_operator != OperatorLevel.Autopilot || !(input.NewMonth || _tick % 30 == 1)) return;
        foreach (var kind in new[] { "fire-station", "police-station", "clinic" })
            if (!_lots.Any(l => l.Kind == kind || kind == "clinic" && l.Kind == "hospital") && _open.FirstOrDefault(o => o.Kind == kind || kind == "clinic" && o.Kind == "hospital") is { } o)
                _wanted.Add(o.BuildingId);
        for (int k = 0; k < 3; k++)
        {
            if (_last.Wages == 0 && _short[k] == 0) continue;
            var kind = (W.UnitKind)k;
            var price = Spec(kind).Price;
            if (_short[k] == 0 || _cash < price + 10_000) continue;
            var lot = _lots.Where(l => StationSpec(l.Kind).Kind == kind && _units.Count(u => u.LeaseId == l.LeaseId) < StationSpec(l.Kind).Holds)
                .OrderBy(l => _units.Count(u => u.LeaseId == l.LeaseId)).ThenBy(l => l.LeaseId).FirstOrDefault();
            if (lot == null) continue;
            var u = AddUnit(kind, lot.LeaseId);
            _cash -= price;
            _this.Vehicles += price;
            Log($"Bought {UnitName(u.Id)}: calls had nobody to send.");
        }
    }

    void Publish(GameToCity output)
    {
        foreach (var lot in _lots)
            output.JobsByLease[lot.LeaseId] = _units.Where(u => u.LeaseId == lot.LeaseId).Sum(u => u.Crew) + StationStaff;
        int avg = _results.Count == 0 ? 50 : (int)_results.Average();
        // Response times count: each kind of call graded A to F over the last thirty days.
        int grades = Ratings().Where(r => r.Grade.Length > 0).Sum(r => r.Grade switch { "A" => 3, "B" => 1, "C" => 0, "D" => -2, _ => -4 });
        _quality = Math.Clamp(avg + _respect / 5 + grades, 0, 100);
        if (_scandals.Count > 0 && _responses.Count > 0)
        {
            var first = _responses[0];
            _responses[0] = first with { Text = (first.Text + " " + string.Join(" ", _scandals)).Trim() };
            _scandals.Clear();
        }
        output.ServiceQuality = _quality;
        output.Responses.AddRange(_responses);
        _responses.Clear();
        output.Holding.AddRange(_scenes.Select(s => s.IncidentId));
        output.LeaseRequests.AddRange(_wanted);
        _wanted.Clear();
        int open = _calls.Count(c => c.Status != "done");
        output.Status = _strike ? $"{_name}: on strike, {open} open call{(open == 1 ? "" : "s")}"
            : _units.Count == 0 ? $"{_name}: no units" : $"{_name}: {_units.Count} units, {open} open call{(open == 1 ? "" : "s")}";
    }

    /// <summary>Response ratings over the last thirty days: the average minutes to each kind of call, graded.</summary>
    List<W.ResponseRating> Ratings() => new[] { "fire", "crime", "injury" }.Select(kind =>
    {
        var times = _responseTimes.Where(r => r.Kind == kind && r.Tick >= _tick - 30).Select(r => r.Minutes).ToList();
        int avg = times.Count == 0 ? 0 : (int)Math.Round(times.Average());
        string grade = times.Count == 0 ? "" : avg <= 6 ? "A" : avg <= 8 ? "B" : avg <= 11 ? "C" : avg <= 15 ? "D" : "F";
        return new W.ResponseRating { Kind = kind, Calls = times.Count, Minutes = avg, Grade = grade };
    }).ToList();

    bool Crooked => _slowWalk >= 0 || _racket >= 0 || _lookAway >= 0 || _overtime || _strike;

    void EndCrooked(string why)
    {
        _slowWalk = _racket = _lookAway = -1;
        _overtime = false;
        if (_strike) foreach (var u in _units.Where(u => u.State == "on strike")) u.State = "ready";
        _strike = false;
        Log(why);
    }

    /// <summary>The papers may find out, a chance in a hundred each time: respect falls and the city hears.</summary>
    void Expose(int percent, string text)
    {
        ulong h = (ulong)_tick * 0x9E3779B97F4A7C15UL ^ (ulong)(_exposed + 1) * 0xC2B2AE3D27D4EB4FUL ^ (ulong)text.Length * 0x165667B19E3779F9UL ^ _seed;
        h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29;
        if ((int)(h % 100) >= percent) return;
        _exposed++;
        _respect = Math.Clamp(_respect - 20, -100, 100);
        _scandals.Add($"Exposed: {text}");
        Log($"The papers have it: {text}");
    }

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    static CommandResult No(string why) => new(false, why);
    static readonly CommandResult Yes = new(true, "");

    public CommandResult Command(string seat, byte[] payload, ICityQueries city)
    {
        W.Command cmd;
        try { cmd = W.Command.Parser.ParseFrom(payload); }
        catch (InvalidProtocolBufferException) { return No("The dispatcher didn't understand that."); }
        switch (cmd.KindCase)
        {
            case W.Command.KindOneofCase.Dispatch:
            {
                var c = _calls.FirstOrDefault(x => x.Id == cmd.Dispatch.CallId);
                if (c == null || c.Status is not ("waiting" or "proposed")) return No("That call has already been answered.");
                var units = cmd.Dispatch.UnitIds.Distinct().Select(Find).ToList();
                if (units.Count == 0) return No("Pick at least one unit to send.");
                if (units.Any(u => u == null || !(u.State == "ready" || u.State == "proposed" && u.CallId == c.Id)))
                    return No("One of those units isn't free.");
                if (units.Any(u => !OnDuty(u!))) return No(_strike ? "The crews are on strike." : "One of those units' crew is off shift today.");
                Roll(c, units!, city);
                if (c.Status == "scene" && _scenes.FirstOrDefault(s => s.Id == c.SceneId) is { } opened) opened.Touched = _tick;
                return Yes;
            }
            case W.Command.KindOneofCase.Take:
            {
                var c = _calls.FirstOrDefault(x => x.Id == cmd.Take.CallId);
                if (c == null || c.Status is "done" or "scene") return No("That call is no longer open.");
                if (!Playing) return No("Take over the service to run a scene.");
                c.Taken = true;
                if (c.Status == "rolling")
                {
                    var units = c.Units.Select(Find).OfType<Unit>().ToList();
                    OpenScene(c, units, units.Select(u => Minutes(u, c, city)).ToList(), city);
                }
                return Yes;
            }
            case W.Command.KindOneofCase.Leave:
            {
                if (FindScene(cmd.Leave.SceneId) is not { } s) return No("That scene has ended.");
                s.FinishByCrews();
                EndScene(s);
                return Yes;
            }
            case W.Command.KindOneofCase.Step:
            {
                if (FindScene(cmd.Step.SceneId) is not { } s) return No("That scene has ended.");
                s.Touched = _tick;
                s.Run(Math.Clamp(cmd.Step.Steps, 1, 40), _settings, UnitName);
                if (s.Step >= Scene.MaxSteps && !s.Done) s.FinishByCrews();
                if (s.Done || s.Step >= Scene.MaxSteps) EndScene(s);
                return Yes;
            }
            case W.Command.KindOneofCase.Move:
            {
                if (FindScene(cmd.Move.SceneId) is not { } s) return No("That scene has ended.");
                s.Touched = _tick;
                foreach (int id in cmd.Move.Ids.Take(32)) s.Move(id, cmd.Move.X, cmd.Move.Y);
                return Yes;
            }
            case W.Command.KindOneofCase.Path:
            {
                if (FindScene(cmd.Path.SceneId) is not { } s) return No("That scene has ended.");
                s.Touched = _tick;
                s.FollowPath(cmd.Path.Id, cmd.Path.Tiles);
                return Yes;
            }
            case W.Command.KindOneofCase.Task:
            {
                if (FindScene(cmd.Task.SceneId) is not { } s) return No("That scene has ended.");
                if (!Enum.IsDefined(cmd.Task.Task)) return No("The crews don't know that order.");
                s.Touched = _tick;
                foreach (int id in cmd.Task.Ids.Take(32)) s.Order(id, cmd.Task.Task, cmd.Task.X, cmd.Task.Y, cmd.Task.TargetId);
                return Yes;
            }
            case W.Command.KindOneofCase.Settings:
            {
                var c = cmd.Settings;
                if (!Enum.IsDefined(c.Style)) return No("There is no such command style.");
                _settings.Style = c.Style;
                _settings.PickResponders = c.PickResponders;
                _settings.CrewsUseHeads = c.CrewsUseHeads;
                if (_settings.DriversWatchOut && !c.DriversWatchOut) Log("Drivers were told not to stop for people at scenes.");
                _settings.DriversWatchOut = c.DriversWatchOut;
                if (c.Shifts is >= 1 and <= 3 && c.Shifts != _shifts)
                {
                    Log(c.Shifts == 3 ? "Crews are on duty around the clock again." : $"Crews now work {c.Shifts} shift{(c.Shifts == 1 ? "" : "s")}: units are off some days.");
                    _shifts = c.Shifts;
                }
                var name = c.Name.Trim();
                if (name.Length > 0) _name = name.Length > 30 ? name[..30] : name;
                // Proposals made for a player who no longer wants them go out as they are.
                return Yes;
            }
            case W.Command.KindOneofCase.Buy:
            {
                if (Lot(cmd.Buy.LeaseId) is not { } lot) return No("The service doesn't lease that station.");
                if (!Enum.IsDefined(cmd.Buy.Kind)) return No("There's no such unit.");
                var spec = StationSpec(lot.Kind);
                bool swat = cmd.Buy.Kind == W.UnitKind.Swat && lot.Kind == "police-station";
                if (spec.Kind != cmd.Buy.Kind && !swat) return No($"A {Describe(lot.Kind)} keeps {Spec(spec.Kind).Name.ToLowerInvariant()}s{(spec.Kind == W.UnitKind.Patrol ? " and SWAT teams" : "")}.");
                if (swat && _units.Any(u => u.LeaseId == lot.LeaseId && u.Kind == W.UnitKind.Swat)) return No("That police station has its SWAT team.");
                if (_units.Count(u => u.LeaseId == lot.LeaseId) >= spec.Holds) return No($"That {Describe(lot.Kind)} holds {spec.Holds} units.");
                var price = Spec(cmd.Buy.Kind).Price;
                if (_cash < price) return No($"A new {Spec(cmd.Buy.Kind).Name.ToLowerInvariant()} costs ${price:N0} and the service has ${_cash:N0}.");
                _cash -= price;
                _this.Vehicles += price;
                var u = AddUnit(cmd.Buy.Kind, lot.LeaseId);
                Log($"Bought {UnitName(u.Id)}.");
                return Yes;
            }
            case W.Command.KindOneofCase.Gear:
            {
                if (Find(cmd.Gear.UnitId) is not { } u) return No("That unit has gone.");
                var g = Gear.FirstOrDefault(g => g.Item == cmd.Gear.Item);
                if (g.Item == null) return No("There's no such equipment.");
                if (g.Kind != u.Kind) return No($"{UnitName(u.Id)} can't carry {g.Name}.");
                if ((u.Gear & g.Bit) != 0) return No($"{UnitName(u.Id)} already has {g.Name}.");
                if (_cash < GearPrice) return No($"{char.ToUpperInvariant(g.Name[0])}{g.Name[1..]} costs ${GearPrice:N0} and the service has ${_cash:N0}.");
                _cash -= GearPrice;
                _this.Vehicles += GearPrice;
                u.Gear |= g.Bit;
                Log($"{UnitName(u.Id)} now carries {g.Name}.");
                return Yes;
            }
            case W.Command.KindOneofCase.Crooked:
            {
                var k = cmd.Crooked;
                if (k.On && !_crookedOk) return No(_crookedWhy.Length > 0 ? _crookedWhy : "Crooked services aren't allowed here.");
                int district = k.On ? k.District : -1;
                if (k.On && k.Kind is "slow-walk" or "racket" or "look-away" && (district < 0 || district > 255)) return No("Pick a district.");
                switch (k.Kind)
                {
                    case "slow-walk": _slowWalk = district; Log(k.On ? $"Calls from district {district + 1} will wait." : "No more slow-walking."); break;
                    case "racket": _racket = district; Log(k.On ? $"The service is leaning on the shops of district {district + 1}." : "The racket is over."); break;
                    case "look-away": _lookAway = district; Log(k.On ? $"Officers will look the other way in district {district + 1}." : "Officers are watching again."); break;
                    case "overtime": _overtime = k.On; Log(k.On ? "Fire crews are on the overtime game." : "No more overtime fires."); break;
                    case "strike":
                        if (_strike == k.On) return Yes;
                        _strike = k.On;
                        foreach (var u in _units.Where(u => u.State is "ready" or "proposed" || u.State == "on strike"))
                        {
                            if (k.On && u.State == "proposed") { u.CallId = 0; }
                            u.State = k.On ? "on strike" : "ready";
                        }
                        foreach (var c in _calls.Where(c => c.Status == "proposed")) { c.Status = "waiting"; c.Units.Clear(); }
                        Log(k.On ? "The crews have walked out. Nobody answers calls until the strike ends." : "The strike is over: crews are back.");
                        break;
                    default: return No("There's no such crooked way.");
                }
                return Yes;
            }
            case W.Command.KindOneofCase.Sell:
            {
                if (Find(cmd.Sell.UnitId) is not { } u) return No("That unit has already gone.");
                if (u.State != "ready") return No("Only a unit at its station can be sold.");
                _units.Remove(u);
                _cash += Spec(u.Kind).Resale;
                Log($"Sold {Spec(u.Kind).Name} {u.Number}.");
                return Yes;
            }
        }
        return No("The dispatcher didn't understand that.");
    }

    Scene? FindScene(int id) => _scenes.FirstOrDefault(s => s.Id == id);

    // ---------------------------------------------------------------------
    // View and saving
    // ---------------------------------------------------------------------

    public byte[] View(string seat)
    {
        var v = new W.View
        {
            Name = _name, Cash = _cash, Owner = _owner, Style = _settings.Style, PickResponders = _settings.PickResponders,
            CrewsUseHeads = _settings.CrewsUseHeads, DriversWatchOut = _settings.DriversWatchOut, Respect = _respect,
            ServiceQuality = _quality, ThisMonth = _this.ToWire(), LastMonth = _last.ToWire(), Day = _day,
            Operator = _operator switch { OperatorLevel.Manual => "manual", OperatorLevel.Autopilot => "autopilot", OperatorLevel.Stasis => "stasis", _ => "caretaker" },
        };
        foreach (var lot in _lots) v.Stations.Add(new W.StationView { LeaseId = lot.LeaseId, Kind = lot.Kind, X = lot.X, Y = lot.Y, Name = $"{Describe(lot.Kind)} at {lot.X},{lot.Y}" });
        foreach (var o in _open) v.OpenStations.Add(new W.OpenStation { BuildingId = o.BuildingId, Kind = o.Kind, X = o.X, Y = o.Y });
        foreach (var u in _units)
            v.Units.Add(new W.UnitView
            {
                Id = u.Id, Name = UnitName(u.Id), Kind = u.Kind, StationLeaseId = u.LeaseId, Crew = u.Crew, State = u.State, CallId = u.CallId,
                BackInDays = u.State == "suspended" ? (int)Math.Max(0, u.Until - _tick) : 0,
                Gear = { GearOf(u.Gear) },
            });
        // A unit whose crew is off today shows it.
        foreach (var uv in v.Units) if (uv.State == "ready" && Find(uv.Id) is { } fu && !OnDuty(fu)) uv.State = "off shift";
        v.Shifts = _shifts;
        v.Ratings.AddRange(Ratings());
        v.Crooked = new W.CrookedView
        {
            Allowed = _crookedOk, WhyNot = _crookedWhy, SlowWalk = _slowWalk, Racket = _racket, LookAway = _lookAway, Overtime = _overtime,
            Strike = _strike, Exposed = _exposed,
        };
        foreach (var c in _calls)
        {
            var cv = new W.CallView
            {
                Id = c.Id, Kind = c.Kind, X = c.X, Y = c.Y, Severity = c.Severity, Text = c.Text, Status = c.Status, WaitedDays = c.WaitedDays,
                Outcome = c.Outcome, Minutes = c.Minutes, SceneId = c.Status == "scene" ? c.SceneId : 0,
            };
            cv.UnitIds.AddRange(c.Units);
            v.Calls.Add(cv);
        }
        foreach (var s in _scenes) v.Scenes.Add(s.ToView());
        v.Cards.AddRange(_cards);
        v.Log.AddRange(_log);
        if (seat == "public")
        {
            // A rival or a spectator sees the calls and the scenes, not the books or the reports.
            v.Cash = 0; v.ThisMonth = null; v.LastMonth = null;
            v.Cards.Clear(); v.Log.Clear();
            v.Crooked = new W.CrookedView { Strike = _strike, Exposed = _exposed, SlowWalk = -1, Racket = -1, LookAway = -1 };
        }
        return v.ToByteArray();
    }

    public void Save(BinaryWriter w)
    {
        w.Write(_seed); w.Write(_name); w.Write(_cash); w.Write(_respect); w.Write(_quality);
        w.Write(_nextUnit); w.Write(_nextCall); w.Write(_nextScene);
        for (int i = 0; i < 3; i++) w.Write(_numbers[i]);
        w.Write((int)_settings.Style); w.Write(_settings.PickResponders); w.Write(_settings.CrewsUseHeads); w.Write(_settings.DriversWatchOut);
        w.Write(_tick); w.Write(_day); w.Write(_month); w.Write(_year); w.Write((byte)_operator); w.Write(_owner);
        _this.Write(w); _last.Write(w);
        w.Write(_units.Count);
        foreach (var u in _units)
        {
            w.Write(u.Id); w.Write(u.Number); w.Write(u.LeaseId); w.Write(u.Crew); w.Write(u.CallId); w.Write((int)u.Kind); w.Write(u.State); w.Write(u.Until);
        }
        w.Write(_calls.Count);
        foreach (var c in _calls)
        {
            w.Write(c.Id); w.Write(c.X); w.Write(c.Y); w.Write(c.Severity); w.Write(c.WaitedDays); w.Write(c.Minutes); w.Write(c.SceneId);
            w.Write(c.IncidentId); w.Write(c.Rolled); w.Write(c.Kind); w.Write(c.Text); w.Write(c.Status); w.Write(c.Outcome); w.Write(c.Taken);
            w.Write(c.Units.Count); foreach (var u in c.Units) w.Write(u);
        }
        w.Write(_scenes.Count);
        foreach (var s in _scenes) s.Save(w);
        w.Write(_cards.Count);
        foreach (var c in _cards) { var b = c.ToByteArray(); w.Write(b.Length); w.Write(b); }
        w.Write(_log.Count); foreach (var s in _log) w.Write(s);
        w.Write(_results.Count); foreach (var r in _results) w.Write(r);
        w.Write(_responses.Count);
        foreach (var r in _responses) { w.Write(r.IncidentId); w.Write(r.Outcome); w.Write(r.Text); }
        w.Write(_equipped.Count); foreach (var e in _equipped) w.Write(e);
        w.Write(_wanted.Count); foreach (var e in _wanted) w.Write(e);
        for (int i = 0; i < 3; i++) w.Write(_short[i]);
        w.Write(_lots.Count);
        foreach (var l in _lots)
        {
            w.Write(l.LeaseId); w.Write(l.BuildingId); w.Write(l.Kind); w.Write(l.X); w.Write(l.Y); w.Write(l.W); w.Write(l.H);
            w.Write(l.AccessCell); w.Write(l.DistrictId); WriteTerms(w, l.Terms); w.Write(l.BadStanding);
        }
        w.Write(_open.Count);
        foreach (var o in _open)
        {
            w.Write(o.BuildingId); w.Write(o.Kind); w.Write(o.X); w.Write(o.Y); w.Write(o.W); w.Write(o.H); w.Write(o.AccessCell); w.Write(o.DistrictId);
            WriteTerms(w, o.Terms);
        }
        // Beta 3 (part 11), after everything Beta 2 saved so its saves still load.
        w.Write(BetaThree);
        w.Write(_numbers[3]); w.Write(_short[3]); w.Write(_shifts);
        foreach (var u in _units) w.Write(u.Gear);
        foreach (var c in _calls) w.Write(c.District);
        foreach (var sc in _scenes) sc.SaveMore(w);
        w.Write(_responseTimes.Count);
        foreach (var (t, k, m) in _responseTimes) { w.Write(t); w.Write(k); w.Write(m); }
        w.Write(_crookedOk); w.Write(_crookedWhy); w.Write(_slowWalk); w.Write(_racket); w.Write(_lookAway); w.Write(_exposed);
        w.Write(_overtime); w.Write(_strike);
        w.Write(_scandals.Count); foreach (var t in _scandals) w.Write(t);
    }

    const int BetaThree = 0x42330001;

    public void Load(BinaryReader r)
    {
        _seed = r.ReadUInt64(); _name = r.ReadString(); _cash = r.ReadInt64(); _respect = r.ReadInt32(); _quality = r.ReadInt32();
        _nextUnit = r.ReadInt32(); _nextCall = r.ReadInt32(); _nextScene = r.ReadInt32();
        for (int i = 0; i < 3; i++) _numbers[i] = r.ReadInt32();
        _settings.Style = (W.CommandStyle)r.ReadInt32(); _settings.PickResponders = r.ReadBoolean(); _settings.CrewsUseHeads = r.ReadBoolean(); _settings.DriversWatchOut = r.ReadBoolean();
        _tick = r.ReadInt64(); _day = r.ReadInt32(); _month = r.ReadInt32(); _year = r.ReadInt32(); _operator = (OperatorLevel)r.ReadByte(); _owner = r.ReadString();
        _this = Money.Read(r); _last = Money.Read(r);
        int nu = r.ReadInt32();
        for (int i = 0; i < nu; i++)
            _units.Add(new Unit
            {
                Id = r.ReadInt32(), Number = r.ReadInt32(), LeaseId = r.ReadInt32(), Crew = r.ReadInt32(), CallId = r.ReadInt32(),
                Kind = (W.UnitKind)r.ReadInt32(), State = r.ReadString(), Until = r.ReadInt64(),
            });
        int nc = r.ReadInt32();
        for (int i = 0; i < nc; i++)
        {
            var c = new Call
            {
                Id = r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(), Severity = r.ReadInt32(), WaitedDays = r.ReadInt32(), Minutes = r.ReadInt32(),
                SceneId = r.ReadInt32(), IncidentId = r.ReadInt64(), Rolled = r.ReadInt64(), Kind = r.ReadString(), Text = r.ReadString(),
                Status = r.ReadString(), Outcome = r.ReadString(), Taken = r.ReadBoolean(),
            };
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++) c.Units.Add(r.ReadInt32());
            _calls.Add(c);
        }
        int ns = r.ReadInt32();
        for (int i = 0; i < ns; i++) _scenes.Add(Scene.Load(r));
        int nk = r.ReadInt32();
        for (int i = 0; i < nk; i++) _cards.Add(W.ReportCard.Parser.ParseFrom(r.ReadBytes(r.ReadInt32())));
        int nl = r.ReadInt32(); for (int i = 0; i < nl; i++) _log.Add(r.ReadString());
        int nr = r.ReadInt32(); for (int i = 0; i < nr; i++) _results.Add(r.ReadInt32());
        int np = r.ReadInt32(); for (int i = 0; i < np; i++) _responses.Add(new IncidentResponse(r.ReadInt64(), r.ReadString(), r.ReadString()));
        int ne = r.ReadInt32(); for (int i = 0; i < ne; i++) _equipped.Add(r.ReadInt32());
        int nw = r.ReadInt32(); for (int i = 0; i < nw; i++) _wanted.Add(r.ReadInt32());
        for (int i = 0; i < 3; i++) _short[i] = r.ReadInt32();
        int nlots = r.ReadInt32();
        for (int i = 0; i < nlots; i++)
            _lots.Add(new LeasedLot(r.ReadInt32(), r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                r.ReadInt32(), r.ReadInt32(), ReadTerms(r), null, 0, r.ReadBoolean()));
        int no = r.ReadInt32();
        for (int i = 0; i < no; i++)
            _open.Add(new OpenLot(r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), ReadTerms(r)));
        if (r.BaseStream.Position >= r.BaseStream.Length || r.ReadInt32() != BetaThree) return;
        _numbers[3] = r.ReadInt32(); _short[3] = r.ReadInt32(); _shifts = r.ReadInt32();
        foreach (var u in _units) u.Gear = r.ReadInt32();
        foreach (var c in _calls) c.District = r.ReadInt32();
        foreach (var sc in _scenes) sc.LoadMore(r);
        int nt = r.ReadInt32();
        for (int i = 0; i < nt; i++) _responseTimes.Add((r.ReadInt64(), r.ReadString(), r.ReadInt32()));
        _crookedOk = r.ReadBoolean(); _crookedWhy = r.ReadString(); _slowWalk = r.ReadInt32(); _racket = r.ReadInt32(); _lookAway = r.ReadInt32(); _exposed = r.ReadInt32();
        _overtime = r.ReadBoolean(); _strike = r.ReadBoolean();
        int nsc = r.ReadInt32(); for (int i = 0; i < nsc; i++) _scandals.Add(r.ReadString());
    }

    static void WriteTerms(BinaryWriter w, LeaseTerms t)
    {
        w.Write((byte)t.Mode); w.Write(t.RentPerMonth); w.Write(t.TaxTenths); w.Write(t.SubsidyPerMonth); w.Write(t.FareCapCents); w.Write(t.NoticeMonths);
    }

    static LeaseTerms ReadTerms(BinaryReader r) => new((LeaseMode)r.ReadByte(), r.ReadInt64(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32());
}
