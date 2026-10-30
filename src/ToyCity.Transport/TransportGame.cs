using Google.Protobuf;
using ToyCity.Contract;
using W = ToyCity.Transport.Wire;

namespace ToyCity.Transport;

/// <summary>
/// The transit company: bus, tram and train lines through leased stops, vehicles kept at leased depots, fares, wages
/// and rent, and a profit and loss for every route. Riders come from the city's commuters, who take a line when it
/// beats driving or walking; the company sees yesterday's riders and boardings per stop and takes the fares. Each
/// vehicle ages and wears; servicing at the depot keeps it going, neglect makes it break down. A line can run to a
/// timetable, keeping the rest of its fleet as spares. Freight lines run trucks or freight trains between leased
/// freight depots, carrying the city's goods to its shops and out to the map edge. Signals on the company's rail
/// lines let more than one train use the track: a block signal reserves the whole block ahead, a path signal only
/// the way the train takes. Two companies can run in one city and bid against each other for district franchises.
/// It plays itself when nobody runs it (caretaker or autopilot), and takes commands from its owner when someone does.
/// </summary>
public sealed class TransportGame : ISideGame
{
    /// <summary>The city's first transit company, and the rival a mayor can invite in.</summary>
    public const string Id = "transport", RivalId = "transport-2";
    public const string RivalName = "Metro Lines Ltd.";
    public const int BusPrice = 3_000, BusResale = 1_500;
    public const int WagePerBus = 500, UpkeepPerBus = 120;
    public const int SeatsPerBus = 40, BusesPerDepot = 12, DriversPerBus = 2, DepotStaff = 2;
    public const int DefaultFareCents = 150, MaxFareCents = 1_000;
    public const int TurnaroundMinutes = 4, MinHeadway = 3, TargetHeadway = 15;
    public const int ClosedDays = 5;
    /// <summary>Days between services unless the owner says otherwise.</summary>
    public const int DefaultServiceDays = 30;
    public const int MaxTimetable = 120, MaxServiceDays = 180;
    /// <summary>What a signal costs to put up, and how many the company may have.</summary>
    public const int SignalPrice = 250, MaxSignals = 200;
    /// <summary>Hours a day freight runs.</summary>
    public const int FreightHours = 16;
    static readonly string[] Palette = { "#e04f5f", "#3f88e0", "#2fa36b", "#e0a030", "#9b59b6", "#16a2b8", "#d35400", "#7f8c8d" };

    /// <summary>
    /// A kind of vehicle. Condition runs 0..1000; a vehicle wears a little each day it runs, a service puts it back to
    /// the best its age allows, and that best falls over its life. Worn vehicles break down more often.
    /// </summary>
    public sealed record Spec(TransitMode Mode, string Word, string Plural, int Price, int Resale, int Wage, int Upkeep, int Seats, int PerDepot,
        int Crew, int LifeYears, int WearPerDay, int RepairDays, int RepairCost, int ServiceCost, bool Freight = false)
    {
        /// <summary>Where these vehicles live, and the kind of lot their line calls at. A freight vehicle's seats are tons.</summary>
        public string Depot => Freight ? "freight-depot" : TransitModes.DepotKind(Mode);
        public string Stop => Freight ? "freight-depot" : TransitModes.StopKind(Mode);
    }

    public static readonly Spec[] Specs =
    {
        new(TransitMode.Bus, "bus", "buses", BusPrice, BusResale, WagePerBus, UpkeepPerBus, SeatsPerBus, BusesPerDepot, DriversPerBus, 12, 4, 2, 300, 150),
        new(TransitMode.Tram, "tram", "trams", 12_000, 5_000, 600, 300, 150, 8, 2, 25, 3, 3, 1_200, 500),
        new(TransitMode.Rail, "train", "trains", 12_000, 5_500, 900, 700, 400, 6, 3, 30, 3, 4, 3_000, 1_200),
    };

    /// <summary>Freight: trucks on the streets and freight trains on the rail lines.</summary>
    public static readonly Spec[] FreightSpecs =
    {
        new(TransitMode.Bus, "truck", "trucks", 4_000, 1_800, 450, 150, 20, 10, 1, 10, 5, 2, 300, 150, true),
        new(TransitMode.Rail, "freight train", "freight trains", 25_000, 11_000, 900, 800, 600, 4, 2, 30, 3, 4, 3_000, 1_200, true),
    };

    static readonly Spec[] AllSpecs = Specs.Concat(FreightSpecs).ToArray();

    public static Spec SpecOf(TransitMode m) => Specs[(int)m];
    public static Spec? FreightSpecOf(TransitMode m) => FreightSpecs.FirstOrDefault(s => s.Mode == m);

    /// <summary>What a ton carried earns: more the longer the run.</summary>
    public static int RatePerTon(int roundTrip) => 3 + roundTrip / 20;

    readonly string _id;
    readonly int _paletteOffset;

    public TransportGame() : this(Id, "Toy Transit Co.") { }

    /// <summary>A transit company under this catalogue id and starting name: the city's own, or a rival.</summary>
    public TransportGame(string id, string name)
    {
        _id = id;
        _name = name;
        _paletteOffset = id == Id ? 0 : 4;
        Manifest = new(id, name, ContractInfo.Version,
            new[] { "bus-stop", "bus-depot", "tram-stop", "tram-depot", "rail-station", "rail-depot", "freight-depot" },
            "A transit company: bus, tram and train lines through leased stops, freight between leased depots, vehicles from leased depots, fares from the city's commuters.");
    }

    public Manifest Manifest { get; }

    sealed class Money
    {
        public long Fares, Wages, Upkeep, Rent, Subsidy, Standing;
        public long Profit => Fares + Subsidy - Wages - Upkeep - Rent - Standing;
        public void Write(BinaryWriter w) { w.Write(Fares); w.Write(Wages); w.Write(Upkeep); w.Write(Rent); w.Write(Subsidy); w.Write(Standing); }
        public static Money Read(BinaryReader r) => new() { Fares = r.ReadInt64(), Wages = r.ReadInt64(), Upkeep = r.ReadInt64(), Rent = r.ReadInt64(), Subsidy = r.ReadInt64(), Standing = r.ReadInt64() };
        public W.MonthMoney ToWire() => new() { Fares = Fares, Wages = Wages, Upkeep = Upkeep, Rent = Rent, Subsidy = Subsidy, Standing = Standing, Profit = Profit };
    }

    sealed class Vehicle
    {
        public int Id;
        public long Bought, ServicedAt, BrokenUntil;
        public int Condition = 1000;
        /// <summary>Today: off the road for a service, or running.</summary>
        public bool Servicing, Running;
    }

    sealed class Line
    {
        public int Id;
        public string Name = "";
        public string Color = "";
        public TransitMode Mode;
        public List<int> Stops = new();
        public List<Vehicle> Fleet = new();
        public int FareCents = DefaultFareCents;
        public int Timetable, ServiceDays = DefaultServiceDays;
        public int RoundTrip;
        public int[] RouteCells = Array.Empty<int>();
        public bool RouteDirty = true;
        public int Riders, Headway, Seats, EffectiveFare, Needed, Running;
        public int DaysRun, DaysOnTime, OnTimePercent = 100;
        public int[] Boardings = Array.Empty<int>();
        public string Problem = "";
        public Money This = new(), Last = new();
        /// <summary>A freight line (trucks or freight trains between freight depots), and whether it runs on to the map edge.</summary>
        public bool Freight, ToEdge;
        /// <summary>Freight carried today, in tons.</summary>
        public int Tons;
        /// <summary>The signals don't let it run as often as its vehicles could.</summary>
        public bool SignalLimited;
        public int Buses => Fleet.Count;
        public Spec Spec => Freight ? FreightSpecOf(Mode)! : SpecOf(Mode);
    }

    public enum SignalKind : byte { None = 0, Block = 1, Path = 2 }

