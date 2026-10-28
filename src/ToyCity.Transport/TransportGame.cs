using Google.Protobuf;
using ToyCity.Contract;
using W = ToyCity.Transport.Wire;

namespace ToyCity.Transport;

/// <summary>
/// The bus company: lines through leased stops, buses kept at a leased depot, fares, wages and rent, and a profit and
/// loss for every route. Riders come from the city's commuters, who take a line when it beats driving or walking;
/// the company sees yesterday's riders and boardings per stop and takes the fares. It plays itself when nobody runs
/// it (caretaker or autopilot), and takes commands from its owner when someone does.
/// </summary>
public sealed class TransportGame : ISideGame
{
    public const string Id = "transport";
    public const int BusPrice = 3_000, BusResale = 1_500;
    public const int WagePerBus = 500, UpkeepPerBus = 120;
    public const int SeatsPerBus = 40, BusesPerDepot = 12, DriversPerBus = 2, DepotStaff = 2;
    public const int DefaultFareCents = 150, MaxFareCents = 1_000;
    public const int TurnaroundMinutes = 4, MinHeadway = 3, TargetHeadway = 15;
    public const int BreakdownOneIn = 500, BreakdownDays = 2, ClosedDays = 5;
    static readonly string[] Palette = { "#e04f5f", "#3f88e0", "#2fa36b", "#e0a030", "#9b59b6", "#16a2b8", "#d35400", "#7f8c8d" };

    public Manifest Manifest { get; } = new(Id, "Toy Transit Co.", ContractInfo.Version, new[] { "bus-stop", "bus-depot" },
        "A bus company: lines through leased stops, buses from a leased depot, fares from the city's commuters.");

    sealed class Money
    {
        public long Fares, Wages, Upkeep, Rent, Subsidy, Standing;
        public long Profit => Fares + Subsidy - Wages - Upkeep - Rent - Standing;
        public void Write(BinaryWriter w) { w.Write(Fares); w.Write(Wages); w.Write(Upkeep); w.Write(Rent); w.Write(Subsidy); w.Write(Standing); }
        public static Money Read(BinaryReader r) => new() { Fares = r.ReadInt64(), Wages = r.ReadInt64(), Upkeep = r.ReadInt64(), Rent = r.ReadInt64(), Subsidy = r.ReadInt64(), Standing = r.ReadInt64() };
        public W.MonthMoney ToWire() => new() { Fares = Fares, Wages = Wages, Upkeep = Upkeep, Rent = Rent, Subsidy = Subsidy, Standing = Standing, Profit = Profit };
    }

    sealed class Line
    {
        public int Id;
        public string Name = "";
        public string Color = "";
        public List<int> Stops = new();
        public int Buses;
        public int FareCents = DefaultFareCents;
        /// <summary>The tick each broken bus is back on the road.</summary>
        public List<long> BrokenUntil = new();
        public int RoundTrip;
        public int[] RouteCells = Array.Empty<int>();
        public bool RouteDirty = true;
        public int Riders, Headway, Seats, EffectiveFare;
        public int[] Boardings = Array.Empty<int>();
        public string Problem = "";
        public Money This = new(), Last = new();
        public int Working => Buses - BrokenUntil.Count;
    }

    ulong _seed;
    string _name = "Toy Transit Co.";
    long _cash = 25_000;
    int _nextLineId = 1;
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

    public void NewGame(ulong seed) => _seed = seed;

    int Buses => _lines.Sum(l => l.Buses);
    int Depots => _lots.Count(l => l.Kind == "bus-depot");
    int Capacity => Depots * BusesPerDepot;
    LeasedLot? Lot(int leaseId) => _lots.FirstOrDefault(l => l.LeaseId == leaseId);

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
        _lots.Clear(); _lots.AddRange(input.Lots);
        _open.Clear(); _open.AddRange(input.OpenLots);
        if (input.NewMonth) { _districts.Clear(); _districts.AddRange(input.Districts); }
        _offers.Clear(); _offers.AddRange(input.Offers);
        HearStanding(input);

        // Lines lose stops the company no longer leases.
        foreach (var line in _lines)
        {
            int before = line.Stops.Count;
            line.Stops.RemoveAll(s => Lot(s) is not { Kind: "bus-stop" });
            if (line.Stops.Count != before) { line.RouteDirty = true; Log($"The {line.Name} line lost a stop the company no longer leases."); }
        }
        // Too many buses for the depots: sell the extras.
        while (Buses > Capacity && _lines.Where(l => l.Buses > 0).OrderBy(l => l.Riders).ThenBy(l => l.Id).FirstOrDefault() is { } spare)
        {
            spare.Buses--;
            _cash += BusResale;
            Log($"Sold a bus from the {spare.Name} line: the depots have no room for it.");
        }

