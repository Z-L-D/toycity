using ToyCity.Paths;
using P = ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tools;

/// <summary>A kind of strip in the path designer's palette.</summary>
public sealed record StripChoice(string Id, string Name, string Colour, double DefaultM, double MinM, double MaxM, string About);

/// <summary>One strip of a design being laid out.</summary>
public sealed record DesignStrip(string Type, double WidthM);

/// <summary>What the city will make of the design being laid out: the same rules the simulation uses.</summary>
public sealed record DesignStats(
    string Kind, bool Cars, bool Bikes, bool Pedestrians, bool OneWay, bool Frontage, int SpeedKmh, int CapacityPerHour,
    int CostPerCell, double MaintenancePerCell, int NoiseDb, double WidthM, string Tram, int MaxGradePct, bool Stairs,
    IReadOnlyList<string> Surfaces, string Color)
{
    /// <summary>Who uses it, in words: "cars, bikes and people".</summary>
    public string Users
    {
        get
        {
            var who = new List<string>();
            if (Cars) who.Add(Tram == "" ? "cars" : "cars and trams");
            else if (Tram != "") who.Add("trams");
            if (Bikes) who.Add("bikes");
            if (Pedestrians) who.Add("people");
            return who.Count switch { 0 => "nobody", 1 => who[0], _ => string.Join(", ", who.Take(who.Count - 1)) + " and " + who[^1] };
        }
    }

    /// <summary>The capacity in words: "1,200 vehicles an hour", or people or bikes for a path.</summary>
    public string CapacityText => Cars ? $"{CapacityPerHour:N0} vehicles an hour"
        : $"{CapacityPerHour:N0} {(Bikes && !Pedestrians ? "bikes" : "people")} an hour";
}

/// <summary>
/// The path designer (RC part 4), the same in any client: a cross-section laid out left to right as strips, as in
/// Streetmix, with one strip picked to widen, move or take out, and the numbers the city will make of it shown as it
/// changes. Save makes the action that designs it (or redesigns the one being edited). Every change bumps
/// <see cref="Version"/> so a panel knows to redraw.
/// </summary>
public sealed class PathDesigner
{
    public static readonly IReadOnlyList<StripChoice> Kinds =
        PathRules.Kinds.Select(k => new StripChoice(k.Id, k.Name, k.Colour, k.Default, k.Min, k.Max, k.About)).ToList();

    public const int MaxStrips = PathRules.MaxStrips;
    public const double MinWidthM = PathRules.MinWidthM, MaxWidthM = PathRules.MaxWidthM;
    /// <summary>How much one press of − or + widens a strip.</summary>
    public const double Step = 0.25;

    readonly List<DesignStrip> _strips = new();
    public IReadOnlyList<DesignStrip> Strips => _strips;
    /// <summary>The design being redesigned, or "" for a new one.</summary>
    public string Id { get; private set; } = "";
    public string Name { get; set; } = "";
    public bool OneWay { get; private set; }
    /// <summary>The strip picked, or -1.</summary>
    public int Selected { get; private set; } = -1;
    public int Version { get; private set; }

    public PathDesigner() => New();

    public static StripChoice? KindOf(string id) => Kinds.FirstOrDefault(k => k.Id == id);

    /// <summary>A new design: a street, two lanes between sidewalks.</summary>
    public void New()
    {
        Id = ""; Name = ""; OneWay = false;
        _strips.Clear();
        foreach (var t in new[] { "walk", "car", "car", "walk" }) _strips.Add(new DesignStrip(t, KindOf(t)!.DefaultM));
        Selected = 1;
        Version++;
    }

    /// <summary>A new design laid out like a path type: a built-in one, or another design. Strips the designer doesn't know (rail) are left out.</summary>
    public void StartFrom(P.PathProfileInfo p)
    {
        Id = ""; Name = $"My {p.Name.ToLowerInvariant()}"; OneWay = p.OneWay;
        Load(p);
    }

