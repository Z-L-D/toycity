using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// How an arc or diagonal is drawn (part 9): the line the player drew, through the centres of its end cells, rather
/// than the stairs of cells the sim runs it on. The cells under it are drawn as open ground, tinted with the zone
/// beside them, so zoning seems to fill the triangles a diagonal leaves.
/// </summary>
public static class RoadShape
{
    /// <summary>Points along a strip in cells (a cell's centre is x + 0.5), about four to a cell, ends included.</summary>
    public static List<(float X, float Y)> Points(RoadStrip s)
    {
        float x0 = s.X0 + 0.5f, y0 = s.Y0 + 0.5f, x1 = s.X1 + 0.5f, y1 = s.Y1 + 0.5f;
        float cx = s.Curved ? s.Cx + 0.5f : (x0 + x1) / 2, cy = s.Curved ? s.Cy + 0.5f : (y0 + y1) / 2;
        float len = MathF.Sqrt((cx - x0) * (cx - x0) + (cy - y0) * (cy - y0)) + MathF.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
        int n = Math.Max(2, (int)MathF.Ceiling(len * 4));
        var pts = new List<(float, float)>(n + 1);
        for (int k = 0; k <= n; k++)
        {
            float t = k / (float)n, a = (1 - t) * (1 - t), b = 2 * (1 - t) * t, c = t * t;
            pts.Add((a * x0 + b * cx + c * x1, a * y0 + b * cy + c * y1));
        }
        return pts;
    }

    /// <summary>
    /// Whether a path cell is drawn by its strip: every cell of a strip but its two ends, which stay ordinary road
    /// cells so the strip joins the streets it meets.
    /// </summary>
    public static bool UnderStrip(CityMirror c, int i) => c.StripCells.Contains(i) && !c.StripEnds.Contains(i);

    /// <summary>The zone the open ground under a strip takes: the zone most cells around it have, or 0.</summary>
    public static int FillZone(CityMirror c, int i)
    {
        int x = i % c.Width, y = i / c.Width;
        Span<int> count = stackalloc int[16];
        int best = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!c.InBounds(x + dx, y + dy)) continue;
                int j = c.Idx(x + dx, y + dy);
                int z = c.Zones[j];
                if (z == 0 || z >= count.Length || c.Paths[j] != 0) continue;
                if (++count[z] > count[best]) best = z;
            }
        return best;
    }
}
