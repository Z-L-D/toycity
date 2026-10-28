using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToyCity.Sim;

public enum Zone : byte { None = 0, Residential = 1, Commercial = 2, Industrial = 3 }

public enum Level : byte { Low = 0, Medium = 1, High = 2 }

public enum Category : byte { Zoned, Power, Water, Garbage, Police, Fire, Health, Education, Park, Landmark, Reward, Transit }

/// <summary>Which vehicles serve a transit stop, and which ride layer of the commute search they run on.</summary>
public enum TransitKind : byte { None = 0, Bus = 1, Tram = 2, Rail = 3 }

/// <summary>A path cross-section, loaded from content/profiles.json.</summary>
public sealed class PathProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SpeedKmh { get; set; }
    public int CapacityPerHour { get; set; }
    public bool Cars { get; set; }
    public bool Bikes { get; set; }
    public bool Pedestrians { get; set; }
    public bool OneWay { get; set; }
    public bool Frontage { get; set; }
    public int CostPerCell { get; set; }
    public double MaintenancePerCell { get; set; }
    public int NoiseDb { get; set; }
    public double WidthM { get; set; }
    public string Color { get; set; } = "#888888";
    /// <summary>"shared" when trams run in the traffic lanes, "dedicated" when they have their own; empty for no tram.</summary>
    public string Tram { get; set; } = "";
    /// <summary>A rail line: trains only, no cars, bikes, people or frontage.</summary>
    public bool Rail { get; set; }

    /// <summary>Free-flow travel time across one 16 m cell, in deciseconds of commute time.</summary>
    [JsonIgnore] public int CellTime { get; set; }
}

/// <summary>
/// An upgrade from one civic building type to another, in place: it keeps its lot, lease and funding. It costs less
/// than building the bigger type new, and is on offer only once the building has earned it.
/// </summary>
public sealed class UpgradeDef
{
    /// <summary>The type it becomes.</summary>
    public string To { get; set; } = "";
    public int Cost { get; set; }
    /// <summary>Transit stops: people getting on and off a day, at least.</summary>
    public int Riders { get; set; }
    /// <summary>The city's population, at least.</summary>
    public int Population { get; set; }
    /// <summary>"R", "C" or "I": a building of that zone within three cells.</summary>
    public string NearZone { get; set; } = "";
    /// <summary>What the upgrade does, for the button.</summary>
    public string Text { get; set; } = "";
}

/// <summary>A building type: either a zoned template that grows by itself or a civic building the mayor places.</summary>
public sealed class BuildingType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Category Category { get; set; }
    public Zone Zone { get; set; }
    public Level Density { get; set; }
    public Level Wealth { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public int Cost { get; set; }
    public int Maintenance { get; set; }
    public int Residents { get; set; }
    public int Jobs { get; set; }
    public int PowerKw { get; set; }
    public int WaterUse { get; set; }
    public int Garbage { get; set; }
    public int PowerOutKw { get; set; }
    public int WaterOut { get; set; }
    public int GarbageCap { get; set; }
    public int Radius { get; set; }
    public int Pollution { get; set; }
    public int WaterPollution { get; set; }
    public int Noise { get; set; }
    /// <summary>Draws visitors: feeds tourist shopping demand and people who move here for the sights.</summary>
    public int Attraction { get; set; }
    /// <summary>Money paid to the city each month while it operates (deal buildings).</summary>
    public int Income { get; set; }
    /// <summary>Only one may exist in a city.</summary>
    public bool Unique { get; set; }
    /// <summary>Rule graph that must turn non-zero before the mayor may place it; null when always available.</summary>
    [JsonIgnore] public RuleGraph? Unlock { get; set; }
    public string UnlockText { get; set; } = "";
    public int BuildDays { get; set; }
    public string Color { get; set; } = "#888888";
    /// <summary>Transit stops: which vehicles stop here.</summary>
    public TransitKind Transit { get; set; }
    /// <summary>Transit stops: boardings and alightings a day before crowding slows boarding.</summary>
    public int Capacity { get; set; }
    /// <summary>Transit stops: how far (cells) people will walk to or from it.</summary>
    public int Catchment { get; set; }
    /// <summary>Transit stops: average wait in minutes when not crowded.</summary>
    public int Wait { get; set; }
    /// <summary>Placed on one side of a road instead of on land: it takes no tile and doesn't block the road.</summary>
    public bool Roadside { get; set; }
    /// <summary>What side games lease it as ("bus-stop", "bus-depot"), whatever size it has grown to; empty when not leasable.</summary>
    public string LotKind { get; set; } = "";
    /// <summary>Monthly rent on the city's standard lease for it; 0 for the lot kind's default.</summary>
    public int LeaseRent { get; set; }
    /// <summary>What it can become, and when. Several make a branch: taking one rules out the others.</summary>
    public List<UpgradeDef> Upgrades { get; set; } = new();

    [JsonIgnore] public int Index { get; set; }
    [JsonIgnore] public bool Placeable => Category != Category.Zoned;
}