    ulong _seed;
    string _name = "Toy Transit Co.";
    long _cash = 25_000;
    int _nextLineId = 1, _nextVehicleId = 1;
    readonly List<Line> _lines = new();
    readonly SortedDictionary<int, long> _closedUntil = new();
    readonly List<string> _log = new();
    Money _this = new(), _last = new();
    int _quality = 50;
    // What the city said last: kept and saved, since commands and the view read it between ticks.
    long _tick;
    int _day, _month = 1, _year;
    OperatorLevel _operator = OperatorLevel.Caretaker;
    string _owner = "";
    readonly List<LeasedLot> _lots = new();
    readonly List<OpenLot> _open = new();
    readonly List<District> _districts = new();
    readonly List<PaidActionOffer> _offers = new();
    /// <summary>Paid actions bought and not yet answered by the city.</summary>
    readonly List<AuthorityAction> _buying = new();
    /// <summary>What the company has paid for, by district: a statue, and when its exclusive rights run out.</summary>
    readonly SortedDictionary<int, (bool Statue, long ExclusiveUntil)> _bought = new();
    /// <summary>Franchises out to tender, and those in force (anyone's), as the city told it today.</summary>
    readonly List<Tender> _tenders = new();
    readonly List<Franchise> _franchises = new();
    /// <summary>Goods at the company's freight depots today.</summary>
    readonly List<FreightLot> _freight = new();
    /// <summary>Signals on the company's rail lines, by cell; and how much of each hour each track cell is reserved, in minutes.</summary>
    readonly SortedDictionary<int, SignalKind> _signals = new();
    readonly SortedDictionary<int, int> _reserved = new();

    public void NewGame(ulong seed) => _seed = seed;

    int Buses => _lines.Sum(l => l.Buses);
    int Owned(Spec s) => _lines.Where(l => l.Spec == s).Sum(l => l.Buses);
    int Depots(Spec s) => _lots.Count(l => l.Kind == s.Depot);
    int Capacity(Spec s) => Depots(s) * s.PerDepot;
    LeasedLot? Lot(int leaseId) => _lots.FirstOrDefault(l => l.LeaseId == leaseId);

    /// <summary>The best condition a vehicle of this age can be serviced back to: a new one 1000, one at the end of its life 300.</summary>
    int MaxCondition(Vehicle v, Spec spec)
    {
        long age = Math.Max(0, _tick - v.Bought);
        return (int)Math.Max(300, 1000 - age * 700 / (spec.LifeYears * 360L));
    }

    /// <summary>What a vehicle sells for: its resale price, less for one worn or old.</summary>
    long ResaleOf(Vehicle v, Spec spec) => (long)spec.Resale * Math.Min(v.Condition, MaxCondition(v, spec)) / 1000;

    Vehicle NewVehicle() => new() { Id = _nextVehicleId++, Bought = _tick, ServicedAt = _tick };

    /// <summary>Takes a vehicle off a line and sells it: the most worn first.</summary>
    long SellOne(Line line)
    {
        var v = line.Fleet.OrderBy(x => x.Condition).ThenBy(x => x.Bought).ThenBy(x => x.Id).First();
        line.Fleet.Remove(v);
        long got = ResaleOf(v, line.Spec);
        _cash += got;
        return got;
    }

    void Log(string text)
    {
        _log.Add($"{Months[(_month + 11) % 12]} {_year}: {text}");
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
        // A stop the city moved to another lot keeps its lease: the lines through it need routing again.
        var moved = input.Lots.Where(n => Lot(n.LeaseId) is { } was && was.BuildingId != n.BuildingId).Select(n => n.LeaseId).ToHashSet();
        foreach (var line in _lines) if (line.Stops.Any(moved.Contains)) line.RouteDirty = true;
        _lots.Clear(); _lots.AddRange(input.Lots);
        _open.Clear(); _open.AddRange(input.OpenLots);
        if (input.NewMonth) { _districts.Clear(); _districts.AddRange(input.Districts); }
        _offers.Clear(); _offers.AddRange(input.Offers);
        _tenders.Clear(); _tenders.AddRange(input.Tenders);
        _franchises.Clear(); _franchises.AddRange(input.Franchises);
        _freight.Clear(); _freight.AddRange(input.Freight);
        HearStanding(input);

        // Lines lose stops the company no longer leases.
        foreach (var line in _lines)
        {
            int before = line.Stops.Count;
            string kind = line.Spec.Stop;
            line.Stops.RemoveAll(s => Lot(s)?.Kind != kind);
            if (line.Stops.Count != before) { line.RouteDirty = true; Log($"The {line.Name} line lost a stop the company no longer leases."); }
        }
        // Too many vehicles for the depots: sell the extras.
        foreach (var spec in AllSpecs)
            while (Owned(spec) > Capacity(spec) && _lines.Where(l => l.Spec == spec && l.Buses > 0).OrderBy(l => l.Riders).ThenBy(l => l.Id).FirstOrDefault() is { } spare)
            {
                SellOne(spare);
                Log($"Sold a {spec.Word} from the {spare.Name} line: the depots have no room for it.");
            }

        HearIncidents(input);
        TakeFares(input, output);
        Maintain(city, output);
        if (input.NewMonth) CloseMonth(input, output);
        if (_operator != OperatorLevel.Manual) PlayItself(input, city);
        foreach (var line in _lines) if (line.RouteDirty) Route(line, city);
        Plan();
        Publish(output);
        CarryFreight(output);
    }

    /// <summary>The city's answers to yesterday's paid actions: pay for those that went ahead.</summary>
    void HearStanding(CityToGame input)
    {
        foreach (var r in input.AuthorityResults)
        {
            int i = _districts.FindIndex(d => d.Id == r.DistrictId);
            if (i >= 0) _districts[i] = _districts[i] with { Rating = r.Rating };
            if (r.Kind is "bid" or "franchise" && (!r.Ok || r.Cost == 0)) { Log(r.Text); continue; }
            if (!r.Ok) { Log($"Couldn't buy that: {r.Text}"); continue; }
            _cash -= r.Cost;
            _this.Standing += r.Cost;
            var b = _bought.GetValueOrDefault(r.DistrictId);
            if (r.Kind == "statue") b.Statue = true;
            if (r.Kind == "exclusive-rights") b.ExclusiveUntil = _tick + 360;
            _bought[r.DistrictId] = b;
            string where = i >= 0 ? _districts[i].Name : "the district";
            Log(r.Kind == "bribe" && r.Text.Length == 0 ? $"A quiet word and ${r.Cost:N0} with the {where} council." : r.Text);
        }
    }

    void HearIncidents(CityToGame input)
    {
        foreach (var inc in input.Incidents)
        {
            if (inc.Kind is not ("fire" or "tornado" or "earthquake" or "robot" or "disaster") || inc.X < 0) continue;
            foreach (var lot in _lots.Where(l => l.Kind is "bus-stop" or "tram-stop" or "rail-station"))
            {
                if (Math.Max(Math.Abs(lot.X - inc.X), Math.Abs(lot.Y - inc.Y)) > 3) continue;
                bool already = _closedUntil.TryGetValue(lot.LeaseId, out long until) && until > _tick;
                _closedUntil[lot.LeaseId] = _tick + ClosedDays;
                if (!already) Log($"Closed the stop at {lot.X},{lot.Y} for {ClosedDays} days: {inc.Kind} nearby.");
            }
        }
        foreach (var k in _closedUntil.Where(c => c.Value <= _tick).Select(c => c.Key).ToList()) _closedUntil.Remove(k);
    }

    /// <summary>Yesterday's riders pay today: each boarding pays the line's fare, credited to the stop it boarded at.</summary>
    void TakeFares(CityToGame input, GameToCity output)
    {
        foreach (var line in _lines) { line.Riders = 0; line.Boardings = new int[line.Stops.Count]; }
        foreach (var r in input.Ridership)
        {
            var line = _lines.FirstOrDefault(l => l.Id == r.LineId);
            if (line == null) continue;
            line.Riders = r.Riders;
            // The city counts boardings by position on the line as it ran yesterday: the open stops.
            var running = RunningStops(line);
            line.Boardings = new int[line.Stops.Count];
            long fares = 0;
            for (int i = 0; i < r.BoardingsByStop.Count && i < running.Count; i++)
            {
                int b = r.BoardingsByStop[i];
                line.Boardings[line.Stops.IndexOf(running[i])] += b;
                long dollars = (long)b * line.EffectiveFare / 100;
                output.RevenueByLease[running[i]] = output.RevenueByLease.GetValueOrDefault(running[i]) + dollars;
                fares += dollars;
            }
            _cash += fares;
            line.This.Fares += fares;
            _this.Fares += fares;
        }
    }

