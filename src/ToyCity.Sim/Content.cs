using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToyCity.Sim;

public enum Zone : byte { None = 0, Residential = 1, Commercial = 2, Industrial = 3 }

public enum Level : byte { Low = 0, Medium = 1, High = 2 }

public enum Category : byte { Zoned, Power, Water, Garbage, Police, Fire, Health, Education, Park }

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

    /// <summary>Free-flow travel time across one 16 m cell, in deciseconds of commute time.</summary>
    [JsonIgnore] public int CellTime { get; set; }
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
    public int BuildDays { get; set; }
    public string Color { get; set; } = "#888888";

    [JsonIgnore] public int Index { get; set; }
    [JsonIgnore] public bool Placeable => Category != Category.Zoned;
}

/// <summary>All data the simulation reads from content files. Order of lists is part of determinism.</summary>
public sealed class ContentDb
{
    public List<PathProfile> Profiles { get; } = new();
    public List<BuildingType> Types { get; } = new();
    public int StartingFunds { get; private set; } = 100_000;
    public int ZoneCostPerCell { get; private set; } = 5;
    public int BulldozeCostPerCell { get; private set; } = 5;
    public int TerraformCostPerCell { get; private set; } = 25;

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
        public string Color { get; set; } = "#888888";
    }

    sealed class BuildingsFile
    {
        public int StartingFunds { get; set; } = 100_000;
        public int ZoneCostPerCell { get; set; } = 5;
        public int BulldozeCostPerCell { get; set; } = 5;
        public int TerraformCostPerCell { get; set; } = 25;
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
        _ => throw new InvalidDataException($"bad category {s}"),
    };

    /// <summary>Loads content from a directory holding profiles.json and buildings.json.</summary>
    public static ContentDb Load(string dir)
    {
        var profilesText = File.ReadAllText(Path.Combine(dir, "profiles.json"));
        var buildingsText = File.ReadAllText(Path.Combine(dir, "buildings.json"));
        return Parse(profilesText, buildingsText);
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

    public static ContentDb Parse(string profilesJson, string buildingsJson)
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
                Pollution = c.Pollution, WaterPollution = c.WaterPollution, Noise = c.Noise, BuildDays = 1, Color = c.Color,
            });
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(profilesJson + "\n" + buildingsJson);
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
