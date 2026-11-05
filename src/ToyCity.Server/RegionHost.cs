using System.Diagnostics;
using System.Net.WebSockets;
using Google.Protobuf;
using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>
/// Owns the running region: its one clock, a <see cref="SimHost"/> for each founded city, the connections and the
/// save folder. Each connection is in one city at a time and can move between them without reconnecting.
/// <para>
/// Locks: <see cref="_gate"/> first, then cities' gates in tile order. The clock holds all of them while it steps
/// the region; a city's own messages take only that city's gate.
/// </para>
/// </summary>
public sealed partial class RegionHost : IDisposable
{
    readonly object _gate = new();
    readonly ContentDb _content;
    readonly string _saveDir;
    readonly ILogger<RegionHost> _log;
    readonly HostConfig _host;
    readonly AccountStore? _accounts;
    readonly Thread? _thread;
    volatile bool _running = true;
    Region _region;
    SimHost?[] _cities;
    readonly Dictionary<int, ByteString> _pictures = new();
    string _regionSig = "";

    /// <summary>Milliseconds per tick (one day) at speeds 1, 2 and 3: a game year takes an hour, twenty minutes or five.</summary>
    /// <summary>How long a day of the day clock lasts at each speed (Beta 3.2, C2): an hour, half an hour, ten minutes.</summary>
    public static readonly int[] ClockDayMs = { 0, 3_600_000, 1_800_000, 600_000 };
    /// <summary>The region's calendar pace: calendar days per clock day. Every city here keeps it.</summary>
    volatile int _pace;

    /// <summary>How long a mayor who isn't an admin may hold the whole region paused.</summary>
    public const int MayorPauseSeconds = 120;

    /// <summary>Sign-in attempts a connection gets before the server hangs up.</summary>
    const int MaxSignInFailures = 5;
    /// <summary>The largest message a client may send; anything bigger is hostile or broken.</summary>
    const int MaxMessageBytes = 1 << 20;
    /// <summary>Unreadable messages a connection may send before it is dropped.</summary>
    const int MaxMalformed = 3;

    // The clock. Each player who set a speed asks for it; the region runs at the slowest ask of anyone online.
    readonly Dictionary<string, (int Speed, long At)> _asks = new(StringComparer.Ordinal);
    int _hostSpeed = 1;
    volatile int _speed = 1;
    /// <summary>A player is at an emergency scene somewhere in the region, as of the last day.</summary>
    volatile bool _sceneOpen;
    string _speedBy = "";
    readonly Stopwatch _clock = Stopwatch.StartNew();

    // Who is connected, anywhere in the region, by name: connections each.
    readonly Dictionary<string, int> _online = new(StringComparer.Ordinal);