/// <summary>All data the simulation reads from content files. Order of lists is part of determinism.</summary>
public sealed class ContentDb
{
    public List<PathProfile> Profiles { get; } = new();
    public List<BuildingType> Types { get; } = new();
    public List<OrdinanceDef> Ordinances { get; private set; } = new();
    public List<EventDef> Events { get; private set; } = new();
    public DirectorSettings Director { get; private set; } = new();
    public Voice Voice { get; private set; } = new();
    public int StartingFunds { get; private set; } = 100_000;
    public int ZoneCostPerCell { get; private set; } = 5;
    public int BulldozeCostPerCell { get; private set; } = 5;
    public int TerraformCostPerCell { get; private set; } = 25;

    /// <summary>Lot sizes (frontage, depth) a zoned building may grow on, by density; largest area first.</summary>
    public List<(int W, int D)>[] LotSizes { get; } = { new(), new(), new() };

    readonly Dictionary<string, int> _profileIndex = new();
    readonly Dictionary<string, int> _typeIndex = new();

    /// <summary>Zoned template for zone x density x wealth.</summary>
    readonly int[] _zoned = new int[27];

    public int ProfileIndex(string id) => _profileIndex.TryGetValue(id, out var i) ? i : -1;
    public int TypeIndex(string id) => _typeIndex.TryGetValue(id, out var i) ? i : -1;

    public BuildingType Zoned(Zone zone, Level density, Level wealth) =>
        Types[_zoned[((int)zone - 1) * 9 + (int)density * 3 + (int)wealth]];

    /// <summary>Hash of the content, stored in saves so a save is not replayed against different rules.</summary>
    public ulong Fingerprint { get; private set; }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class ProfilesFile { public List<PathProfile> Profiles { get; set; } = new(); }

