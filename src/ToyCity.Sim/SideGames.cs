using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>Adds a side game to the city, by its catalogue id. Logged like any action, so replays attach it too.</summary>
public sealed record AttachSideGameAction(string GameId) : SimAction;

/// <summary>A command for a side game from one of its seats, as the side game's own message bytes.</summary>
public sealed record SideGameAction(string GameId, string Seat, byte[] Payload) : SimAction;

/// <summary>
/// The side games this build can host, by id. A host registers them at startup; loading a city that names one not
/// registered fails rather than silently dropping it.
/// </summary>
public static class SideGameCatalog
{
    static readonly Dictionary<string, Func<ISideGame>> Factories = new();

    public static void Register(string id, Func<ISideGame> factory)
    {
        lock (Factories) Factories[id] = factory;
    }

    public static bool Has(string id) { lock (Factories) return Factories.ContainsKey(id); }

    public static ISideGame Create(string id)
    {
        Func<ISideGame>? f;
        lock (Factories) Factories.TryGetValue(id, out f);
        if (f == null) throw new InvalidDataException($"side game '{id}' isn't available in this build");
        var g = f();
        if (g.Manifest.Id != id) throw new InvalidDataException($"side game registered as '{id}' calls itself '{g.Manifest.Id}'");
        if (g.Manifest.ContractVersion != ContractInfo.Version)
            throw new InvalidDataException($"{g.Manifest.Name} speaks contract v{g.Manifest.ContractVersion}; this city speaks v{ContractInfo.Version}");
        return g;
    }
}

/// <summary>A side game running in this city's process, with what it last told the city.</summary>
public sealed class HostedGame
{
    public required ISideGame Game;
    public string Id => Game.Manifest.Id;
    /// <summary>The side game's output from the last tick: its lines, jobs and status. Saved, since the city reads it next tick.</summary>
    public GameToCity Last = new();
    /// <summary>Riders per line from the last commute count, sent with the next tick.</summary>
    public readonly Dictionary<int, (int Riders, int[] Boardings)> Ridership = new();
}

public sealed partial class World
{
    public readonly List<HostedGame> SideGames = new();

    /// <summary>Incidents raised since the last side-game tick, by the city or a side game; handed on at the next one.</summary>
    readonly List<Incident> _incidents = new();
    long _incidentSeq;
    public IReadOnlyList<Incident> RecentIncidents => _recentIncidents;
    readonly List<Incident> _recentIncidents = new();

    public HostedGame? SideGame(string id) => SideGames.FirstOrDefault(g => g.Id == id);

    /// <summary>Records something side games may react to: a fire, a disaster, a breakdown.</summary>
    public void RaiseIncident(string kind, int x, int y, string source, string cause, int severity, string text)
    {
        var inc = new Incident(++_incidentSeq, kind, x, y, source, cause, Math.Clamp(severity, 1, 3), text);
        _incidents.Add(inc);
        _recentIncidents.Add(inc);
        if (_recentIncidents.Count > 50) _recentIncidents.RemoveAt(0);
    }

    void ApplyAttach(AttachSideGameAction a)
    {
        if (SideGame(a.GameId) != null) return;
        if (!SideGameCatalog.Has(a.GameId)) { Reject($"There is no side game called '{a.GameId}' in this build."); return; }
        var g = SideGameCatalog.Create(a.GameId);
        g.NewGame(Seed ^ (ulong)XxHashOf(a.GameId));
        SideGames.Add(new HostedGame { Game = g });
        Notices.Add(new SimNotice($"{g.Manifest.Name} has set up in town.", "info"));
    }

