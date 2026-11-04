using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tools;

/// <summary>One thing to build: a tile in the build menu's flyout, and the tool it picks.</summary>
public sealed record BuildItem(string Name, string Price, string Icon, ToolKind Kind)
{
    public ZoneKind Zone { get; init; } = ZoneKind.ZoneResidential;
    public Density Density { get; init; } = Density.Low;
    public string Profile { get; init; } = "";
    public string Type { get; init; } = "";
    public UtilityKind Utility { get; init; } = UtilityKind.UtilityPowerLine;
    public TerraformMode Terraform { get; init; } = TerraformMode.TerraformRaise;
    public DisasterKind Disaster { get; init; } = DisasterKind.DisasterFire;
    /// <summary>The tree planter's plant kind (Beta 4.1).</summary>
    public int TreeKind { get; init; }
    /// <summary>Why it can't be picked yet ("Reach 2,000 people"), or "" when it can.</summary>
    public string Locked { get; init; } = "";
}

/// <summary>A tab in a category's flyout: "Roads", "Power".</summary>
public sealed record BuildGroup(string Name, IReadOnlyList<BuildItem> Items);

/// <summary>A button on the build rail: "Transport", and the groups in its flyout.</summary>
public sealed record BuildCategory(string Id, string Name, string Icon, string Key, string Description, IReadOnlyList<BuildGroup> Groups);

/// <summary>
/// The build menu of the UI overhaul (Beta 4, part 2, variation A), the same in both clients: a rail of categories
/// down the left, each opening a flyout of groups and the things in them with their prices. Picking a thing sets the
/// tool. What's in it comes from the city's content: the path types, the buildings the mayor places, and the zones,
/// utilities, terraforming and disasters every city has. The rail stays put; the flyout closes once a thing is picked,
/// unless it's pinned open.
/// </summary>
public sealed class BuildMenu
{
    public static readonly string[] DensityNames = { "Low density", "Medium density", "High density" };
    public static readonly string[] TerraformNames = { "Raise", "Lower", "Level", "Smooth", "Even slope" };
    public static readonly string[] DisasterNames = { "Fire", "Tornado", "Earthquake", "Clockwork Colossus" };

    public string? Open { get; private set; }
    public int Group { get; private set; }
    public bool Pinned { get; private set; }
    public int Version { get; private set; }

    /// <summary>A rail button: opens its flyout, or closes it when it's the one open.</summary>
    public void Toggle(string category)
    {
        if (Open == category) Open = null;
        else (Open, Group) = (category, 0);
        Version++;
    }

    public void PickGroup(int group) { Group = group; Version++; }
    public void Pin() { Pinned = !Pinned; Version++; }
    public void Close() { if (Open != null) { Open = null; Version++; } }

    /// <summary>A tile: the tool it stands for, and the flyout closes unless pinned. False (nothing changes) when it's locked.</summary>
    public bool Pick(BuildItem item, ToolState state)
    {
        if (item.Locked.Length > 0) return false;
        Apply(item, state);
        if (!Pinned) Open = null;
        Version++;
        return true;
    }

    /// <summary>The open category's groups, and which one is shown (clamped).</summary>
    public (BuildCategory Category, int Group)? Shown(IReadOnlyList<BuildCategory> menu)
    {
        if (Open == null || menu.FirstOrDefault(c => c.Id == Open) is not { } cat) return null;
        return (cat, Math.Clamp(Group, 0, Math.Max(0, cat.Groups.Count - 1)));
    }

    /// <summary>Sets the tool for a thing in the menu.</summary>
    public static void Apply(BuildItem i, ToolState s)
    {
        s.Kind = i.Kind;
        s.CurveEnds = null;
        switch (i.Kind)
        {
            case ToolKind.Zone: s.Zone = i.Zone; s.Density = i.Density; break;
            case ToolKind.Path: s.ProfileId = i.Profile; s.Surface = ""; break;
            case ToolKind.Utility: s.Utility = i.Utility; break;
            case ToolKind.Building: s.BuildingTypeId = i.Type; break;
            case ToolKind.Terraform: s.Terraform = i.Terraform; break;
            case ToolKind.Disaster: s.Disaster = i.Disaster; break;
            case ToolKind.Trees: s.TreeKind = i.TreeKind; break;
        }
    }

