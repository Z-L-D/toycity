using System.IO.Hashing;

namespace ToyCity.Sim;

/// <summary>Coverage layers produced by civic buildings.</summary>
public enum Service : byte { Police = 0, Fire = 1, Health = 2, Education = 3, Parks = 4 }

/// <summary>Where some of a home's workers go each day, and the road cells they drive through.</summary>
public sealed class Assignment
{
    public int SiteId;
    public int Count;
    public int Minutes;
    /// <summary>Street cells driven, walked or cycled: these make traffic.</summary>
    public int[] PathCells = Array.Empty<int>();
    /// <summary>Transit legs: search states (layer * N + cell) ridden.</summary>
    public int[] RideCells = Array.Empty<int>();
    /// <summary>Stops used, in order: where each leg got on, then off.</summary>
    public int[] Stops = Array.Empty<int>();
    /// <summary>Rides on side games' lines, four numbers each: game slot, line id, boarding stop index, alighting stop index.</summary>
    public int[] LineLegs = Array.Empty<int>();

    public bool ByTransit => RideCells.Length > 0;
}

public sealed class Building
{
    public int Id;
    public int Type;
    public int X, Y, W, H;
    /// <summary>Roadside buildings: which kerb of their road cell they stand on (0 north, 1 east, 2 south, 3 west).</summary>
    public int Side;
    // Totals for this building: zoned templates are per cell, so a bigger lot holds more. Derived, not saved.
    public int Residents, Jobs, PowerKw, WaterUse, Garbage;
    public int Funding = 100;
    public int Condition = 100;
    public bool Abandoned;
    public int ConstructionLeft;
    /// <summary>Months in a row a zoned building failed its requirements. Six means it is abandoned.</summary>
    public int UnmetMonths;
    /// <summary>Months in a row an abandoned building met its requirements. Three means it recovers.</summary>
    public int GoodMonths;
    public bool Powered;
    public bool Watered;
    /// <summary>For job sites: workers who commute here.</summary>
    public int Workers;
    /// <summary>For homes: residents with a job.</summary>
    public int Employed;
    public int CommuteMinutes;
    /// <summary>Tags on the walls, 0..3. Lowers upkeep and spreads blight.</summary>
    public int Graffiti;
    /// <summary>Days this building has been on fire, 0 when it isn't.</summary>
    public int BurningDays;
    /// <summary>Transit stops: people getting on and off a day, from last month's commutes.</summary>
    public int Riders;
    /// <summary>Homes: the month each resident moved in, by slot. Names and ages come from it.</summary>
    public int[]? Since;
    /// <summary>Driveway: the path cell this building uses to reach the network, or -1. Derived, not saved.</summary>
    public int Access = -1;
    public List<Assignment> Assignments = new();

    public bool Operating => !Abandoned && ConstructionLeft == 0;
}

/// <summary>
/// The whole city state. Systems live in partial class files next to this one. Everything here is
/// deterministic: integer math, index-order iteration and the world's own <see cref="Rng"/>.
/// </summary>
public sealed partial class World
{
    public const int FormatVersion = 6;
    public const int DaysPerMonth = 30;
    public const int MonthsPerYear = 12;
    public const int StartYear = 2000;
    public const int MaxHeight = 31;

    public ContentDb Content { get; }
    public int Width { get; }
    public int HeightCells { get; }
    public int N { get; }
    public int WaterLevel { get; private set; } = 8;
    public ulong Seed { get; }
    public string Name { get; set; }
    public long Tick { get; private set; }
    /// <summary>The day this city was founded: 0 for a lone city, the region's day for a tile founded later.</summary>
    public long FoundedTick { get; private set; }
    /// <summary>Days since the city was founded. Grace periods and first-day checks count from here.</summary>
    public long Age => Tick - FoundedTick;
    public Rng Rng { get; private set; }

