namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>Fractal value noise from the seed: rolling hills, a few lakes and a coast.</summary>
    void GenerateTerrain() => GenerateHeights(Seed, Width, HeightCells, WaterLevel).CopyTo(Height, 0);

    /// <summary>
    /// Heights for a map of the given size from the seed. A region makes one map for all its tiles and cuts it up,
    /// so their edges meet; a single city is the same thing at its own size.
    /// </summary>
    public static byte[] GenerateHeights(ulong seed, int width, int heightCells, int waterLevel = 8)
    {
        int N = width * heightCells;
        int Idx(int x, int y) => y * width + x;
        var height = new byte[N];
        ulong salt = seed * 0x9E3779B97F4A7C15UL + 0x1234567UL;
        // Lattice values for each octave, from a hash of (octave, x, y) so the result does not depend on call order.
        double Lattice(int o, int x, int y)
        {
            ulong h = salt ^ ((ulong)(uint)o * 0xD6E8FEB86659FD93UL) ^ ((ulong)(uint)x * 0x9E3779B185EBCA87UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
        static double Smooth(double t) => t * t * (3 - 2 * t);

        var raw = new double[N];
        double min = double.MaxValue, max = double.MinValue;
        // Hills about half a city across: a region's are the same size as a lone city's, just more of them.
        int basePeriod = Math.Min(128, Math.Max(16, Math.Min(width, heightCells) / 2));
        for (int y = 0; y < heightCells; y++)
        for (int x = 0; x < width; x++)
        {
            double v = 0, amp = 1, total = 0;
            int period = basePeriod;
            for (int o = 0; o < 5 && period >= 2; o++)
            {
                int gx = x / period, gy = y / period;
                double fx = Smooth((x % period) / (double)period), fy = Smooth((y % period) / (double)period);
                double a = Lattice(o, gx, gy), b = Lattice(o, gx + 1, gy), c = Lattice(o, gx, gy + 1), d = Lattice(o, gx + 1, gy + 1);
                double top = a + (b - a) * fx, bottom = c + (d - c) * fx;
                v += (top + (bottom - top) * fy) * amp;
                total += amp;
                amp *= 0.5;
                period /= 2;
            }
            v /= total;
            raw[Idx(x, y)] = v;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        // Map so the lowest ~18% of the map sits under water and the rest climbs gently to about 24.
        var sorted = (double[])raw.Clone();
        Array.Sort(sorted);
        double waterCut = sorted[(int)(N * 0.18)];
        for (int i = 0; i < N; i++)
        {
            double v = raw[i];
            int h;
            if (v < waterCut)
                h = waterLevel - 1 - (int)((waterCut - v) / Math.Max(1e-9, waterCut - min) * 5);
            else
                h = waterLevel + (int)((v - waterCut) / Math.Max(1e-9, max - waterCut) * 16);
            height[i] = (byte)Math.Clamp(h, 0, MaxHeight);
        }
        return height;
    }

    /// <summary>Distance from each cell to the nearest water, capped at 255.</summary>
    void RecomputeWaterDistance()
    {
        var queue = new Queue<int>();
        for (int i = 0; i < N; i++)
        {
            if (IsWater(i)) { WaterDistance[i] = 0; queue.Enqueue(i); }
            else WaterDistance[i] = 255;
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % Width, y = i / Width;
            int d = WaterDistance[i] + 1;
            if (d >= 255) continue;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int n = Idx(nx, ny);
                if (WaterDistance[n] > d) { WaterDistance[n] = (byte)d; queue.Enqueue(n); }
            }
        }
    }

    static readonly int[] Dx4 = { 1, -1, 0, 0 };
    static readonly int[] Dy4 = { 0, 0, 1, -1 };
}
