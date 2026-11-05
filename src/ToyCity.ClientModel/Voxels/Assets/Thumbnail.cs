namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// A small picture of a voxel model for the library's buttons: drawn from the south-east and above, each voxel a
/// little cube with its top lit, its east side a shade darker and its south side darker still.
/// </summary>
public static class Thumbnail
{
    /// <summary>RGBA pixels, <paramref name="size"/> square, transparent round the model.</summary>
    public static byte[] Render(VoxModel m, int size)
    {
        var px = new byte[size * size * 4];
        // Screen from voxel corner (x, y, z): u = (x − y)·c, v = (x + y)·c/2 − z·c.
        double uMin = -m.SizeY, uMax = m.SizeX, vMin = -m.SizeZ, vMax = (m.SizeX + m.SizeY) / 2.0;
        double c = Math.Min((size - 2) / (uMax - uMin), (size - 2) / (vMax - vMin));
        double ou = (size - (uMax - uMin) * c) / 2 - uMin * c, ov = (size - (vMax - vMin) * c) / 2 - vMin * c;
        (double U, double V) P(double x, double y, double z) => (ou + (x - y) * c, ov + ((x + y) / 2 - z) * c);
        // Far to near: least x + y first, and lowest first.
        for (int s = 0; s <= m.SizeX + m.SizeY - 2; s++)
            for (int z = 0; z < m.SizeZ; z++)
                for (int x = Math.Max(0, s - m.SizeY + 1); x <= Math.Min(m.SizeX - 1, s); x++)
                {
                    int y = s - x;
                    byte col = m[x, y, z];
                    if (col == 0) continue;
                    bool top = m[x, y, z + 1] == 0, east = m[x + 1, y, z] == 0, south = m[x, y + 1, z] == 0;
                    if (!top && !east && !south) continue;
                    var (r, g, b, _) = VoxModel.Unpack(m.Palette[col]);
                    if (top) Quad(px, size, P(x, y, z + 1), P(x + 1, y, z + 1), P(x + 1, y + 1, z + 1), P(x, y + 1, z + 1), r, g, b, 1.0);
                    if (east) Quad(px, size, P(x + 1, y, z + 1), P(x + 1, y + 1, z + 1), P(x + 1, y + 1, z), P(x + 1, y, z), r, g, b, 0.8);
                    if (south) Quad(px, size, P(x, y + 1, z + 1), P(x + 1, y + 1, z + 1), P(x + 1, y + 1, z), P(x, y + 1, z), r, g, b, 0.62);
                }
        return px;
    }

    static void Quad(byte[] px, int size, (double U, double V) a, (double U, double V) b, (double U, double V) c, (double U, double V) d, byte r, byte g, byte bl, double shade)
    {
        var pts = new[] { a, b, c, d };
        int x0 = Math.Max(0, (int)Math.Floor(pts.Min(p => p.U))), x1 = Math.Min(size - 1, (int)Math.Ceiling(pts.Max(p => p.U)));
        int y0 = Math.Max(0, (int)Math.Floor(pts.Min(p => p.V))), y1 = Math.Min(size - 1, (int)Math.Ceiling(pts.Max(p => p.V)));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (!Inside(pts, x + 0.5, y + 0.5)) continue;
                int i = (y * size + x) * 4;
                px[i] = (byte)(r * shade); px[i + 1] = (byte)(g * shade); px[i + 2] = (byte)(bl * shade); px[i + 3] = 255;
            }
    }

    // A convex quad: the point is on the same side of every edge.
    static bool Inside((double U, double V)[] p, double x, double y)
    {
        int sign = 0;
        for (int i = 0; i < p.Length; i++)
        {
            var (a, b) = (p[i], p[(i + 1) % p.Length]);
            double cross = (b.U - a.U) * (y - a.V) - (b.V - a.V) * (x - a.U);
            if (Math.Abs(cross) < 1e-9) continue;
            int s = Math.Sign(cross);
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }
}
