namespace ToyCity.Sim;

/// <summary>
/// Roadside buildings (bus stops and their upgrades) stand on the kerb of a road cell instead of on land: they take no
/// tile, don't block the road, and go when the road does. Their position is the road cell and Side is the kerb.
/// </summary>
public sealed partial class World
{
    static readonly string[] SideNames = { "north", "east", "south", "west" };

    /// <summary>Where a roadside building would go for a click at (x,y): on that road's kerb, or the kerb facing land clicked beside a road.</summary>
    string? CheckKerb(BuildingType type, int x, int y, int side, out (int X, int Y, int Side) at)
    {
        at = (x, y, 0);
        if (!InBounds(x, y)) return "That's off the map.";
        var kind = type.Transit;
        bool Road(int cx, int cy) => InBounds(cx, cy) && (kind == TransitKind.None ? Path[Idx(cx, cy)] != 0 : Carries(kind, Idx(cx, cy)));
        string what = kind == TransitKind.None ? "road" : $"road {kind.ToString().ToLowerInvariant()}es can use";
        if (!Road(x, y))
        {
            // Clicked beside a road: the stop goes on the kerb facing the click.
            if (Path[Idx(x, y)] != 0) return $"{type.Name}s go beside a {what}.";
            if (side is >= 0 and <= 3)
            {
                // The kerb facing the clicked land is on the side pointing back at it.
                int rx = x - Dx4Side[side], ry = y - Dy4Side[side];
                if (!Road(rx, ry)) return $"{type.Name}s go beside a {what}.";
                x = rx; y = ry;
            }
            for (int k = 0; k < 4 && side < 0; k++)
            {
                int rx = x - Dx4Side[k], ry = y - Dy4Side[k];
                if (Road(rx, ry)) { x = rx; y = ry; side = k; }
            }
            if (side < 0) return $"{type.Name}s go beside a {what}.";
        }
        if (side < 0)
            for (int k = 0; k < 4 && side < 0; k++)
                if (Kerb(x, y, k)) side = k;
        if (side is < 0 or > 3) return "Pick a side of the road.";
        if (!Kerb(x, y, side)) return $"There's no kerb on the {SideNames[side]} side there: another road joins it.";
        if (RoadsideAt(Idx(x, y)).Any(b => b.Side == side)) return "There's already a stop on that side of the road.";
        at = (x, y, side);
        return null;
    }

    // Side k points from the road cell to its kerb: north, east, south, west.
    static readonly int[] Dx4Side = { 0, 1, 0, -1 }, Dy4Side = { -1, 0, 1, 0 };

    /// <summary>Whether a road cell has a kerb on side k: the cell beyond is not a path.</summary>
    bool Kerb(int x, int y, int k)
    {
        int nx = x + Dx4Side[k], ny = y + Dy4Side[k];
        return !InBounds(nx, ny) || Path[Idx(nx, ny)] == 0;
    }

    /// <summary>The roadside buildings on a road cell.</summary>
    public IEnumerable<Building> RoadsideAt(int cell)
    {
        int x = cell % Width, y = cell / Width;
        foreach (var b in Buildings)
            if (b != null && b.X == x && b.Y == y && TypeOf(b).Roadside) yield return b;
    }

    /// <summary>The building at a cell: one standing on it, or else a roadside one on its kerb.</summary>
    public Building? BuildingOrStopAt(int cell) => BuildingAtCell(cell) ?? RoadsideAt(cell).FirstOrDefault();

    /// <summary>Roadside buildings whose road has gone (bulldozed, flattened) go too. Runs with RebuildAccess.</summary>
    void ClearStrandedRoadside()
    {
        for (int id = 0; id < Buildings.Count; id++)
            if (Buildings[id] is { } b && TypeOf(b).Roadside && Path[Idx(b.X, b.Y)] == 0) RemoveBuilding(b);
    }
}