    // The side games every city here runs are registered with the first city host; a save may need them before that.
    static RegionHost() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SimHost).TypeHandle);

    public RegionHost(ContentDb content, string saveDir, ILogger<RegionHost> log, HostConfig? host = null, AccountStore? accounts = null)
    {
        _content = content;
        _saveDir = saveDir;
        _log = log;
        _host = host ?? HostConfig.Solo();
        _accounts = accounts ?? (_host.Dedicated ? new AccountStore(Path.Combine(saveDir, "accounts.json")) : null);
        Directory.CreateDirectory(saveDir);
        _region = StartingRegion(content);
        _pace = World.IsPace(_host.Pace) ? _host.Pace : 360;
        _cities = new SimHost?[_region.Tiles.Length];
        InitLinks();
        lock (_gate) OpenCities();
        _thread = new Thread(Loop) { IsBackground = true, Name = "region" };
        _thread.Start();
    }

    /// <summary>A new server's region: Toy Valley, three tiles by three, farm country, with Toyville founded in the middle.</summary>
    static Region StartingRegion(ContentDb content)
    {
        var r = Region.Create(content, "Toy Valley", 2026, 3, 3, 128);
        // Farm country: the valley's low ground to the south and east is farmed.
        r.Farms = true;
        r.Found(1, 1, "Toyville", SimHost.LocalPlayer, starter: true);
        return r;
    }

    public Region Region => _region;

    /// <summary>The region's speed, 0 (paused) to 3.</summary>
    public int Speed => _speed;
    /// <summary>The region's calendar pace, in calendar days per clock day.</summary>
    public int Pace => _pace;

    /// <summary>How long a calendar day lasts in real time at a speed and pace: a clock day's share.</summary>
    public static int DayMs(int speed, int pace) => speed <= 0 ? 0 : ClockDayMs[Math.Min(speed, 3)] / Math.Max(1, pace);

    /// <summary>
    /// Every city here keeps the region's pace: one that differs (just founded, or arrived from elsewhere) is told, by
    /// a logged action, so its replays keep the same hours. Caller holds the gates.
    /// </summary>
    void KeepPace()
    {
        foreach (var t in _region.Tiles)
            if (t.City is { } w && w.Pace != _pace) { w.Enqueue(new SetPaceAction(_pace)); w.ApplyPending(); }
    }

    /// <summary>
    /// Whether this player may add that tile to the region now (G4), under the region's charter (Beta 4): an admin
    /// always; else the charter's rule, any mayor here, or none (admins only). Returns why not, or null; sets
    /// <paramref name="vote"/> when it has to go to a vote of the mayors first.
    /// </summary>
    string? CantGrow(Client c, int col, int row, out bool vote)
    {
        vote = false;
        if (_region.CantGrow(col, row) is { } why) return why;
        if (c.Admin) return null;
        bool mayor = _region.Tiles.Any(t => t.Mayor == c.Player);
        switch (_region.Charter.Grow)
        {
            case CharterRule.Anyone when mayor: return null;
            case CharterRule.Vote when mayor:
                // A sole mayor is the whole vote.
                vote = _region.Voters().Count > 1;
                return null;
            case CharterRule.Admins: return "This region's charter lets only the server's admins add land to it.";
            default: return "Only a mayor here, or this server's admins, can add land to the region.";
        }
    }

    /// <summary>Adds a tile, with the notices. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> Grown(int col, int row, string by)
    {
        _region.Grow(col, row);
        _pictures.Remove(row * _region.Cols + col);
        var list = Cities.SelectMany(city => city.Notice($"{by} added a tile of open land to {_region.Name}. Anyone can found a city on it.", "info")).ToList();
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    /// <summary>
    /// Why this player can't annex that land for the city they're in, as the region's charter has it, or null. With
    /// <paramref name="vote"/> set, the region's mayors vote on it first. Caller holds every gate.
    /// </summary>
    string? CantAnnex(Client c, RegionTile here, int col, int row, out bool vote)
    {
        vote = false;
        var a = here.Anchor ?? here;
        if (_links.ElementAtOrDefault(a.Row * _region.Cols + a.Col) != null) return $"{a.Name} runs on its own host: a city hosted apart can't annex, take in a neighbour or be taken in.";
        if (_region.CantAnnex(a, col, row) is { } why) return why;
        if (c.Admin) return null;
        bool mine = a.Mayor == c.Player;
        switch (_region.Charter.Annex)
        {
            case CharterRule.Admins: return "This region's charter lets only the server's admins annex land.";
            case CharterRule.Anyone when mine: return null;
            case CharterRule.Vote when mine:
                vote = _region.Voters().Count > 1;
                return null;
            default: return $"Only {a.Name}'s mayor can annex land for it.";
        }
    }

    /// <summary>
    /// A city annexes the land beside it: its host lets everyone go, the region puts the city on its bigger map, and a
    /// new host takes them back in. Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> Annexed(RegionTile city, int col, int row, string by)
    {
        var a = city.Anchor ?? city;
        string name = a.Name, who = by == a.Mayor ? "" : $" ({by})";
        return Rehost(new[] { a }, () => _region.Annex(a, col, row),
            (cols, rows) => $"{name} annexed the land beside it{who}: it's {cols} × {rows} tiles now.");
    }

    /// <summary>A city takes in its neighbour, as agreed: both hosts let everyone go, and the one city's host takes them in. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> TookOver(RegionTile buyer, RegionTile seller, long price)
    {
        var a = buyer.Anchor ?? buyer;
        var b = seller.Anchor ?? seller;
        string an = a.Name, bn = b.Name;
        return Rehost(new[] { a, b }, () => _region.TakeOver(a, b, price),
            (cols, rows) => $"{an} took in {bn} for ${price:N0}: it's one city of {cols} × {rows} tiles now.");
    }

    /// <summary>
    /// Puts cities onto a new world (an annex or a takeover): their hosts let everyone go, the change is made, and the
    /// new city's host takes them all in. Everyone else hears of it. Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> Rehost(RegionTile[] cities, Func<World> change, Func<int, int, string> told)
    {
        var list = new List<(Client, P.ServerMessage)>();
        var slots = cities.Select(Slot).Distinct().ToList();
        var moving = new List<Client>();
        foreach (int i in slots)
            if (_cities[i] is { } h)
                foreach (var cl in h.Clients.ToList()) { list.AddRange(h.Leave(cl)); moving.Add(cl); }
        var w = change();
        var anchor = _region.TileOf(w)!;
        foreach (int i in slots) { _cities[i]?.Dispose(); _cities[i] = null; }
        var (nc, nr, ncols, nrows) = _region.Footprint(anchor);
        for (int r = nr; r < nr + nrows; r++)
        for (int k = nc; k < nc + ncols; k++)
            _pictures.Remove(r * _region.Cols + k);
        OpenCities();
        var host = _cities[Slot(anchor)]!;
        foreach (var cl in moving)
        {
            var (mine, others) = host.Join(cl);
            list.AddRange(mine.Select(m => (cl, m)));
            list.AddRange(others);
        }
        string text = told(ncols, nrows);
        list.AddRange(Cities.Where(h => h != host).SelectMany(h => h.Notice(text, "info")));
        list.AddRange(host.Deltas(forceAlerts: true));
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    /// <summary>A mayor's offer, answer or withdrawal on taking in a neighbour. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> Takeover(Client c, P.TakeoverRequest t, out string? why)
    {
        why = null;
        if (c.Tile is not { } at) { why = "Open a city first."; return new(); }
        var here = at.Anchor ?? at;
        bool Hosted(RegionTile x) => _links.ElementAtOrDefault(Slot(x)) != null;
        switch (t.Op)
        {
            case P.TakeoverRequest.Types.Op.Offer:
            {
                if (_region.TileAt(t.Col, t.Row) is not { Founded: true } there) { why = "There's no city there."; return new(); }
                var them = there.Anchor!;
                if (!c.Admin && here.Mayor != c.Player) { why = $"Only {here.Name}'s mayor can offer to take in a neighbour."; return new(); }
                if (Hosted(here) || Hosted(them)) { why = "A city on its own host can't take in a neighbour or be taken in."; return new(); }
                TakeoverOffer? o;
                string? answer;
                try { o = _region.OfferTakeover(here, them, t.Price, c.Player, out answer); }
                catch (InvalidOperationException e) { why = e.Message; return new(); }
                if (answer != null) { why = answer; return new(); }
                // A computer mayor agreed: done now.
                if (them.City!.ComputerMayor) return TookOver(here, them, t.Price);
                var list = _cities[Slot(them)]?.Notice($"{c.Player} offers ${t.Price:N0} for {here.Name} to take in {them.Name}. Answer on the region map.", "info") ?? new();
                list.AddRange(RegionIfChanged(force: true));
                return list;
            }
            case P.TakeoverRequest.Types.Op.Accept:
            {
                if (_region.Takeovers.FirstOrDefault(x => x.Id == t.Id) is not { } o) { why = "That offer isn't open any more."; return new(); }
                var (buyer, seller) = _region.Parties(o);
                if (seller.Mayor != c.Player) { why = $"Only {seller.Name}'s mayor can agree to it."; return new(); }
                if (Hosted(buyer) || Hosted(seller)) { why = "A city on its own host can't take in a neighbour or be taken in."; return new(); }
                if (_region.CantTakeOver(buyer, seller, o.Price) is { } no) { why = no; return new(); }
                return TookOver(buyer, seller, o.Price);
            }
            default:
            {
                if (_region.Takeovers.FirstOrDefault(x => x.Id == t.Id) is not { } o) { why = "That offer isn't open any more."; return new(); }
                var (buyer, seller) = _region.Parties(o);
                bool decline = t.Op == P.TakeoverRequest.Types.Op.Decline;
                if (decline ? seller.Mayor != c.Player : buyer.Mayor != c.Player && o.By != c.Player) { why = "That offer isn't yours to answer."; return new(); }
                _region.Takeovers.Remove(o);
                var list = _cities[Slot(decline ? buyer : seller)]?.Notice(decline ? $"{seller.Name}'s mayor turned down {buyer.Name}'s offer to take it in." : $"{buyer.Name} withdrew its offer to take in {seller.Name}.", "info") ?? new();
                list.AddRange(RegionIfChanged(force: true));
                return list;
            }
        }
    }

    /// <summary>
    /// A player asks for a new charter: an admin's takes effect now; a mayor's goes to a vote (or takes effect, when
    /// they're the only mayor). Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> SetCharter(Client c, P.SetCharter set, out string? why)
    {
        why = null;
        var charter = RegionMapping.Charter(set.Charter ?? new P.RegionCharter());
        if (_region.CantCharter(charter) is { } bad) { why = bad; return new(); }
        bool mayor = _region.Tiles.Any(t => t.Mayor == c.Player);
        if (!c.Admin && !mayor) { why = "Only the region's mayors and this server's admins can change its charter."; return new(); }
        if (c.Admin || _region.Voters().Count <= 1)
        {
            _region.Charter = charter;
            var list = Cities.SelectMany(city => city.Notice($"{c.Player} set {_region.Name}'s charter: growing by {RegionCharter.Name(charter.Grow)}, annexing by {RegionCharter.Name(charter.Annex)}.", "info")).ToList();
            list.AddRange(RegionIfChanged(force: true));
            return list;
        }
        return Asked(c, VoteKind.Charter, -1, -1, charter, out why);
    }

    /// <summary>Opens a vote, and tells the region. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> Asked(Client c, VoteKind kind, int col, int row, RegionCharter? charter, out string? why, int toCol = -1, int toRow = -1)
    {
        why = null;
        RegionVote v;
        try { v = _region.Ask(kind, c.Player, col, row, charter, toCol, toRow); }
        catch (InvalidOperationException e) { why = e.Message; return new(); }
        var list = Cities.SelectMany(city => city.Notice($"{c.Player} asks the region's mayors to vote on {_region.Describe(v)}. Open the region map to vote.", "info")).ToList();
        list.AddRange(DecideVotes());
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    /// <summary>Carries out the votes that passed and drops those that failed or lapsed, with notices. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> DecideVotes()
    {
        var list = new List<(Client, P.ServerMessage)>();
        if (_region.Votes.Count == 0) return list;
        var (passed, failed) = _region.Decide();
        foreach (var v in failed)
            list.AddRange(Cities.SelectMany(city => city.Notice($"The region's mayors turned down {_region.Describe(v)}.", "info")));
        foreach (var v in passed)
        {
            string what = _region.Describe(v);
            switch (v.Kind)
            {
                case VoteKind.Charter:
                    _region.Charter = v.Charter!;
                    list.AddRange(Cities.SelectMany(city => city.Notice($"The region's mayors agreed {what}.", "info")));
                    break;
                case VoteKind.Grow:
                    if (_region.CantGrow(v.Col, v.Row) is { } why)
                        list.AddRange(Cities.SelectMany(city => city.Notice($"The region's mayors agreed {what}, but it can't be added now: {why}", "info")));
                    else list.AddRange(Grown(v.Col, v.Row, "A vote of the mayors"));
                    break;
                case VoteKind.Annex:
                    if (_region.TileAt(v.Col, v.Row) is not { Founded: true } at) break;
                    if (_links.ElementAtOrDefault((at.Anchor ?? at).Row * _region.Cols + (at.Anchor ?? at).Col) != null
                        || _region.CantAnnex(at.Anchor ?? at, v.ToCol, v.ToRow) is { } no)
                        list.AddRange(Cities.SelectMany(city => city.Notice($"The region's mayors agreed {what}, but it can't be done now.", "info")));
                    else list.AddRange(Annexed(at, v.ToCol, v.ToRow, "a vote of the mayors"));
                    break;
            }
        }
        if (passed.Count + failed.Count > 0) _regionSig = "";
        return list;
    }

    /// <summary>
    /// A player sets the calendar's pace for the region: an admin, or the mayor when every city here is theirs (a
    /// region shares one calendar, so one mayor can't speed it up for the others). Returns why not, or null.
    /// </summary>
    string? SetPace(Client c, int pace)
    {
        if (!World.IsPace(pace)) return "That isn't one of the calendar's paces.";
        var mayors = _region.Tiles.Where(t => t.City != null).Select(t => t.Mayor).Distinct().ToList();
        if (!c.Admin && !(mayors.Count == 1 && mayors[0] == c.Player))
            return "The region shares one calendar: only this server's admins can change its pace while other mayors play here.";
        _pace = pace;
        KeepPace();
        return null;
    }

    /// <summary>
    /// A player pins the region's sky, or lets its fronts back: as with the pace, an admin or the mayor of every city
    /// here. Each city hears the weather at once, by a logged action. Returns why not, or null.
    /// </summary>
    string? PinWeather(Client c, P.PinWeather pin)
    {
        var kind = (WeatherKind)(int)pin.Kind;
        if (!Enum.IsDefined(kind) || pin.Strength is < 0 or > 3) return "That isn't weather.";
        var mayors = _region.Tiles.Where(t => t.City != null).Select(t => t.Mayor).Distinct().ToList();
        if (!c.Admin && !(mayors.Count == 1 && mayors[0] == c.Player))
            return "The region shares one sky: only this server's admins can change it while other mayors play here.";
        _region.PinnedWeather = kind == WeatherKind.Clear && pin.Strength == 0 ? null : (kind, kind == WeatherKind.Clear ? 0 : Math.Max(1, pin.Strength));
        _region.SendWeather();
        foreach (var t in _region.Tiles) t.City?.ApplyPending();
        return null;
    }

    /// <summary>
    /// A player sets whether foul water runs on down a river into the next city: as with the sky, an admin or the
    /// mayor of every city here. Returns why not, or null.
    /// </summary>
    string? SetRegionWater(Client c, P.SetRegionWater set)
    {
        var mayors = _region.Tiles.Where(t => t.City != null).Select(t => t.Mayor).Distinct().ToList();
        if (!c.Admin && !(mayors.Count == 1 && mayors[0] == c.Player))
            return "The rivers run through every city here: only this server's admins can change how far their water carries while other mayors play here.";
        _region.WaterCrossesBorders = !set.Stays;
        return null;
    }

    public bool Online(string player)
    {
        lock (_online) if (_online.TryGetValue(player, out int n) && n > 0) return true;
        return OnlineOnTileHosts(player);
    }

    void CountOnline(string player, int delta)
    {
        if (player.Length == 0) return;
        lock (_online)
        {
            int n = (_online.TryGetValue(player, out int v) ? v : 0) + delta;
            if (n > 0) _online[player] = n; else _online.Remove(player);
        }
    }

    // ------------------------------------------------------------------
    // Cities
    // ------------------------------------------------------------------

    /// <summary>Makes a host for every founded city that hasn't one, except those a tile host runs. Caller holds the gate.</summary>
    void OpenCities()
    {
        for (int i = 0; i < _region.Tiles.Length; i++)
            if (_region.Tiles[i].IsAnchor && _cities[i] == null && _links.ElementAtOrDefault(i) == null)
                _cities[i] = new SimHost(_content, _saveDir, _log, _host, _accounts, this, _region.Tiles[i]);
    }

    IEnumerable<SimHost> Cities => _cities.Where(c => c != null).Select(c => c!);

    /// <summary>Where a tile's city is kept: at its top-left tile, for a city of several.</summary>
    int Slot(RegionTile t) { var a = t.Anchor ?? t; return a.Row * _region.Cols + a.Col; }

    /// <summary>Runs <paramref name="f"/> holding the region's gate and every city's, in tile order.</summary>
    T WithAll<T>(Func<T> f)
    {
        lock (_gate)
        {
            var held = Cities.ToList();
            int taken = 0;
            try
            {
                foreach (var c in held) { Monitor.Enter(c._gate); taken++; }
                return f();
            }
            finally
            {
                for (int k = taken - 1; k >= 0; k--) Monitor.Exit(held[k]._gate);
            }
        }
    }

    /// <summary>The city a new connection lands in: the one asked for, else one the player is mayor of, else the first.</summary>
    SimHost Landing(string player, (int Col, int Row)? asked)
    {
        if (asked is { } a && _region.TileAt(a.Col, a.Row) is { } t && _cities[Slot(t)] is { } wanted) return wanted;
        return Cities.FirstOrDefault(c => c.Tile.Mayor == player) ?? Cities.First();
    }

    // ------------------------------------------------------------------
    // The clock
    // ------------------------------------------------------------------

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        long next = 0;
        while (_running)
        {
            ExpirePauses();
            int speed = _speed;
            if (speed == 0) { Thread.Sleep(50); next = sw.ElapsedMilliseconds; continue; }
            long now = sw.ElapsedMilliseconds;
            if (now < next) { Thread.Sleep((int)Math.Min(20, next - now)); continue; }
            // The day clock sets the pace: a clock day an hour at speed 1, shared by the calendar's days at its pace.
            int dayMs = speed == 3 && _host.FastDayMs is int fast ? Math.Max(0, fast) : DayMs(speed, _pace);
            // A player at an emergency scene slows the day clock to speed 1.
            if (_sceneOpen) dayMs = Math.Max(dayMs, _host.SceneDayMs ?? DayMs(1, _pace));
            next = Math.Max(next + dayMs, now - 1000);

            var outbox = StepDay(() =>
            {
                var list = new List<(Client, P.ServerMessage)>();
                foreach (var c in Cities) list.AddRange(c.AfterStep());
                list.AddRange(DecideVotes());
                foreach (var o in _region.LapseTakeovers())
                {
                    _regionSig = "";
                    if (_cities[o.Buyer] is { } bh) list.AddRange(bh.Notice($"The offer to take in {_region.Tiles[o.Seller].Name} lapsed.", "info"));
                }
                _sceneOpen = _region.Cities.Any(w => w.SideGames.Any(g => g.Last.Holding.Count > 0));
                var first = _region.Cities.FirstOrDefault();
                if (first != null && first.Day == 1)
                {
                    // Pictures change slowly: made again monthly.
                    if (first.Month == 1) AutoSave();
                    // Only cities change: open land's pictures stay (a huge region has a thousand of them).
                    foreach (var t in _region.Tiles)
                        if (t.City != null) _pictures.Remove(t.Row * _region.Cols + t.Col);
                    _regionSig = "";
                }
                list.AddRange(RegionIfChanged());
                return list;
            });
            foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
        }
    }

    void AutoSave()
    {
        try { WriteSave("autosave"); }
        catch (Exception e) { _log.LogWarning(e, "autosave failed"); }
    }

    /// <summary>Lets a mayor's pause go after <see cref="MayorPauseSeconds"/>; an admin's holds.</summary>
    void ExpirePauses()
    {
        List<(Client, P.ServerMessage)>? outbox = null;
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            bool changed = false;
            foreach (var (p, ask) in _asks.ToList())
            {
                if (ask.Speed == 0 && now - ask.At > MayorPauseSeconds * 1000L && !IsAdmin(p)) { _asks.Remove(p); changed = true; }
            }
            if (changed || _speedDirty)
            {
                _speedDirty = false;
                if (Recompute()) outbox = WithAll(Broadcast);
            }
        }
        if (outbox != null) foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
    }

    volatile bool _speedDirty;

    bool IsAdmin(string player) => _host.Dedicated ? _host.IsAdmin(player) : player == SimHost.LocalPlayer;

    /// <summary>The slowest speed any online player asked for, or the host's. True when it changed. Caller holds the gate.</summary>
    bool Recompute()
    {
        int speed = _hostSpeed;
        string by = "";
        foreach (var (p, ask) in _asks.OrderBy(a => a.Key, StringComparer.Ordinal))
            if (Online(p) && ask.Speed < speed) { speed = ask.Speed; by = p; }
        bool changed = speed != _speed || by != _speedBy;
        _speed = speed;
        _speedBy = by;
        return changed;
    }

    /// <summary>
    /// A player sets the speed. An admin sets the region's own; a mayor (of any city here) asks for one, and the
    /// region never runs faster than its slowest ask. Returns why not, or null.
    /// </summary>
    string? SetSpeed(Client c, int speed)
    {
        speed = Math.Clamp(speed, 0, 3);
        bool mayor = _region.Tiles.Any(t => t.Mayor == c.Player);
        if (!c.Admin && !mayor) return "Only a mayor or this server's admins can change the speed.";
        if (c.Admin)
        {
            _hostSpeed = speed;
            // An admin's choice clears asks for anything slower than it, so pressing play gets the region going.
            foreach (var p in _asks.Where(a => a.Value.Speed < speed).Select(a => a.Key).ToList()) _asks.Remove(p);
            _asks.Remove(c.Player);
        }
        else if (speed >= _hostSpeed) _asks.Remove(c.Player);
        else _asks[c.Player] = (speed, _clock.ElapsedMilliseconds);
        Recompute();
        return null;
    }

    // ------------------------------------------------------------------
    // The region map
    // ------------------------------------------------------------------

    /// <summary>The region map as seen from one city. Caller holds every gate.</summary>
    P.RegionState State(SimHost here) => State(here.Tile);

    P.RegionState State(RegionTile here)
    {
        if (Following) return FollowerState();
        var s = RegionMapping.State(_region, here.Col, here.Row, _speed,
            t => _cities[Slot(t)]?.Clients.Where(c => c.SignedIn).Select(c => c.Player).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                 ?? Enumerable.Empty<string>(),
            _pictures);
        s.SpeedSetBy = _speedBy;
        s.Pace = _pace;
        s.Waiting = _waiting;
        s.Stopped = _stopped;
        s.LedgerMonths = _ledgerMonths;
        s.CrossBorder = _crossBorder;
        // The wire carries only some of a growing region's tiles: find each linked one by where it is.
        for (int i = 0; i < _links.Length; i++)
            if (_links[i] is { } l && s.Tiles.FirstOrDefault(x => x.Col == i % _region.Cols && x.Row == i / _region.Cols) is { } ts) ts.Address = l.Address;
        // A city on its own host can't annex, take in a neighbour or be taken in (Beta 4, part 7).
        const string Apart = "runs on its own host: a city hosted apart can't annex, take in a neighbour or be taken in.";
        bool hosted = _links.ElementAtOrDefault(Slot(here)) != null;
        foreach (var a in s.Annex) if (hosted && a.Why.Length == 0) a.Why = $"{here.Name} {Apart}";
        foreach (var o in s.TakeoverOptions)
            if (o.Why.Length == 0 && (hosted || _links.ElementAtOrDefault(o.Row * _region.Cols + o.Col) != null))
                o.Why = $"{(hosted ? here.Name : _region.TileAt(o.Col, o.Row)!.Name)} {Apart}";
        return s;
    }

    /// <summary>The region map to everyone, when something on it changed. Caller holds every gate.</summary>
    List<(Client, P.ServerMessage)> RegionIfChanged(bool force = false)
    {
        var list = new List<(Client, P.ServerMessage)>();
        string sig = $"{_speed}/{_speedBy}/{_region.Tick / World.DaysPerMonth}/{string.Join(",", _region.Deals.Select(d => $"{d.Id}{(d.Active ? "+" : "?")}"))}/{string.Join(",", _region.Tiles.Where(t => !t.Wild).Select(t => _region.WeatherAt(t, _region.Tick)))}/" + string.Join(",", _region.Tiles.Where(t => !t.Wild).Select(t =>
            $"{t.Name}:{t.Mayor}:{string.Join("/", t.City?.NextDoor.Select(n => n?.Sig ?? "") ?? Enumerable.Empty<string>())}:{string.Join("+", _cities[Slot(t)]?.Clients.Select(c => c.Player).Distinct().OrderBy(n => n, StringComparer.Ordinal) ?? Enumerable.Empty<string>())}"));
        sig += $"/{_waiting}/{_stopped}/{string.Join(",", _links.Where(l => l != null).Select(l => $"{l!.Index}:{l.Address}"))}";
        sig += $"/{_region.Charter}/{string.Join(",", _region.Votes.Select(v => $"{v.Id}:{v.Yes.Count}:{v.No.Count}"))}/{string.Join(",", _region.Takeovers.Select(o => o.Id))}";
        if (!force && sig == _regionSig) return list;
        _regionSig = sig;
        foreach (var city in Cities)
        {
            var m = new P.ServerMessage { Region = State(city) };
            foreach (var c in city.Clients) list.Add((c, m));
        }
        foreach (var l in _links)
            if (l is { Dead: false, Map: { } map }) list.Add((map, new P.ServerMessage { Region = State(_region.Tiles[l.Index]) }));
        return list;
    }

    List<(Client, P.ServerMessage)> Broadcast() => RegionIfChanged(force: true);

    // ------------------------------------------------------------------
    // Connections
    // ------------------------------------------------------------------

    public async Task RunClientAsync(WebSocket socket, (int Col, int Row)? tile, CancellationToken ct)
    {
        var client = new Client { Socket = socket, Conn = Interlocked.Increment(ref _nextConn) };
        bool solo = !_host.Dedicated;
        if (solo) { client.Player = SimHost.LocalPlayer; client.Admin = true; }
        await SimHost.SendAsync(client, new P.ServerMessage
        {
            Welcome = new P.Welcome
            {
                ProtocolVersion = Mapping.ProtocolVersion, ServerName = _host.Name, SignInRequired = !solo,
                InviteRequired = _host.Join == JoinPolicy.Invite, Player = client.Player, Admin = client.Admin,
            },
        });
        if (solo) await EnterAsync(client, tile);

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        string? drop = null;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > MaxMessageBytes) { drop = "message too large"; break; }
                if (!r.EndOfMessage) continue;
                var data = ms.ToArray();
                ms.SetLength(0);
                if (!client.Limiter.Take()) { drop = "too many messages"; break; }
                P.ClientMessage msg;
                try { msg = P.ClientMessage.Parser.ParseFrom(data); }
                catch (InvalidProtocolBufferException)
                {
                    if (++client.Malformed >= MaxMalformed) { drop = "unreadable messages"; break; }
                    await SimHost.SendAsync(client, SimHost.Error(0, "Could not read that message."));
                    continue;
                }
                try
                {
                    if (msg.BodyCase == P.ClientMessage.BodyOneofCase.SignIn)
                    {
                        if (!await SignInAsync(client, msg, tile)) break;
                    }
                    else if (!client.SignedIn)
                    {
                        if (msg.BodyCase != P.ClientMessage.BodyOneofCase.Hello) await SimHost.SendAsync(client, SimHost.Error(msg.Seq, "Sign in first."));
                    }
                    else if (!await HandleRegionAsync(client, msg) && client.Host is { } city) await city.HandleAsync(client, msg);
                }
                catch (Exception e) when (e is not OperationCanceledException and not WebSocketException)
                {
                    // A message the server can't handle drops the client that sent it, never the server.
                    _log.LogWarning(e, "dropping {Player}: a {Kind} message failed", client.Player, msg.BodyCase);
                    drop = "a message the server couldn't handle";
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (drop != null)
            {
                _log.LogWarning("dropped a connection ({Player}): {Why}", client.Player.Length > 0 ? client.Player : "not signed in", drop);
                try
                {
                    var status = drop == "message too large" ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.PolicyViolation;
                    await socket.CloseOutputAsync(status, drop, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { }
            }
            var outbox = WithAll(() =>
            {
                if (client.Host == null) return new List<(Client, P.ServerMessage)>();
                CountOnline(client.Player, -1);
                var list = client.Host.Leave(client);
                _speedDirty = true;
                list.AddRange(RegionIfChanged());
                return list;
            });
            foreach (var (c, m) in outbox) _ = SimHost.SendAsync(c, m);
        }
    }

    /// <summary>Puts a signed-in connection in its first city.</summary>
    async Task EnterAsync(Client client, (int Col, int Row)? tile)
    {
        var (mine, others) = WithAll(() =>
        {
            CountOnline(client.Player, 1);
            _speedDirty = true;
            var (m, o) = Landing(client.Player, tile).Join(client);
            m.Add(new P.ServerMessage { Region = State(client.Host!) });
            o.AddRange(RegionIfChanged().Where(x => x.Item1 != client));
            return (m, o);
        });
        foreach (var (c, m) in others) _ = SimHost.SendAsync(c, m);
        foreach (var m in mine) await SimHost.SendAsync(client, m);
    }

    /// <summary>Answers a SignIn. False when the connection has failed too often and should be closed.</summary>
    async Task<bool> SignInAsync(Client client, P.ClientMessage msg, (int Col, int Row)? tile)
    {
        var s = msg.SignIn;
        if (client.SignedIn) { await SimHost.SendAsync(client, SimHost.Error(msg.Seq, $"Already signed in as {client.Player}.")); return true; }
        if (_accounts == null && !Following) { await SimHost.SendAsync(client, SimHost.Error(msg.Seq, "This server has no accounts.")); return true; }
        SignInResult r;
        if (Following) r = (await FollowerSignInAsync(s))!;
        else if (s.Token.Length > 0) r = _accounts!.SignInWithToken(s.Token);
        else if (s.Create)
        {
            string name = s.Name.Trim();
            r = _host.MayCreate(name, s.InviteCode) is { } no ? new SignInResult(null, null, no) : _accounts!.Create(name, s.Password);
            if (r.Ok)
            {
                _host.PromoteFirst(r.Name!);
                WithAll(() => { foreach (var c in Cities) c.AccountMade(); return 0; });
            }
        }
        else r = _accounts!.SignIn(s.Name.Trim(), s.Password);
        if (!r.Ok)
        {
            client.SignInFailures++;
            await SimHost.SendAsync(client, SimHost.Error(msg.Seq, r.Error!));
            if (client.SignInFailures < MaxSignInFailures) return true;
            _log.LogInformation("closing a connection after {N} failed sign-ins", client.SignInFailures);
            return false;
        }
        client.Player = r.Name!;
        client.Admin = r.Admin ?? _host.IsAdmin(r.Name!);
        _log.LogInformation("{Player} signed in", client.Player);
        await SimHost.SendAsync(client, new P.ServerMessage { ReplyTo = msg.Seq, SignedIn = new P.SignedIn { Name = client.Player, Token = r.Token!, Admin = client.Admin } });
        await EnterAsync(client, tile);
        return true;
    }

    /// <summary>Region-wide messages: the clock, saves, new cities and moving between them. False for a city's own.</summary>
    async Task<bool> HandleRegionAsync(Client c, P.ClientMessage msg)
    {
        if (Following) return await FollowerRegionAsync(c, msg);
        P.ServerMessage? reply = null;
        List<(Client, P.ServerMessage)>? broadcast = null;
        List<P.ServerMessage>? mine = null;
        switch (msg.BodyCase)
        {
            case P.ClientMessage.BodyOneofCase.Load or P.ClientMessage.BodyOneofCase.NewCity or P.ClientMessage.BodyOneofCase.NewRegion when !c.Admin:
            case P.ClientMessage.BodyOneofCase.Save when !c.Admin && msg.Save.Name.Length > 0:
                reply = SimHost.Error(msg.Seq, "Only this server's admins can save, load, or start a new city or region.");
                break;
            case P.ClientMessage.BodyOneofCase.Pace:
                broadcast = WithAll(() =>
                {
                    if (SetPace(c, msg.Pace.DaysPerClockDay) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var list = new List<(Client, P.ServerMessage)>();
                    foreach (var city in Cities) list.AddRange(city.Deltas());
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.PinWeather:
                broadcast = WithAll(() =>
                {
                    if (PinWeather(c, msg.PinWeather) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var list = new List<(Client, P.ServerMessage)>();
                    foreach (var city in Cities) list.AddRange(city.Deltas());
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.RegionWater:
                broadcast = WithAll(() =>
                {
                    if (SetRegionWater(c, msg.RegionWater) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    return RegionIfChanged(force: true).ToList();
                });
                break;
            case P.ClientMessage.BodyOneofCase.Speed:
                broadcast = WithAll(() =>
                {
                    if (SetSpeed(c, msg.Speed.Speed) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var list = new List<(Client, P.ServerMessage)>();
                    foreach (var city in Cities) list.AddRange(city.Deltas());
                    list.AddRange(RegionIfChanged());
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.Save:
                if (msg.Save.Name.Length > 0)
                {
                    if (!SimHost.ValidName(msg.Save.Name)) { reply = SimHost.Error(msg.Seq, "Save names can use letters, numbers, spaces, - and _."); break; }
                    broadcast = WithAll(() =>
                    {
                        WriteSave(msg.Save.Name);
                        return Cities.SelectMany(city => city.Notice($"Saved as \"{msg.Save.Name}\".", "good")).ToList();
                    });
                }
                reply = new P.ServerMessage { Saves = ListSaves() };
                break;
            case P.ClientMessage.BodyOneofCase.Load:
                try
                {
                    if (!SimHost.ValidName(msg.Load.Name)) throw new InvalidDataException("bad save name");
                    var loaded = ReadSave(msg.Load.Name);
                    broadcast = WithAll(() => Replace(loaded, c.Player));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or InvalidProtocolBufferException)
                {
                    reply = SimHost.Error(msg.Seq, $"Could not load \"{msg.Load.Name}\": {e.Message}");
                }
                break;
            case P.ClientMessage.BodyOneofCase.NewCity:
            {
                // A lone city: a region of one tile, as every city was before regions.
                var n = msg.NewCity;
                int size = n.Benchmark ? BenchmarkCity.Size : n.Size is 64 or 128 or 256 ? n.Size : 128;
                ulong seed = n.Seed != 0 ? n.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                var w = World.Create(_content, seed, size, string.IsNullOrWhiteSpace(n.Name) ? "New City" : n.Name.Trim(), n.Farms && !n.Benchmark);
                if (n.Benchmark) BenchmarkCity.Apply(w);
                else if (n.StarterTown) StarterTown.Apply(w);
                w.ApplyPending();
                broadcast = WithAll(() => Replace(Region.Single(w), c.Player));
                break;
            }
            case P.ClientMessage.BodyOneofCase.NewRegion:
            {
                var n = msg.NewRegion;
                int cols = Math.Clamp(n.Cols, 1, Region.MaxSide), rows = Math.Clamp(n.Rows, 1, Region.MaxSide);
                var charter = n.Charter != null ? RegionMapping.Charter(n.Charter) : RegionCharter.Default;
                int size = n.TileSize is 64 or 128 or 256 ? n.TileSize : 128;
                ulong seed = n.Seed != 0 ? n.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                string regionName = string.IsNullOrWhiteSpace(n.Name) ? "New Region" : n.Name.Trim();
                // A growing region (G4) starts as one tile in the middle of its frame.
                var r = n.Grows ? Region.CreateGrowing(_content, regionName, seed, size) : Region.Create(_content, regionName, seed, cols, rows, size);
                if (r.CantCharter(charter) is { } badCharter) { r.Dispose(); reply = SimHost.Error(msg.Seq, badCharter); break; }
                r.Charter = charter;
                r.Farms = n.Farms;
                // Someone has to be somewhere: the middle tile always has a city, a starter town if asked for.
                r.Found(n.Grows ? Region.GrowStart : cols / 2, n.Grows ? Region.GrowStart : rows / 2, string.IsNullOrWhiteSpace(n.CityName) ? "New City" : n.CityName.Trim()[..Math.Min(40, n.CityName.Trim().Length)], c.Player, n.StarterTown);
                broadcast = WithAll(() => { _pace = World.IsPace(_host.Pace) ? _host.Pace : 360; return Replace(r, c.Player); });
                break;
            }
            case P.ClientMessage.BodyOneofCase.FoundCity:
            {
                var f = msg.FoundCity;
                string name = string.IsNullOrWhiteSpace(f.Name) ? "New City" : f.Name.Trim();
                if (name.Length > 40) name = name[..40];
                broadcast = WithAll(() =>
                {
                    if (_region.CantFound(f.Col, f.Row) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var computer = RegionMapping.Style(f.Computer);
                    if (f.Computer.Length > 0 && computer == null) { reply = SimHost.Error(msg.Seq, "That isn't a temperament."); return null; }
                    if (computer != null && !c.Admin) { reply = SimHost.Error(msg.Seq, "Only this server's admins found cities for computer mayors."); return null; }
                    if (computer == null && !c.Admin && _host.Dedicated && _region.Tiles.Count(t => t.Mayor == c.Player) >= 3)
                    {
                        reply = SimHost.Error(msg.Seq, "You're mayor of three cities here already.");
                        return null;
                    }
                    _region.Found(f.Col, f.Row, name, c.Player, f.StarterTown, computer);
                    KeepPace();
                    _pictures.Remove(f.Row * _region.Cols + f.Col);
                    OpenCities();
                    string by = computer is { } st ? $"{World.ComputerName(st, _region.TileAt(f.Col, f.Row)!.Seed)}, a {World.Temperament(st)} computer mayor," : c.Player;
                    var list = Cities.SelectMany(city => city.Notice($"{by} founded {name} in the region.", "info")).ToList();
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            }
            case P.ClientMessage.BodyOneofCase.Grow:
            {
                var g = msg.Grow;
                broadcast = WithAll(() =>
                {
                    if (CantGrow(c, g.Col, g.Row, out bool vote) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    if (!vote) return Grown(g.Col, g.Row, c.Player);
                    var list = Asked(c, VoteKind.Grow, g.Col, g.Row, null, out var no);
                    if (no != null) reply = SimHost.Error(msg.Seq, no);
                    return list;
                });
                break;
            }
            case P.ClientMessage.BodyOneofCase.Annex:
            {
                var an = msg.Annex;
                broadcast = WithAll(() =>
                {
                    if (c.Tile is not { } here) { reply = SimHost.Error(msg.Seq, "Open a city first."); return null; }
                    if (CantAnnex(c, here, an.Col, an.Row, out bool vote) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var a = here.Anchor ?? here;
                    if (!vote) return Annexed(a, an.Col, an.Row, c.Player);
                    var list = Asked(c, VoteKind.Annex, a.Col, a.Row, null, out var no, an.Col, an.Row);
                    if (no != null) reply = SimHost.Error(msg.Seq, no);
                    return list;
                });
                break;
            }
            case P.ClientMessage.BodyOneofCase.Takeover:
                broadcast = WithAll(() =>
                {
                    var list = Takeover(c, msg.Takeover, out var why);
                    if (why != null) reply = SimHost.Error(msg.Seq, why);
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.Charter:
                broadcast = WithAll(() =>
                {
                    var list = SetCharter(c, msg.Charter, out var why);
                    if (why != null) reply = SimHost.Error(msg.Seq, why);
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.Vote:
                broadcast = WithAll(() =>
                {
                    if (_region.Cast(msg.Vote.Id, c.Player, msg.Vote.Yes) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    var list = DecideVotes();
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            case P.ClientMessage.BodyOneofCase.GoTo:
            {
                var g = msg.GoTo;
                var (m, o) = WithAll<(List<P.ServerMessage>?, List<(Client, P.ServerMessage)>?)>(() =>
                {
                    var t = _region.TileAt(g.Col, g.Row);
                    if (t == null || !t.Founded) { reply = SimHost.Error(msg.Seq, "There's no city there yet. Found one first."); return (null, null); }
                    int at = Slot(t);
                    // A city another host runs, or one of this host's asked for from a tile host: go there.
                    if (_links[at] is { } link && link != (c.RemoteTile is { } rt ? _links[rt.Row * _region.Cols + rt.Col] : null))
                        return (new List<P.ServerMessage> { new() { Redirect = new P.Redirect { Address = link.Address, Col = t.Col, Row = t.Row } } }, null);
                    if (c.Relay != null)
                        return (new List<P.ServerMessage> { _links[at] != null ? new() { Region = State(t) } : new() { Redirect = new P.Redirect { Address = _host.PublicAddress, Col = t.Col, Row = t.Row } } }, null);
                    var to = _cities[at]!;
                    var list = new List<(Client, P.ServerMessage)>();
                    if (c.Host == to) return (new List<P.ServerMessage> { new() { Region = State(to) } }, list);
                    if (c.Host != null) list.AddRange(c.Host.Leave(c));
                    var (mm, oo) = to.Join(c);
                    list.AddRange(oo);
                    mm.Add(new P.ServerMessage { Region = State(to) });
                    list.AddRange(RegionIfChanged().Where(x => x.Item1 != c));
                    return (mm, list);
                });
                mine = m;
                broadcast = o;
                break;
            }
            case P.ClientMessage.BodyOneofCase.Region:
                reply = WithAll(() => c.Tile == null ? null : new P.ServerMessage { Region = State(c.Tile) });
                break;
            case P.ClientMessage.BodyOneofCase.Mayor:
            {
                var m = msg.Mayor;
                broadcast = WithAll(() =>
                {
                    var t = _region.TileAt(m.Col, m.Row);
                    string? why;
                    if (t?.City == null) why = "There's no city there.";
                    else if (m.TakeOver)
                    {
                        if (!c.Admin && _host.Dedicated && _region.Tiles.Count(x => x.Mayor == c.Player) >= 3) why = "You're mayor of three cities here already.";
                        else why = _region.TakeOver(t, c.Player);
                    }
                    else if (RegionMapping.Style(m.Computer) is not { } style) why = "That isn't a temperament.";
                    else if (!c.Admin && t.Mayor != c.Player) why = $"Only {t.Name}'s mayor can hand it to the computer.";
                    else why = _region.HandToComputer(t, style);
                    if (why != null) { reply = SimHost.Error(msg.Seq, why); return null; }
                    string news = m.TakeOver ? $"{c.Player} took over {t!.Name} from its computer mayor." : $"{t!.Name} has a {World.Temperament(t.City!.MayorSeat.Style)} computer mayor now: {t.Mayor}.";
                    var list = Cities.SelectMany(city => city.Notice(news, "info")).ToList();
                    foreach (var city in Cities) list.AddRange(city.Deltas());
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            }
            case P.ClientMessage.BodyOneofCase.Deal:
                broadcast = WithAll(() =>
                {
                    var list = new List<(Client, P.ServerMessage)>();
                    if (Deal(c, msg.Deal, list) is { } why) { reply = SimHost.Error(msg.Seq, why); return null; }
                    foreach (var city in Cities) list.AddRange(city.Deltas());
                    list.AddRange(RegionIfChanged(force: true));
                    return list;
                });
                break;
            default:
                return false;
        }
        if (reply != null)
        {
            reply.ReplyTo = msg.Seq;
            await SimHost.SendAsync(c, reply);
        }
        if (mine != null)
        {
            if (mine.Count > 0) mine[0].ReplyTo = msg.Seq;
            foreach (var m in mine) await SimHost.SendAsync(c, m);
        }
        if (broadcast != null)
            foreach (var (cl, m) in broadcast) await SimHost.SendAsync(cl, m);
        return true;
    }

    /// <summary>
    /// A mayor offers, answers, withdraws or ends a deal with a neighbour, from the city they're looking at. Admins may
    /// act for any city. Returns why not, or null. Caller holds every gate.
    /// </summary>
    string? Deal(Client c, P.DealRequest d, List<(Client, P.ServerMessage)> outbox)
    {
        if (c.Tile is not { } here) return "Open a city first.";
        if (!c.Admin && here.Mayor != c.Player) return $"Only {here.Name}'s mayor makes deals for it.";
        switch (d.Op)
        {
            case P.DealRequest.Types.Op.Offer:
            {
                var kind = (DealKind)(int)d.Kind;
                if (_region.CantOffer(here, d.Side, kind, d.Amount, d.Price) is { } why) return why;
                var deal = _region.Offer(here, d.Side, kind, d.Amount, d.Price, d.Selling);
                var other = _region.Tiles[deal.AnsweredBy];
                string what = d.Selling ? $"sell {other.Name} {World.DealUnit(kind, d.Amount)}" : $"buy {World.DealUnit(kind, d.Amount)} from {other.Name}";
                if (_cities[deal.AnsweredBy] is { } them) outbox.AddRange(them.Notice($"{here.Name} offers to {(d.Selling ? $"sell {World.DealUnit(kind, d.Amount)}" : $"buy {World.DealUnit(kind, d.Amount)}")} for ${d.Price:N0} a month. Answer on the region map.", "info"));
                _log.LogInformation("{Player} offered to {What} for {Price} a month", c.Player, what, d.Price);
                return null;
            }
            case P.DealRequest.Types.Op.Accept:
            case P.DealRequest.Types.Op.Decline:
                return _region.Answer(here, d.Id, d.Op == P.DealRequest.Types.Op.Accept);
            case P.DealRequest.Types.Op.Counter:
            {
                var old = _region.Deals.FirstOrDefault(x => x.Id == d.Id);
                if (_region.Counter(here, d.Id, d.Amount, d.Price) is { } why) return why;
                if (old != null && _cities[old.OfferedBy] is { } them)
                    outbox.AddRange(them.Notice($"{here.Name} answers your {World.DealNoun(old.Kind)} offer: {World.DealUnit(old.Kind, d.Amount)} for ${d.Price:N0} a month. See the region map.", "info"));
                return null;
            }
            default:
                return _region.EndDeal(here, d.Id);
        }
    }

    /// <summary>
    /// Puts another region in place of this one (a load, a new city or region). Everyone moves to the same tile in the
    /// new region if it has a city, else to one they're mayor of, else to the first. Caller holds every gate.
    /// </summary>
    List<(Client, P.ServerMessage)> Replace(Region next, string by)
    {
        var old = _cities;
        var oldRegion = _region;
        var moving = Cities.SelectMany(city => city.Clients.Select(cl => (cl, city.Tile.Col, city.Tile.Row))).ToList();
        foreach (var city in Cities) foreach (var (cl, _, _) in moving.Where(x => x.cl.Host == city).ToList()) city.Leave(cl);
        _region = next;
        _cities = new SimHost?[next.Tiles.Length];
        // A loaded region keeps the pace it was saved at; a new one (all of it founded today) takes the host's.
        var first = next.Tiles.Select(t => t.City).FirstOrDefault(w => w != null);
        if (first != null && first.Age > 0) _pace = first.Pace;
        KeepPace();
        // Tile hosts of the old region let go; they join the new one if it has their tile.
        foreach (var l in _links)
            if (l != null) { l.Dead = true; _ = l.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "the region changed", CancellationToken.None).ContinueWith(_ => { }); }
        InitLinks();
        _coveredMayors.Clear();
        _halted = false;
        _stopped = _waiting = "";
        _pictures.Clear();
        _regionSig = "";
        OpenCities();
        foreach (var city in old) city?.Dispose();
        oldRegion.Dispose();
        // Every city that arrived gets a real mayor and the host's minimums (logged in each).
        var list = new List<(Client, P.ServerMessage)>();
        foreach (var city in Cities) city.Arrived(by);
        // Monitor.Enter the new cities' gates is not needed: nobody can reach them until this returns.
        foreach (var (cl, col, row) in moving)
        {
            var (mine, _) = Landing(cl.Player, (col, row)).Join(cl);
            list.AddRange(mine.Select(m => (cl, m)));
        }
        foreach (var city in Cities) list.AddRange(city.Deltas(forceAlerts: true));
        list.AddRange(RegionIfChanged(force: true));
        return list;
    }

    // ------------------------------------------------------------------
    // Saves
    // ------------------------------------------------------------------

    const string RegionExt = ".toyregion", CityExt = ".toycity";

    /// <summary>A lone city saves as a city, as before regions; a region of several tiles saves whole. Caller holds every gate.</summary>
    void WriteSave(string name)
    {
        byte[] bytes;
        string ext;
        if (_region.Tiles.Length == 1 && _region.Tiles[0].City is { } only) { bytes = Mapping.Save(only); ext = CityExt; }
        else { bytes = RegionMapping.Save(_region); ext = RegionExt; }
        // One name, one save: a region save replaces a city save of the same name and the other way round.
        File.Delete(Path.Combine(_saveDir, name + (ext == CityExt ? RegionExt : CityExt)));
        File.WriteAllBytes(Path.Combine(_saveDir, name + ext), bytes);
    }

    Region ReadSave(string name)
    {
        string region = Path.Combine(_saveDir, name + RegionExt);
        if (File.Exists(region)) return RegionMapping.Load(_content, File.ReadAllBytes(region));
        return Region.Single(Mapping.Load(_content, File.ReadAllBytes(Path.Combine(_saveDir, name + CityExt))));
    }

    public P.SaveList ListSaves()
    {
        var list = new P.SaveList();
        foreach (var name in Directory.GetFiles(_saveDir, "*" + CityExt).Concat(Directory.GetFiles(_saveDir, "*" + RegionExt))
                     .Select(Path.GetFileNameWithoutExtension).Distinct().OrderBy(f => f, StringComparer.Ordinal))
            list.Names.Add(name!);
        return list;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
        _leader?.Dispose();
        foreach (var c in Cities) c.Dispose();
    }
}
