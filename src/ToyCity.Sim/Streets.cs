namespace ToyCity.Sim;

/// <summary>
/// Street names come from the roads themselves: a road running east-west takes its row's name and one running
/// north-south its column's, so every cell of a straight road has the same name, the one the news and petitions use.
/// </summary>
public sealed partial class World
{
    bool RoadEW(int x, int y) => (x > 0 && Path[Idx(x - 1, y)] != 0) || (x < Width - 1 && Path[Idx(x + 1, y)] != 0);
    bool RoadNS(int x, int y) => (y > 0 && Path[Idx(x, y - 1)] != 0) || (y < HeightCells - 1 && Path[Idx(x, y + 1)] != 0);

    /// <summary>The name of the road on a path cell. A crossing takes the east-west road's name.</summary>
    public string StreetAt(int x, int y) =>
        !RoadEW(x, y) && RoadNS(x, y) ? Content.Voice.RoadName(true, x) : Content.Voice.RoadName(false, y);

    /// <summary>At a crossing, the north-south road that crosses there; null elsewhere.</summary>
    public string? CrossStreetAt(int x, int y) =>
        Path[Idx(x, y)] != 0 && RoadEW(x, y) && RoadNS(x, y) ? Content.Voice.RoadName(true, x) : null;

    /// <summary>
    /// The street a place is on: the road itself, the road a building faces, or else the nearest road within a few
    /// cells. Far from any road it's "the edge of town".
    /// </summary>
    public string StreetNear(int x, int y)
    {
        if (!InBounds(x, y)) return "the edge of town";
        int i = Idx(x, y);
        if (Path[i] != 0) return StreetAt(x, y);
        var b = BuildingOrStopAt(i);
        if (b != null && b.Access >= 0 && Path[b.Access] != 0) return StreetAt(b.Access % Width, b.Access / Width);
        for (int r = 1; r <= 8; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int nx = x + dx, ny = y + dy;
            if (InBounds(nx, ny) && Path[Idx(nx, ny)] != 0) return StreetAt(nx, ny);
        }
        return "the edge of town";
    }
}
