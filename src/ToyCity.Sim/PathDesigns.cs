using ToyCity.Paths;

namespace ToyCity.Sim;

/// <summary>
/// Designs a path type from its cross-section (RC part 4), or redesigns one this city made: an empty
/// <c>Id</c> makes a new one. Strips run left to right; see <see cref="PathRules"/> for what follows from them.
/// Designing costs nothing, but redesigning a type that's built charges the difference in its cost on every cell of it.
/// </summary>
public sealed record DesignPathAction(string Id, string Name, IReadOnlyList<PathStrip> Strips, bool OneWay = false) : SimAction;

/// <summary>Takes one of this city's path designs off its list. Only one nothing is built with.</summary>
public sealed record DeletePathDesignAction(string Id) : SimAction;

/// <summary>
/// The path designer in the simulation (RC part 4): each city's own path types, designed from strips, kept at the end
/// of its list of path types (its copy of the content), saved with it (format 22), and built like any other.
/// </summary>
public sealed partial class World
{
    /// <summary>The most designs a city keeps.</summary>
    public const int MaxPathDesigns = 24;

    /// <summary>Goes up whenever the city's path types change, so the clients are sent the list again.</summary>
    public int ProfilesVersion { get; private set; }

    /// <summary>This city's own path designs, in order.</summary>
    public IEnumerable<PathProfile> PathDesigns => Content.Profiles.Skip(Content.BuiltInProfiles);

    static List<(string, double)> Pairs(IReadOnlyList<PathStrip> strips) => strips.Select(s => (s.Type, s.WidthM)).ToList();

    /// <summary>Why a design can't be made, or null when it can.</summary>
    public static string? CheckDesign(IReadOnlyList<PathStrip> strips, bool oneWay) => PathRules.Check(Pairs(strips), oneWay);

    /// <summary>A designed path type: its strips, and the numbers that follow from them.</summary>
    public static PathProfile DesignedProfile(string id, string name, IReadOnlyList<PathStrip> strips, bool oneWay)
    {
        var s = PathRules.Derive(Pairs(strips), oneWay);
        return new PathProfile
        {
            Id = id, Name = name, SpeedKmh = s.SpeedKmh, CapacityPerHour = s.CapacityPerHour, Cars = s.Cars, Bikes = s.Bikes,
            Pedestrians = s.Pedestrians, OneWay = s.OneWay, Frontage = s.Frontage, CostPerCell = s.CostPerCell,
            MaintenancePerCell = s.MaintenancePerCell, NoiseDb = s.NoiseDb, WidthM = s.WidthM, Color = s.Color, Tram = s.Tram,
            MaxGradePct = s.MaxGradePct, Stairs = s.Stairs, Surfaces = s.Surfaces.ToList(), WinterTier = s.WinterTier,
            Drained = s.Drained, Strips = strips.ToList(), Custom = true,
        };
    }

    void ApplyDesignPath(DesignPathAction a)
    {
        string name = (a.Name ?? "").Trim();
        if (CheckDesign(a.Strips, a.OneWay) is { } why) { Reject(why); return; }
        int at = -1;
        if (!string.IsNullOrEmpty(a.Id))
        {
            at = Content.ProfileIndex(a.Id);
            if (at < 0) { Reject("There's no such path design."); return; }
            if (at < Content.BuiltInProfiles) { Reject("Built-in path types can't be redesigned. Start a design from one instead."); return; }
        }
        else if (Content.Profiles.Count - Content.BuiltInProfiles >= MaxPathDesigns) { Reject($"A city keeps at most {MaxPathDesigns} path designs. Delete one first."); return; }
        string id = at >= 0 ? a.Id : NewDesignId();
        if (name.Length == 0) name = at >= 0 ? Content.Profiles[at].Name : $"Design {id[(id.LastIndexOf('-') + 1)..]}";
        var p = DesignedProfile(id, name, a.Strips, a.OneWay);
        if (at >= 0)
        {
            // Every cell already built with it is rebuilt to the new design: the difference in cost, if it's dearer.
            int cells = 0;
            for (int i = 0; i < N; i++) if (Path[i] == at + 1) cells++;
            long cost = Math.Max(0, (long)(p.CostPerCell - Content.Profiles[at].CostPerCell) * cells);
            if (cost > 0 && !Spend(cost)) return;
            Content.SetCustomProfile(p);
            ProfilesVersion++;
            Notices.Add(new SimNotice($"Redesigned the {name}{(cells > 0 ? $" and rebuilt its {cells:N0} cells{(cost > 0 ? $" for ${cost:N0}" : "")}" : "")}: {Describe(p)}.", "info", -1, -1, _actor));
            return;
        }
        Content.SetCustomProfile(p);
        ProfilesVersion++;
        Notices.Add(new SimNotice($"Designed the {name}: {Describe(p)}. It's in the build menu under Your designs.", "info", -1, -1, _actor));
    }

