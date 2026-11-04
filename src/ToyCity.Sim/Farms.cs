namespace ToyCity.Sim;

/// <summary>
/// What the farm layer holds on a cell (Beta 4.1, part 2), packed in a byte: the low two bits are what the cell is
/// (<see cref="Field"/>, <see cref="Track"/> or <see cref="Yard"/>), the next three a field's crop, and bit 5 which
/// way its rows run. The clients unpack it the same way.
/// </summary>
public static class FarmLand
{
    public const int None = 0, Field = 1, Track = 2, Yard = 3;
    public const int Wheat = 0, Corn = 1, Soy = 2, Hay = 3, Canola = 4;
    public static readonly string[] CropNames = { "wheat", "corn", "soybeans", "hay", "canola" };

    /// <summary>Farms lie on a grid of blocks this many cells a side (256 m), a farm track along the north and west edge of each.</summary>
    public const int Block = 16;

    public static int What(byte farm) => farm & 3;
    public static int Crop(byte farm) => (farm >> 2) & 7;
    public static bool RowsNorthSouth(byte farm) => (farm & 32) != 0;
    public static byte Pack(int what, int crop = 0, bool northSouth = false) => (byte)(what | crop << 2 | (northSouth ? 32 : 0));
}

/// <summary>
/// Farm country (Beta 4.1, part 2). A region can be made with its fertile low ground already farmed: a grid of farm
/// tracks every 256 m, in the region's own coordinates so the grid runs on over the tiles' borders, and fields of
/// about a hectare and a half between them, a farmstead on some blocks and a windbreak of trees across others.
/// <para>
/// Fertility comes from the ground: flat, deep soil, low down and near a river or the floodplain is the best. It's
/// derived, never saved. The farm layer itself is saved (format 20): building, paving, zoning or flooding a cell
/// takes its farm, bulldozing clears it for nothing, and the farms pay the city a small rate each month.
/// </para>
/// </summary>
public sealed partial class World
{
    /// <summary>Each cell's farm (<see cref="FarmLand"/>), 0 for none. Saved.</summary>
    public readonly byte[] Farm;

    /// <summary>Whether this city was made with farm country (so a replay makes it the same way). Saved.</summary>
    public bool FarmCountry { get; private set; }

    /// <summary>Fields, tracks and farmsteads on the map now, counted when the farms change.</summary>
    public int FieldCells { get; private set; }

    /// <summary>How much a field cell pays the city a month, as a twentieth of a dollar.</summary>
    public const int FarmRateTwentieths = 1;

    /// <summary>
    /// How good a cell is for farming, 0..100: flat, deep soil, low down, and better still near a river. Water, rock,
    /// steep ground and the wet margins of the water score nothing.
    /// </summary>
    public int FertilityAt(int i)
    {
        if (IsWater(i) || WaterDistance[i] <= 1) return 0;
        int cw = Width + 1, c = i / Width * cw + i % Width;
        int nw = Corner[c], ne = Corner[c + 1], sw = Corner[c + cw], se = Corner[c + cw + 1];
        return Fertility(nw, ne, sw, se, WaterLevel * StepDm, CellSoilDm(i), WaterDistance[i]);
    }

    static int Fertility(int nw, int ne, int sw, int se, int seaDm, int soilDm, int waterDistance)
    {
        int lo = Math.Min(Math.Min(nw, ne), Math.Min(sw, se)), hi = Math.Max(Math.Max(nw, ne), Math.Max(sw, se));
        int rise = hi - lo, above = (nw + ne + sw + se) / 4 - seaDm;
        if (above < 0) return 0;
        // Flat ground ploughs; past a rise of 6 m across a cell (a slope of 20 %) nobody would.
        double flat = Math.Clamp(1 - (rise - 10) / 50.0, 0, 1);
        // Deep soil is best, but a plough turns thin soil over rock too; bare rock grows nothing.
        double soil = soilDm <= 0 ? 0.3 : 0.55 + 0.45 * Math.Clamp(soilDm / 15.0, 0, 1);
        // Low-lying land: the valley floors and the coastal plain, up to 30 m over the sea, then less up to 150 m.
        double low = Math.Clamp(1 - (above - 300) / 1200.0, 0, 1);
        // A river's floodplain lays down silt.
        double silt = waterDistance <= 8 ? 0.25 * (1 - waterDistance / 8.0) : 0;
        return (int)Math.Round(100 * Math.Clamp(flat * soil * (0.35 + 0.65 * low) + silt * flat, 0, 1));
    }

    static int Mod(int a, int m) => ((a % m) + m) % m;

