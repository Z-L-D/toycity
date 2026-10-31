namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>A height step, the unit the city's rules count slopes in, in decimetres: 2 m.</summary>
    public const int StepDm = 20;
    /// <summary>The highest ground, in decimetres.</summary>
    public const int MaxCornerDm = MaxHeight * StepDm;

    /// <summary>
    /// The ground, as SimCity 4 kept it: a height in decimetres at every corner of the grid, (Width + 1) by
    /// (HeightCells + 1), row-major. A cell's ground is the four corners around it, so it can slope any way. Each
    /// cell's <see cref="Height"/> in steps is derived from its corners (their average, rounded) for the rules that
    /// count steps: what's water, how steep a road may climb, how uneven a lot may be.
    /// </summary>
    public readonly ushort[] Corner;
    public int CornerIdx(int x, int y) => y * (Width + 1) + x;

    /// <summary>A cell's height in steps from its four corners.</summary>
    static byte StepOf(int nw, int ne, int se, int sw) => (byte)Math.Clamp((nw + ne + se + sw + 2 * StepDm) / (4 * StepDm), 0, MaxHeight);

    /// <summary>Re-derives the step heights of the cells in a rectangle (clipped to the map) from their corners.</summary>
    void DeriveHeights(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width - 1, x1); y1 = Math.Min(HeightCells - 1, y1);
        int cw = Width + 1;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int c = y * cw + x;
            Height[Idx(x, y)] = StepOf(Corner[c], Corner[c + 1], Corner[c + cw + 1], Corner[c + cw]);
        }
    }

    /// <summary>
    /// Sets cells to step heights, SimCity 4's way: each corner the cells touch goes to the average of their targets,
    /// so a run of cells a step apart becomes a smooth ramp and a level pad is flat. Ground beside them slopes to meet
    /// them. The cells' and their neighbours' step heights are derived again.
    /// </summary>
    void SetLevels(IReadOnlyCollection<(int I, int H)> cells)
    {
        if (cells.Count == 0) return;
        int cw = Width + 1;
        var sum = new Dictionary<int, (int Sum, int N)>();
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var (i, h) in cells)
        {
            int x = i % Width, y = i / Width;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            foreach (int c in new[] { y * cw + x, y * cw + x + 1, (y + 1) * cw + x + 1, (y + 1) * cw + x })
            {
                sum.TryGetValue(c, out var t);
                sum[c] = (t.Sum + h * StepDm, t.N + 1);
            }
        }
        foreach (var (c, t) in sum) Corner[c] = (ushort)Math.Clamp((t.Sum + t.N / 2) / t.N, 0, MaxCornerDm);
        DeriveHeights(x0 - 1, y0 - 1, x1 + 1, y1 + 1);
        // A cell set to a level keeps it: where its neighbours' targets pulled its corners off by enough to round
        // away, its own corners win.
        foreach (var (i, h) in cells)
        {
            if (Height[i] == h) continue;
            int x = i % Width, y = i / Width, c = y * cw + x;
            Corner[c] = Corner[c + 1] = Corner[c + cw + 1] = Corner[c + cw] = (ushort)(h * StepDm);
            DeriveHeights(x - 1, y - 1, x + 1, y + 1);
        }
    }

    /// <summary>Fractal value noise from the seed: rolling hills, a few lakes and a coast.</summary>
    void GenerateTerrain()
    {
        GenerateCorners(Seed, Width, HeightCells, WaterLevel).CopyTo(Corner, 0);
        DeriveHeights(0, 0, Width - 1, HeightCells - 1);
    }

    /// <summary>
    /// The ground at every corner, in decimetres, for a map of the given size from the seed. A region makes one map
    /// for all its tiles and cuts it up, so their edges meet (a tile's last row of corners is its neighbour's first);
    /// a single city is the same thing at its own size.
    /// </summary>
    public static ushort[] GenerateCorners(ulong seed, int width, int heightCells, int waterLevel = 8) =>
        CornersFromSteps(GenerateSteps(seed, width, heightCells, waterLevel), width, heightCells);

    /// <summary>Step heights for a map: each cell's, derived from the generated corners.</summary>
    public static byte[] GenerateHeights(ulong seed, int width, int heightCells, int waterLevel = 8) =>
        StepsFromCorners(GenerateCorners(seed, width, heightCells, waterLevel), width, heightCells);

    /// <summary>Each cell's step height from a corner grid.</summary>
    public static byte[] StepsFromCorners(ushort[] corners, int width, int heightCells)
    {
        int cw = width + 1;
        var h = new byte[width * heightCells];
        for (int y = 0; y < heightCells; y++)
        for (int x = 0; x < width; x++)
        {
            int c = y * cw + x;
            h[y * width + x] = StepOf(corners[c], corners[c + 1], corners[c + cw + 1], corners[c + cw]);
        }
        return h;
    }

    /// <summary>
    /// Corners for ground known only by its cells' steps (a save from before corners): each corner is the average of
    /// the cells around it, so the old terraces become slopes.
    /// </summary>
    public static ushort[] CornersFromSteps(byte[] steps, int width, int heightCells)
    {
        int cw = width + 1;
        var corners = new ushort[cw * (heightCells + 1)];
        for (int y = 0; y <= heightCells; y++)
        for (int x = 0; x <= width; x++)
        {
            int sum = 0, n = 0;
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (cx < 0 || cy < 0 || cx >= width || cy >= heightCells) continue;
                sum += steps[cy * width + cx]; n++;
            }
            corners[y * cw + x] = (ushort)((sum * StepDm + n / 2) / n);
        }
        return corners;
    }

    static byte[] GenerateSteps(ulong seed, int width, int heightCells, int waterLevel)
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