    /// <summary>
    /// The workshop's day. Repaired vehicles come back; vehicles due a service go in (one a line a day, while the
    /// company has a depot for them); the timetable takes the vehicles it needs from the rest, best first, and each
    /// that runs wears a little and may break down, the more worn the likelier. A spare steps in for one that does.
    /// </summary>
    void Maintain(ICityQueries city, GameToCity output)
    {
        foreach (var line in _lines)
        {
            var spec = line.Spec;
            foreach (var v in line.Fleet) { v.Servicing = false; v.Running = false; }
            if (line.ServiceDays > 0 && Depots(spec) > 0
                && line.Fleet.Where(v => v.BrokenUntil <= _tick && v.ServicedAt + line.ServiceDays <= _tick).OrderBy(v => v.ServicedAt).ThenBy(v => v.Id).FirstOrDefault() is { } due)
            {
                due.Servicing = true;
                due.ServicedAt = _tick;
                due.Condition = Math.Max(due.Condition, MaxCondition(due, spec));
                Spend(line, spec.ServiceCost);
            }
            var ready = line.Fleet.Where(v => v.BrokenUntil <= _tick && !v.Servicing).OrderByDescending(v => v.Condition).ThenBy(v => v.Id).ToList();
            line.Needed = line.Timetable > 0 && line.RoundTrip > 0 ? (line.RoundTrip + line.Timetable - 1) / line.Timetable : ready.Count;
            line.Running = 0;
            if (line.RoundTrip == 0 || !Runnable(line)) continue;
            foreach (var v in ready)
            {
                if (line.Running >= line.Needed) break;
                v.Condition = Math.Max(0, v.Condition - spec.WearPerDay);
                // Per 100,000 days: 15 for a vehicle in top condition, rising steeply as it wears.
                int odds = 15 + (1000 - v.Condition) * (1000 - v.Condition) / 2_500;
                if ((int)(city.Hash((int)_tick, line.Id, v.Id, 0x6273) % 100_000) < odds)
                {
                    v.BrokenUntil = _tick + spec.RepairDays;
                    Spend(line, spec.RepairCost);
                    var at = Lot(line.Stops[(int)(city.Hash((int)_tick, line.Id, v.Id, 0x6274) % (ulong)line.Stops.Count)]);
                    // A worn tram or train failing, or any badly worn vehicle, hurts people aboard.
                    int severity = line.Freight ? 1 : v.Condition < 300 || (line.Mode != TransitMode.Bus && v.Condition < 500) ? 2 : 1;
                    string text = line.Freight ? $"A {_name} {spec.Word} broke down on the {line.Name} run."
                        : severity >= 2
                        ? $"A worn-out {_name} {spec.Word} broke down on the {line.Name} line and passengers were hurt."
                        : $"A {_name} {spec.Word} broke down on the {line.Name} line. Riders wait longer for a few days.";
                    output.Incidents.Add(new Incident(0, "breakdown", at?.X ?? -1, at?.Y ?? -1, _id, $"{spec.Word} breakdown on the {line.Name} line", severity, text));
                    Log($"A {spec.Word} broke down on the {line.Name} line{(v.Condition < 500 ? $" ({v.Condition / 10}% condition)" : "")}.");
                    continue;
                }
                v.Running = true;
                line.Running++;
            }
        }
    }

    /// <summary>Workshop money: services and repairs, paid as they happen.</summary>
    void Spend(Line line, long amount)
    {
        _cash -= amount;
        line.This.Upkeep += amount;
        _this.Upkeep += amount;
    }

    /// <summary>The first of the month: the month's wages and upkeep, the city's bills, and the books close.</summary>
    void CloseMonth(CityToGame input, GameToCity output)
    {
        foreach (var line in _lines)
        {
            long wages = (long)line.Buses * line.Spec.Wage, upkeep = (long)line.Buses * line.Spec.Upkeep;
            line.This.Wages += wages; line.This.Upkeep += upkeep;
            _this.Wages += wages; _this.Upkeep += upkeep;
            _cash -= wages + upkeep;
            line.OnTimePercent = line.DaysRun == 0 ? 100 : line.DaysOnTime * 100 / line.DaysRun;
            line.DaysRun = line.DaysOnTime = 0;
        }
        long rent = 0;
        foreach (var bill in input.Bills.OrderBy(b => b.LeaseId).ThenBy(b => b.Label))
        {
            if (bill.Amount < 0)
            {
                _cash -= bill.Amount;
                _this.Subsidy -= bill.Amount;
                if (bill.Label != "Subsidy") Log($"The city paid ${-bill.Amount:N0} in {bill.Label.ToLowerInvariant()}.");
                continue;
            }
            long pay = Math.Clamp(_cash, 0, bill.Amount);
            if (pay < bill.Amount) Log($"Couldn't pay the city's {bill.Label.ToLowerInvariant()} in full: ${bill.Amount - pay:N0} short.");
            output.Paid[bill.LeaseId] = output.Paid.GetValueOrDefault(bill.LeaseId) + pay;
            _cash -= pay;
            rent += pay;
        }
        _this.Rent += rent;
        // Each line carries a share of the rent and subsidy by its buses.
        int buses = Math.Max(1, Buses);
        foreach (var line in _lines)
        {
            line.This.Rent += rent * line.Buses / buses;
            line.This.Subsidy += _this.Subsidy * line.Buses / buses;
            line.Last = line.This;
            line.This = new Money();
        }
        _last = _this;
        _this = new Money();
        if (_last.Fares + _last.Wages + _last.Rent + _last.Subsidy > 0)
            Log($"Closed the books: {(_last.Profit >= 0 ? "profit" : "loss")} of ${Math.Abs(_last.Profit):N0}.");
    }

    /// <summary>The line's stops open today: not shut by a disaster, and not in a district another company holds exclusively.</summary>
    List<int> RunningStops(Line line) => line.Stops.Where(s => !_closedUntil.ContainsKey(s) && !Barred(s)).ToList();

    bool Barred(int leaseId) => Lot(leaseId) is { } lot && _franchises.Any(f => f.Exclusive && f.Holder != _id && f.DistrictId == lot.DistrictId);

    /// <summary>Whether the line has enough stops to run: two, or for freight one and the run out to the edge.</summary>
    static bool Runnable(Line line) => line.Stops.Count >= 2 || (line.Freight && line.ToEdge && line.Stops.Count >= 1);
    static int StopsNeeded(Line line) => line.Freight && line.ToEdge ? 1 : 2;

    /// <summary>Times the line along the streets and lays out its route for drawing.</summary>
    void Route(Line line, ICityQueries city)
    {
        line.RouteDirty = false;
        line.RoundTrip = 0;
        line.RouteCells = Array.Empty<int>();
        if (!Runnable(line)) return;
        var cells = new List<int>();
        int minutes = 0;
        for (int i = 0; i + 1 < line.Stops.Count; i++)
        {
            var a = Lot(line.Stops[i]); var b = Lot(line.Stops[i + 1]);
            if (a == null || b == null) return;
            // Buses by street from stop to stop; trams and trains along their tracks (contract v1.2).
            StreetRoute? r;
            if (line.Mode == TransitMode.Bus) r = a.AccessCell < 0 || b.AccessCell < 0 ? null : city.Route(a.AccessCell, b.AccessCell);
            else r = city.RouteFor(line.Mode, a.BuildingId, b.BuildingId);
            if (r == null) return;
            minutes += r.Minutes + 1;
            if (cells.Count > 0) cells.RemoveAt(cells.Count - 1);
            cells.AddRange(r.Cells);
        }
        if (line.ToEdge)
        {
            // On from the last depot to the map edge, where the goods leave the city (contract v1.3).
            var last = Lot(line.Stops[^1]);
            if (last == null || city.RouteFor(line.Mode, last.BuildingId, -1) is not { } edge) return;
            minutes += edge.Minutes + 1;
            if (cells.Count > 0) cells.RemoveAt(cells.Count - 1);
            cells.AddRange(edge.Cells);
        }
        line.RoundTrip = minutes * 11 / 10 * 2 + TurnaroundMinutes;
        line.RouteCells = cells.ToArray();
    }

    /// <summary>Each line's day: what stops it running, if anything, and how often it goes, the signals allowing.</summary>
    void Plan()
    {
        foreach (var line in _lines)
        {
            line.Headway = 0; line.Seats = 0; line.SignalLimited = false;
            line.EffectiveFare = Math.Min(line.FareCents, FareCap(line) is int cap and >= 0 ? cap : int.MaxValue);
            var stops = RunningStops(line);
            var spec = line.Spec;
            line.Problem =
                Depots(spec) == 0 ? $"No depot: lease a {DepotName(spec)} to keep {spec.Plural}." :
                !Runnable(line) ? (line.Freight ? "A freight line needs two depots, or one and the run out to the edge." : "A line needs at least two stops.") :
                line.RoundTrip == 0 ? (line.Mode == TransitMode.Bus ? $"No street route joins its {(line.Freight ? "depots" : "stops")}{(line.ToEdge ? " and the edge" : "")}." : $"No {(line.Mode == TransitMode.Tram ? "tram track" : "rail line")} joins its {(line.Freight ? "depots" : "stops")}{(line.ToEdge ? " and the edge" : "")}.") :
                line.Buses == 0 ? $"No {spec.Plural} on it." :
                line.Running == 0 ? $"Every {spec.Word} is in the workshop." :
                stops.Count < StopsNeeded(line) ? (line.Stops.Any(Barred) ? "Its stops are in a district where another company holds exclusive rights." : "Too many of its stops are closed.") : "";
            if (line.Problem.Length > 0) continue;
            line.Headway = Math.Max(MinHeadway, line.RoundTrip / line.Running);
            if (line.Timetable > 0) line.Headway = Math.Max(line.Headway, line.Timetable);
        }
        Signal();
    }

