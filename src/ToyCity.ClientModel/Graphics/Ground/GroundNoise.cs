namespace ToyCity.ClientModel.Graphics.Ground;

/// <summary>
/// Noise that repeats, for the ground's textures (RC part 6): every function here wraps at the tile's edge, so a
/// texture made from them tiles with no seam. Coordinates u and v run 0 to 1 across the tile. All of it is plain
/// arithmetic on integers and floats, the same on every machine.
/// </summary>
public static class GroundNoise
{
    /// <summary>A hash of a lattice point and a seed.</summary>
    public static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)x * 0x85EBCA6Bu ^ (uint)y * 0xC2B2AE35u ^ (uint)seed * 0x27D4EB2Fu;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }
    }

    /// <summary>A number from 0 up to (not including) 1 for a lattice point and a seed.</summary>
    public static float Rand(int x, int y, int seed) => (Hash(x, y, seed) >> 8) / 16777216f;

    static int Wrap(int i, int period) { i %= period; return i < 0 ? i + period : i; }

    /// <summary>
    /// Gradient noise on a lattice that repeats every <paramref name="period"/> points each way: about -1 to 1,
    /// 0 at the lattice points. x and y are in lattice units.
    /// </summary>
    public static float Perlin(float x, float y, int period, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int xa = Wrap(x0, period), xb = Wrap(x0 + 1, period), ya = Wrap(y0, period), yb = Wrap(y0 + 1, period);
        float u = fx * fx * fx * (fx * (fx * 6 - 15) + 10), v = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        float a = Grad(xa, ya, seed, fx, fy), b = Grad(xb, ya, seed, fx - 1, fy);
        float c = Grad(xa, yb, seed, fx, fy - 1), d = Grad(xb, yb, seed, fx - 1, fy - 1);
        return (a + (b - a) * u + (c + (d - c) * u - (a + (b - a) * u)) * v) * 1.41f;
    }

    static float Grad(int x, int y, int seed, float dx, float dy)
    {
        // Sixteen directions round the circle.
        uint h = Hash(x, y, seed) >> 28;
        float a = h * (MathF.PI / 8);
        return MathF.Cos(a) * dx + MathF.Sin(a) * dy;
    }

    /// <summary>
    /// Fractal noise, about -1 to 1: <paramref name="octaves"/> layers of <see cref="Perlin"/>, the first with
    /// <paramref name="freq"/> lattice cells across the tile, each next twice as fine and <paramref name="gain"/> as strong.
    /// </summary>
    public static float Fbm(float u, float v, int freq, int octaves, int seed, float gain = 0.5f)
    {
        float sum = 0, amp = 1, total = 0;
        int f = Math.Max(1, freq);
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Perlin(u * f, v * f, f, seed + o * 131);
            total += amp; amp *= gain; f *= 2;
        }
        return sum / total;
    }

    /// <summary>Fractal noise folded into ridges, 0 to 1: sharp crests where the noise crosses zero.</summary>
    public static float Ridged(float u, float v, int freq, int octaves, int seed, float gain = 0.5f)
    {
        float sum = 0, amp = 1, total = 0;
        int f = Math.Max(1, freq);
        for (int o = 0; o < octaves; o++)
        {
            float r = 1 - MathF.Abs(Perlin(u * f, v * f, f, seed + o * 131));
            sum += amp * r * r;
            total += amp; amp *= gain; f *= 2;
        }
        return sum / total;
    }

    /// <summary>What <see cref="Cells"/> finds at a point.</summary>
    public readonly record struct Cell(float F1, float F2, int Id, float Dx, float Dy, float Cx, float Cy);

    /// <summary>
    /// Cellular (Worley) noise: the tile is <paramref name="cells"/> squares each way with one point in each, pushed
    /// off its centre by up to <paramref name="jitter"/> of the square. Gives the distance to the nearest point (F1)
    /// and the next (F2), in squares; the nearest's id; the way to it (Dx, Dy, in squares); and where it is
    /// (Cx, Cy, 0 to 1 across the tile). F2 - F1 is 0 on the borders between cells.
    /// <paramref name="stretchX"/> over 1 makes the cells long across.
    /// </summary>
    public static Cell Cells(float u, float v, int cells, int seed, float jitter = 1f, float stretchX = 1f)
    {
        int nx = Math.Max(1, (int)MathF.Round(cells / stretchX)), ny = Math.Max(1, cells);
        float x = u * nx, y = v * ny;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float f1 = 1e9f, f2 = 1e9f, bdx = 0, bdy = 0, bcx = 0, bcy = 0;
        int id = 0;
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int cx = ix + i, cy = iy + j, wx = Wrap(cx, nx), wy = Wrap(cy, ny);
                uint h = Hash(wx, wy, seed);
                float px = cx + 0.5f + ((h & 0xFFFF) / 65535f - 0.5f) * jitter;
                float py = cy + 0.5f + ((h >> 16) / 65535f - 0.5f) * jitter;
                // Distances in the squares' own units, with the stretch put back so cells are measured as they look.
                float dx = (px - x) * stretchX, dy = py - y;
                float d = dx * dx + dy * dy;
                if (d < f1) { f2 = f1; f1 = d; id = (int)(Hash(wx, wy, seed + 7919) & 0x7FFFFFFF); bdx = dx; bdy = dy; bcx = px / nx; bcy = py / ny; }
                else if (d < f2) f2 = d;
            }
        return new Cell(MathF.Sqrt(f1), MathF.Sqrt(f2), id, bdx, bdy, bcx - MathF.Floor(bcx), bcy - MathF.Floor(bcy));
    }

    /// <summary>0 below <paramref name="a"/>, 1 above <paramref name="b"/>, eased between.</summary>
    public static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>A small repeatable source of random numbers for scattering things over a texture.</summary>
public struct Scatter
{
    uint _s;
    public Scatter(int seed) { _s = unchecked((uint)seed * 2654435761u + 0x9E3779B9u); if (_s == 0) _s = 0x1234567u; Next(); Next(); }
    /// <summary>From 0 up to (not including) 1.</summary>
    public float Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s >> 8) / 16777216f; }
    public float Range(float a, float b) => a + (b - a) * Next();
    public int Int(int n) => Math.Min(n - 1, (int)(Next() * n));
    public bool Chance(float p) => Next() < p;
}
