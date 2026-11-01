namespace ToyCity.Sim;

/// <summary>
/// The ground in layers (Beta 3.2, G1 to G3): loose soil over bedrock, and what that rock is. Made once, when the
/// ground is made, from its shape: flat ground and valley floors hold deep soil, near rivers and lakes deeper still
/// (silt they left), steep slopes and high ground thin soil or bare rock. The rock under a cell comes from slow noise
/// in the map's own coordinates, with hard rock under the mountains. A region makes it over all its tiles at once and
/// cuts it up with the ground, so it meets at every border.
/// <para>
/// A cut follows its material: soil stands at about 32 degrees, rock nearly sheer, each kind its own angle
/// (<see cref="PathGrade.RockCutDm"/>). Cutting rock costs more than digging soil. Fill is always soil.
/// </para>
/// </summary>
public sealed partial class World
{
    /// <summary>The top of the bedrock at each grid corner, in decimetres; the ground above it is soil. Never above
    /// the ground: where a cut goes below it, the rock is the ground.</summary>
    public readonly ushort[] Bedrock;

    /// <summary>The kind of rock under each cell (<see cref="PathGrade.RockKinds"/>): 1 shale, 2 sandstone,
    /// 3 limestone, 4 granite.</summary>
    public readonly byte[] Rock;

    /// <summary>Soil depth (dm) at a corner.</summary>
    public int SoilDm(int corner) => Corner[corner] - Bedrock[corner];

    /// <summary>A cell's soil depth (dm): its corners' averaged.</summary>
    public int CellSoilDm(int i)
    {
        int cw = Width + 1, c = i / Width * cw + i % Width;
        return (SoilDm(c) + SoilDm(c + 1) + SoilDm(c + cw) + SoilDm(c + cw + 1)) / 4;
    }

    /// <summary>A corner that moved can't leave rock above the ground: dug below the rock, the rock is the ground.</summary>
    void KeepBedrockUnder(int corner)
    {
        if (Bedrock[corner] > Corner[corner]) Bedrock[corner] = Corner[corner];
    }

    /// <summary>Lays out the soil and rock under this city's own ground (a lone city, or an old save).</summary>
    void GenerateGeology()
    {
        var (bed, rock) = GenerateGeology(Seed, Corner, River, Width, HeightCells, WaterLevel);
        bed.CopyTo(Bedrock, 0);
        rock.CopyTo(Rock, 0);
    }

    /// <summary>The deepest soil the ground holds, on flat lowland far from water: 8 m.</summary>
    public const int DeepSoilDm = 80;
    /// <summary>What a river or lake adds to the soil beside it: up to 10 m of silt.</summary>
    public const int SiltDm = 100;
    /// <summary>How far from water its silt reaches, in cells.</summary>
    public const int SiltReach = 6;