    // Persistent cell layers (row-major, index = y * Width + x).
    public readonly byte[] Height;
    public readonly byte[] ZoneCode;     // zone * 3 + density, 0 = none
    public readonly ushort[] Path;       // profile index + 1, 0 = none
    public readonly byte[] PowerLine;
    public readonly byte[] WaterPipe;
    public readonly int[] BuildingAt;    // building id + 1, 0 = none

    // Derived cell layers, recomputed by systems (saved too, so a loaded city continues identically).
    public readonly byte[] Powered;      // 1 when the cell is on a grid with a working plant
    public readonly byte[] Watered;      // 1 when the cell is within reach of a pipe network with a pump
    public readonly byte[] LandValue;
    public readonly short[] DesR, DesC, DesI;
    public readonly byte[] Pollution;
    public readonly byte[] WaterPollution;
    public readonly byte[] Noise;
    public readonly byte[] Traffic;      // 128 = at capacity
    public readonly int[] Trips;
    public readonly int[] TravelCost;    // deciseconds to cross the cell, with congestion
    public readonly byte[] WaterDistance;
    public readonly byte[][] Coverage;

    public readonly List<Building?> Buildings = new();

    // Economy.
    public long Funds;
    public long Debt;
    public readonly int[] TaxTenths = new int[9];
    public readonly int[] Demand = new int[9];
    public long LastIncome, LastExpenses;
    /// <summary>
    /// Money that moved outside the monthly budget since the last step began: building, loans, grants. With the
    /// budget, it accounts for every change in <see cref="Funds"/>. Not saved: each step starts it again.
    /// </summary>
    public long OneOffs;
    /// <summary>True when the last step ran the monthly budget.</summary>
    public bool BudgetRan;
    public readonly List<(string Label, long Amount)> LastBudget = new();

    // City-wide stats, refreshed each tick.
    public int Population, Jobs, Workers, LaborForce;
    public long PowerCapacity, PowerDemand, WaterCapacity, WaterDemand, GarbageCapacity, GarbageProduced;
    /// <summary>Average education coverage at homes, 0..100. Feeds high-wealth demand.</summary>
    public int EducationIndex;
    public int MilestoneIndex;

    readonly List<SimAction> _pending = new();
    public readonly List<LoggedAction> Log = new();
    /// <summary>Transient messages for the player. The host drains this; it is not part of the saved state.</summary>
    public readonly List<SimNotice> Notices = new();

    public int Day => (int)(Tick % DaysPerMonth) + 1;
    public int Month => (int)(Tick / DaysPerMonth % MonthsPerYear) + 1;
    public int Year => StartYear + (int)(Tick / (DaysPerMonth * MonthsPerYear));

    World(ContentDb content, int width, int height, ulong seed, string name)
    {
        Content = content;
        Width = width;
        HeightCells = height;
        N = width * height;
        Seed = seed;
        Name = name;
        Rng = new Rng(seed);
        Height = new byte[N];
        ZoneCode = new byte[N];
        Path = new ushort[N];
        PowerLine = new byte[N];
        WaterPipe = new byte[N];
        BuildingAt = new int[N];
        Powered = new byte[N];
        Watered = new byte[N];
        LandValue = new byte[N];
        DesR = new short[N];
        DesC = new short[N];
        DesI = new short[N];
        Pollution = new byte[N];
        WaterPollution = new byte[N];
        Noise = new byte[N];
        Traffic = new byte[N];
        Trips = new int[N];
        TravelCost = new int[N];
        WaterDistance = new byte[N];
        Coverage = new byte[5][];
        for (int i = 0; i < 5; i++) Coverage[i] = new byte[N];
        Litter = new byte[N];
        Riders = new int[N];
        Blight = new byte[N];
        Array.Fill(TaxTenths, 90);
        InitSeats();
        InitOrdinances();
        InitDirector();
    }

    /// <summary>Creates a new city with generated terrain.</summary>
    public static World Create(ContentDb content, ulong seed, int size = 128, string name = "New City")
    {
        if (size is not (64 or 128 or 256)) throw new ArgumentOutOfRangeException(nameof(size), "size must be 64, 128 or 256");
        var w = new World(content, size, size, seed, name);
        w.Funds = content.StartingFunds;
        w.GenerateTerrain();
        w.RecomputeWaterDistance();
        w.RunMonthly();
        w.RunUtilities();
        w.UpdateStats();
        return w;
    }

