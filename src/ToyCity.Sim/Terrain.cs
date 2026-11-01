namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>A height step, the unit the city's rules count slopes in, in decimetres: 2 m.</summary>
    public const int StepDm = 20;
    /// <summary>The steepest an even-slope terraform leaves the ground, in percent (Beta 3.2): gentler than a road climbs.</summary>
    public const int MaxSlopePct = 7;
    /// <summary>The highest ground, in decimetres.</summary>
    public const int MaxCornerDm = MaxHeight * StepDm;

    /// <summary>
    /// The ground, as SimCity 4 kept it: a height in decimetres at every corner of the grid, (Width + 1) by
    /// (HeightCells + 1), row-major. A cell's ground is the four corners around it, so it can slope any way. Each
    /// cell's <see cref="Height"/> in steps, for the rules that count steps (what's water, how steep a road may
    /// climb, how uneven a lot may be), is its corners averaged and rounded when the ground is made or terraformed;
    /// a road or pad sets the step it needs and the corners follow it.
    /// </summary>
    public readonly ushort[] Corner;
    public int CornerIdx(int x, int y) => y * (Width + 1) + x;

    /// <summary>A cell's height in steps from its four corners.</summary>
    static byte StepOf(int nw, int ne, int se, int sw) => (byte)Math.Clamp((nw + ne + se + sw + 2 * StepDm) / (4 * StepDm), 0, MaxHeight);

    /// <summary>Re-derives the step heights of the cells in a rectangle (clipped to the map) from their corners.</summary>
    void DeriveHeights(int x0, int y0, int x1, int y1)
    {
        FloodGroundChanged();
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
    /// Sets cells to step heights (a road's grade, a building's pad, levelled land) and reshapes the ground around
    /// them: every corner they touch goes to the average of the cells around it, so a run of cells a step apart
    /// becomes a smooth ramp, a pad is flat, and the ground beside slopes to meet them. The cells' steps are exactly
    /// what was asked; nothing else's step changes.
    /// </summary>
    /// <summary>
    /// Gentles the dry land to a <paramref name="factor"/>th of its rise above the water, so a dense grid of streets
    /// can be laid over it: mountains become hills. The benchmark city uses it. The ground follows as it does a road.
    /// </summary>
    public void Soften(int factor)
    {
        var cells = new List<(int, int)>();
        for (int i = 0; i < N; i++)
            if (!IsWater(i)) cells.Add((i, WaterLevel + (Height[i] - WaterLevel) / factor));
        SetLevels(cells, made: false);
    }

    /// <summary>
    /// <paramref name="made"/>: the cells around the corners it moves are made ground (pads and levelling), as
    /// opposed to softening the whole map, which stays natural.
    /// </summary>
    void SetLevels(IReadOnlyCollection<(int I, int H)> cells, bool made = true)
    {
        if (cells.Count == 0) return;
        FloodGroundChanged();
        foreach (var (i, h) in cells) Height[i] = (byte)h;
        int cw = Width + 1;
        var done = new HashSet<int>();
        foreach (var (i, _) in cells)
        {
            int x = i % Width, y = i / Width;
            for (int cy = y; cy <= y + 1; cy++)
            for (int cx = x; cx <= x + 1; cx++)
            {
                if (!done.Add(cy * cw + cx)) continue;
                int was = Corner[cy * cw + cx];
                Corner[cy * cw + cx] = (ushort)CornerFromCells(cx, cy);
                if (made && was != Corner[cy * cw + cx]) MarkMade(cx, cy);
            }
        }
    }

    /// <summary>The cells around a corner that moved are made ground.</summary>
    void MarkMade(int cx, int cy)
    {
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
            if (InBounds(cx + dx, cy + dy)) Made[Idx(cx + dx, cy + dy)] = 1;
    }

    /// <summary>
    /// A corner's height from the cells around it: a building's lot wins (its pad is flat to its edges, the highest
    /// if two meet), otherwise the average of the cells.
    /// </summary>
    int CornerFromCells(int cx, int cy)
    {
        int sum = 0, n = 0, lot = -1;
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
        {
            if (!InBounds(cx + dx, cy + dy)) continue;
            int i = Idx(cx + dx, cy + dy);
            sum += Height[i]; n++;
            if (BuildingAt[i] != 0) lot = Math.Max(lot, Height[i]);
        }
        return lot >= 0 ? lot * StepDm : (sum * StepDm + n / 2) / n;
    }

    /// <summary>Flattens the ground under a lot to its cells' level, all the way to its edges.</summary>
    void FlattenLot(int x, int y, int w, int h)
    {
        int cw = Width + 1;
        for (int cy = y; cy <= y + h; cy++)
        for (int cx = x; cx <= x + w; cx++)
            if (cx <= Width && cy <= HeightCells) Corner[cy * cw + cx] = (ushort)CornerFromCells(cx, cy);
    }

    /// <summary>Every lot's ground flat to its edges: for ground made from cells, which averages across them.</summary>
    void FlattenAllLots()
    {
        foreach (var b in Buildings)
            if (b != null && !TypeOf(b).Roadside) FlattenLot(b.X, b.Y, b.W, b.H);
    }

    /// <summary>Fractal value noise from the seed: rolling hills, a few lakes and a coast.</summary>
    void GenerateTerrain()
    {
        var g = GenerateGround(Seed, Width, HeightCells, WaterLevel);
        g.Corners.CopyTo(Corner, 0);
        g.Rivers.CopyTo(River, 0);
        DeriveHeights(0, 0, Width - 1, HeightCells - 1);
    }

    /// <summary>
    /// The ground at every corner, in decimetres, for a map of the given size from the seed. A region makes one map
    /// for all its tiles and cuts it up, so their edges meet (a tile's last row of corners is its neighbour's first);
    /// a single city is the same thing at its own size.
    /// <para>
    /// Rolling lowland from fractal noise, a coast and lakes in its lowest fifth, and where a slow mask rises,
    /// mountains: ridged noise, sharp crests and long spurs, up to a few hundred metres. Then rain wears it: droplets
    /// run downhill, cutting where they run fast and dropping silt where they slow, so valleys and fans form the way
    /// they do on real ground.
    /// </para>
    /// </summary>
    public static ushort[] GenerateCorners(ulong seed, int width, int heightCells, int waterLevel = 8) =>
        GenerateGround(seed, width, heightCells, waterLevel).Corners;

    /// <summary>The generated corners before the rivers cut their channels.</summary>
    static ushort[] GenerateRawCorners(ulong seed, int width, int heightCells, int waterLevel)
    {
        int cw = width + 1, ch = heightCells + 1, n = cw * ch;
        ulong salt = seed * 0x9E3779B97F4A7C15UL + 0x1234567UL;
        double Lattice(int o, int x, int y)
        {
            ulong h = salt ^ ((ulong)(uint)o * 0xD6E8FEB86659FD93UL) ^ ((ulong)(uint)x * 0x9E3779B185EBCA87UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
        static double Smooth(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        // Gradient noise at a point, in cells, with lattice spacing `period`, about 0..1; octave o picks its own
        // lattice. Gradients rather than values, so it has no square blocks.
        double Noise(int o, double x, double y, double period)
        {
            double fx = x / period, fy = y / period;
            int gx = (int)Math.Floor(fx), gy = (int)Math.Floor(fy);
            double rx = fx - gx, ry = fy - gy;
            double Dot(int cx, int cy, double ox, double oy)
            {
                double a = Lattice(o, cx, cy) * Math.Tau;
                return Math.Cos(a) * ox + Math.Sin(a) * oy;
            }
            double tx = Smooth(rx), ty = Smooth(ry);
            double top = Dot(gx, gy, rx, ry) + (Dot(gx + 1, gy, rx - 1, ry) - Dot(gx, gy, rx, ry)) * tx;
            double bottom = Dot(gx, gy + 1, rx, ry - 1) + (Dot(gx + 1, gy + 1, rx - 1, ry - 1) - Dot(gx, gy + 1, rx, ry - 1)) * tx;
            return 0.5 + (top + (bottom - top) * ty) * 0.7071;
        }
        // Hills about half a city across: a region's are the same size as a lone city's, just more of them.
        double basePeriod = Math.Min(128, Math.Max(16, Math.Min(width, heightCells) / 2));
        var m = new double[n];  // metres
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            // The ground is sampled through a slow warp, so coasts and ranges bend rather than run straight.
            double wx = x + (Noise(30, x, y, basePeriod) - 0.5) * basePeriod * 0.6, wy = y + (Noise(31, x, y, basePeriod) - 0.5) * basePeriod * 0.6;
            // Lowland: five octaves of rolling ground.
            double v = 0, amp = 1, total = 0, period = basePeriod;
            for (int o = 0; o < 5; o++) { v += Noise(o, wx, wy, period) * amp; total += amp; amp *= 0.5; period /= 2; }
            v /= total;
            // Where mountains stand: a slow mask, about two cities across.
            double mask = Noise(10, wx, wy, basePeriod * 3);
            mask = Math.Clamp((mask - 0.5) / 0.2, 0, 1);
            mask = mask * mask * (3 - 2 * mask);
            // Ridged noise: sharp crests where the noise crosses its middle, each octave adding finer spurs.
            double ridge = 0; amp = 1; total = 0; period = basePeriod * 0.7; double weight = 1;
            for (int o = 0; o < 5; o++)
            {
                double r = 1 - Math.Abs(Noise(20 + o, wx, wy, period) * 2 - 1);
                r *= r * weight;
                weight = Math.Clamp(r * 1.6, 0, 1);
                ridge += r * amp; total += amp; amp *= 0.5; period /= 2;
            }
            ridge /= total;
            m[y * cw + x] = v * 40 + mask * ridge * 420;
        }
        Erode(m, cw, ch, seed);

        // The lowest fifth of the map is under water; the rest stands on the shore and climbs from there.
        var sorted = (double[])m.Clone();
        Array.Sort(sorted);
        double shore = sorted[(int)(n * 0.18)], lowest = sorted[0];
        double sea = waterLevel * StepDm / 10.0;
        var corners = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            double h = m[i] >= shore
                ? sea + 0.1 + (m[i] - shore)
                // Under water the bed shelves down to ten metres at the deepest.
                : sea - 0.5 - (shore - m[i]) / Math.Max(1e-9, shore - lowest) * 10;
            corners[i] = (ushort)Math.Clamp((int)Math.Round(h * 10), 0, MaxCornerDm);
        }
        return corners;
    }

    /// <summary>
    /// Hydraulic erosion on a height grid in metres: droplets of rain, from a seeded generator so the same seed makes
    /// the same ground, run downhill carrying silt, cutting where they speed up and dropping it where they slow or
    /// pool. A few on each corner of the map.
    /// </summary>
    static void Erode(double[] h, int w, int hgt, ulong seed)
    {
        var rng = new Rng(seed ^ 0xE20510A11UL);
        int drops = w * hgt / 2;
        const double Inertia = 0.1, Capacity = 4, Deposit = 0.3, ErodeRate = 0.3, Evaporate = 0.02, Gravity = 4, MinSlope = 0.01;
        double At(int x, int y) => h[y * w + x];
        for (int d = 0; d < drops; d++)
        {
            double px = (rng.NextU64() >> 11) * (1.0 / (1UL << 53)) * (w - 2), py = (rng.NextU64() >> 11) * (1.0 / (1UL << 53)) * (hgt - 2);
            double dx = 0, dy = 0, speed = 1, water = 1, silt = 0;
            for (int life = 0; life < 48; life++)
            {
                int ix = (int)px, iy = (int)py;
                double u = px - ix, v = py - iy;
                double a = At(ix, iy), b = At(ix + 1, iy), c = At(ix, iy + 1), e = At(ix + 1, iy + 1);
                double gx = (b - a) * (1 - v) + (e - c) * v, gy = (c - a) * (1 - u) + (e - b) * u;
                double here = a * (1 - u) * (1 - v) + b * u * (1 - v) + c * (1 - u) * v + e * u * v;
                dx = dx * Inertia - gx * (1 - Inertia);
                dy = dy * Inertia - gy * (1 - Inertia);
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) break;
                dx /= len; dy /= len;
                double nx = px + dx, ny = py + dy;
                if (nx < 0 || ny < 0 || nx >= w - 1 || ny >= hgt - 1) break;
                int jx = (int)nx, jy = (int)ny;
                double ju = nx - jx, jv = ny - jy;
                double next = At(jx, jy) * (1 - ju) * (1 - jv) + At(jx + 1, jy) * ju * (1 - jv) + At(jx, jy + 1) * (1 - ju) * jv + At(jx + 1, jy + 1) * ju * jv;
                double drop = next - here;
                double cap = Math.Max(-drop, MinSlope) * speed * water * Capacity;
                void Spread(double amount)
                {
                    h[iy * w + ix] += amount * (1 - u) * (1 - v);
                    h[iy * w + ix + 1] += amount * u * (1 - v);
                    h[(iy + 1) * w + ix] += amount * (1 - u) * v;
                    h[(iy + 1) * w + ix + 1] += amount * u * v;
                }
                if (silt > cap || drop > 0)
                {
                    // Uphill, fill the pit it's in; otherwise drop some of what it can't carry.
                    double put = drop > 0 ? Math.Min(drop, silt) : (silt - cap) * Deposit;
                    silt -= put;
                    Spread(put);
                }
                else
                {
                    double take = Math.Min((cap - silt) * ErodeRate, -drop);
                    silt += take;
                    Spread(-take);
                }
                speed = Math.Sqrt(Math.Max(0, speed * speed - drop * Gravity));
                water *= 1 - Evaporate;
                px = nx; py = ny;
            }
        }
    }

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
