namespace ToyCity.ClientModel;

/// <summary>
/// The ground's shape cell by cell: the height of each of a cell's four corners. Stepped terrain gives every corner
/// the cell's own height, so slopes are stairs. Smooth terrain slopes the ground between neighbouring levels, as
/// SC4's does: a corner shared by cells at most <see cref="MaxBlend"/> steps apart sits at their average, so the
/// cells around it meet there without a step. Roads take part, so a street climbing a hill ramps instead of
/// stepping, and so does the shore: the bed under the water counts as one step below the waterline, so land runs
/// down into the water and the waterline curves where the slope meets it. Only built cells stay flat, which keeps
/// what stands on them level, and the map's edge keeps its walls. The sim's heights are untouched: this is only how
/// the ground is drawn.
/// </summary>
public static class TerrainShape
{
    /// <summary>The largest difference in height steps between cells that still slope into each other.</summary>
    public const int MaxBlend = 2;

    /// <summary>A cell's corner heights in height steps: north-west, north-east, south-east, south-west.</summary>
    public static (float NW, float NE, float SE, float SW) Corners(CityMirror c, int x, int y, bool smooth)
    {
        if (!smooth) { float h = c.Heights[c.Idx(x, y)]; return (h, h, h, h); }
        float own = Level(c, c.Idx(x, y));
        if (Locked(c, x, y)) return (own, own, own, own);
        return (Corner(c, x, y, own), Corner(c, x + 1, y, own), Corner(c, x + 1, y + 1, own), Corner(c, x, y + 1, own));
    }

    /// <summary>The ground's height in height steps at a point (in cells), between its cell's corners.</summary>
    public static float Surface(CityMirror c, float fx, float fy, bool smooth)
    {
        int x = Math.Clamp((int)MathF.Floor(fx), 0, c.Width - 1), y = Math.Clamp((int)MathF.Floor(fy), 0, c.Height - 1);
        var (nw, ne, se, sw) = Corners(c, x, y, smooth);
        float u = Math.Clamp(fx - x, 0, 1), v = Math.Clamp(fy - y, 0, 1);
        return (nw + (ne - nw) * u) * (1 - v) + (sw + (se - sw) * u) * v;
    }

    /// <summary>Whether a cell's ground must stay flat: a building on it.</summary>
    public static bool Locked(CityMirror c, int x, int y) => c.BuildingAt[c.Idx(x, y)] != 0;

    /// <summary>A cell's height for shaping: its own, or for water a bed one step below the waterline.</summary>
    static float Level(CityMirror c, int i) => c.IsWater(i) ? c.WaterLevel - 1 : c.Heights[i];

    /// <summary>The corner at (cx, cy), between cells cx-1..cx and cy-1..cy.</summary>
    static float Corner(CityMirror c, int cx, int cy, float own)
    {
        float lo = float.MaxValue, hi = float.MinValue, sum = 0;
        int n = 0;
        for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int x = cx + dx, y = cy + dy;
                // The map's edge keeps its walls.
                if (!c.InBounds(x, y)) return own;
                // A building's ground stays at its own height; the open ground around it slopes without it.
                if (Locked(c, x, y)) continue;
                float h = Level(c, c.Idx(x, y));
                lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                sum += h; n++;
            }
        return hi - lo <= MaxBlend ? sum / n : own;
    }
}