    /// <summary>
    /// Rail capacity. Each train holds the track it is on: from the signal it passed to the next one, and with a block
    /// signal (or none, leaving a station) the whole block it enters, branches and all. A path signal reserves only the
    /// way the train takes. A cell can be held 60 minutes an hour; a line whose track is asked for more runs less often.
    /// Without signals the whole line is one block, so only one train can be on it.
    /// </summary>
    void Signal()
    {
        _reserved.Clear();
        var rail = _lines.Where(l => l.Mode == TransitMode.Rail && l.Problem.Length == 0 && l.RouteCells.Length > 1 && l.Headway > 0).ToList();
        if (rail.Count == 0) return;
        // The company's track, split into blocks at the signals.
        var next = new Dictionary<int, List<int>>();
        void Link(int a, int b) { (next.TryGetValue(a, out var la) ? la : next[a] = new()).Add(b); (next.TryGetValue(b, out var lb) ? lb : next[b] = new()).Add(a); }
        foreach (var l in rail) for (int i = 0; i + 1 < l.RouteCells.Length; i++) Link(l.RouteCells[i], l.RouteCells[i + 1]);
        var blockOf = new Dictionary<int, int>();
        var blocks = new List<List<int>>();
        foreach (int start in next.Keys.OrderBy(c => c))
        {
            if (blockOf.ContainsKey(start) || _signals.ContainsKey(start)) continue;
            var cells = new List<int>();
            var todo = new Stack<int>();
            todo.Push(start);
            blockOf[start] = blocks.Count;
            while (todo.Count > 0)
            {
                int c = todo.Pop();
                cells.Add(c);
                foreach (int n in next[c])
                    if (!blockOf.ContainsKey(n) && !_signals.ContainsKey(n)) { blockOf[n] = blocks.Count; todo.Push(n); }
            }
            blocks.Add(cells);
        }
        var load = new Dictionary<int, int>();
        foreach (var l in rail)
        {
            var cells = l.RouteCells;
            int oneWay = Math.Max(1, (l.RoundTrip - TurnaroundMinutes) / 2);
            int from = 0;
            for (int i = 1; i <= cells.Length; i++)
            {
                if (i < cells.Length && !_signals.ContainsKey(cells[i])) continue;
                // A section: from the signal at cells[from] (or the line's end) to the next signal.
                var kind = _signals.GetValueOrDefault(cells[from]);
                var held = new HashSet<int>();
                for (int k = from; k < i; k++) held.Add(cells[k]);
                if (kind != SignalKind.Path)
                    foreach (int k in held.ToList()) if (blockOf.TryGetValue(k, out int b)) held.UnionWith(blocks[b]);
                int minutes = Math.Max(1, oneWay * (i - from) / cells.Length);
                // Trains both ways each hour, each holding this much track for the section's minutes.
                int minutesPerHour = 2 * 60 * minutes / l.Headway;
                foreach (int c in held) load[c] = load.GetValueOrDefault(c) + minutesPerHour;
                from = i;
            }
        }
        foreach (var (c, m) in load) _reserved[c] = m;
        foreach (var l in rail)
        {
            int most = l.RouteCells.Max(c => load.GetValueOrDefault(c));
            if (most <= 60) continue;
            l.Headway = (l.Headway * most + 59) / 60;
            l.SignalLimited = true;
        }
    }

    /// <summary>What the company runs today, and what it tells the city.</summary>
    void Publish(GameToCity output)
    {
        int quality = 0, running = 0, riders = 0;
        foreach (var line in _lines)
        {
            if (line.Problem.Length > 0 || line.Headway == 0) continue;
            var stops = RunningStops(line);
            var spec = line.Spec;
            line.DaysRun++;
            if (line.Running >= line.Needed) line.DaysOnTime++;
            line.Seats = spec.Seats * 60 / line.Headway;
            if (line.Freight) continue;
            output.Lines.Add(new TransitLine(line.Id, line.Name, line.Color, stops, line.Headway, line.EffectiveFare, line.Seats, line.Mode));
            running++;
            riders += line.Riders;
            int load = LoadPercent(line);
            int offRoad = line.Fleet.Count(v => v.BrokenUntil > _tick);
            int late = line.Running < line.Needed ? 15 : 0;
            int worn = line.Fleet.Count == 0 ? 0 : Math.Max(0, 600 - (int)line.Fleet.Average(v => v.Condition)) / 20;
            quality += Math.Clamp(100 - Math.Max(0, load - 80) - offRoad * 10 - late - worn - (stops.Count < line.Stops.Count ? 10 : 0), 0, 100);
        }
        _quality = running == 0 ? 50 : quality / running;
        output.ServiceQuality = _quality;
        // Drivers and mechanics work at the depots of each mode.
        foreach (var spec in AllSpecs)
        {
            var depots = _lots.Where(l => l.Kind == spec.Depot).ToList();
            int owned = Owned(spec);
            for (int i = 0; i < depots.Count; i++)
            {
                int share = owned / depots.Count + (i < owned % depots.Count ? 1 : 0);
                output.JobsByLease[depots[i].LeaseId] = output.JobsByLease.GetValueOrDefault(depots[i].LeaseId, DepotStaff) + share * spec.Crew;
            }
        }
        output.LeaseRequests.AddRange(_wanted);
        _wanted.Clear();
        output.AuthorityActions.AddRange(_buying);
        _buying.Clear();
        output.Status = running == 0 ? $"{_name}: nothing running" : $"{_name}: {running} line{(running == 1 ? "" : "s")}, {riders:N0} riders yesterday";
    }

    static string DepotName(Spec s) => s.Freight ? "freight depot" : s.Mode switch { TransitMode.Tram => "tram depot", TransitMode.Rail => "rail yard", _ => "bus depot" };

    /// <summary>
    /// Freight's day. Each freight line can move a load each departure over the day. It picks up the goods ready at
    /// its depots, in order, drops them at the other depots whose shops want them, and takes the rest to the edge if
    /// it runs there; goods with nowhere to go stay where they are. Each ton delivered pays by the length of the run.
    /// </summary>
    void CarryFreight(GameToCity output)
    {
        var left = _freight.ToDictionary(f => f.LeaseId, f => (Ready: f.TonsReady, Wanted: f.TonsWanted));
        foreach (var line in _lines.Where(l => l.Freight))
        {
            line.Tons = 0;
            if (line.Problem.Length > 0 || line.Headway == 0) continue;
            var stops = RunningStops(line);
            int capacity = FreightHours * 60 / line.Headway * line.Spec.Seats;
            var picks = new int[stops.Count];
            var drops = new int[stops.Count];
            int picked = 0, dropped = 0;
            for (int i = 0; i < stops.Count && picked < capacity; i++)
            {
                picks[i] = Math.Min(left.GetValueOrDefault(stops[i]).Ready, capacity - picked);
                picked += picks[i];
            }
            for (int i = 0; i < stops.Count; i++)
            {
                // Goods from the line's other depots, for the shops around this one.
                drops[i] = Math.Max(0, Math.Min(left.GetValueOrDefault(stops[i]).Wanted, Math.Min(picked - picks[i], picked - dropped)));
                dropped += drops[i];
            }
            int edge = line.ToEdge ? picked - dropped : 0;
            // Goods with nowhere to go aren't picked up: give back from the last depot first.
            for (int i = stops.Count - 1, extra = picked - dropped - edge; i >= 0 && extra > 0; i--)
            {
                int back = Math.Min(extra, picks[i]);
                picks[i] -= back; picked -= back; extra -= back;
            }
            if (picked == 0) continue;
            int rate = RatePerTon(line.RoundTrip);
            for (int i = 0; i < stops.Count; i++)
            {
                int s = stops[i];
                var f = left.GetValueOrDefault(s);
                left[s] = (f.Ready - picks[i], f.Wanted - drops[i]);
                if (picks[i] > 0)
                {
                    output.FreightPicked[s] = output.FreightPicked.GetValueOrDefault(s) + picks[i];
                    output.RevenueByLease[s] = output.RevenueByLease.GetValueOrDefault(s) + (long)picks[i] * rate;
                }
                if (drops[i] > 0) output.FreightDropped[s] = output.FreightDropped.GetValueOrDefault(s) + drops[i];
            }
            if (edge > 0) output.FreightDropped[0] = output.FreightDropped.GetValueOrDefault(0) + edge;
            long earned = (long)picked * rate;
            line.Tons = picked;
            _cash += earned;
            line.This.Fares += earned;
            _this.Fares += earned;
        }
    }