    /// <summary>
    /// Lays farms over a new map's fertile ground. (<paramref name="ox"/>, <paramref name="oy"/>) is where this map
    /// starts in its region's cells: tile sides are whole numbers of blocks, so each block lies in one tile and its
    /// neighbours across a border agree on the tracks between them.
    /// </summary>
    void GenerateFarms(ulong regionSeed, int ox, int oy)
    {
        FarmCountry = true;
        const int B = FarmLand.Block;
        ulong salt = regionSeed * 0x2545F4914F6CDD1DUL + 0x5851F42D4C957F2DUL;
        ulong Roll(int bx, int by, int k) => Hash(salt ^ (ulong)(uint)k * 0x9E3779B97F4A7C15UL, bx * 7919 + by * 104729);
        var fert = new int[N];
        for (int i = 0; i < N; i++) fert[i] = FertilityAt(i);
        int bx0 = (int)Math.Floor(ox / (double)B), by0 = (int)Math.Floor(oy / (double)B);
        int bx1 = (int)Math.Floor((ox + Width - 1) / (double)B), by1 = (int)Math.Floor((oy + HeightCells - 1) / (double)B);
        var farmed = new HashSet<(int, int)>();
        for (int by = by0; by <= by1; by++)
        for (int bx = bx0; bx <= bx1; bx++)
        {
            // A block is farmed when most of it is good ground: four fifths of its cells fertile, and fertile on the whole.
            int good = 0, all = 0; long sum = 0;
            for (int v = 1; v < B; v++)
            for (int u = 1; u < B; u++)
            {
                int x = bx * B + u - ox, y = by * B + v - oy;
                if (!InBounds(x, y)) continue;
                int f = fert[Idx(x, y)];
                all++; sum += f;
                if (f >= 45) good++;
            }
            if (all == 0 || good * 5 < all * 4 || sum < all * 55L) continue;
            farmed.Add((bx, by));
        }
        foreach (var (bx, by) in farmed)
        {
            bool windbreak = Roll(bx, by, 1) % 3 == 0, yard = Roll(bx, by, 2) % 3 != 0;
            for (int v = 0; v < B; v++)
            for (int u = 0; u < B; u++)
            {
                int x = bx * B + u - ox, y = by * B + v - oy;
                if (!InBounds(x, y)) continue;
                int i = Idx(x, y);
                if (IsWater(i)) continue;
                if (u == 0 || v == 0)
                {
                    if (CellRise(i) <= 60) Set(i, FarmLand.Pack(FarmLand.Track));
                    continue;
                }
                // A windbreak of conifers across the middle of the block, against the north wind.
                if (windbreak && v == B / 2)
                {
                    if (CanGrow(i)) { Plant[i] = (byte)PlantKind.Conifer; Canopy[i] = 5; Farm[i] = 0; }
                    continue;
                }
                if (fert[i] < 45) continue;
                if (yard && u <= 3 && v <= 3) { Set(i, FarmLand.Pack(FarmLand.Yard)); continue; }
                // Four fields to a block, each its own crop, rows one way or the other.
                int field = (u >= B / 2 ? 1 : 0) + (v >= B / 2 ? 2 : 0);
                ulong h = Roll(bx, by, 10 + field);
                Set(i, FarmLand.Pack(FarmLand.Field, (int)(h % 5), (h >> 8) % 2 == 0));
            }
            // The tracks of a farmed block reach on along its east and south edges too, to close the grid.
            for (int k = 0; k <= B; k++)
            {
                foreach (var (x, y) in new[] { (bx * B + B - ox, by * B + k - oy), (bx * B + k - ox, by * B + B - oy) })
                {
                    if (!InBounds(x, y)) continue;
                    int i = Idx(x, y);
                    if (!IsWater(i) && CellRise(i) <= 60 && FarmLand.What(Farm[i]) != FarmLand.Field) Set(i, FarmLand.Pack(FarmLand.Track));
                }
            }
        }
        FarmsChanged();
        PlantsChanged();

        void Set(int i, byte f)
        {
            Farm[i] = f;
            Plant[i] = 0; Canopy[i] = 0;
        }
    }

    int CellRise(int i)
    {
        int cw = Width + 1, c = i / Width * cw + i % Width;
        int nw = Corner[c], ne = Corner[c + 1], sw = Corner[c + cw], se = Corner[c + cw + 1];
        return Math.Max(Math.Max(nw, ne), Math.Max(sw, se)) - Math.Min(Math.Min(nw, ne), Math.Min(sw, se));
    }

    void FarmsChanged()
    {
        int n = 0;
        for (int i = 0; i < N; i++) if (FarmLand.What(Farm[i]) == FarmLand.Field) n++;
        FieldCells = n;
        _anyFarm = Array.Exists(Farm, f => f != 0);
    }

    bool _anyFarm;

    /// <summary>Every step: a farm goes where it's built on, paved, zoned for the city or flooded.</summary>
    void TrimFarms()
    {
        if (!_anyFarm) return;
        bool changed = false;
        for (int i = 0; i < N; i++)
        {
            if (Farm[i] == 0) continue;
            if (!IsWater(i) && BuildingAt[i] == 0 && Path[i] == 0 && Dam[i] == 0 && ZoneCode[i] == 0) continue;
            Farm[i] = 0; changed = true;
        }
        if (changed) FarmsChanged();
    }

    /// <summary>The farm cells in a bulldozed area are cleared, for nothing.</summary>
    void ClearFarms(int x0, int y0, int x1, int y1)
    {
        if (!_anyFarm) return;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++) Farm[Idx(x, y)] = 0;
        FarmsChanged();
    }

    /// <summary>The farm rates for the month's budget.</summary>
    long FarmRates() => (long)FieldCells * FarmRateTwentieths / 20;

    /// <summary>What a cell's farm is, for the query panel, or null where there's none.</summary>
    public string? FarmName(int i) => FarmLand.What(Farm[i]) switch
    {
        FarmLand.Field => $"A field of {FarmLand.CropNames[FarmLand.Crop(Farm[i])]}",
        FarmLand.Track => "A farm track",
        FarmLand.Yard => "A farmstead",
        _ => null,
    };
}
