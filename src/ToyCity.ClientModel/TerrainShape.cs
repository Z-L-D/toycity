namespace ToyCity.ClientModel;

/// <summary>
/// The ground's shape cell by cell: the height of each of a cell's four corners. Stepped terrain gives every corner
/// the cell's own height, so slopes are stairs. Smooth terrain slopes open ground between neighbouring levels: a
/// corner shared by cells at most one step apart, none of them built on, paved or under water, sits at their
/// average, so the cells around it meet there without a step. Built cells stay flat, which keeps what stands on
/// them level; the sim's heights are untouched, this is only how the ground is drawn.
/// </summary>
public static class TerrainShape
{
    /// <summary>A cell's corner heights in height steps: north-west, north-east, south-east, south-west.</summary>
    public static (float NW, float NE, float SE, float SW) Corners(CityMirror c, int x, int y, bool smooth)
    {
        float own = c.Heights[c.Idx(x, y)];
        if (!smooth || Locked(c, x, y)) return (own, own, own, own);
        return (Corner(c, x, y, own), Corner(c, x + 1, y, own), Corner(c, x + 1, y + 1, own), Corner(c, x, y + 1, own));
    }

    /// <summary>Whether a cell's ground must stay flat: a building or a path on it, or water.</summary>
    public static bool Locked(CityMirror c, int x, int y)
    {
        int i = c.Idx(x, y);
        return c.Paths[i] != 0 || c.BuildingAt[i] != 0 || c.IsWater(i);
    }

    /// <summary>The corner at (cx, cy), between cells cx-1..cx and cy-1..cy.</summary>
    static float Corner(CityMirror c, int cx, int cy, float own)
    {
        int lo = int.MaxValue, hi = int.MinValue, sum = 0, n = 0;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int x = cx + dx, y = cy + dy;
                // The map's edge keeps its walls.
                if (!c.InBounds(x, y) || Locked(c, x, y)) return own;
                int h = c.Heights[c.Idx(x, y)];
                lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                sum += h; n++;
            }
        return hi - lo <= 1 ? (float)sum / n : own;
    }
}
