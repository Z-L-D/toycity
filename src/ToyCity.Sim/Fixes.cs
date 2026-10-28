namespace ToyCity.Sim;

/// <summary>
/// What an alert or a petition is about on the map (the area a front end highlights) and the building that would
/// help, if one would, so the player can go straight from the problem to the fix.
/// </summary>
public sealed partial class World
{
    /// <summary>No area: the problem isn't anywhere in particular.</summary>
    public static readonly CellRect NoArea = new(-1, -1, -1, -1);

    /// <summary>The neighbourhood a street name stands for: the 8×8 square <see cref="Voice.Street"/> names.</summary>
    public CellRect NeighbourhoodOf(int x, int y)
    {
        if (!InBounds(x, y)) return NoArea;
        int x0 = x / 8 * 8, y0 = y / 8 * 8;
        return new CellRect(x0, y0, Math.Min(Width - 1, x0 + 7), Math.Min(HeightCells - 1, y0 + 7));
    }

    /// <summary>The building's footprint at a cell, or else its neighbourhood.</summary>
    CellRect AreaAt(int x, int y) =>
        InBounds(x, y) && BuildingAtCell(Idx(x, y)) is { } b ? new CellRect(b.X, b.Y, b.X + b.W - 1, b.Y + b.H - 1) : NeighbourhoodOf(x, y);

    /// <summary>The first building of a category the player can place now, in content order; empty when none is.</summary>
    public string FirstUnlocked(Category c)
    {
        for (int t = 0; t < Content.Types.Count; t++)
            if (Content.Types[t] is { Placeable: true } type && type.Category == c && TypeUnlocked[t]) return type.Id;
        return "";
    }

    static Category? CategoryForLayer(string layer) => layer switch
    {
        "police" => Category.Police, "fire" => Category.Fire, "health" => Category.Health,
        "education" => Category.Education, "parks" => Category.Park, _ => null,
    };

    public (string Fix, CellRect Area) AlertHelp(Alert a)
    {
        var area = AreaAt(a.X, a.Y);
        Category? c = a.Key switch
        {
            "no-power" or "brownout" => Category.Power,
            "water-short" => Category.Water,
            "garbage" => Category.Garbage,
            "graffiti" => Category.Police,
            _ when a.Key.StartsWith("disaster-") && Disasters.Any(d => $"disaster-{d.Id}" == a.Key && d.Kind == DisasterKind.Fire) => Category.Fire,
            _ => null,
        };
        string fix = c is { } cat ? FirstUnlocked(cat) : "";
        // A crowded stop wants another of the same kind nearby.
        if (a.Key == "stop-crowded" && InBounds(a.X, a.Y) && BuildingAtCell(Idx(a.X, a.Y)) is { } stop) fix = TypeOf(stop).Id;
        return (fix, area);
    }

    /// <summary>A petition's neighbourhood, and the building whose coverage its goal measures.</summary>
    public (string Fix, CellRect Area) PromptHelp(Prompt p)
    {
        var area = NeighbourhoodOf(p.X, p.Y);
        string fix = "";
        if (p.Kind == EventKind.Petition && Content.Events[p.Event].Goal is { } goal)
            foreach (var n in goal.Nodes)
                if (n.Op == "measure.here" && n.Params.TryGetValue("layer", out var layer) && CategoryForLayer(layer) is { } c) { fix = FirstUnlocked(c); break; }
        return (fix, area);
    }
}