    /// <summary>
    /// Founds a city on a region tile: the ground is the region's, cut out for this tile, and the calendar starts on
    /// the region's day so every tile shares one clock.
    /// </summary>
    public static World CreateTile(ContentDb content, ulong seed, int size, string name, byte[] heights, long tick)
    {
        if (size is not (64 or 128 or 256)) throw new ArgumentOutOfRangeException(nameof(size), "size must be 64, 128 or 256");
        if (heights.Length != size * size) throw new ArgumentException("heights must cover the tile", nameof(heights));
        var w = new World(content, size, size, seed, name) { Tick = tick, FoundedTick = tick };
        w.Funds = content.StartingFunds;
        heights.CopyTo(w.Height, 0);
        w.RecomputeWaterDistance();
        w.RunMonthly();
        w.RunUtilities();
        w.UpdateStats();
        return w;
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < HeightCells;
    public int Idx(int x, int y) => y * Width + x;
    public bool IsWater(int i) => Height[i] < WaterLevel;

    public static Zone ZoneOf(byte code) => (Zone)(code / 3);
    public static Level DensityOf(byte code) => (Level)(code % 3);
    public static byte MakeZoneCode(Zone z, Level d) => z == Zone.None ? (byte)0 : (byte)((int)z * 3 + (int)d);

    public Building? BuildingAtCell(int i) => BuildingAt[i] == 0 ? null : Buildings[BuildingAt[i] - 1];
    public BuildingType TypeOf(Building b) => Content.Types[b.Type];
    public PathProfile? ProfileAt(int i) => Path[i] == 0 ? null : Content.Profiles[Path[i] - 1];

    /// <summary>Queues an action. It takes effect at the start of the next tick.</summary>
    public void Enqueue(SimAction action) => _pending.Add(action);

    public int PendingCount => _pending.Count;

    /// <summary>
    /// Applies queued actions now, logged at the current tick. Step does this first; a paused host calls it directly
    /// so building works while paused. Either way the log replays to the same state.
    /// </summary>
    /// <summary>Where in the log the action being applied is.</summary>
    int _logging;

    public void ApplyPending()
    {
        if (_pending.Count == 0) return;
        foreach (var a in _pending)
        {
            _logging = Log.Count;
            Log.Add(new LoggedAction(Tick, a));
            _actor = a.By;
            if ((OutOfBounds(a) ?? Forbidden(a)) is { } why) Reject(why);
            else if (!SendToCouncil(a)) Apply(a);
            _actor = "";
            if (a is PlacePathAction or BulldozeAction) _edgeDirty = true;
        }
        _pending.Clear();
        if (_edgeDirty)
        {
            RebuildAccess();
            RunNeighborConnections();
        }
    }

    /// <summary>Advances the city by one day.</summary>
    public void Step()
    {
        OneOffs = 0;
        BudgetRan = false;
        ApplyPending();
        RunCouncil();
        RunDisputes();
        RunTenders();
        bool stasis = InStasis;
        if (stasis) HoldPrompts();
        else RunDisasters();

        if (Day == 1 && Age > 0) RunMonthly();
        RunUtilities();
        // Coverage is cheap and petitions hang on it, so a new station counts from the day it opens.
        RunCoverage();
        if (!stasis && Age > 0) CheckPetitions();
        if (!stasis)
        {
            RunConstruction();
            RunGrowth();
        }
        RunCommutes();
        UpdateStats();
        if (!stasis) RaiseCityIncidents();
        RunSideGames();
        Tick++;
    }

    public void Run(int days)
    {
        for (int i = 0; i < days; i++) Step();
    }

    /// <summary>
    /// Monthly. In stasis only the derived layers are recomputed (coverage, traffic, desirability and so on); nothing
    /// that persists changes: no prompts, disasters, life events, wear, budget, decline or approval.
    /// </summary>
    void RunMonthly()
    {
        bool live = Age > 0 && !InStasis;
        if (live)
        {
            CloseFreightMonth();
            CheckUnlocks();
            RunDirector();
            RollDisaster();
            RunLifeEvents();
        }
        UpdateOrdinances();
        RunNeighborConnections();
        RunBorderFlows();
        RecountWorkers();
        RunPollutionAndNoise();
        RunCoverage();
        RunTraffic();
        if (!InStasis) RunWear();
        RunLandValue();
        RunDesirability();
        UpdateStats();
        if (live)
        {
            RunBudget();
            RunAutomation();
            RunDecline();
            UpdateApproval();
        }
    }

    // ------------------------------------------------------------------
    // Snapshot and hash
    // ------------------------------------------------------------------

    /// <summary>Writes the full world state. Two worlds with equal snapshots are the same city.</summary>
    public byte[] Snapshot()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(FormatVersion);
            w.Write(Content.Fingerprint);
            w.Write(Width); w.Write(HeightCells); w.Write(WaterLevel); w.Write(Seed); w.Write(Name); w.Write(Tick);
            Rng.Write(w);
            w.Write(Height); w.Write(ZoneCode); WriteU16(w, Path); w.Write(PowerLine); w.Write(WaterPipe); WriteI32(w, BuildingAt);
            w.Write(Powered); w.Write(Watered); w.Write(LandValue);
            WriteI16(w, DesR); WriteI16(w, DesC); WriteI16(w, DesI);
            w.Write(Pollution); w.Write(WaterPollution); w.Write(Noise); w.Write(Traffic);
            WriteI32(w, Trips); WriteI32(w, TravelCost); w.Write(WaterDistance);
            foreach (var c in Coverage) w.Write(c);
            w.Write(Litter); w.Write(Blight); WriteI32(w, Riders); w.Write(TransitBoardings);

            w.Write(Buildings.Count);
            foreach (var b in Buildings)
            {
                w.Write(b != null);
                if (b == null) continue;
                w.Write(b.Type); w.Write(b.X); w.Write(b.Y); w.Write(b.W); w.Write(b.H); w.Write(b.Side);
                w.Write(b.Funding); w.Write(b.Condition); w.Write(b.Abandoned); w.Write(b.ConstructionLeft);
                w.Write(b.UnmetMonths); w.Write(b.GoodMonths); w.Write(b.Powered); w.Write(b.Watered);
                w.Write(b.Workers); w.Write(b.Employed); w.Write(b.CommuteMinutes); w.Write(b.Graffiti); w.Write(b.BurningDays); w.Write(b.Riders);
                w.Write(b.Since?.Length ?? 0);
                if (b.Since != null) foreach (var m in b.Since) w.Write(m);
                w.Write(b.Assignments.Count);
                foreach (var a in b.Assignments)
                {
                    w.Write(a.SiteId); w.Write(a.Count); w.Write(a.Minutes);
                    w.Write(a.PathCells.Length);
                    foreach (var c in a.PathCells) w.Write(c);
                    w.Write(a.RideCells.Length);
                    foreach (var c in a.RideCells) w.Write(c);
                    w.Write(a.Stops.Length);
                    foreach (var c in a.Stops) w.Write(c);
                    w.Write(a.LineLegs.Length);
                    foreach (var c in a.LineLegs) w.Write(c);
                }
            }

            w.Write(Funds); w.Write(Debt);
            foreach (var t in TaxTenths) w.Write(t);
            foreach (var d in Demand) w.Write(d);
            w.Write(LastIncome); w.Write(LastExpenses);
            w.Write(LastBudget.Count);
            foreach (var (label, amount) in LastBudget) { w.Write(label); w.Write(amount); }
            w.Write(EducationIndex); w.Write(MilestoneIndex);
            w.Write(EdgeCapacity); w.Write(EdgeCell);
            // Utility totals from the last tick: queries and demand limits read them before the next tick measures again.
            w.Write(PowerCapacity); w.Write(PowerDemand); w.Write(WaterCapacity); w.Write(WaterDemand);
            w.Write(GarbageCapacity); w.Write(GarbageProduced);
            WriteOrdinances(w);
            WriteDirector(w);
            WriteDisasters(w);
            WriteSideGames(w);
            WriteSeats(w);
            WriteLeases(w);
            WriteAuthority(w);
            WriteRoles(w);
            w.Write(FoundedTick);
            WriteNeighbours(w);
        }
        return ms.ToArray();
    }

    /// <summary>Rebuilds a world from <see cref="Snapshot"/> bytes.</summary>
    public static World FromSnapshot(ContentDb content, byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        int version = r.ReadInt32();
        // Format 5 is the same less the founding day, which was always day 0.
        if (version != FormatVersion && version != 5) throw new InvalidDataException($"save format {version} is not supported (expected {FormatVersion})");
        ulong fp = r.ReadUInt64();
        if (fp != content.Fingerprint) throw new InvalidDataException("this save was made with different content files");
        int width = r.ReadInt32(), height = r.ReadInt32(), waterLevel = r.ReadInt32();
        ulong seed = r.ReadUInt64();
        string name = r.ReadString();
        var w = new World(content, width, height, seed, name) { WaterLevel = waterLevel };
        w.Tick = r.ReadInt64();
        w.Rng = Rng.Read(r);
        ReadBytes(r, w.Height); ReadBytes(r, w.ZoneCode); ReadU16(r, w.Path); ReadBytes(r, w.PowerLine); ReadBytes(r, w.WaterPipe); ReadI32(r, w.BuildingAt);
        ReadBytes(r, w.Powered); ReadBytes(r, w.Watered); ReadBytes(r, w.LandValue);
        ReadI16(r, w.DesR); ReadI16(r, w.DesC); ReadI16(r, w.DesI);
        ReadBytes(r, w.Pollution); ReadBytes(r, w.WaterPollution); ReadBytes(r, w.Noise); ReadBytes(r, w.Traffic);
        ReadI32(r, w.Trips); ReadI32(r, w.TravelCost); ReadBytes(r, w.WaterDistance);
        foreach (var c in w.Coverage) ReadBytes(r, c);
        ReadBytes(r, w.Litter); ReadBytes(r, w.Blight); ReadI32(r, w.Riders); w.TransitBoardings = r.ReadInt32();

        int count = r.ReadInt32();
        for (int id = 0; id < count; id++)
        {
            if (!r.ReadBoolean()) { w.Buildings.Add(null); continue; }
            var b = new Building
            {
                Id = id, Type = r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(), W = r.ReadInt32(), H = r.ReadInt32(), Side = r.ReadInt32(),
                Funding = r.ReadInt32(), Condition = r.ReadInt32(), Abandoned = r.ReadBoolean(), ConstructionLeft = r.ReadInt32(),
                UnmetMonths = r.ReadInt32(), GoodMonths = r.ReadInt32(), Powered = r.ReadBoolean(), Watered = r.ReadBoolean(),
                Workers = r.ReadInt32(), Employed = r.ReadInt32(), CommuteMinutes = r.ReadInt32(),
                Graffiti = r.ReadInt32(), BurningDays = r.ReadInt32(), Riders = r.ReadInt32(),
            };
            int ns = r.ReadInt32();
            if (ns > 0) { b.Since = new int[ns]; for (int k = 0; k < ns; k++) b.Since[k] = r.ReadInt32(); }
            int na = r.ReadInt32();
            for (int k = 0; k < na; k++)
            {
                var a = new Assignment { SiteId = r.ReadInt32(), Count = r.ReadInt32(), Minutes = r.ReadInt32() };
                a.PathCells = new int[r.ReadInt32()];
                for (int c = 0; c < a.PathCells.Length; c++) a.PathCells[c] = r.ReadInt32();
                a.RideCells = new int[r.ReadInt32()];
                for (int c = 0; c < a.RideCells.Length; c++) a.RideCells[c] = r.ReadInt32();
                a.Stops = new int[r.ReadInt32()];
                for (int c = 0; c < a.Stops.Length; c++) a.Stops[c] = r.ReadInt32();
                a.LineLegs = new int[r.ReadInt32()];
                for (int c = 0; c < a.LineLegs.Length; c++) a.LineLegs[c] = r.ReadInt32();
                b.Assignments.Add(a);
            }
            w.InitTotals(b);
            w.Buildings.Add(b);
        }

        w.Funds = r.ReadInt64(); w.Debt = r.ReadInt64();
        for (int i = 0; i < 9; i++) w.TaxTenths[i] = r.ReadInt32();
        for (int i = 0; i < 9; i++) w.Demand[i] = r.ReadInt32();
        w.LastIncome = r.ReadInt64(); w.LastExpenses = r.ReadInt64();
        int nb = r.ReadInt32();
        for (int i = 0; i < nb; i++) w.LastBudget.Add((r.ReadString(), r.ReadInt64()));
        w.EducationIndex = r.ReadInt32(); w.MilestoneIndex = r.ReadInt32();
        w.EdgeCapacity = r.ReadInt32(); w.EdgeCell = r.ReadInt32();
        w.PowerCapacity = r.ReadInt64(); w.PowerDemand = r.ReadInt64(); w.WaterCapacity = r.ReadInt64(); w.WaterDemand = r.ReadInt64();
        w.GarbageCapacity = r.ReadInt64(); w.GarbageProduced = r.ReadInt64();
        w.ReadOrdinances(r);
        w.ReadDirector(r);
        w.ReadDisasters(r);
        w.ReadSideGames(r);
        w.ReadSeats(r);
        w.ReadLeases(r);
        w.ReadAuthority(r);
        w.ReadRoles(r);
        if (version >= 6)
        {
            w.FoundedTick = r.ReadInt64();
            w.ReadNeighbours(r);
        }
        w.RebuildAccess();
        // Stats are derived, but demand is kept as saved: growth reads it before the next tick recounts, and actions
        // applied while paused may have changed the counts since.
        var demand = (int[])w.Demand.Clone();
        w.UpdateStats();
        demand.CopyTo(w.Demand, 0);
        return w;
    }

    /// <summary>Hash of the full state. Equal hashes mean equal cities; used by replay and save tests.</summary>
    public ulong StateHash() => XxHash64.HashToUInt64(Snapshot());

    /// <summary>Rebuilds a city from its seed, size and action log. Used to verify determinism.</summary>
    public static World Replay(ContentDb content, ulong seed, int size, string name, IReadOnlyList<LoggedAction> log, long untilTick)
        => Replay(Create(content, seed, size, name), log, untilTick);

    /// <summary>Plays a log forward on a freshly made city (a lone one, or a region tile from <see cref="CreateTile"/>).</summary>
    public static World Replay(World w, IReadOnlyList<LoggedAction> log, long untilTick)
    {
        int next = 0;
        while (next < log.Count && log[next].Tick < w.Tick) next++;
        while (w.Tick < untilTick)
        {
            while (next < log.Count && log[next].Tick == w.Tick) w.Enqueue(log[next++].Action);
            w.Step();
        }
        return w;
    }

    static void WriteU16(BinaryWriter w, ushort[] a) { foreach (var v in a) w.Write(v); }
    static void WriteI16(BinaryWriter w, short[] a) { foreach (var v in a) w.Write(v); }
    static void WriteI32(BinaryWriter w, int[] a) { foreach (var v in a) w.Write(v); }
    static void ReadBytes(BinaryReader r, byte[] a) { var b = r.ReadBytes(a.Length); if (b.Length != a.Length) throw new EndOfStreamException(); b.CopyTo(a, 0); }
    static void ReadU16(BinaryReader r, ushort[] a) { for (int i = 0; i < a.Length; i++) a[i] = r.ReadUInt16(); }
    static void ReadI16(BinaryReader r, short[] a) { for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt16(); }
    static void ReadI32(BinaryReader r, int[] a) { for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32(); }
}
