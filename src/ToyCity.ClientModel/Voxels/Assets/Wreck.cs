namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// A wrecked building's voxels: the model with holes knocked through its roof and walls and the edges of them
/// charred. The city draws buildings as polygons and swaps to this when one burns, falls derelict or comes down. The
/// holes come from a seed (the building's id), so every client wrecks a building the same way.
/// </summary>
public static class Wreck
{
    public const byte Char = AssetPalette.Black;

    /// <summary>
    /// A wrecked copy of a model at a detail (voxels a half metre). <paramref name="amount"/> 0..1 is how badly:
    /// how many holes, a few a cell at 0.35.
    /// </summary>
    public static VoxModel Carve(VoxModel m, int detail, int seed, double amount = 0.35)
    {
        var o = new VoxModel(m.SizeX, m.SizeY, m.SizeZ);
        Array.Copy(m.Voxels, o.Voxels, m.Voxels.Length);
        Array.Copy(m.Palette, o.Palette, 256);
        Array.Copy(m.Smooth, o.Smooth, 256);
        var r = new Random(seed);
        double v = 0.5 / detail;
        double cells = m.SizeX * m.SizeY * v * v / 256.0;
        int holes = Math.Max(4, (int)Math.Round(cells * 14 * amount));
        var top = new int[m.SizeX * m.SizeY];
        for (int y = 0; y < m.SizeY; y++)
            for (int x = 0; x < m.SizeX; x++)
            {
                int z = m.SizeZ - 1;
                while (z > 0 && m[x, y, z] == 0) z--;
                top[x + y * m.SizeX] = z;
            }
        int highest = top.Max();
        double big = Math.Max(1, highest * v / 25);
        int tries = 0;
        for (int h = 0; h < holes && tries++ < holes * 20;)
        {
            double rad = (1.6 + r.NextDouble() * 2.6) * big;
            double hx, hy, hz;
            if (r.NextDouble() < 0.5)
            {
                // Through the roof: somewhere the building stands a storey or more above its ground.
                int x = r.Next(m.SizeX), y = r.Next(m.SizeY), t = top[x + y * m.SizeX];
                if (t * v < 3) continue;
                (hx, hy, hz) = (x * v, y * v, t * v - rad * 0.3);
            }
            else
            {
                // Into a wall: in from one side of the lot at a height until the building is met.
                hz = 2 + r.NextDouble() * Math.Max(0, highest * v - 3);
                int z = (int)(hz / v), side = r.Next(4), along = r.Next(side < 2 ? m.SizeX : m.SizeY), depth = side < 2 ? m.SizeY : m.SizeX;
                int hit = -1;
                for (int i = 0; i < depth && hit < 0; i++)
                {
                    var (x, y) = side switch { 0 => (along, i), 1 => (along, m.SizeY - 1 - i), 2 => (i, along), _ => (m.SizeX - 1 - i, along) };
                    if (m[x, y, z] != 0) hit = i;
                }
                if (hit < 0) continue;
                var (cx, cy) = side switch { 0 => (along, hit), 1 => (along, m.SizeY - 1 - hit), 2 => (hit, along), _ => (m.SizeX - 1 - hit, along) };
                (hx, hy) = (cx * v, cy * v);
            }
            Ball(o, hx, hy, hz, rad, v, 0);
            Ball(o, hx, hy, hz, rad + Math.Max(v, 0.6), v, Char);
            // What came down lies in a heap below the hole.
            Heap(o, hx, hy, Math.Min(rad, 2.5), v, r);
            h++;
        }
        Unhang(o);
        return o;
    }

    /// <summary>Takes away what the holes cut loose: every voxel no longer joined to the ground (a chimney's top, half a crown).</summary>
    static void Unhang(VoxModel m)
    {
        int sx = m.SizeX, sy = m.SizeY, plane = sx * sy;
        var held = new bool[m.Voxels.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < plane; i++) if (m.Voxels[i] != 0) { held[i] = true; stack.Push(i); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % sx, y = i / sx % sy, z = i / plane;
            void Try(int j) { if (!held[j] && m.Voxels[j] != 0) { held[j] = true; stack.Push(j); } }
            if (x > 0) Try(i - 1);
            if (x < sx - 1) Try(i + 1);
            if (y > 0) Try(i - sx);
            if (y < sy - 1) Try(i + sx);
            if (z > 0) Try(i - plane);
            if (z < m.SizeZ - 1) Try(i + plane);
        }
        for (int i = 0; i < held.Length; i++) if (!held[i]) m.Voxels[i] = 0;
    }

    /// <summary>A low heap of rubble on the ground: grey and charred voxels, the voxels above them left as they are.</summary>
    static void Heap(VoxModel m, double cx, double cy, double rad, double v, Random r)
    {
        int n = (int)Math.Ceiling(rad / v);
        int ix = (int)(cx / v), iy = (int)(cy / v);
        for (int y = Math.Max(0, iy - n); y <= Math.Min(m.SizeY - 1, iy + n); y++)
            for (int x = Math.Max(0, ix - n); x <= Math.Min(m.SizeX - 1, ix + n); x++)
            {
                double d = Math.Sqrt((x - ix) * (x - ix) + (y - iy) * (y - iy)) * v / rad;
                if (d > 1) continue;
                int h = (int)Math.Round((1 - d) * rad * 0.45 / v * (0.6 + 0.4 * r.NextDouble()));
                for (int z = 1; z <= h && z < m.SizeZ; z++)
                    if (m[x, y, z] == 0) m[x, y, z] = ((x + y + z) % 3 == 0) ? Char : AssetPalette.ConcreteDark;
            }
    }

    /// <summary>Empties (colour 0) or chars (any other colour: only solid voxels) a ball, in metres.</summary>
    static void Ball(VoxModel m, double cx, double cy, double cz, double rad, double v, byte c)
    {
        int r = (int)Math.Ceiling(rad / v) + 1, ix = (int)(cx / v), iy = (int)(cy / v), iz = (int)(cz / v);
        for (int z = Math.Max(1, iz - r); z <= Math.Min(m.SizeZ - 1, iz + r); z++)
            for (int y = Math.Max(0, iy - r); y <= Math.Min(m.SizeY - 1, iy + r); y++)
                for (int x = Math.Max(0, ix - r); x <= Math.Min(m.SizeX - 1, ix + r); x++)
                {
                    double dx = (x + 0.5) * v - cx, dy = (y + 0.5) * v - cy, dz = (z + 0.5) * v - cz;
                    if (dx * dx + dy * dy + dz * dz > rad * rad) continue;
                    if (c == 0) m[x, y, z] = 0;
                    else if (m[x, y, z] != 0) m[x, y, z] = c;
                }
    }
}