    /// <summary>Redesigns one of the city's designs.</summary>
    public void Edit(P.PathProfileInfo p)
    {
        Id = p.Custom ? p.Id : ""; Name = p.Name; OneWay = p.OneWay;
        Load(p);
    }

    void Load(P.PathProfileInfo p)
    {
        _strips.Clear();
        foreach (var s in p.Strips)
            if (KindOf(s.Type) is { } k) _strips.Add(new DesignStrip(k.Id, Math.Clamp(s.WidthM, k.MinM, k.MaxM)));
        Selected = _strips.Count > 0 ? 0 : -1;
        Version++;
    }

    /// <summary>Whether a path type can be a design's start: anything but rail.</summary>
    public static bool CanStartFrom(P.PathProfileInfo p) => !p.Rail && p.Strips.Any(s => KindOf(s.Type) != null);

    public void Select(int i) { Selected = i >= 0 && i < _strips.Count ? i : -1; Version++; }

    /// <summary>Puts a strip of a kind in, after the one picked (or at the right), at its usual width, and picks it.</summary>
    public bool Add(string type)
    {
        if (KindOf(type) is not { } k || _strips.Count >= MaxStrips) return false;
        int at = Selected >= 0 ? Selected + 1 : _strips.Count;
        _strips.Insert(at, new DesignStrip(k.Id, k.DefaultM));
        Selected = at;
        Version++;
        return true;
    }

    public bool Remove()
    {
        if (Selected < 0) return false;
        _strips.RemoveAt(Selected);
        Selected = Math.Min(Selected, _strips.Count - 1);
        Version++;
        return true;
    }

    /// <summary>Moves the picked strip one place left (-1) or right (+1).</summary>
    public bool Move(int dir)
    {
        int to = Selected + dir;
        if (Selected < 0 || to < 0 || to >= _strips.Count) return false;
        (_strips[Selected], _strips[to]) = (_strips[to], _strips[Selected]);
        Selected = to;
        Version++;
        return true;
    }

    /// <summary>Widens (or narrows) the picked strip, kept to its kind's widths.</summary>
    public bool Widen(double by)
    {
        if (Selected < 0) return false;
        var s = _strips[Selected];
        var k = KindOf(s.Type)!;
        double w = Math.Clamp(Math.Round((s.WidthM + by) / Step) * Step, k.MinM, k.MaxM);
        if (Math.Abs(w - s.WidthM) < 1e-9) return false;
        _strips[Selected] = s with { WidthM = w };
        Version++;
        return true;
    }

    public void SetOneWay(bool on) { OneWay = on; Version++; }

    public double WidthM => _strips.Sum(s => s.WidthM);

    List<(string, double)> Pairs() => _strips.Select(s => (s.Type, s.WidthM)).ToList();

    /// <summary>Why the design can't be built, or null when it can.</summary>
    public string? Problem => PathRules.Check(Pairs(), OneWay);

    /// <summary>What the city will make of it, or null while there's a <see cref="Problem"/>.</summary>
    public DesignStats? Stats
    {
        get
        {
            if (Problem != null) return null;
            var s = PathRules.Derive(Pairs(), OneWay);
            return new DesignStats(s.Kind, s.Cars, s.Bikes, s.Pedestrians, s.OneWay, s.Frontage, s.SpeedKmh, s.CapacityPerHour,
                s.CostPerCell, s.MaintenancePerCell, s.NoiseDb, s.WidthM, s.Tram, s.MaxGradePct, s.Stairs, s.Surfaces, s.Color);
        }
    }

    /// <summary>The action that saves it: a new design, or the one being redesigned.</summary>
    public P.Action Save() => new()
    {
        DesignPath = new P.DesignPath
        {
            Id = Id, Name = Name.Trim(), OneWay = OneWay,
            Strips = { _strips.Select(s => new P.PathStrip { Type = s.Type, WidthM = s.WidthM }) },
        },
    };

    /// <summary>The action that takes a design off the city's list.</summary>
    public static P.Action Delete(string id) => new() { DeletePathDesign = new P.DeletePathDesign { Id = id } };
}
