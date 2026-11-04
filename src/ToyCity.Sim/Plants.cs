namespace ToyCity.Sim;

/// <summary>What grows on a cell (Beta 4.1, part 1). Grass is the ground's own, so None is open grassland.</summary>
public enum PlantKind : byte { None = 0, Broadleaf = 1, Conifer = 2, Scrub = 3, Reeds = 4, Orchard = 5 }

/// <summary>
/// Plants trees over an area (Beta 4.1, part 1): <c>Kind</c> None plants what suits each cell's ground. Cells with a
/// building, a path, water or a dam are skipped, and so are cells already that thick with trees.
/// </summary>
public sealed record PlantTreesAction(CellRect Area, PlantKind Kind = PlantKind.None) : SimAction;

/// <summary>
/// The plant layer (Beta 4.1, part 1): trees, scrub and reeds over the ground. Each cell holds a kind and a canopy,
/// the number of tree crowns on it from 0 (open grass) to <see cref="FullCanopy"/> (closed wood). The ground is
/// planted when it's made, from its height, slope, soil, the way it faces and how near water is, with slow noise in
/// the map's own coordinates for woods and clearings, so a region's tiles meet at their borders.
/// <para>
/// Building clears a cell: a path, a building or a dam takes its trees, as the water does; zoned land keeps them
/// until something is built there. Open land that nobody uses grows back slowly from the woods beside it, and a
/// thin wood thickens. Trees lift land value and homes' desirability around them and take up some of the air's
/// pollution. Saved (format 19).
/// </para>
/// </summary>
public sealed partial class World
{
    /// <summary>The most tree crowns a cell holds: a closed wood.</summary>
    public const int FullCanopy = 8;
    /// <summary>What the tree planter plants a cell up to, and what a cell of it costs.</summary>
    public const int PlantedCanopy = 4, TreeCostPerCell = 12;
    /// <summary>Bulldozing a cell of trees costs this.</summary>
    public const int ClearTreesCostPerCell = 2;

    /// <summary>What grows on each cell. Saved.</summary>
    public readonly byte[] Plant;
    /// <summary>Tree crowns on each cell, 0..<see cref="FullCanopy"/>. Saved.</summary>
    public readonly byte[] Canopy;

    short[]? _greenery;

    public PlantKind PlantAt(int i) => (PlantKind)Plant[i];

    /// <summary>
    /// The tree crowns within two cells of a cell (a five by five square), 0..200: how leafy its surroundings are.
    /// Derived from <see cref="Canopy"/> whenever it's needed.
    /// </summary>
    public int GreeneryAt(int i) => (_greenery ??= SumGreenery())[i];

    short[] SumGreenery()
    {
        var g = new short[N];
        // Two passes of a running sum: along the rows, then down the columns.
        var row = new int[N];
        for (int y = 0; y < HeightCells; y++)
        {
            int s = 0, o = y * Width;
            for (int x = -2; x < Width; x++)
            {
                if (x + 2 < Width) s += Canopy[o + x + 2];
                if (x - 3 >= 0) s -= Canopy[o + x - 3];
                if (x >= 0) row[o + x] = s;
            }
        }
        for (int x = 0; x < Width; x++)
        {
            int s = 0;
            for (int y = -2; y < HeightCells; y++)
            {
                if (y + 2 < HeightCells) s += row[(y + 2) * Width + x];
                if (y - 3 >= 0) s -= row[(y - 3) * Width + x];
                if (y >= 0) g[y * Width + x] = (short)s;
            }
        }
        return g;
    }

    void PlantsChanged() => _greenery = null;

    /// <summary>Whether a cell can hold plants at all: dry land with nothing built on it.</summary>
    bool CanGrow(int i) => !IsWater(i) && BuildingAt[i] == 0 && Path[i] == 0 && Dam[i] == 0;

    /// <summary>Every step: whatever was built over trees, or flooded, has taken them.</summary>
    void TrimPlants()
    {
        bool changed = false;
        for (int i = 0; i < N; i++)
        {
            if (Canopy[i] == 0 && Plant[i] == 0) continue;
            if (CanGrow(i)) continue;
            Canopy[i] = 0; Plant[i] = 0; changed = true;
        }
        if (changed) PlantsChanged();
    }

