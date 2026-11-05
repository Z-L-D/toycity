namespace ToyCity.ClientModel.Graphics;

/// <summary>One tree crown as the views draw it: where on the map (in cells), how big and what shade.</summary>
public readonly record struct TreeSpot(float X, float Y, int Kind, float Scale, float Shade, float Turn);

/// <summary>
/// Where the trees on each cell stand (Beta 4.1, part 1), the same in every client and every frame: each cell's
/// crowns are scattered from a hash of its index, so a wood never shuffles when it redraws. Orchards stand in rows.
/// The graphics detail caps how many crowns a cell draws: a quarter, half or all of them.
/// </summary>
public static class TreeLayout
{
    public const int Broadleaf = 1, Conifer = 2, Scrub = 3, Reeds = 4, Orchard = 5;

    /// <summary>How many of a cell's crowns to draw at a building detail level.</summary>
    public static int Drawn(int canopy, BuildingDetail detail) => detail switch
    {
        BuildingDetail.Low => (canopy + 3) / 4,
        BuildingDetail.Medium => (canopy + 1) / 2,
        _ => canopy,
    };

    static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
        return x;
    }

    static float Unit(uint h) => (h & 0xFFFFFF) / (float)0x1000000;

    /// <summary>The crowns on cell (x, y) of a map <paramref name="width"/> wide holding <paramref name="plants"/> (kind * 16 + crowns).</summary>
    public static void Spots(int x, int y, int width, byte plants, BuildingDetail detail, List<TreeSpot> into)
    {
        int kind = plants >> 4, canopy = plants & 15;
        if (kind == 0 || canopy == 0) return;
        int n = Drawn(canopy, detail);
        // With fewer drawn, each stands for several: bigger crowns keep the wood looking closed.
        float fill = (float)Math.Sqrt(canopy / (float)Math.Max(1, n));
        uint seed = Hash((uint)(y * width + x) * 0x9E3779B9u + 0x632BE5ABu);
        for (int k = 0; k < n; k++)
        {
            uint h1 = Hash(seed + (uint)k * 0x85EBCA6Bu), h2 = Hash(h1), h3 = Hash(h2);
            float px, py;
            if (kind == Orchard)
            {
                // Rows of four, a quarter cell apart.
                px = 0.125f + 0.25f * (k % 4) + (Unit(h1) - 0.5f) * 0.04f;
                py = 0.25f + 0.5f * (k / 4 % 2) + (Unit(h2) - 0.5f) * 0.04f;
            }
            else { px = 0.08f + 0.84f * Unit(h1); py = 0.08f + 0.84f * Unit(h2); }
            float scale = (0.75f + 0.5f * Unit(h3)) * (kind is Scrub or Reeds ? 1f : Math.Min(1.6f, fill));
            into.Add(new TreeSpot(x + px, y + py, kind, scale, Unit(Hash(h3)), Unit(Hash(h3 + 1)) * 6.2831853f));
        }
    }
}