    static int LoadPercent(Line line) => line.Seats == 0 ? 0 : line.Riders / 4 * 100 / line.Seats;

    /// <summary>The lowest fare cap among the line's public-service stops, or -1 when none caps it.</summary>
    int FareCap(Line line)
    {
        int cap = -1;
        foreach (var s in line.Stops)
            if (Lot(s) is { Terms.Mode: LeaseMode.PublicService } lot) cap = cap < 0 ? lot.Terms.FareCapCents : Math.Min(cap, lot.Terms.FareCapCents);
        return cap;
    }

    // ---------------------------------------------------------------------
    // Playing itself
    // ---------------------------------------------------------------------

    /// <summary>
    /// Caretaker keeps the company solvent and what it has running: services every vehicle and replaces those worn
    /// out by age. Autopilot also grows it: leases a bus depot and stops where people live, draws a line through them,
    /// and adds buses where they're full. It leaves trams and trains to players.
    /// </summary>
    void PlayItself(CityToGame input, ICityQueries city)
    {
        if (_cash < 0 && _lines.Where(l => l.Buses > 1).OrderBy(LoadPercent).ThenBy(l => l.Id).FirstOrDefault() is { } thin)
        {
            SellOne(thin);
            Log($"Sold a {thin.Spec.Word} from the {thin.Name} line to pay the bills.");
        }
        if (_operator == OperatorLevel.Autopilot) BidForFranchises();
        bool monthly = input.NewMonth || _tick % 30 == 1;
        if (!monthly) return;
        foreach (var line in _lines)
        {
            if (line.ServiceDays == 0) { line.ServiceDays = DefaultServiceDays; Log($"Put the {line.Name} line's {line.Spec.Plural} back on a service every {DefaultServiceDays} days."); }
            var spec = line.Spec;
            if (line.Fleet.Where(v => MaxCondition(v, spec) <= 400).OrderBy(v => v.Bought).ThenBy(v => v.Id).FirstOrDefault() is { } old && _cash >= spec.Price + 5_000)
            {
                Renew(line, old);
                Log($"Replaced a worn-out {spec.Word} on the {line.Name} line.");
            }
        }
        if (_operator != OperatorLevel.Autopilot) return;

        // A district that has gone off the company: an advertising campaign, when there's money for one.
        if (_offers.FirstOrDefault(o => o.Kind == "advertise") is { Allowed: true } ad && _cash >= ad.Cost + 20_000
            && _districts.Where(d => d.Rating < 0 && _lots.Any(l => l.DistrictId == d.Id)).OrderBy(d => d.Rating).ThenBy(d => d.Id).FirstOrDefault() is { } sour)
            _buying.Add(new AuthorityAction("advertise", sour.Id));

        var bus = SpecOf(TransitMode.Bus);
        if (Depots(bus) == 0 && _open.FirstOrDefault(o => o.Kind == "bus-depot") is { } depot) _wanted.Add(depot.BuildingId);
        int stops = _lots.Count(l => l.Kind == "bus-stop");
        if (stops < 8)
        {
            // Stops in the busiest districts first.
            var busy = _districts.OrderByDescending(d => d.Population + d.Jobs).ThenBy(d => d.Id).Select(d => d.Id).ToList();
            foreach (var o in _open.Where(o => o.Kind == "bus-stop").OrderBy(o => busy.IndexOf(o.DistrictId) is int k and >= 0 ? k : 999).ThenBy(o => o.BuildingId).Take(2))
                _wanted.Add(o.BuildingId);
        }
        var leasedStops = _lots.Where(l => l.Kind == "bus-stop" && !l.BadStanding).Select(l => l.LeaseId).ToList();
        var busLines = _lines.Where(l => l.Mode == TransitMode.Bus && !l.Freight).ToList();
        if (busLines.Count == 0 && leasedStops.Count >= 2)
        {
            var line = NewLine("Route 1", TransitMode.Bus, Chain(leasedStops));
            Log($"Opened the {line.Name} line through {line.Stops.Count} stops.");
        }
        else if (busLines.Count > 0)
        {
            var main = busLines[0];
            foreach (var s in leasedStops.Where(s => !_lines.Any(l => l.Stops.Contains(s))))
            {
                main.Stops = Chain(main.Stops.Append(s).ToList());
                main.RouteDirty = true;
                Log($"Added a stop to the {main.Name} line.");
            }
        }
        foreach (var line in busLines)
        {
            if (Depots(bus) == 0) break;
            int load = LoadPercent(line);
            // A bus at least every quarter of an hour, and more when they're full.
            bool sparse = line.Buses == 0 || line.RoundTrip / Math.Max(1, line.Buses) > TargetHeadway;
            int before = line.Buses;
            while ((sparse || load > 90) && Owned(bus) < Capacity(bus) && _cash >= BusPrice + 5_000)
            {
                line.Fleet.Add(NewVehicle());
                _cash -= BusPrice;
                Log(line.Buses == 1 ? $"Bought a bus for the {line.Name} line."
                    : sparse ? $"Bought another bus for the {line.Name} line, to run every {line.RoundTrip / line.Buses} minutes."
                    : $"Bought another bus for the {line.Name} line: it was {load}% full at the peak.");
                if (!sparse) break;
                sparse = line.RoundTrip / line.Buses > TargetHeadway;
            }
            if (line.Buses == before && line.Buses > 1 && load < 25 && line.Riders > 0 && line.RoundTrip / (line.Buses - 1) <= TargetHeadway)
            {
                SellOne(line);
                Log($"Sold a bus from the {line.Name} line: it ran {load}% full.");
            }
        }
    }

    /// <summary>
    /// Autopilot's bids: for a franchise out to tender where the company has lots, a bid it can afford, more for a
    /// district where it has more. It bids once and lets the bid stand.
    /// </summary>
    void BidForFranchises()
    {
        foreach (var t in _tenders.Where(t => t.YourBid == 0).OrderBy(t => t.DistrictId))
        {
            int lots = _lots.Count(l => l.DistrictId == t.DistrictId);
            if (lots == 0 || _buying.Any(b => b.Kind == "bid" && b.DistrictId == t.DistrictId)) continue;
            long amount = Math.Min(_cash - 20_000, t.MinBid * (2 + Math.Min(lots, 6)) / 2);
            if (amount < t.MinBid) continue;
            _buying.Add(new AuthorityAction("bid", t.DistrictId, amount));
            Log($"Bid ${amount:N0} for the {DistrictName(t.DistrictId)} franchise.");
        }
    }

    string DistrictName(int id) => _districts.FirstOrDefault(d => d.Id == id)?.Name ?? $"district {id + 1}";

    /// <summary>Sells a vehicle and buys a new one in its place.</summary>
    void Renew(Line line, Vehicle old)
    {
        _cash += ResaleOf(old, line.Spec) - line.Spec.Price;
        line.Fleet[line.Fleet.IndexOf(old)] = NewVehicle();
    }

    /// <summary>Lots the company would like to lease, asked for in the next output.</summary>
    readonly SortedSet<int> _wanted = new();

    /// <summary>Orders stops into a chain: start at the first and keep going to the nearest one left.</summary>
    List<int> Chain(List<int> leaseIds)
    {
        var left = leaseIds.Where(s => Lot(s) != null).ToList();
        if (left.Count == 0) return left;
        var chain = new List<int> { left[0] };
        left.RemoveAt(0);
        // Start from an end: the stop farthest from the first.
        var far = left.OrderByDescending(s => Dist(chain[0], s)).ThenBy(s => s).FirstOrDefault();
        if (left.Count > 1) { left.Add(chain[0]); chain[0] = far; left.Remove(far); }
        while (left.Count > 0)
        {
            var next = left.OrderBy(s => Dist(chain[^1], s)).ThenBy(s => s).First();
            chain.Add(next);
            left.Remove(next);
        }
        return chain;
    }