    static long XxHashOf(string s) => (long)System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(s));

    void ApplySideGameAction(SideGameAction a)
    {
        var hg = SideGame(a.GameId);
        if (hg == null) { Reject($"There is no side game called '{a.GameId}' in this city."); return; }
        var result = hg.Game.Command(a.Seat, a.Payload, Queries);
        if (!result.Ok) Reject(result.Message);
        else if (result.Message.Length > 0) Notices.Add(new SimNotice(result.Message, "info"));
    }

    /// <summary>Each side game's day, after the city's: what the city tells it, its tick, and what it tells the city.</summary>
    void RunSideGames()
    {
        if (SideGames.Count == 0) { _incidents.Clear(); return; }
        var incidents = _incidents.ToList();
        _incidents.Clear();
        bool newMonth = Day == 1;
        var districts = newMonth ? DistrictFigures() : null;
        var demand = newMonth ? DistrictDemand() : null;
        foreach (var hg in SideGames)
        {
            var input = new CityToGame
            {
                Tick = Tick, Day = Day, Month = Month, Year = Year, NewMonth = newMonth,
                Operator = OperatorLevel.Manual,
            };
            FillSeat(hg, input);
            FillLeases(hg, input);
            if (districts != null) input.Districts.AddRange(districts.Select(d => d with { Rating = RatingOf(hg.Id, d.Id) }));
            if (demand != null) input.Demand.AddRange(demand);
            foreach (var (line, (riders, boardings)) in hg.Ridership.OrderBy(r => r.Key)) input.Ridership.Add(new LineRidership(line, riders, boardings));
            // Each game hears about everything except what it raised itself.
            input.Incidents.AddRange(incidents.Where(i => i.Source != hg.Id));
            if (newMonth) BillLeases(hg, input);
            var output = new GameToCity();
            hg.Game.Tick(input, Queries, output);
            hg.Last = output;
            foreach (var inc in output.Incidents) TakeIncident(hg, inc);
            if (newMonth) SettleLeases(hg, output);
        }
    }

    /// <summary>A side game's incident: renumbered by the city, told to the other games next tick, and in the news.</summary>
    void TakeIncident(HostedGame hg, Incident inc)
    {
        RaiseIncident(inc.Kind, inc.X, inc.Y, hg.Id, inc.Cause, inc.Severity, inc.Text);
        if (inc.Text.Length == 0) return;
        string sev = inc.Severity >= 2 ? "warning" : "info";
        Notices.Add(new SimNotice(inc.Text, sev, inc.X, inc.Y));
        if (inc.Severity >= 2) AddHeadline(inc.Text, sev, inc.X, inc.Y);
    }

    CityQueries? _queries;
    internal ICityQueries Queries => _queries ??= new CityQueries(this);

    /// <summary>The city as a side game may see it: read-only.</summary>
    sealed class CityQueries(World w) : ICityQueries
    {
        public int Width => w.Width;
        public int Height => w.HeightCells;
        public int DistrictOf(int cell) => w.DistrictOf(cell);
        public ulong Hash(int a, int b, int c, int d) => w.Hash(a, b, c, d);
        public StreetRoute? Route(int fromCell, int toCell) => w.StreetRouteBetween(fromCell, toCell);
    }

    // Scratch for street routing on behalf of side games; never saved.
    int[] _rDist = Array.Empty<int>(), _rParent = Array.Empty<int>(), _rStamp = Array.Empty<int>();
    int _rGen;
    readonly PriorityQueue<int, long> _rPq = new();

    /// <summary>The fastest street route between two cells at today's travel times, or null.</summary>
    public StreetRoute? StreetRouteBetween(int from, int to)
    {
        if (from < 0 || to < 0 || from >= N || to >= N) return null;
        if (ProfileAt(from) is not { } pf || !StreetPassable(pf) || ProfileAt(to) is not { } pt || !StreetPassable(pt)) return null;
        if (_rDist.Length < N) { _rDist = new int[N]; _rParent = new int[N]; _rStamp = new int[N]; _rGen = 0; }
        if (++_rGen == int.MaxValue) { Array.Clear(_rStamp); _rGen = 1; }
        _rPq.Clear();
        void Push(int c, int cost, int parent)
        {
            if (_rStamp[c] == _rGen && _rDist[c] <= cost) return;
            _rStamp[c] = _rGen; _rDist[c] = cost; _rParent[c] = parent;
            _rPq.Enqueue(c, (long)cost * N + c);
        }
        Push(from, Math.Max(1, TravelCost[from]), -1);
        while (_rPq.TryDequeue(out int c, out long pri))
        {
            int cost = (int)(pri / N);
            if (cost != _rDist[c]) continue;
            if (c == to)
            {
                var cells = new List<int>();
                for (int p = c; p >= 0; p = _rParent[p]) cells.Add(p);
                cells.Reverse();
                return new StreetRoute(cells, Math.Max(1, cost / 600));
            }
            int x = c % Width, y = c / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int i = Idx(nx, ny);
                if (ProfileAt(i) is { } p && StreetPassable(p)) Push(i, cost + Math.Max(1, TravelCost[i]), c);
            }
        }
        return null;
    }

    void WriteSideGames(BinaryWriter w)
    {
        w.Write(_incidentSeq);
        w.Write(_incidents.Count);
        foreach (var i in _incidents) WriteIncident(w, i);
        w.Write(_recentIncidents.Count);
        foreach (var i in _recentIncidents) WriteIncident(w, i);
        w.Write(SideGames.Count);
        foreach (var hg in SideGames)
        {
            w.Write(hg.Id);
            using var ms = new MemoryStream();
            using (var gw = new BinaryWriter(ms)) hg.Game.Save(gw);
            var bytes = ms.ToArray();
            w.Write(bytes.Length);
            w.Write(bytes);
            WriteGameOutput(w, hg.Last);
            w.Write(hg.Ridership.Count);
            foreach (var (line, (riders, boardings)) in hg.Ridership.OrderBy(r => r.Key))
            {
                w.Write(line); w.Write(riders); w.Write(boardings.Length);
                foreach (var b in boardings) w.Write(b);
            }
        }
    }

    void ReadSideGames(BinaryReader r)
    {
        _incidentSeq = r.ReadInt64();
        int ni = r.ReadInt32();
        for (int i = 0; i < ni; i++) _incidents.Add(ReadIncident(r));
        int nr0 = r.ReadInt32();
        for (int i = 0; i < nr0; i++) _recentIncidents.Add(ReadIncident(r));
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++)
        {
            string id = r.ReadString();
            var g = SideGameCatalog.Create(id);
            var bytes = r.ReadBytes(r.ReadInt32());
            using (var gr = new BinaryReader(new MemoryStream(bytes))) g.Load(gr);
            var hg = new HostedGame { Game = g, Last = ReadGameOutput(r) };
            int nr = r.ReadInt32();
            for (int i = 0; i < nr; i++)
            {
                int line = r.ReadInt32(), riders = r.ReadInt32();
                var b = new int[r.ReadInt32()];
                for (int j = 0; j < b.Length; j++) b[j] = r.ReadInt32();
                hg.Ridership[line] = (riders, b);
            }
            SideGames.Add(hg);
        }
    }

    static void WriteIncident(BinaryWriter w, Incident i)
    {
        w.Write(i.Id); w.Write(i.Kind); w.Write(i.X); w.Write(i.Y); w.Write(i.Source); w.Write(i.Cause); w.Write(i.Severity); w.Write(i.Text);
    }

    static Incident ReadIncident(BinaryReader r) =>
        new(r.ReadInt64(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadString());

    static void WriteGameOutput(BinaryWriter w, GameToCity o)
    {
        w.Write(o.Lines.Count);
        foreach (var l in o.Lines)
        {
            w.Write(l.LineId); w.Write(l.Name); w.Write(l.Color); w.Write(l.HeadwayMinutes); w.Write(l.FareCents); w.Write(l.SeatsPerHour);
            w.Write(l.StopLeaseIds.Count);
            foreach (var s in l.StopLeaseIds) w.Write(s);
        }
        w.Write(o.JobsByLease.Count);
        foreach (var (lease, jobs) in o.JobsByLease.OrderBy(j => j.Key)) { w.Write(lease); w.Write(jobs); }
        w.Write(o.ServiceQuality);
        w.Write(o.TaxableRevenue);
        w.Write(o.Status);
    }

    static GameToCity ReadGameOutput(BinaryReader r)
    {
        var o = new GameToCity();
        int nl = r.ReadInt32();
        for (int i = 0; i < nl; i++)
        {
            int id = r.ReadInt32();
            string name = r.ReadString(), color = r.ReadString();
            int headway = r.ReadInt32(), fare = r.ReadInt32(), seats = r.ReadInt32();
            var stops = new int[r.ReadInt32()];
            for (int j = 0; j < stops.Length; j++) stops[j] = r.ReadInt32();
            o.Lines.Add(new TransitLine(id, name, color, stops, headway, fare, seats));
        }
        int nj = r.ReadInt32();
        for (int i = 0; i < nj; i++) o.JobsByLease[r.ReadInt32()] = r.ReadInt32();
        o.ServiceQuality = r.ReadInt32();
        o.TaxableRevenue = r.ReadInt64();
        o.Status = r.ReadString();
        return o;
    }
}