    sealed class ZonedEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Zone { get; set; } = "R";
        public string Density { get; set; } = "L";
        public string Wealth { get; set; } = "L";
        public int Residents { get; set; }
        public int Jobs { get; set; }
        public int PowerKw { get; set; }
        public int WaterUse { get; set; }
        public int Garbage { get; set; }
        public int Pollution { get; set; }
        public int WaterPollution { get; set; }
        public int Noise { get; set; }
        public int BuildDays { get; set; }
        public string Color { get; set; } = "#888888";
    }

    sealed class CivicEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public int W { get; set; } = 1;
        public int H { get; set; } = 1;
        public int Cost { get; set; }
        public int Maintenance { get; set; }
        public int Jobs { get; set; }
        public int PowerKw { get; set; }
        public int WaterUse { get; set; }
        public int Garbage { get; set; }
        public int PowerOutKw { get; set; }
        public int WaterOut { get; set; }
        public int GarbageCap { get; set; }
        public int Radius { get; set; }
        public int Pollution { get; set; }
        public int WaterPollution { get; set; }
        public int Noise { get; set; }
        public int Attraction { get; set; }
        public int Income { get; set; }
        public bool Unique { get; set; }
        public JsonElement? Unlock { get; set; }
        public string UnlockText { get; set; } = "";
        public string Color { get; set; } = "#888888";
        public string Transit { get; set; } = "";
        public int Capacity { get; set; }
        public int Catchment { get; set; }
        public int Wait { get; set; }
        public bool Roadside { get; set; }
        public string LotKind { get; set; } = "";
        public int LeaseRent { get; set; }
        public List<UpgradeDef> Upgrades { get; set; } = new();
    }

    sealed class BuildingsFile
    {
        public int StartingFunds { get; set; } = 100_000;
        public int ZoneCostPerCell { get; set; } = 5;
        public int BulldozeCostPerCell { get; set; } = 5;
        public int TerraformCostPerCell { get; set; } = 25;
        public Dictionary<string, List<int[]>> LotSizes { get; set; } = new();
        public List<ZonedEntry> Zoned { get; set; } = new();
        public List<CivicEntry> Civic { get; set; } = new();
    }

    static Zone ParseZone(string s) => s switch { "R" => Zone.Residential, "C" => Zone.Commercial, "I" => Zone.Industrial, _ => throw new InvalidDataException($"bad zone {s}") };
    static Level ParseLevel(string s) => s switch { "L" => Level.Low, "M" => Level.Medium, "H" => Level.High, _ => throw new InvalidDataException($"bad level {s}") };

    static Category ParseCategory(string s) => s switch
    {
        "power" => Category.Power,
        "water" => Category.Water,
        "garbage" => Category.Garbage,
        "police" => Category.Police,
        "fire" => Category.Fire,
        "health" => Category.Health,
        "education" => Category.Education,
        "park" => Category.Park,
        "landmark" => Category.Landmark,
        "reward" => Category.Reward,
        "transit" => Category.Transit,
        _ => throw new InvalidDataException($"bad category {s}"),
    };

    /// <summary>Loads content from a directory holding profiles.json and buildings.json.</summary>
    public static ContentDb Load(string dir)
    {
        var profilesText = File.ReadAllText(Path.Combine(dir, "profiles.json"));
        var buildingsText = File.ReadAllText(Path.Combine(dir, "buildings.json"));
        var ordinancesPath = Path.Combine(dir, "ordinances.json");
        var ordinancesText = File.Exists(ordinancesPath) ? File.ReadAllText(ordinancesPath) : null;
        string? Optional(string name) => File.Exists(Path.Combine(dir, name)) ? File.ReadAllText(Path.Combine(dir, name)) : null;
        return Parse(profilesText, buildingsText, ordinancesText, Optional("events.json"), Optional("voice.json"));
    }

    /// <summary>Finds the content directory by walking up from a start directory.</summary>
    public static string FindContentDir(string? start = null)
    {
        var d = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (d != null)
        {
            var c = Path.Combine(d.FullName, "content");
            if (File.Exists(Path.Combine(c, "buildings.json"))) return c;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the content directory (content/buildings.json).");
    }

    public static ContentDb LoadDefault() => Load(FindContentDir());

    public static ContentDb Parse(string profilesJson, string buildingsJson, string? ordinancesJson = null, string? eventsJson = null, string? voiceJson = null)
    {
        var db = new ContentDb();
        var pf = JsonSerializer.Deserialize<ProfilesFile>(profilesJson, Json) ?? throw new InvalidDataException("profiles.json");
        foreach (var p in pf.Profiles)
        {
            // 16 m at the profile speed, stretched 30x so a 60 minute commute spans about 100 road cells.
            double seconds = 16.0 / (Math.Max(1, p.SpeedKmh) / 3.6) * 30.0;
            p.CellTime = Math.Max(1, (int)Math.Round(seconds * 10));
            db._profileIndex[p.Id] = db.Profiles.Count;
            db.Profiles.Add(p);
        }

        var bf = JsonSerializer.Deserialize<BuildingsFile>(buildingsJson, Json) ?? throw new InvalidDataException("buildings.json");
        db.StartingFunds = bf.StartingFunds;
        db.ZoneCostPerCell = bf.ZoneCostPerCell;
        db.BulldozeCostPerCell = bf.BulldozeCostPerCell;
        db.TerraformCostPerCell = bf.TerraformCostPerCell;
        foreach (var (key, sizes) in bf.LotSizes)
        {
            var list = db.LotSizes[(int)ParseLevel(key)];
            foreach (var s in sizes)
                if (s.Length == 2 && s[0] >= 1 && s[1] >= 1 && s[0] <= 8 && s[1] <= 8) list.Add((s[0], s[1]));
        }
        foreach (var list in db.LotSizes)
        {
            if (list.Count == 0) list.Add((1, 1));
            list.Sort((a, b) => (b.W * b.D).CompareTo(a.W * a.D) != 0 ? (b.W * b.D).CompareTo(a.W * a.D) : a.W.CompareTo(b.W));
        }
        Array.Fill(db._zoned, -1);
        foreach (var z in bf.Zoned)
        {
            var t = new BuildingType
            {
                Id = z.Id, Name = z.Name, Category = Category.Zoned,
                Zone = ParseZone(z.Zone), Density = ParseLevel(z.Density), Wealth = ParseLevel(z.Wealth),
                W = 1, H = 1, Residents = z.Residents, Jobs = z.Jobs, PowerKw = z.PowerKw, WaterUse = z.WaterUse,
                Garbage = z.Garbage, Pollution = z.Pollution, WaterPollution = z.WaterPollution, Noise = z.Noise,
                BuildDays = Math.Max(1, z.BuildDays), Color = z.Color,
            };
            db.AddType(t);
            db._zoned[((int)t.Zone - 1) * 9 + (int)t.Density * 3 + (int)t.Wealth] = t.Index;
        }
        if (db._zoned.Any(i => i < 0)) throw new InvalidDataException("buildings.json must define all 27 zoned templates");
        foreach (var c in bf.Civic)
        {
            db.AddType(new BuildingType
            {
                Id = c.Id, Name = c.Name, Category = ParseCategory(c.Category), W = c.W, H = c.H, Cost = c.Cost,
                Maintenance = c.Maintenance, Jobs = c.Jobs, PowerKw = c.PowerKw, WaterUse = c.WaterUse, Garbage = c.Garbage,
                PowerOutKw = c.PowerOutKw, WaterOut = c.WaterOut, GarbageCap = c.GarbageCap, Radius = c.Radius,
                Pollution = c.Pollution, WaterPollution = c.WaterPollution, Noise = c.Noise, Attraction = c.Attraction, BuildDays = 1, Color = c.Color,
                Income = c.Income, Unique = c.Unique, UnlockText = c.UnlockText,
                Transit = c.Transit switch { "" => TransitKind.None, "bus" => TransitKind.Bus, "tram" => TransitKind.Tram, "rail" => TransitKind.Rail, _ => throw new InvalidDataException($"{c.Id}: bad transit '{c.Transit}'") },
                Capacity = c.Capacity, Catchment = c.Catchment, Wait = c.Wait,
                Roadside = c.Roadside, LotKind = c.LotKind, LeaseRent = c.LeaseRent, Upgrades = c.Upgrades,
                Unlock = c.Unlock is { } u ? RuleGraph.Compile(u, c.Id + " unlock") : null,
            });
        }
        foreach (var t in db.Types)
            foreach (var u in t.Upgrades)
            {
                int to = db.TypeIndex(u.To);
                if (to < 0 || !db.Types[to].Placeable) throw new InvalidDataException($"{t.Id}: upgrade to unknown type '{u.To}'");
                if (db.Types[to].Roadside != t.Roadside || db.Types[to].W != t.W || db.Types[to].H != t.H)
                    throw new InvalidDataException($"{t.Id}: an upgrade must keep the footprint ('{u.To}' doesn't)");
                if (u.NearZone is not ("" or "R" or "C" or "I")) throw new InvalidDataException($"{t.Id}: bad nearZone '{u.NearZone}'");
            }
        if (ordinancesJson != null) db.Ordinances = OrdinanceDef.Parse(ordinancesJson);
        if (eventsJson != null) (db.Events, db.Director) = EventDef.Parse(eventsJson);
        if (voiceJson != null) db.Voice = Voice.Parse(voiceJson);
        // Every graph's references must name real things.
        var graphs = db.Ordinances.SelectMany(o => new[] { (o.Id, o.Graph), (o.Id, o.Unlock) })
            .Concat(db.Types.Select(t => (t.Id, t.Unlock)))
            .Concat(db.Events.SelectMany(e => e.Graphs.Select(g => (e.Id, (RuleGraph?)g))));
        foreach (var (owner, g) in graphs)
        {
            if (g == null) continue;
            foreach (var n in g.Nodes)
            {
                if (n.Params.TryGetValue("type", out var tid) && db.TypeIndex(tid) < 0)
                    throw new InvalidDataException($"{owner}: unknown building type '{tid}'");
                if (n.Params.TryGetValue("ordinance", out var oid) && db.Ordinances.All(o => o.Id != oid))
                    throw new InvalidDataException($"{owner}: unknown ordinance '{oid}'");
            }
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(profilesJson + "\n" + buildingsJson + "\n" + ordinancesJson + "\n" + eventsJson + "\n" + voiceJson);
        db.Fingerprint = System.IO.Hashing.XxHash64.HashToUInt64(bytes);
        return db;
    }

    void AddType(BuildingType t)
    {
        if (_typeIndex.ContainsKey(t.Id)) throw new InvalidDataException($"duplicate building type {t.Id}");
        t.Index = Types.Count;
        _typeIndex[t.Id] = t.Index;
        Types.Add(t);
    }
}