    /// <summary>Whether a thing is what the tool is set to now (its tile is lit).</summary>
    public static bool IsCurrent(BuildItem i, ToolState s) => i.Kind == s.Kind && i.Kind switch
    {
        ToolKind.Zone => i.Zone == s.Zone && i.Density == s.Density,
        ToolKind.Path => i.Profile == s.ProfileId,
        ToolKind.Utility => i.Utility == s.Utility,
        ToolKind.Building => i.Type == s.BuildingTypeId,
        ToolKind.Terraform => i.Terraform == s.Terraform,
        ToolKind.Disaster => i.Disaster == s.Disaster,
        ToolKind.Trees => i.TreeKind == s.TreeKind,
        _ => true,
    };

    /// <summary>The category whose rail button is lit for the tool now.</summary>
    public static string? CategoryOf(ToolState s, IReadOnlyList<BuildCategory> menu) =>
        menu.FirstOrDefault(c => c.Groups.Any(g => g.Items.Any(i => IsCurrent(i, s))))?.Id;

    static string Money(long n) => n <= 0 ? "" : $"${n:N0}";

    /// <summary>The menu for a city, from its content.</summary>
    public static List<BuildCategory> For(CityMirror c)
    {
        string Locked(BuildingTypeInfo t)
        {
            if (t.UnlockText.Length > 0 && !c.Unlocked.Contains(t.Id)) return t.UnlockText;
            if (t.Unique && c.Buildings.Values.Any(b => b.TypeId == t.Id)) return "Built";
            return "";
        }
        BuildItem Place(BuildingTypeInfo t) => new(t.Name, Money(t.Cost), Icon(t), ToolKind.Building) { Type = t.Id, Locked = Locked(t) };
        List<BuildItem> Types(Func<BuildingTypeInfo, bool> which) => c.Types.Where(t => t.Placeable && which(t)).OrderBy(t => t.Cost).ThenBy(t => t.Name).Select(Place).ToList();
        BuildItem PathItem(PathProfileInfo p) => new(p.Name, p.CostPerCell > 0 ? $"${p.CostPerCell} / cell" : "", p.Rail || p.Tram.Length > 0 ? "tram" : "road", ToolKind.Path) { Profile = p.Id };
        List<BuildItem> Paths(Func<PathProfileInfo, bool> which) => c.Profiles.Where(which).Select(PathItem).ToList();

        var menu = new List<BuildCategory>();
        var zoneGroups = new List<BuildGroup>();
        foreach (var (zone, name) in new[] { (ZoneKind.ZoneResidential, "Residential"), (ZoneKind.ZoneCommercial, "Commercial"), (ZoneKind.ZoneIndustrial, "Industrial") })
            zoneGroups.Add(new BuildGroup(name, Enumerable.Range(0, 3).Select(d => new BuildItem($"{name}, {DensityNames[d].ToLowerInvariant()}", "", "zones", ToolKind.Zone) { Zone = zone, Density = (Density)d }).ToList()));
        zoneGroups.Add(new BuildGroup("Dezone", new[] { new BuildItem("Dezone", "", "bulldoze", ToolKind.Dezone) }));
        menu.Add(new BuildCategory("zones", "Zones", "zones", "Z", "Mark land for homes, shops and work.", zoneGroups));

        var transport = new List<BuildGroup>
        {
            new("Paths", Paths(p => !p.Cars && !p.Rail)),
            new("Roads", Paths(p => p.Cars && p.Frontage && p.Tram.Length == 0)),
            new("Highways", Paths(p => p.Cars && !p.Frontage)),
            new("Rail and tram", Paths(p => p.Rail || p.Tram.Length > 0)),
            new("Stops", Types(t => t.Category == "transit" && !t.Id.Contains("depot") && !t.Id.Contains("yard"))),
            new("Depots", Types(t => t.Category == "transit" && (t.Id.Contains("depot") || t.Id.Contains("yard")))),
        };
        menu.Add(new BuildCategory("transport", "Transport", "transport", "R", "Paths, roads, highways and rail, and the stops and depots on them.", transport.Where(g => g.Items.Count > 0).ToList()));

        var power = Types(t => t.Category == "power");
        power.Add(new BuildItem("Power line", $"${5} / cell", "power", ToolKind.Utility) { Utility = UtilityKind.UtilityPowerLine });
        var water = Types(t => t.Category == "water");
        water.Add(new BuildItem("Water and sewer pipe", "$3 / cell", "water", ToolKind.Utility) { Utility = UtilityKind.UtilityWaterPipe });
        var utilities = new List<BuildGroup>
        {
            new("Power", power),
            new("Water", water),
            new("Garbage", Types(t => t.Category == "garbage")),
            new("Underground", new[] { new BuildItem("Subway tunnel", "$150 / cell", "subway", ToolKind.Utility) { Utility = UtilityKind.UtilitySubway } }),
        };
        menu.Add(new BuildCategory("utilities", "Utilities", "utilities", "P", "Power, water and sewage, garbage, and what runs under the city.", utilities.Where(g => g.Items.Count > 0).ToList()));

        var civic = new List<BuildGroup>
        {
            new("Police", Types(t => t.Category == "police")),
            new("Fire", Types(t => t.Category == "fire")),
            new("Health", Types(t => t.Category == "health")),
            new("Education", Types(t => t.Category == "education")),
            new("Government", Types(t => t.Category is "government" or "civic")),
        };
        menu.Add(new BuildCategory("civic", "Civic", "civic", "E", "Safety, health, schools and the town hall.", civic.Where(g => g.Items.Count > 0).ToList()));
        menu.Add(new BuildCategory("parks", "Parks", "parks", "", "Green space lifts the value of the land around it.", new[] { new BuildGroup("Parks", Types(t => t.Category == "park")) }));
        var landmarks = new List<BuildGroup> { new("Landmarks", Types(t => t.Category == "landmark")), new("Rewards", Types(t => t.Category == "reward")) };
        menu.Add(new BuildCategory("landmarks", "Landmarks", "landmarks", "", "One of a kind, and the rewards a city earns.", landmarks.Where(g => g.Items.Count > 0).ToList()));
        menu.Add(new BuildCategory("land", "Land", "land", "T", "Shape the ground. Roads grade their own way.",
            new[]
            {
                new BuildGroup("Terraform", TerraformNames.Select((n, k) => new BuildItem(n, "", "land", ToolKind.Terraform) { Terraform = (TerraformMode)k }).ToList()),
                new BuildGroup("Water", new List<BuildItem> { new("River", "", "water", ToolKind.River), new("Dam", "", "water", ToolKind.Dam) }),
                new BuildGroup("Plants", Planner.TreeChoices.Select(k => new BuildItem(k.Name, $"${Planner.TreesPerCell} / cell", "parks", ToolKind.Trees) { TreeKind = k.Kind }).ToList()),
            }));
        menu.Add(new BuildCategory("disasters", "Disasters", "disasters", "K", "Set one loose on your own city.",
            new[] { new BuildGroup("Disasters", DisasterNames.Select((n, k) => new BuildItem(n, "", "disasters", ToolKind.Disaster) { Disaster = (DisasterKind)k }).ToList()) }));
        menu.Add(new BuildCategory("bulldoze", "Bulldoze", "bulldoze", "B", "Clear anything you built.", new[] { new BuildGroup("Bulldoze", new[] { new BuildItem("Bulldoze", "", "bulldoze", ToolKind.Bulldoze) }) }));
        return menu;
    }

    /// <summary>An icon name for a building, by its category.</summary>
    static string Icon(BuildingTypeInfo t) => t.Category switch
    {
        "transit" => t.Id.Contains("depot") || t.Id.Contains("yard") ? "rail" : t.Id.Contains("subway") ? "subway" : "bus",
        "power" => "power", "water" => "water", "garbage" => "garbage", "police" => "police", "fire" => "fire", "health" => "health",
        "education" => "education", "park" => "parks", "landmark" or "reward" => "landmarks",
        _ => "civic",
    };
}