    /// <summary>
    /// The bedrock (dm at each corner) and rock kind (each cell) for a map's ground. <paramref name="rivers"/> is each
    /// cell's river surface in steps (0 for none).
    /// </summary>
    public static (ushort[] Bedrock, byte[] Rock) GenerateGeology(ulong seed, ushort[] corners, byte[] rivers, int width, int height, int waterLevel = 8)
    {
        int cw = width + 1, ch = height + 1;
        ulong salt = seed * 0xA24BAED4963EE407UL + 0x9FB21C651E98DF25UL;
        double Lattice(int o, int x, int y)
        {
            ulong h = salt ^ ((ulong)(uint)o * 0xD6E8FEB86659FD93UL) ^ ((ulong)(uint)x * 0x9E3779B185EBCA87UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
        // Smooth value noise about 0..1 with the given period, in cells.
        double Noise(int o, double x, double y, double period)
        {
            double fx = x / period, fy = y / period;
            int gx = (int)Math.Floor(fx), gy = (int)Math.Floor(fy);
            double rx = fx - gx, ry = fy - gy;
            rx = rx * rx * (3 - 2 * rx); ry = ry * ry * (3 - 2 * ry);
            double a = Lattice(o, gx, gy), b = Lattice(o, gx + 1, gy), c = Lattice(o, gx, gy + 1), d = Lattice(o, gx + 1, gy + 1);
            return a + (b - a) * rx + (c - a) * ry + (a - b - c + d) * rx * ry;
        }
        int sea = waterLevel * StepDm;
        // How far each corner is from water, in cells: rivers and the sea or lakes below the waterline.
        var dist = new int[cw * ch];
        Array.Fill(dist, int.MaxValue);
        var queue = new Queue<int>();
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            int c = y * cw + x;
            bool wet = corners[c] < sea;
            for (int dy = -1; dy <= 0 && !wet; dy++)
            for (int dx = -1; dx <= 0 && !wet; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px >= 0 && py >= 0 && px < width && py < height && rivers[py * width + px] != 0) wet = true;
            }
            if (wet) { dist[c] = 0; queue.Enqueue(c); }
        }
        while (queue.Count > 0)
        {
            int c = queue.Dequeue(), x = c % cw, y = c / cw, d = dist[c] + 1;
            if (d > SiltReach) continue;
            if (x > 0 && dist[c - 1] > d) { dist[c - 1] = d; queue.Enqueue(c - 1); }
            if (x < cw - 1 && dist[c + 1] > d) { dist[c + 1] = d; queue.Enqueue(c + 1); }
            if (y > 0 && dist[c - cw] > d) { dist[c - cw] = d; queue.Enqueue(c - cw); }
            if (y < ch - 1 && dist[c + cw] > d) { dist[c + cw] = d; queue.Enqueue(c + cw); }
        }
        var bed = new ushort[cw * ch];
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            int c = y * cw + x, h = corners[c];
            // The steepest rise to a neighbour, in dm across a cell.
            int steep = 0;
            if (x > 0) steep = Math.Max(steep, Math.Abs(h - corners[c - 1]));
            if (x < cw - 1) steep = Math.Max(steep, Math.Abs(h - corners[c + 1]));
            if (y > 0) steep = Math.Max(steep, Math.Abs(h - corners[c - cw]));
            if (y < ch - 1) steep = Math.Max(steep, Math.Abs(h - corners[c + cw]));
            // Soil slides off what's steeper than it stands: none left past about 45 degrees.
            double flat = Math.Clamp(1 - steep / 160.0, 0, 1);
            double soil = DeepSoilDm * flat * flat;
            // High ground is thinner: frost and wind strip it.
            soil *= Math.Clamp(1 - (h - sea) / 4000.0, 0.3, 1);
            // Silt beside water, on ground near the water's height.
            if (dist[c] <= SiltReach && h < sea + 120) soil += SiltDm * (1 - dist[c] / (double)(SiltReach + 1)) * flat;
            // Patchy: half to one and a half times, varying over a few cells and over a city.
            soil *= 0.5 + 0.6 * Noise(0, x, y, 24) + 0.4 * Noise(1, x, y, 5) - 0.2;
            bed[c] = (ushort)Math.Clamp(h - (int)Math.Round(soil), 0, h);
        }
        var rock = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x, c = y * cw + x;
            int h = (corners[c] + corners[c + 1] + corners[c + cw] + corners[c + cw + 1]) / 4;
            // Beds of rock, bent by a slow warp; granite comes up through the high ground.
            double wx = x + (Noise(3, x, y, 40) - 0.5) * 30, wy = y + (Noise(4, x, y, 40) - 0.5) * 30;
            double bedNoise = Noise(2, wx, wy, 56);
            double high = Math.Clamp((h - sea - 1800) / 1500.0, 0, 1) + (Noise(5, x, y, 32) - 0.5) * 0.4;
            rock[i] = high > 0.5 ? (byte)4 : bedNoise < 0.38 ? (byte)1 : bedNoise < 0.62 ? (byte)2 : (byte)3;
        }
        return (bed, rock);
    }
}