    int Dist(int a, int b)
    {
        var p = Lot(a)!; var q = Lot(b)!;
        return Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y);
    }

    Line NewLine(string name, TransitMode mode, List<int> stops)
    {
        var line = new Line { Id = _nextLineId++, Name = name, Mode = mode, Stops = stops };
        line.Color = Palette[(line.Id - 1 + _paletteOffset) % Palette.Length];
        _lines.Add(line);
        return line;
    }

    // ---------------------------------------------------------------------
    // Commands, view, save
    // ---------------------------------------------------------------------

    public CommandResult Command(string seat, byte[] payload, ICityQueries city)
    {
        W.Command cmd;
        try { cmd = W.Command.Parser.ParseFrom(payload); }
        catch (InvalidProtocolBufferException) { return new CommandResult(false, "The bus company didn't understand that."); }
        switch (cmd.KindCase)
        {
            case W.Command.KindOneofCase.SaveLine:
            {
                var c = cmd.SaveLine;
                var stops = c.StopLeaseIds.ToList();
                var existing = c.LineId == 0 ? null : _lines.FirstOrDefault(l => l.Id == c.LineId);
                if (c.LineId != 0 && existing == null) return new CommandResult(false, "That line has been closed.");
                if (!Enum.IsDefined((TransitMode)(int)c.Mode)) return new CommandResult(false, "The company runs buses, trams and trains.");
                var mode = existing?.Mode ?? (TransitMode)(int)c.Mode;
                bool freight = existing?.Freight ?? c.Freight;
                if (freight && FreightSpecOf(mode) == null) return new CommandResult(false, "Freight goes by truck or by train.");
                bool toEdge = freight && c.ToEdge;
                var lineSpec = freight ? FreightSpecOf(mode)! : SpecOf(mode);
                string kind = lineSpec.Stop;
                if (stops.Count < (toEdge ? 1 : 2)) return new CommandResult(false, freight ? "A freight line needs two depots, or one and the run out to the edge." : "A line needs at least two stops.");
                if (stops.Any(s => Lot(s)?.Kind != kind))
                    return new CommandResult(false, $"Every stop on a {lineSpec.Word} line must be a {kind.Replace('-', ' ')} the company leases.");
                if (stops.Distinct().Count() != stops.Count) return new CommandResult(false, "A line can't visit the same stop twice.");
                if (c.FareCents is < 0 or > MaxFareCents) return new CommandResult(false, $"Fares run from free to ${MaxFareCents / 100}.");
                var line = existing ?? NewLine(c.Name.Length > 0 ? c.Name : $"{(freight ? "Freight" : mode == TransitMode.Bus ? "Route" : mode == TransitMode.Tram ? "Tram" : "Rail")} {_nextLineId}", mode, stops);
                line.Freight = freight;
                line.ToEdge = toEdge;
                if (c.Name.Length > 0) line.Name = c.Name.Length > 30 ? c.Name[..30] : c.Name;
                line.Stops = stops;
                line.FareCents = c.FareCents;
                Route(line, city);
                if (line.RoundTrip == 0)
                    return new CommandResult(true, $"Saved the {line.Name} line, but no {(mode == TransitMode.Bus ? "street route" : mode == TransitMode.Tram ? "tram track" : "rail line")} joins all its stops{(toEdge ? " and the edge" : "")}.");
                Log(c.LineId == 0 ? $"Opened the {line.Name} line." : $"Changed the {line.Name} line.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.DeleteLine:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.DeleteLine.LineId);
                if (line == null) return new CommandResult(false, "That line has already closed.");
                int n = line.Buses;
                while (line.Fleet.Count > 0) SellOne(line);
                _lines.Remove(line);
                Log($"Closed the {line.Name} line and sold its {n} {(n == 1 ? line.Spec.Word : line.Spec.Plural)}.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Buses:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.Buses.LineId);
                if (line == null) return new CommandResult(false, "That line has closed.");
                var spec = line.Spec;
                int want = Math.Clamp(cmd.Buses.Buses, 0, 500);
                if (want > line.Buses)
                {
                    if (Depots(spec) == 0) return new CommandResult(false, $"Lease a {DepotName(spec)} first: {spec.Plural} need somewhere to live.");
                    int add = want - line.Buses;
                    if (Owned(spec) + add > Capacity(spec)) return new CommandResult(false, $"The depots hold {Capacity(spec)} {spec.Plural}. Lease another {DepotName(spec)} for more.");
                    long cost = (long)add * spec.Price;
                    if (cost > _cash) return new CommandResult(false, $"{add} {(add == 1 ? spec.Word : spec.Plural)} cost ${cost:N0} and the company has ${_cash:N0}.");
                    _cash -= cost;
                    for (int k = 0; k < add; k++) line.Fleet.Add(NewVehicle());
                }
                else while (line.Buses > want) SellOne(line);
                Log($"The {line.Name} line now has {want} {(want == 1 ? spec.Word : spec.Plural)}.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Timetable:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.Timetable.LineId);
                if (line == null) return new CommandResult(false, "That line has closed.");
                int every = cmd.Timetable.HeadwayMinutes;
                if (every != 0 && every is < MinHeadway or > MaxTimetable) return new CommandResult(false, $"A timetable runs from every {MinHeadway} minutes to every {MaxTimetable}, or 0 to run every vehicle.");
                line.Timetable = every;
                Log(every == 0 ? $"The {line.Name} line runs every {line.Spec.Word} it has." : $"The {line.Name} line runs every {every} minutes, with any {line.Spec.Plural} left over as spares.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Service:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.Service.LineId);
                if (line == null) return new CommandResult(false, "That line has closed.");
                int days = cmd.Service.EveryDays;
                if (days != 0 && days is < 7 or > MaxServiceDays) return new CommandResult(false, $"Services run from every week to every {MaxServiceDays} days, or 0 for never.");
                line.ServiceDays = days;
                Log(days == 0 ? $"Stopped servicing the {line.Name} line's {line.Spec.Plural}." : $"The {line.Name} line's {line.Spec.Plural} are serviced every {days} days.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Renew:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.Renew.LineId);
                if (line == null) return new CommandResult(false, "That line has closed.");
                var spec = line.Spec;
                var old = line.Fleet.OrderBy(v => v.Bought).ThenBy(v => v.Id).FirstOrDefault();
                if (old == null) return new CommandResult(false, $"The line has no {spec.Plural} to replace.");
                long cost = spec.Price - ResaleOf(old, spec);
                if (cost > _cash) return new CommandResult(false, $"A new {spec.Word} costs ${spec.Price:N0} less ${ResaleOf(old, spec):N0} for the old one, and the company has ${_cash:N0}.");
                Renew(line, old);
                Log($"Replaced the oldest {spec.Word} on the {line.Name} line.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Standing:
            {
                var c = cmd.Standing;
                var offer = _offers.FirstOrDefault(o => o.Kind == c.Kind);
                if (offer == null) return new CommandResult(false, "The city doesn't sell that.");
                if (!offer.Allowed) return new CommandResult(false, offer.WhyNot);
                if (!_lots.Any(l => l.DistrictId == c.District)) return new CommandResult(false, "The company has no lots in that district.");
                long owed = _buying.Sum(b => _offers.FirstOrDefault(o => o.Kind == b.Kind)?.Cost ?? 0);
                if (_cash < offer.Cost + owed) return new CommandResult(false, $"{offer.Name} costs ${offer.Cost:N0} and the company has ${_cash - owed:N0}.");
                _buying.Add(new AuthorityAction(c.Kind, c.District));
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Signal:
            {
                var c = cmd.Signal;
                var kind = (SignalKind)(int)c.Kind;
                if (!Enum.IsDefined(kind)) return new CommandResult(false, "Signals are block signals or path signals.");
                if (kind == SignalKind.None)
                {
                    if (!_signals.Remove(c.Cell)) return new CommandResult(false, "There's no signal there.");
                    Log($"Took down a signal.");
                    return new CommandResult(true, "");
                }
                if (!_lines.Any(l => l.Mode == TransitMode.Rail && l.RouteCells.Contains(c.Cell)))
                    return new CommandResult(false, "Signals go on the company's own rail lines: put it on a cell one of its trains runs over.");
                bool had = _signals.TryGetValue(c.Cell, out var was);
                if (had && was == kind) return new CommandResult(true, "");
                if (!had && _signals.Count >= MaxSignals) return new CommandResult(false, $"The company can keep {MaxSignals} signals.");
                if (!had && _cash < SignalPrice) return new CommandResult(false, $"A signal costs ${SignalPrice:N0} and the company has ${_cash:N0}.");
                if (!had) { _cash -= SignalPrice; _this.Upkeep += SignalPrice; }
                _signals[c.Cell] = kind;
                Log(had ? $"Changed a signal to a {(kind == SignalKind.Path ? "path" : "block")} signal." : $"Put up a {(kind == SignalKind.Path ? "path" : "block")} signal for ${SignalPrice:N0}.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Bid:
            {
                var c = cmd.Bid;
                var t = _tenders.FirstOrDefault(t => t.DistrictId == c.District);
                if (t == null) return new CommandResult(false, "That district's franchise isn't out to tender.");
                if (c.Amount < t.MinBid) return new CommandResult(false, $"The city wants at least ${t.MinBid:N0} for {t.Months} months.");
                if (c.Amount > _cash) return new CommandResult(false, $"The company has ${_cash:N0}: it can't bid ${c.Amount:N0}.");
                _buying.RemoveAll(b => b.Kind == "bid" && b.DistrictId == c.District);
                _buying.Add(new AuthorityAction("bid", c.District, c.Amount));
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Rename:
                if (cmd.Rename.Name.Trim().Length == 0) return new CommandResult(false, "The company needs a name.");
                _name = cmd.Rename.Name.Trim().Length > 30 ? cmd.Rename.Name.Trim()[..30] : cmd.Rename.Name.Trim();
                return new CommandResult(true, "");
        }
        return new CommandResult(false, "The bus company didn't understand that.");
    }

    public byte[] View(string seat)
    {
        var v = new W.View
        {
            Name = _name, Cash = _cash, Operator = _operator switch { OperatorLevel.Manual => "manual", OperatorLevel.Autopilot => "autopilot", _ => "caretaker" },
            Owner = _owner, Buses = Owned(SpecOf(TransitMode.Bus)), BusCapacity = Capacity(SpecOf(TransitMode.Bus)), BusPrice = BusPrice, ThisMonth = _this.ToWire(), LastMonth = _last.ToWire(),
            ServiceQuality = _quality, Day = _day, GameId = _id, SignalPrice = SignalPrice,
        };
        foreach (var l in _lines)
        {
            var lv = new W.LineView
            {
                Id = l.Id, Name = l.Name, Color = l.Color, Buses = l.Buses, Broken = l.Fleet.Count(v => v.BrokenUntil > _tick || v.Servicing),
                FareCents = l.FareCents, FareCapCents = FareCap(l),
                HeadwayMinutes = l.Headway, RoundTripMinutes = l.RoundTrip, Riders = l.Riders, SeatsPerHour = l.Seats, LoadPercent = LoadPercent(l),
                ThisMonth = l.This.ToWire(), LastMonth = l.Last.ToWire(), Problem = l.Problem,
                Mode = (W.Mode)(int)l.Mode, TimetableMinutes = l.Timetable, ServiceDays = l.ServiceDays, Needed = l.Needed, Running = l.Running,
                OnTimePercent = l.OnTimePercent, Condition = l.Fleet.Count == 0 ? 0 : (int)l.Fleet.Average(v => v.Condition) / 10,
                Freight = l.Freight, ToEdge = l.ToEdge, Tons = l.Tons, SignalLimited = l.SignalLimited,
            };
            foreach (var veh in l.Fleet)
                lv.Vehicles.Add(new W.VehicleView
                {
                    Id = veh.Id, AgeDays = (int)Math.Max(0, _tick - veh.Bought), Condition = veh.Condition / 10,
                    State = veh.BrokenUntil > _tick ? "broken" : veh.Servicing ? "workshop" : veh.Running ? "running" : "spare",
                    BackInDays = (int)Math.Max(0, veh.BrokenUntil - _tick),
                });
            lv.StopLeaseIds.AddRange(l.Stops);
            lv.Boardings.AddRange(l.Boardings);
            lv.RouteCells.AddRange(l.RouteCells);
            v.Lines.Add(lv);
        }
        foreach (var lot in _lots)
            v.Lots.Add(new W.LotView
            {
                LeaseId = lot.LeaseId, BuildingId = lot.BuildingId, Kind = lot.Kind, X = lot.X, Y = lot.Y, District = lot.DistrictId,
                Terms = lot.Terms.Mode == LeaseMode.Business ? $"${lot.Terms.RentPerMonth:N0}/month + {lot.Terms.TaxTenths / 10.0:0.#}% of fares" : $"public service, ${lot.Terms.SubsidyPerMonth:N0}/month subsidy",
                BadStanding = lot.BadStanding, ClosedDays = _closedUntil.TryGetValue(lot.LeaseId, out long until) ? (int)(until - _tick) : 0,
                TonsReady = _freight.FirstOrDefault(f => f.LeaseId == lot.LeaseId)?.TonsReady ?? 0,
                TonsWanted = _freight.FirstOrDefault(f => f.LeaseId == lot.LeaseId)?.TonsWanted ?? 0,
                Barred = Barred(lot.LeaseId),
            });
        foreach (var o in _open)
            v.OpenLots.Add(new W.LotView { BuildingId = o.BuildingId, Kind = o.Kind, X = o.X, Y = o.Y, District = o.DistrictId, Terms = $"${o.Terms.RentPerMonth:N0}/month + {o.Terms.TaxTenths / 10.0:0.#}% of fares" });
        foreach (var d in _districts.Where(d => _lots.Any(l => l.DistrictId == d.Id)))
        {
            var b = _bought.GetValueOrDefault(d.Id);
            v.Districts.Add(new W.DistrictView
            {
                Id = d.Id, Name = d.Name, Rating = d.Rating, Lots = _lots.Count(l => l.DistrictId == d.Id), Statue = b.Statue,
                ExclusiveDays = (int)Math.Max(0, b.ExclusiveUntil - _tick),
            });
        }
        foreach (var spec in AllSpecs)
            (spec.Freight ? v.FreightModes : v.Modes).Add(new W.ModeView
            {
                Mode = (W.Mode)(int)spec.Mode, Word = spec.Word, Plural = spec.Plural, Price = spec.Price, Seats = spec.Seats, Capacity = Capacity(spec),
                Owned = Owned(spec), LifeYears = spec.LifeYears, ServiceCost = spec.ServiceCost, Freight = spec.Freight,
            });
        foreach (var (cell, kind) in _signals) v.Signals.Add(new W.SignalView { Cell = cell, Kind = (W.Signal)(int)kind });
        foreach (var (cell, minutes) in _reserved) v.Reserved.Add(new W.CellLoad { Cell = cell, Percent = minutes * 100 / 60 });
        foreach (var t in _tenders)
            v.Tenders.Add(new W.TenderView { District = t.DistrictId, Name = DistrictName(t.DistrictId), Months = t.Months, DaysLeft = t.DaysLeft, Exclusive = t.Exclusive, MinBid = t.MinBid, YourBid = t.YourBid, Lots = _lots.Count(l => l.DistrictId == t.DistrictId) });
        foreach (var f in _franchises)
            v.Franchises.Add(new W.FranchiseView { District = f.DistrictId, Name = DistrictName(f.DistrictId), Holder = f.Holder, Mine = f.Holder == _id, Exclusive = f.Exclusive, DaysLeft = f.DaysLeft });
        foreach (var o in _offers) v.Offers.Add(new W.OfferView { Kind = o.Kind, Name = o.Name, Cost = o.Cost, Allowed = o.Allowed, WhyNot = o.WhyNot });
        v.Log.AddRange(_log);
        if (seat == "public")
        {
            // What a rival or a spectator sees: the routes and lots on the map, not the books, the standing or the dealings.
            v.Cash = 0; v.ThisMonth = null; v.LastMonth = null;
            v.Districts.Clear(); v.Offers.Clear(); v.Log.Clear(); v.Tenders.Clear();
            foreach (var l in v.Lines) { l.ThisMonth = null; l.LastMonth = null; }
        }
        return v.ToByteArray();
    }

    public void Save(BinaryWriter w)
    {
        w.Write(_seed); w.Write(_name); w.Write(_cash); w.Write(_nextLineId); w.Write(_nextVehicleId); w.Write(_quality);
        w.Write(_tick); w.Write(_day); w.Write(_month); w.Write(_year); w.Write((byte)_operator); w.Write(_owner);
        _this.Write(w); _last.Write(w);
        w.Write(_lines.Count);
        foreach (var l in _lines)
        {
            w.Write(l.Id); w.Write(l.Name); w.Write(l.Color); w.Write((byte)l.Mode); w.Write(l.FareCents);
            w.Write(l.Timetable); w.Write(l.ServiceDays); w.Write(l.Needed); w.Write(l.Running);
            w.Write(l.DaysRun); w.Write(l.DaysOnTime); w.Write(l.OnTimePercent);
            w.Write(l.Stops.Count); foreach (var s in l.Stops) w.Write(s);
            w.Write(l.Fleet.Count);
            foreach (var v in l.Fleet)
            {
                w.Write(v.Id); w.Write(v.Bought); w.Write(v.ServicedAt); w.Write(v.BrokenUntil); w.Write(v.Condition); w.Write(v.Servicing); w.Write(v.Running);
            }
            w.Write(l.RoundTrip); w.Write(l.RouteDirty);
            w.Write(l.RouteCells.Length); foreach (var c in l.RouteCells) w.Write(c);
            w.Write(l.Riders); w.Write(l.Headway); w.Write(l.Seats); w.Write(l.EffectiveFare); w.Write(l.Problem);
            w.Write(l.Boardings.Length); foreach (var b in l.Boardings) w.Write(b);
            l.This.Write(w); l.Last.Write(w);
            w.Write(l.Freight); w.Write(l.ToEdge); w.Write(l.Tons); w.Write(l.SignalLimited);
        }
        w.Write(_closedUntil.Count);
        foreach (var (k, v) in _closedUntil) { w.Write(k); w.Write(v); }
        w.Write(_log.Count); foreach (var s in _log) w.Write(s);
        w.Write(_wanted.Count); foreach (var b in _wanted) w.Write(b);
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
        w.Write(_districts.Count);
        foreach (var d in _districts) { w.Write(d.Id); w.Write(d.Name); w.Write(d.Population); w.Write(d.Jobs); w.Write(d.Rating); }
        w.Write(_offers.Count);
        foreach (var o in _offers) { w.Write(o.Kind); w.Write(o.Name); w.Write(o.Cost); w.Write(o.Allowed); w.Write(o.WhyNot); }
        w.Write(_buying.Count);
        foreach (var b in _buying) { w.Write(b.Kind); w.Write(b.DistrictId); w.Write(b.Amount); }
        w.Write(_bought.Count);
        foreach (var (d, b) in _bought) { w.Write(d); w.Write(b.Statue); w.Write(b.ExclusiveUntil); }
        w.Write(_tenders.Count);
        foreach (var t in _tenders) { w.Write(t.DistrictId); w.Write(t.Months); w.Write(t.DaysLeft); w.Write(t.Exclusive); w.Write(t.MinBid); w.Write(t.YourBid); }
        w.Write(_franchises.Count);
        foreach (var f in _franchises) { w.Write(f.DistrictId); w.Write(f.Holder); w.Write(f.Exclusive); w.Write(f.DaysLeft); }
        w.Write(_freight.Count);
        foreach (var f in _freight) { w.Write(f.LeaseId); w.Write(f.TonsReady); w.Write(f.TonsWanted); }
        w.Write(_signals.Count);
        foreach (var (c, k) in _signals) { w.Write(c); w.Write((byte)k); }
        w.Write(_reserved.Count);
        foreach (var (c, m) in _reserved) { w.Write(c); w.Write(m); }
    }

    public void Load(BinaryReader r)
    {
        _seed = r.ReadUInt64(); _name = r.ReadString(); _cash = r.ReadInt64(); _nextLineId = r.ReadInt32(); _nextVehicleId = r.ReadInt32(); _quality = r.ReadInt32();
        _tick = r.ReadInt64(); _day = r.ReadInt32(); _month = r.ReadInt32(); _year = r.ReadInt32(); _operator = (OperatorLevel)r.ReadByte(); _owner = r.ReadString();
        _this = Money.Read(r); _last = Money.Read(r);
        int nl = r.ReadInt32();
        for (int i = 0; i < nl; i++)
        {
            var l = new Line { Id = r.ReadInt32(), Name = r.ReadString(), Color = r.ReadString(), Mode = (TransitMode)r.ReadByte(), FareCents = r.ReadInt32() };
            l.Timetable = r.ReadInt32(); l.ServiceDays = r.ReadInt32(); l.Needed = r.ReadInt32(); l.Running = r.ReadInt32();
            l.DaysRun = r.ReadInt32(); l.DaysOnTime = r.ReadInt32(); l.OnTimePercent = r.ReadInt32();
            int ns = r.ReadInt32(); for (int k = 0; k < ns; k++) l.Stops.Add(r.ReadInt32());
            int nv = r.ReadInt32();
            for (int k = 0; k < nv; k++)
                l.Fleet.Add(new Vehicle
                {
                    Id = r.ReadInt32(), Bought = r.ReadInt64(), ServicedAt = r.ReadInt64(), BrokenUntil = r.ReadInt64(), Condition = r.ReadInt32(),
                    Servicing = r.ReadBoolean(), Running = r.ReadBoolean(),
                });
            l.RoundTrip = r.ReadInt32(); l.RouteDirty = r.ReadBoolean();
            l.RouteCells = new int[r.ReadInt32()]; for (int k = 0; k < l.RouteCells.Length; k++) l.RouteCells[k] = r.ReadInt32();
            l.Riders = r.ReadInt32(); l.Headway = r.ReadInt32(); l.Seats = r.ReadInt32(); l.EffectiveFare = r.ReadInt32(); l.Problem = r.ReadString();
            l.Boardings = new int[r.ReadInt32()]; for (int k = 0; k < l.Boardings.Length; k++) l.Boardings[k] = r.ReadInt32();
            l.This = Money.Read(r); l.Last = Money.Read(r);
            l.Freight = r.ReadBoolean(); l.ToEdge = r.ReadBoolean(); l.Tons = r.ReadInt32(); l.SignalLimited = r.ReadBoolean();
            _lines.Add(l);
        }
        int nc = r.ReadInt32(); for (int i = 0; i < nc; i++) _closedUntil[r.ReadInt32()] = r.ReadInt64();
        int ng = r.ReadInt32(); for (int i = 0; i < ng; i++) _log.Add(r.ReadString());
        int nw = r.ReadInt32(); for (int i = 0; i < nw; i++) _wanted.Add(r.ReadInt32());
        int nlots = r.ReadInt32();
        for (int i = 0; i < nlots; i++)
            _lots.Add(new LeasedLot(r.ReadInt32(), r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                r.ReadInt32(), r.ReadInt32(), ReadTerms(r), null, 0, r.ReadBoolean()));
        int no = r.ReadInt32();
        for (int i = 0; i < no; i++)
            _open.Add(new OpenLot(r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), ReadTerms(r)));
        int nd = r.ReadInt32();
        for (int i = 0; i < nd; i++)
        {
            int id = r.ReadInt32();
            string name = r.ReadString();
            int pop = r.ReadInt32(), jobs = r.ReadInt32();
            _districts.Add(new District(id, name, 0, 0, 0, 0, pop, jobs, 0, 0, 0, r.ReadInt32()));
        }
        int nof = r.ReadInt32();
        for (int i = 0; i < nof; i++) _offers.Add(new PaidActionOffer(r.ReadString(), r.ReadString(), r.ReadInt64(), r.ReadBoolean(), r.ReadString()));
        int nbuy = r.ReadInt32();
        for (int i = 0; i < nbuy; i++) _buying.Add(new AuthorityAction(r.ReadString(), r.ReadInt32(), r.ReadInt64()));
        int nbo = r.ReadInt32();
        for (int i = 0; i < nbo; i++) _bought[r.ReadInt32()] = (r.ReadBoolean(), r.ReadInt64());
        int nt = r.ReadInt32();
        for (int i = 0; i < nt; i++) _tenders.Add(new Tender(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean(), r.ReadInt64(), r.ReadInt64()));
        int nf = r.ReadInt32();
        for (int i = 0; i < nf; i++) _franchises.Add(new Franchise(r.ReadInt32(), r.ReadString(), r.ReadBoolean(), r.ReadInt32()));
        int nfr = r.ReadInt32();
        for (int i = 0; i < nfr; i++) _freight.Add(new FreightLot(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        int nsg = r.ReadInt32();
        for (int i = 0; i < nsg; i++) _signals[r.ReadInt32()] = (SignalKind)r.ReadByte();
        int nrs = r.ReadInt32();
        for (int i = 0; i < nrs; i++) _reserved[r.ReadInt32()] = r.ReadInt32();
    }

    static void WriteTerms(BinaryWriter w, LeaseTerms t)
    {
        w.Write((byte)t.Mode); w.Write(t.RentPerMonth); w.Write(t.TaxTenths); w.Write(t.SubsidyPerMonth); w.Write(t.FareCapCents); w.Write(t.NoticeMonths);
    }

    static LeaseTerms ReadTerms(BinaryReader r) => new((LeaseMode)r.ReadByte(), r.ReadInt64(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32());
}