    static string Describe(PathProfile p) => p.Cars
        ? $"{p.SpeedKmh} km/h, {p.CapacityPerHour:N0} vehicles an hour, ${p.CostPerCell} a cell"
        : $"{p.CapacityPerHour:N0} {(p.Bikes && !p.Pedestrians ? "bikes" : "people")} an hour, ${p.CostPerCell} a cell";

    string NewDesignId()
    {
        for (int n = 1; ; n++)
            if (Content.ProfileIndex($"design-{n}") < 0) return $"design-{n}";
    }

    void ApplyDeletePathDesign(DeletePathDesignAction a)
    {
        int at = Content.ProfileIndex(a.Id ?? "");
        if (at < Content.BuiltInProfiles) { Reject(at < 0 ? "There's no such path design." : "Built-in path types can't be deleted."); return; }
        int cells = 0;
        for (int i = 0; i < N; i++) if (Path[i] == at + 1) cells++;
        if (cells > 0) { Reject($"The {Content.Profiles[at].Name} is built on {cells:N0} cells. Bulldoze them or build over them first."); return; }
        string name = Content.Profiles[at].Name;
        Content.RemoveCustomProfile(at);
        // The designs after it move up the list.
        for (int i = 0; i < N; i++) if (Path[i] > at + 1) Path[i]--;
        ProfilesVersion++;
        Notices.Add(new SimNotice($"Deleted the {name} design.", "info", -1, -1, _actor));
    }

    // ---- saving ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The designs a merged city writes: this one's, then the other's under new ids where theirs are taken, and where
    /// each of the other's path types lands in the merged list.
    /// </summary>
    (List<(string Id, PathProfile P)> Designs, Dictionary<int, int> TheirIndex) MergedDesigns()
    {
        var list = PathDesigns.Select(p => (p.Id, p)).ToList();
        var map = new Dictionary<int, int>();
        if (_merge is not { } m) return (list, map);
        var taken = new HashSet<string>(list.Select(d => d.Id));
        int b = Content.BuiltInProfiles;
        foreach (var (p, k) in m.Other.PathDesigns.Select((p, k) => (p, k)))
        {
            string id = p.Id;
            for (int n = 1; taken.Contains(id); n++) id = $"design-{n}";
            taken.Add(id);
            map[b + k + 1] = b + list.Count + 1;
            list.Add((id, p));
        }
        return (list, map);
    }

    Dictionary<int, int>? _theirPaths;

    /// <summary>The other city's path layer while merging, its designs moved to where they land in this one's list.</summary>
    ushort[] TheirPaths(ushort[] theirs)
    {
        _theirPaths ??= MergedDesigns().TheirIndex;
        if (_theirPaths.Count == 0) return theirs;
        var b = (ushort[])theirs.Clone();
        for (int i = 0; i < b.Length; i++) if (_theirPaths.TryGetValue(b[i], out int to)) b[i] = (ushort)to;
        return b;
    }

    void WriteDesigns(BinaryWriter w)
    {
        var (designs, _) = MergedDesigns();
        _theirPaths = null;
        w.Write(Content.BuiltInProfiles);
        w.Write(designs.Count);
        foreach (var (id, p) in designs)
        {
            w.Write(id); w.Write(p.Name); w.Write(p.OneWay);
            var strips = p.Strips ?? new List<PathStrip>();
            w.Write(strips.Count);
            foreach (var s in strips) { w.Write(s.Type); w.Write(s.WidthM); }
        }
    }

    void ReadDesigns(BinaryReader r)
    {
        int savedBuiltIn = r.ReadInt32(), n = r.ReadInt32();
        for (int k = 0; k < n; k++)
        {
            string id = r.ReadString(), name = r.ReadString();
            bool oneWay = r.ReadBoolean();
            int count = r.ReadInt32();
            var strips = new List<PathStrip>(count);
            for (int j = 0; j < count; j++) strips.Add(new PathStrip(r.ReadString(), r.ReadDouble()));
            Content.SetCustomProfile(DesignedProfile(id, name, strips, oneWay));
        }
        // Saved before built-in types were added (or taken away), the designs' cells move with them.
        int shift = Content.BuiltInProfiles - savedBuiltIn;
        if (shift != 0)
            for (int i = 0; i < N; i++) if (Path[i] > savedBuiltIn) Path[i] = (ushort)(Path[i] + shift);
    }
}
