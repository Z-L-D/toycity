using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// The ground's shape as the views draw it: SimCity 4's heightfield, a height at every grid corner, each cell two
/// triangles split from its north-west corner to its south-east. Heights here are in height steps (2 m); the views
/// scale them.
/// </summary>
public static class TerrainShape
{
    /// <summary>A cell's corner heights in height steps: north-west, north-east, south-east, south-west.</summary>
    public static (float NW, float NE, float SE, float SW) Corners(CityMirror c, int x, int y) =>
        (c.CornerSteps(x, y), c.CornerSteps(x + 1, y), c.CornerSteps(x + 1, y + 1), c.CornerSteps(x, y + 1));

    /// <summary>
    /// The ground's height in height steps at a point (in cells), on the same triangles the views draw, so what
    /// stands on the ground meets it exactly.
    /// </summary>
    public static float Surface(CityMirror c, float fx, float fy)
    {
        int x = Math.Clamp((int)MathF.Floor(fx), 0, c.Width - 1), y = Math.Clamp((int)MathF.Floor(fy), 0, c.Height - 1);
        var (nw, ne, se, sw) = Corners(c, x, y);
        float u = Math.Clamp(fx - x, 0, 1), v = Math.Clamp(fy - y, 0, 1);
        return u >= v ? nw + (ne - nw) * u + (se - ne) * v : nw + (sw - nw) * v + (se - sw) * u;
    }

    /// <summary>The ground's slope at a point, as rise over run in height steps per cell (0 is flat).</summary>
    public static float Slope(CityMirror c, int x, int y)
    {
        var (nw, ne, se, sw) = Corners(c, x, y);
        float gx = (ne + se - nw - sw) / 2, gy = (sw + se - nw - ne) / 2;
        return MathF.Sqrt(gx * gx + gy * gy);
    }
}