    /// <summary>
    /// Each month: open land that nobody uses grows back from the woods beside it, a crown at a time, and thin woods
    /// thicken. Zoned land and land by a path stays as it is, so a city's own lots don't fill with saplings. Uses a hash
    /// of the day and cell, not the city's dice, so it can't change anything else's luck.
    /// </summary>
    void RunRegrowth()
    {
        bool changed = false;
        ulong salt = Seed * 0x9E3779B97F4A7C15UL ^ (ulong)Tick * 0xD1B54A32D192ED03UL;
        for (int i = 0; i < N; i++)
        {
            if (Canopy[i] >= FullCanopy || ZoneCode[i] != 0 || !CanGrow(i)) continue;
            var suits = SuitedKind(i);
            if (suits == PlantKind.None) continue;
            int x = i % Width, y = i / Width;
            int near = 0;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (InBounds(nx, ny)) near = Math.Max(near, Canopy[Idx(nx, ny)]);
            }
            // A wood spreads into the open beside it one month in twenty; an existing stand thickens one in twelve,
            // never past the wood beside it plus two, so clearings fill in from their edges.
            int odds = Canopy[i] == 0 ? (near >= 3 ? 20 : 0) : 12;
            if (odds == 0) continue;
            if (Canopy[i] > 0 && Canopy[i] >= Math.Max(near, Canopy[i]) + 2) continue;
            if (Hash(salt, i) % (ulong)odds != 0) continue;
            if (Canopy[i] == 0) Plant[i] = (byte)suits;
            Canopy[i]++;
            changed = true;
        }
        if (changed) PlantsChanged();
    }

    static ulong Hash(ulong salt, int i)
    {
        ulong h = salt ^ ((ulong)(uint)i * 0x9E3779B185EBCA87UL);
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
        return h;
    }

    /// <summary>The kind of plant a cell's ground suits, or None where nothing woody grows (bare rock, high peaks).</summary>
    public PlantKind SuitedKind(int i)
    {
        int cw = Width + 1, c = i / Width * cw + i % Width;
        return Suited(Corner[c], Corner[c + 1], Corner[c + cw], Corner[c + cw + 1], WaterLevel * StepDm, CellSoilDm(i), WaterDistance[i]);
    }

    /// <summary>What a cell suits from its four corners (dm), the sea's level (dm), its soil (dm) and how far water is.</summary>
    static PlantKind Suited(int nw, int ne, int sw, int se, int seaDm, int soilDm, int waterDistance)
    {
        int lo = Math.Min(Math.Min(nw, ne), Math.Min(sw, se)), hi = Math.Max(Math.Max(nw, ne), Math.Max(sw, se));
        int rise = hi - lo, above = (nw + ne + sw + se) / 4 - seaDm;
        if (above < 0) return PlantKind.None;
        // Steeper than 50 degrees or bare rock: at most scrub in the cracks.
        if (rise > 190 || soilDm < 4) return rise > 260 ? PlantKind.None : PlantKind.Scrub;
        // Over the treeline, 420 m above the sea, nothing woody; conifers from 260 m, and on north faces from 150 m.
        if (above > 4200) return PlantKind.None;
        bool northFacing = (sw + se) - (nw + ne) > 40;
        if (above > 2600 || (above > 1500 && northFacing)) return PlantKind.Conifer;
        if (waterDistance <= 1 && rise < 30 && above < 300) return PlantKind.Reeds;
        // Thin soil on a slope dries out: scrub.
        if (soilDm < 15 && rise > 60) return PlantKind.Scrub;
        return PlantKind.Broadleaf;
    }

    /// <summary>
    /// Plants a new map's ground. (<paramref name="ox"/>, <paramref name="oy"/>) is where this map starts in its
    /// region's cells and <paramref name="regionSeed"/> the region's seed, so woods carry on across tile borders.
    /// </summary>
    void GeneratePlants(ulong regionSeed, int ox, int oy)
    {
        ulong salt = regionSeed * 0xBF58476D1CE4E5B9UL + 0x94D049BB133111EBUL;
        double Lattice(int o, int x, int y)
        {
            ulong h = salt ^ ((ulong)(uint)o * 0xD6E8FEB86659FD93UL) ^ ((ulong)(uint)x * 0x9E3779B185EBCA87UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
        double Noise(int o, double x, double y, double period)
        {
            double fx = x / period, fy = y / period;
            int gx = (int)Math.Floor(fx), gy = (int)Math.Floor(fy);
            double rx = fx - gx, ry = fy - gy;
            rx = rx * rx * (3 - 2 * rx); ry = ry * ry * (3 - 2 * ry);
            double a = Lattice(o, gx, gy), b = Lattice(o, gx + 1, gy), c = Lattice(o, gx, gy + 1), d = Lattice(o, gx + 1, gy + 1);
            return a + (b - a) * rx + (c - a) * ry + (a - b - c + d) * rx * ry;
        }
        for (int y = 0; y < HeightCells; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = Idx(x, y);
            Plant[i] = 0; Canopy[i] = 0;
            if (!CanGrow(i)) continue;
            var kind = SuitedKind(i);
            if (kind == PlantKind.None) continue;
            double gx = ox + x, gy = oy + y;
            // Woods and clearings: big slow patches with ragged edges.
            double wood = 0.6 * Noise(1, gx, gy, 36) + 0.3 * Noise(2, gx, gy, 12) + 0.1 * Noise(3, gx, gy, 4);
            // Valley floors and the land by water are greener; conifer slopes are mostly wooded.
            if (WaterDistance[i] <= 3) wood += 0.08;
            if (kind == PlantKind.Conifer) wood += 0.12;
            int canopy;
            if (kind == PlantKind.Reeds) canopy = wood > 0.45 ? 2 + (int)((wood - 0.45) * 12) : 0;
            else if (wood > 0.56) canopy = 3 + (int)((wood - 0.56) * 28);
            else if (wood > 0.46) canopy = 1 + (int)((wood - 0.46) * 18);
            // A lone tree here and there in the open.
            else canopy = Lattice(4, (int)gx, (int)gy) < 0.035 ? 1 : 0;
            if (kind == PlantKind.Scrub) canopy = Math.Min(canopy, 4);
            if (canopy <= 0) continue;
            // Broadleaf woods carry some conifers through them, and conifer woods some broadleaf low down.
            if (kind == PlantKind.Broadleaf && Noise(5, gx, gy, 9) > 0.78) kind = PlantKind.Conifer;
            Plant[i] = (byte)kind;
            Canopy[i] = (byte)Math.Min(FullCanopy, canopy);
        }
        PlantsChanged();
    }

    /// <summary>Plants an older save's ground as it stands: the woods it would have had, cleared wherever it's built.</summary>
    void PlantOldSave()
    {
        GeneratePlants(Seed, 0, 0);
        for (int i = 0; i < N; i++)
            if (ZoneCode[i] != 0 || Made[i] != 0) { Plant[i] = 0; Canopy[i] = 0; }
        PlantsChanged();
    }

    void ApplyPlantTrees(PlantTreesAction a)
    {
        var r = a.Area;
        int x0 = Math.Max(0, r.MinX), x1 = Math.Min(Width - 1, r.MaxX), y0 = Math.Max(0, r.MinY), y1 = Math.Min(HeightCells - 1, r.MaxY);
        var cells = new List<(int I, PlantKind K)>();
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            if (!CanGrow(i) || Canopy[i] >= PlantedCanopy) continue;
            var k = a.Kind != PlantKind.None ? a.Kind : SuitedKind(i);
            // Where nothing woody grows by itself, a planted tree is a hardy broadleaf.
            if (k == PlantKind.None) k = PlantKind.Broadleaf;
            cells.Add((i, k));
        }
        if (cells.Count == 0) { Reject("There's nowhere there to plant: it's built on, under water, or already wooded.", r.X0, r.Y0); return; }
        if (!Spend((long)cells.Count * TreeCostPerCell, r.X0, r.Y0)) return;
        foreach (var (i, k) in cells)
        {
            Plant[i] = (byte)k;
            Canopy[i] = PlantedCanopy;
        }
        PlantsChanged();
    }

    /// <summary>The trees in a bulldozed area: what clearing them costs.</summary>
    long TreesIn(int x0, int y0, int x1, int y1)
    {
        long cost = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            if (Canopy[Idx(x, y)] > 0) cost += ClearTreesCostPerCell;
        return cost;
    }

    void ClearTrees(int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            Plant[i] = 0; Canopy[i] = 0;
        }
        PlantsChanged();
    }
}