        HearIncidents(input);
        TakeFares(input, output);
        RepairAndBreak(city, output);
        if (input.NewMonth) CloseMonth(input, output);
        if (_operator != OperatorLevel.Manual) PlayItself(input, city);
        foreach (var line in _lines) if (line.RouteDirty) Route(line, city);
        Publish(output);
    }

    /// <summary>The city's answers to yesterday's paid actions: pay for those that went ahead.</summary>
    void HearStanding(CityToGame input)
    {
        foreach (var r in input.AuthorityResults)
        {
            int i = _districts.FindIndex(d => d.Id == r.DistrictId);
            if (i >= 0) _districts[i] = _districts[i] with { Rating = r.Rating };
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
            foreach (var lot in _lots.Where(l => l.Kind == "bus-stop"))
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

    void RepairAndBreak(ICityQueries city, GameToCity output)
    {
        foreach (var line in _lines)
        {
            line.BrokenUntil.RemoveAll(t => t <= _tick);
            if (line.BrokenUntil.Count > line.Buses) line.BrokenUntil.RemoveRange(line.Buses, line.BrokenUntil.Count - line.Buses);
            if (line.Headway == 0) continue;
            for (int bus = 0; bus < line.Working; bus++)
            {
                if (city.Hash((int)_tick, line.Id, bus, 0x6273) % BreakdownOneIn != 0) continue;
                line.BrokenUntil.Add(_tick + BreakdownDays);
                var at = Lot(line.Stops[(int)(city.Hash((int)_tick, line.Id, bus, 0x6274) % (ulong)line.Stops.Count)]);
                string text = $"A {_name} bus broke down on the {line.Name} line. Riders wait longer for a couple of days.";
                output.Incidents.Add(new Incident(0, "breakdown", at?.X ?? -1, at?.Y ?? -1, Id, line.Name, 1, text));
                Log($"A bus broke down on the {line.Name} line.");
                break;
            }
        }
    }

    /// <summary>The first of the month: the month's wages and upkeep, the city's bills, and the books close.</summary>
    void CloseMonth(CityToGame input, GameToCity output)
    {
        foreach (var line in _lines)
        {
            long wages = (long)line.Buses * WagePerBus, upkeep = (long)line.Buses * UpkeepPerBus;
            line.This.Wages += wages; line.This.Upkeep += upkeep;
            _this.Wages += wages; _this.Upkeep += upkeep;
            _cash -= wages + upkeep;
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

    List<int> RunningStops(Line line) => line.Stops.Where(s => !_closedUntil.ContainsKey(s)).ToList();

    /// <summary>Times the line along the streets and lays out its route for drawing.</summary>
    void Route(Line line, ICityQueries city)
    {
        line.RouteDirty = false;
        line.RoundTrip = 0;
        line.RouteCells = Array.Empty<int>();
        if (line.Stops.Count < 2) return;
        var cells = new List<int>();
        int minutes = 0;
        for (int i = 0; i + 1 < line.Stops.Count; i++)
        {
            var a = Lot(line.Stops[i]); var b = Lot(line.Stops[i + 1]);
            if (a == null || b == null || a.AccessCell < 0 || b.AccessCell < 0) return;
            var r = city.Route(a.AccessCell, b.AccessCell);
            if (r == null) return;
            minutes += r.Minutes + 1;
            if (cells.Count > 0) cells.RemoveAt(cells.Count - 1);
            cells.AddRange(r.Cells);
        }
        line.RoundTrip = minutes * 11 / 10 * 2 + TurnaroundMinutes;
        line.RouteCells = cells.ToArray();
    }

    /// <summary>What the company runs today, and what it tells the city.</summary>
    void Publish(GameToCity output)
    {
        int quality = 0, running = 0, riders = 0;
        foreach (var line in _lines)
        {
            line.Headway = 0; line.Seats = 0;
            line.EffectiveFare = Math.Min(line.FareCents, FareCap(line) is int cap and >= 0 ? cap : int.MaxValue);
            var stops = RunningStops(line);
            line.Problem =
                Depots == 0 ? "No depot: lease a bus depot to keep buses." :
                line.Stops.Count < 2 ? "A line needs at least two stops." :
                line.RoundTrip == 0 ? "No street route joins its stops." :
                line.Buses == 0 ? "No buses on it." :
                line.Working == 0 ? "Every bus is in the workshop." :
                stops.Count < 2 ? "Too many of its stops are closed." : "";
            if (line.Problem.Length > 0) continue;
            line.Headway = Math.Max(MinHeadway, line.RoundTrip / line.Working);
            line.Seats = SeatsPerBus * 60 / line.Headway;
            output.Lines.Add(new TransitLine(line.Id, line.Name, line.Color, stops, line.Headway, line.EffectiveFare, line.Seats));
            running++;
            riders += line.Riders;
            int load = LoadPercent(line);
            quality += Math.Clamp(100 - Math.Max(0, load - 80) - line.BrokenUntil.Count * 10 - (stops.Count < line.Stops.Count ? 10 : 0), 0, 100);
        }
        _quality = running == 0 ? 50 : quality / running;
        output.ServiceQuality = _quality;
        // Drivers and mechanics work at the depots.
        var depots = _lots.Where(l => l.Kind == "bus-depot").ToList();
        for (int i = 0; i < depots.Count; i++)
        {
            int share = Buses / depots.Count + (i < Buses % depots.Count ? 1 : 0);
            output.JobsByLease[depots[i].LeaseId] = share * DriversPerBus + DepotStaff;
        }
        output.LeaseRequests.AddRange(_wanted);
        _wanted.Clear();
        output.AuthorityActions.AddRange(_buying);
        _buying.Clear();
        output.Status = running == 0 ? $"{_name}: no buses running" : $"{_name}: {running} line{(running == 1 ? "" : "s")}, {riders:N0} riders yesterday";
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
    /// Caretaker keeps the company solvent and the buses it has running. Autopilot also grows it: leases a depot and
    /// stops where people live, draws a line through them, and adds buses where they're full.
    /// </summary>
    void PlayItself(CityToGame input, ICityQueries city)
    {
        if (_cash < 0 && _lines.Where(l => l.Buses > 1).OrderBy(LoadPercent).ThenBy(l => l.Id).FirstOrDefault() is { } thin)
        {
            thin.Buses--;
            _cash += BusResale;
            Log($"Sold a bus from the {thin.Name} line to pay the bills.");
        }
        if (_operator != OperatorLevel.Autopilot) return;
        bool monthly = input.NewMonth || _tick % 30 == 1;
        if (!monthly) return;

        // A district that has gone off the company: an advertising campaign, when there's money for one.
        if (_offers.FirstOrDefault(o => o.Kind == "advertise") is { Allowed: true } ad && _cash >= ad.Cost + 20_000
            && _districts.Where(d => d.Rating < 0 && _lots.Any(l => l.DistrictId == d.Id)).OrderBy(d => d.Rating).ThenBy(d => d.Id).FirstOrDefault() is { } sour)
            _buying.Add(new AuthorityAction("advertise", sour.Id));

        if (Depots == 0 && _open.FirstOrDefault(o => o.Kind == "bus-depot") is { } depot) _wanted.Add(depot.BuildingId);
        int stops = _lots.Count(l => l.Kind == "bus-stop");
        if (stops < 8)
        {
            // Stops in the busiest districts first.
            var busy = _districts.OrderByDescending(d => d.Population + d.Jobs).ThenBy(d => d.Id).Select(d => d.Id).ToList();
            foreach (var o in _open.Where(o => o.Kind == "bus-stop").OrderBy(o => busy.IndexOf(o.DistrictId) is int k and >= 0 ? k : 999).ThenBy(o => o.BuildingId).Take(2))
                _wanted.Add(o.BuildingId);
        }
        var leasedStops = _lots.Where(l => l.Kind == "bus-stop" && !l.BadStanding).Select(l => l.LeaseId).ToList();
        if (_lines.Count == 0 && leasedStops.Count >= 2)
        {
            var line = NewLine("Route 1", Chain(leasedStops));
            Log($"Opened the {line.Name} line through {line.Stops.Count} stops.");
        }
        else if (_lines.Count > 0)
        {
            var main = _lines[0];
            foreach (var s in leasedStops.Where(s => !_lines.Any(l => l.Stops.Contains(s))))
            {
                main.Stops = Chain(main.Stops.Append(s).ToList());
                main.RouteDirty = true;
                Log($"Added a stop to the {main.Name} line.");
            }
        }
        foreach (var line in _lines)
        {
            if (Depots == 0) break;
            int load = LoadPercent(line);
            // A bus at least every quarter of an hour, and more when they're full.
            bool sparse = line.Buses == 0 || line.RoundTrip / Math.Max(1, line.Buses) > TargetHeadway;
            int before = line.Buses;
            while ((sparse || load > 90) && Buses < Capacity && _cash >= BusPrice + 5_000)
            {
                line.Buses++;
                _cash -= BusPrice;
                Log(line.Buses == 1 ? $"Bought a bus for the {line.Name} line."
                    : sparse ? $"Bought another bus for the {line.Name} line, to run every {line.RoundTrip / line.Buses} minutes."
                    : $"Bought another bus for the {line.Name} line: it was {load}% full at the peak.");
                if (!sparse) break;
                sparse = line.RoundTrip / line.Buses > TargetHeadway;
            }
            if (line.Buses == before && line.Buses > 1 && load < 25 && line.Riders > 0 && line.RoundTrip / (line.Buses - 1) <= TargetHeadway)
            {
                line.Buses--;
                _cash += BusResale;
                Log($"Sold a bus from the {line.Name} line: it ran {load}% full.");
            }
        }
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

    Line NewLine(string name, List<int> stops)
    {
        var line = new Line { Id = _nextLineId++, Name = name, Stops = stops };
        line.Color = Palette[(line.Id - 1) % Palette.Length];
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
                if (stops.Count < 2) return new CommandResult(false, "A line needs at least two stops.");
                if (stops.Any(s => Lot(s) is not { Kind: "bus-stop" })) return new CommandResult(false, "Every stop on a line must be a bus stop the company leases.");
                if (stops.Distinct().Count() != stops.Count) return new CommandResult(false, "A line can't visit the same stop twice.");
                if (c.FareCents is < 0 or > MaxFareCents) return new CommandResult(false, $"Fares run from free to ${MaxFareCents / 100}.");
                var line = c.LineId == 0 ? NewLine(c.Name.Length > 0 ? c.Name : $"Route {_nextLineId}", stops) : _lines.FirstOrDefault(l => l.Id == c.LineId);
                if (line == null) return new CommandResult(false, "That line has been closed.");
                if (c.Name.Length > 0) line.Name = c.Name.Length > 30 ? c.Name[..30] : c.Name;
                line.Stops = stops;
                line.FareCents = c.FareCents;
                Route(line, city);
                if (line.RoundTrip == 0) return new CommandResult(true, $"Saved the {line.Name} line, but no street route joins all its stops.");
                Log(c.LineId == 0 ? $"Opened the {line.Name} line." : $"Changed the {line.Name} line.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.DeleteLine:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.DeleteLine.LineId);
                if (line == null) return new CommandResult(false, "That line has already closed.");
                _cash += (long)line.Buses * BusResale;
                _lines.Remove(line);
                Log($"Closed the {line.Name} line and sold its {line.Buses} buses.");
                return new CommandResult(true, "");
            }
            case W.Command.KindOneofCase.Buses:
            {
                var line = _lines.FirstOrDefault(l => l.Id == cmd.Buses.LineId);
                if (line == null) return new CommandResult(false, "That line has closed.");
                int want = Math.Max(0, cmd.Buses.Buses);
                if (want > line.Buses)
                {
                    if (Depots == 0) return new CommandResult(false, "Lease a bus depot first: buses need somewhere to live.");
                    int add = want - line.Buses;
                    if (Buses + add > Capacity) return new CommandResult(false, $"The depots hold {Capacity} buses. Lease another depot for more.");
                    long cost = (long)add * BusPrice;
                    if (cost > _cash) return new CommandResult(false, $"{add} bus{(add == 1 ? "" : "es")} cost ${cost:N0} and the company has ${_cash:N0}.");
                    _cash -= cost;
                }
                else _cash += (long)(line.Buses - want) * BusResale;
                Log($"The {line.Name} line now has {want} bus{(want == 1 ? "" : "es")}.");
                line.Buses = want;
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
            Owner = _owner, Buses = Buses, BusCapacity = Capacity, BusPrice = BusPrice, ThisMonth = _this.ToWire(), LastMonth = _last.ToWire(),
            ServiceQuality = _quality, Day = _day,
        };
        foreach (var l in _lines)
        {
            var lv = new W.LineView
            {
                Id = l.Id, Name = l.Name, Color = l.Color, Buses = l.Buses, Broken = l.BrokenUntil.Count, FareCents = l.FareCents, FareCapCents = FareCap(l),
                HeadwayMinutes = l.Headway, RoundTripMinutes = l.RoundTrip, Riders = l.Riders, SeatsPerHour = l.Seats, LoadPercent = LoadPercent(l),
                ThisMonth = l.This.ToWire(), LastMonth = l.Last.ToWire(), Problem = l.Problem,
            };
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
        foreach (var o in _offers) v.Offers.Add(new W.OfferView { Kind = o.Kind, Name = o.Name, Cost = o.Cost, Allowed = o.Allowed, WhyNot = o.WhyNot });
        v.Log.AddRange(_log);
        return v.ToByteArray();
    }

    public void Save(BinaryWriter w)
    {
        w.Write(_seed); w.Write(_name); w.Write(_cash); w.Write(_nextLineId); w.Write(_quality);
        w.Write(_tick); w.Write(_day); w.Write(_month); w.Write(_year); w.Write((byte)_operator); w.Write(_owner);
        _this.Write(w); _last.Write(w);
        w.Write(_lines.Count);
        foreach (var l in _lines)
        {
            w.Write(l.Id); w.Write(l.Name); w.Write(l.Color); w.Write(l.Buses); w.Write(l.FareCents);
            w.Write(l.Stops.Count); foreach (var s in l.Stops) w.Write(s);
            w.Write(l.BrokenUntil.Count); foreach (var t in l.BrokenUntil) w.Write(t);
            w.Write(l.RoundTrip); w.Write(l.RouteDirty);
            w.Write(l.RouteCells.Length); foreach (var c in l.RouteCells) w.Write(c);
            w.Write(l.Riders); w.Write(l.Headway); w.Write(l.Seats); w.Write(l.EffectiveFare); w.Write(l.Problem);
            w.Write(l.Boardings.Length); foreach (var b in l.Boardings) w.Write(b);
            l.This.Write(w); l.Last.Write(w);
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
        foreach (var b in _buying) { w.Write(b.Kind); w.Write(b.DistrictId); }
        w.Write(_bought.Count);
        foreach (var (d, b) in _bought) { w.Write(d); w.Write(b.Statue); w.Write(b.ExclusiveUntil); }
    }

    public void Load(BinaryReader r)
    {
        _seed = r.ReadUInt64(); _name = r.ReadString(); _cash = r.ReadInt64(); _nextLineId = r.ReadInt32(); _quality = r.ReadInt32();
        _tick = r.ReadInt64(); _day = r.ReadInt32(); _month = r.ReadInt32(); _year = r.ReadInt32(); _operator = (OperatorLevel)r.ReadByte(); _owner = r.ReadString();
        _this = Money.Read(r); _last = Money.Read(r);
        int nl = r.ReadInt32();
        for (int i = 0; i < nl; i++)
        {
            var l = new Line { Id = r.ReadInt32(), Name = r.ReadString(), Color = r.ReadString(), Buses = r.ReadInt32(), FareCents = r.ReadInt32() };
            int ns = r.ReadInt32(); for (int k = 0; k < ns; k++) l.Stops.Add(r.ReadInt32());
            int nb = r.ReadInt32(); for (int k = 0; k < nb; k++) l.BrokenUntil.Add(r.ReadInt64());
            l.RoundTrip = r.ReadInt32(); l.RouteDirty = r.ReadBoolean();
            l.RouteCells = new int[r.ReadInt32()]; for (int k = 0; k < l.RouteCells.Length; k++) l.RouteCells[k] = r.ReadInt32();
            l.Riders = r.ReadInt32(); l.Headway = r.ReadInt32(); l.Seats = r.ReadInt32(); l.EffectiveFare = r.ReadInt32(); l.Problem = r.ReadString();
            l.Boardings = new int[r.ReadInt32()]; for (int k = 0; k < l.Boardings.Length; k++) l.Boardings[k] = r.ReadInt32();
            l.This = Money.Read(r); l.Last = Money.Read(r);
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
        for (int i = 0; i < nbuy; i++) _buying.Add(new AuthorityAction(r.ReadString(), r.ReadInt32()));
        int nbo = r.ReadInt32();
        for (int i = 0; i < nbo; i++) _bought[r.ReadInt32()] = (r.ReadBoolean(), r.ReadInt64());
    }

    static void WriteTerms(BinaryWriter w, LeaseTerms t)
    {
        w.Write((byte)t.Mode); w.Write(t.RentPerMonth); w.Write(t.TaxTenths); w.Write(t.SubsidyPerMonth); w.Write(t.FareCapCents); w.Write(t.NoticeMonths);
    }

    static LeaseTerms ReadTerms(BinaryReader r) => new((LeaseMode)r.ReadByte(), r.ReadInt64(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32());
}
