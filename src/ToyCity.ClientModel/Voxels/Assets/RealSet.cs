using static ToyCity.ClientModel.Voxels.Assets.AssetPalette;
using G = ToyCity.ClientModel.Voxels.Assets.Draw.Glazing;

namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// The realistic asset set (RC part 2): a model for every building type, drawn in code from the library's pieces,
/// the drawing helpers and the auto-roof, in place of the placeholder blocks. Each zoned type has a few variants, so
/// a street of one type isn't one house copied; a building's variant comes from its id. A model is made for a type
/// on a lot w × d cells (16 m each), front to the south, at the finest detail that fits the lot and its height in a
/// .vox model's 256 voxels a side. A .vox file for the type still wins over these.
/// </summary>
public static partial class RealSet
{
    public const double Cell = 16;
    public const int Variants = 3;

    /// <summary>A generated model: its voxels, detail (voxels a half metre) and auto-roof, if any.</summary>
    /// <summary>
    /// A model: its voxels (all of it, shapes too: what a wrecked building shows), its detail, its auto-roof, and its
    /// smooth shapes, which the city draws as polygons over <see cref="Boxy"/>, the voxels without them.
    /// </summary>
    public sealed record Result(VoxModel Model, int Detail, RoofSpec? Roof, IReadOnlyList<Shape> Shapes)
    {
        VoxModel? _boxy;
        public VoxModel Boxy => _boxy ??= Shapes.Count == 0 ? Model : Draw.Without(Model, Detail, Shapes);
    }

    /// <summary>The roadside transit stops, drawn by the game itself beside the road, so not in the set.</summary>
    public static readonly HashSet<string> Roadside = new() { "bus-stop", "bus-shelter", "bus-bay", "kiosk-shelter", "tram-stop" };

    /// <summary>The variant a building is drawn with: picked from its id, the same on every client.</summary>
    public static int VariantOf(int buildingId) => (int)((uint)(buildingId * 2654435761u) >> 16) % Variants;

    public static bool Has(string typeId) => Recipes.ContainsKey(typeId);

    public static IEnumerable<string> Types => Recipes.Keys;

    /// <summary>Makes the model for a type on a lot w × d cells, or null when the set has none for it.</summary>
    public static Result? Build(string typeId, int w, int d, int variant)
    {
        if (!Recipes.TryGetValue(typeId, out var recipe) || w < 1 || d < 1) return null;
        var lot = new Lot(typeId, w, d, ((variant % Variants) + Variants) % Variants);
        recipe(lot);
        return lot.Dr == null ? null : new Result(lot.Dr.M, lot.Dr.K, lot.Roof, lot.Dr.Shapes);
    }

    /// <summary>The finest detail that fits a lot w × d cells and a height in metres in 256 voxels, at most <paramref name="max"/>.</summary>
    public static int DetailFor(int w, int d, double height, int max = 8)
    {
        foreach (int k in new[] { 8, 4, 2, 1 })
            if (k <= max && Math.Max(w, d) * VoxModel.PerCell * k <= 256 && Math.Ceiling(height * 2 * k) + 1 <= 256) return k;
        return 1;
    }

    /// <summary>The highest a model can be, in metres: 256 voxels at the coarsest detail, less a voxel.</summary>
    public const double MaxHeight = 127;

    sealed class Lot
    {
        public readonly string Id;
        public readonly int Cw, Cd, Variant;
        public readonly Random R;
        public Draw? Dr;
        public RoofSpec? Roof;
        public Lot(string id, int cw, int cd, int variant)
        {
            Id = id; Cw = cw; Cd = cd; Variant = variant;
            uint h = 2166136261;
            foreach (char c in id) h = (h ^ c) * 16777619;
            R = new Random((int)(h ^ (uint)(cw * 7919 + cd * 104729 + variant * 15485863)));
        }
        public double W => Cw * Cell;
        public double D => Cd * Cell;
        /// <summary>The type's wealth letter for a zoned type: l, m or h.</summary>
        public char Wealth => Id.Length == 5 ? Id[4] : 'm';
        /// <summary>Starts the model for a height in metres.</summary>
        public Draw Begin(double height, int maxDetail = 0)
        {
            // 12.5 cm voxels on a one-cell lot and 25 cm on bigger ones, unless asked (houses ask for 6.25 cm):
            // finer would cost more to make and draw than a city camera can show.
            if (maxDetail == 0) maxDetail = Math.Max(Cw, Cd) == 1 ? 4 : 2;
            height = Math.Min(MaxHeight, height);
            int k = DetailFor(Cw, Cd, height, maxDetail);
            var m = new VoxModel(Cw * VoxModel.PerCell * k, Cd * VoxModel.PerCell * k, Math.Min(256, (int)Math.Ceiling(height * 2 * k) + 1));
            AssetPalette.Apply(m);
            m.Smooth[Leaf] = m.Smooth[LeafDark] = true;
            Dr = new Draw(m, k, R.Next());
            return Dr;
        }
        public T Pick<T>(params T[] xs) => xs[R.Next(xs.Length)];
        public double Between(double a, double b) => a + R.NextDouble() * (b - a);
    }

    static readonly Dictionary<string, Action<Lot>> Recipes = new()
    {
        ["r-l-l"] = House, ["r-l-m"] = House, ["r-l-h"] = House,
        ["r-m-l"] = WalkUp, ["r-m-m"] = Brownstones, ["r-m-h"] = GardenCourts,
        ["r-h-l"] = Beehive, ["r-h-m"] = SkylineFlats, ["r-h-h"] = Spire,
        ["c-l-l"] = PawnShop, ["c-l-m"] = Bodega, ["c-l-h"] = CheeseCellar,
        ["c-m-l"] = StripMall, ["c-m-m"] = Shoebox, ["c-m-h"] = Atrium,
        ["c-h-l"] = DiscountTower, ["c-h-m"] = Monolith, ["c-h-h"] = Exchange,
        ["i-l-l"] = Tannery, ["i-l-m"] = WidgetWorks, ["i-l-h"] = CleanRoom,
        ["i-m-l"] = Foundry, ["i-m-m"] = AssemblyHall, ["i-m-h"] = ChipFab,
        ["i-h-l"] = Smelter, ["i-h-m"] = RobotPlant, ["i-h-h"] = QuantumCampus,
        ["coal-plant"] = CoalPlant, ["gas-plant"] = GasPlant, ["hydro-plant"] = HydroPlant, ["wind-turbine"] = WindTurbine,
        ["solar-farm"] = SolarFarm, ["water-pump"] = WaterPump, ["sewage-plant"] = SewagePlant, ["water-treatment"] = WaterTreatment,
        ["landfill"] = Landfill, ["police-station"] = Police, ["fire-station"] = FireStation, ["clinic"] = Clinic,
        ["hospital"] = Hospital, ["elementary-school"] = ElementarySchool, ["high-school"] = HighSchool,
        ["small-park"] = SmallPark, ["plaza"] = Plaza, ["large-park"] = LargePark, ["pocket-park"] = PocketPark,
        ["bus-depot"] = BusDepot, ["rail-station"] = RailStation, ["tram-depot"] = TramDepot, ["rail-depot"] = RailYard,
        ["subway-station"] = SubwayStation, ["subway-depot"] = SubwayYard, ["freight-depot"] = FreightDepot,
        ["toy-soldier-statue"] = ToySoldier, ["rubber-duck-fountain"] = DuckFountain, ["clocktower"] = Clocktower,
        ["observation-wheel"] = Wheel, ["mayors-house"] = MayorsHouse, ["city-hall"] = CityHall, ["mayor-statue"] = MayorStatue,
        ["casino"] = Casino, ["toxic-dump"] = ToxicDump, ["pickle-plant"] = PicklePlant,
    };

    // ---- shared bits ------------------------------------------------------------------------------------------------

    /// <summary>Pale house paints: cream, white, sage, sky, butter, rose, putty.</summary>
    static byte HousePaint(Lot l) => l.Pick(Cream, Trim, Draw.Ramp(5, 11), Draw.Ramp(9, 12), Draw.Ramp(3, 12), Draw.Ramp(0, 12), Draw.Ramp(2, 10));

    static byte BrickOf(Lot l) => l.Pick(Brick, Brick, Draw.Ramp(0, 4), Draw.Ramp(1, 5), Draw.Ramp(1, 3));

    /// <summary>A path or drive on the ground: a voxel-thin strip over the grass.</summary>
    static void Path(Draw dr, double x0, double y0, double x1, double y1, byte c = Concrete) => dr.Box(x0, y0, 0, x1, y1, dr.V, c);

    /// <summary>
    /// A house shell of library pieces: floors, and a wall on every outer edge of the frames (4 m squares, by module
    /// from <paramref name="ox"/>, <paramref name="oy"/> metres), windows in a pattern, the front door on the south
    /// edge of <paramref name="door"/>. <paramref name="paint"/> recolours the walls' main colour.
    /// </summary>
    static void Shell(Draw dr, double ox, double oy, ICollection<(int X, int Y)> frames, int storeys, Material mat, (int, int) door, byte? paint, int seed)
    {
        var (main, _) = Of(mat);
        Func<byte, byte>? map = paint is { } p ? c => c == main ? p : c : null;
        for (int s = 0; s < storeys; s++)
            foreach (var (gx, gy) in frames)
            {
                dr.Piece("floor", Material.Wood, ox + gx * 4, oy + gy * 4, s * 3, 0);
                foreach (var (dx, dy, rot) in new[] { (0, -1, 0), (1, 0, 1), (0, 1, 2), (-1, 0, 3) })
                {
                    if (frames.Contains((gx + dx, gy + dy))) continue;
                    int h = (gx * 7 + gy * 13 + rot * 5 + s * 3 + seed) & 7;
                    string id = s == 0 && (gx, gy) == door && rot == 2 ? "wall-door"
                        : rot == 2 && s == 0 && h < 4 ? "wall-wide-window"
                        : h < 2 ? "wall" : "wall-window";
                    dr.Piece(id, mat, ox + gx * 4, oy + gy * 4, s * 3, rot, map);
                }
            }
    }

    /// <summary>Walls of a shed or hall, hollow, with a band of high windows and roller doors on the south face.</summary>
    static void Shed(Draw dr, double x0, double y0, double x1, double y1, double h, byte wall, int doors = 2, byte door = MetalDark, bool clerestory = true)
    {
        double t = Math.Max(dr.V, 0.3);
        dr.Box(x0, y0, 0, x1, y1, h, wall);
        dr.Box(x0 + t, y0 + t, 0, x1 - t, y1 - t, h, 0);
        dr.Box(x0 + t, y0 + t, 0, x1 - t, y1 - t, dr.V, ConcreteDark);
        // Ribs up the long walls every 6 m.
        for (double x = x0 + 6; x < x1 - 1; x += 6)
        {
            dr.Box(x, y0 - 0.2, 0, x + 0.3, y0, h, Darker(wall));
            dr.Box(x, y1, 0, x + 0.3, y1 + 0.2, h, Darker(wall));
        }
        if (clerestory && h > 5)
        {
            dr.Box(x0 + 1, y0, h - 1.6, x1 - 1, y0 + t, h - 0.6, Glass);
            dr.Box(x0 + 1, y1 - t, h - 1.6, x1 - 1, y1, h - 0.6, Glass);
        }
        double span = (x1 - x0) / Math.Max(1, doors);
        for (int i = 0; i < doors; i++)
        {
            double c = x0 + span * (i + 0.5), dw = Math.Min(4.5, span * 0.6), dh = Math.Min(h - 1, 4.5);
            dr.Box(c - dw / 2, y1 - t, 0, c + dw / 2, y1, dh, door);
            for (double z = 0.4; z < dh; z += 0.5) dr.Box(c - dw / 2, y1 - dr.V, z, c + dw / 2, y1, z + dr.V, Darker(door));
            dr.Box(c - dw / 2 - 0.3, y1, dh, c + dw / 2 + 0.3, y1 + 0.3, dh + 0.3, Yellow);
        }
    }

    static byte Darker(byte c) => c switch
    {
        Metal => MetalDark, Concrete => ConcreteDark, Stone => StoneDark, Wood => WoodDark, Brick => Draw.Ramp(0, 3),
        Trim => Concrete, Cream => Sand, MetalDark => Black,
        >= 32 when (c - 32) % 16 > 1 => (byte)(c - 2),
        _ => c,
    };

    /// <summary>Sawtooth north lights over a shed: each tooth slopes up to the north and faces it with glass.</summary>
    static void Sawtooth(Draw dr, double x0, double y0, double x1, double y1, double z, double tooth = 6, double rise = 2.5, byte c = Metal)
    {
        for (double y = y1; y > y0 + 0.01; y -= tooth)
        {
            double ya = Math.Max(y0, y - tooth);
            int a = dr.I(ya), b = dr.I(y);
            for (int yy = a; yy < b; yy++)
            {
                double f = (b - 1 - yy) / (double)Math.Max(1, b - a - 1);
                double top = z + rise * f;
                dr.Box(x0, yy * dr.V, z, x1, (yy + 1) * dr.V, top + dr.V, yy == a ? Glass : c);
            }
            dr.Box(x0, ya, z, x1, ya + dr.V, z + rise, Glass);
        }
    }

    /// <summary>A tower: a podium, then storeys in setback tiers, a flat roof with plant and a crown or spire. Returns the roof.</summary>
    static double Tower(Draw dr, double x0, double y0, double x1, double y1, int podium, int storeys, double storeyH, byte wall, byte band, G glazing,
        double bay, int tiers = 1, double setback = 3, G ground = G.Shop, byte glass = Glass, byte frame = Frame)
    {
        double z = 0;
        if (podium > 0)
            z = dr.Block(x0, y0, x1, y1, 0, podium, 4.2, band, wall, G.Shop, 4, true, G.Shop, glass, frame);
        double tx0 = x0, ty0 = y0, tx1 = x1, ty1 = y1;
        if (podium > 0) { tx0 += setback; ty0 += setback; tx1 -= setback; ty1 -= setback; dr.FlatRoof(x0, y0, x1, y1, z, band, plant: false, rim: 1.1); }
        int per = (int)Math.Ceiling(storeys / (double)tiers);
        for (int t = 0, left = storeys; t < tiers && left > 0; t++)
        {
            int n = Math.Min(per, left);
            z = dr.Block(tx0, ty0, tx1, ty1, z, n, storeyH, wall, band, glazing, bay, podium == 0 && t == 0, podium == 0 && t == 0 ? ground : glazing, glass, frame);
            left -= n;
            if (left > 0)
            {
                dr.FlatRoof(tx0, ty0, tx1, ty1, z, band, plant: false, rim: 1.1);
                double s = Math.Min(setback, Math.Min(tx1 - tx0, ty1 - ty0) / 6);
                tx0 += s; ty0 += s; tx1 -= s; ty1 -= s;
            }
        }
        dr.FlatRoof(tx0, ty0, tx1, ty1, z, band, plant: true, rim: 1.2);
        return z;
    }

    /// <summary>Balconies on the south face: a slab and a rail at every storey, every other bay.</summary>
    static void Balconies(Draw dr, double x0, double x1, double y, double z0, int storeys, double storeyH, double bay, byte slab, byte rail, int offset = 0)
    {
        int bays = Math.Max(1, (int)Math.Round((x1 - x0) / bay));
        double step = (x1 - x0) / bays;
        for (int s = 1; s < storeys; s++)
            for (int i = (s + offset) & 1; i < bays; i += 2)
            {
                double a = x0 + i * step + 0.3, b = x0 + (i + 1) * step - 0.3, z = z0 + s * storeyH;
                dr.Box(a, y, z - 0.2, b, y + 1.3, z, slab);
                dr.Box(a, y + 1.2, z, b, y + 1.3, z + 1.0, rail);
                dr.Box(a, y, z, a + 0.1, y + 1.3, z + 1.0, rail);
                dr.Box(b - 0.1, y, z, b, y + 1.3, z + 1.0, rail);
            }
    }

    static void Trees(Lot l, Draw dr, int n, double x0, double y0, double x1, double y1, double conifers = 0.3, double scale = 1)
    {
        for (int i = 0; i < n; i++)
            if (x1 - x0 > 1 && y1 - y0 > 1) dr.Tree(l.Between(x0, x1), l.Between(y0, y1), l.R.NextDouble() < conifers, scale * l.Between(0.8, 1.15));
    }

    /// <summary>A shop awning over a stretch of the south face: stripes of a colour and white.</summary>
    static void Awning(Draw dr, double x0, double x1, double y, double z, byte c, bool stripes = true)
    {
        int i = 0;
        for (double x = x0; x < x1 - 0.01; x += 0.8, i++)
            dr.Box(x, y, z, Math.Min(x1, x + 0.8), y + 1.6, z + 0.25, stripes && (i & 1) == 1 ? Trim : c);
        dr.Box(x0, y + 1.5, z - 0.4, x1, y + 1.6, z, c);
    }

    /// <summary>A sign board across a face at a height.</summary>
    static void Sign(Draw dr, double x0, double x1, double y, double z, double h, byte c, byte letters = Trim)
    {
        dr.Box(x0, y, z, x1, y + 0.3, z + h, c);
        for (double x = x0 + 0.6; x < x1 - 0.8; x += 1.1)
            dr.Box(x, y + 0.3, z + h * 0.25, x + 0.6, y + 0.4, z + h * 0.75, letters);
    }

    // ---- residential ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A detached house of library pieces under an auto-roof. Low wealth: a one-storey painted timber bungalow with a
    /// chain-link fence and an old car. Middle: a cottage or two-storey house with a picket fence, porch and garden.
    /// High: a two-storey brick or stone villa, often an L, with a garage, hedges and on big lots a pool.
    /// </summary>
    static void House(Lot l)
    {
        char w = l.Wealth;
        // RC part 5: one in three of the better-off houses is built of textured polygon walls.
        if (w != 'l' && l.Variant == 1) { WalledHouse(l); return; }
        int mx = (int)(l.W / 4), my = (int)(l.D / 4);
        int storeys = w == 'l' ? 1 : w == 'm' ? 1 + l.R.Next(2) : 2;
        var dr = l.Begin(Math.Max(storeys * 3 + 1, 11), 8);
        dr.Ground(Grass);
        int extra = mx > 4 ? 1 + l.R.Next(2) : 0;
        int hw = Math.Min(mx - 2, (w == 'l' ? 2 + l.R.Next(2) : w == 'm' ? 3 : 3) + extra);
        if (mx <= 4) hw = Math.Min(mx - 1, hw);
        int hd = Math.Min(my - 2, w == 'l' ? 2 : w == 'm' ? 2 + (my > 4 ? 1 : 0) : 2 + (my > 4 ? 1 : 0));
        bool garage = w != 'l' && hw + 1 < mx;
        int gx0 = (mx - hw - (garage ? 1 : 0)) / 2, gy1 = my - 2, gy0 = gy1 - hd + 1;
        var frames = new HashSet<(int, int)>();
        for (int x = 0; x < hw; x++) for (int y = 0; y < hd; y++) frames.Add((gx0 + x, gy0 + y));
        // A wing forward makes an L on a well-off house.
        bool wing = w == 'h' && l.Variant != 1 && hw >= 3;
        if (wing) frames.Add((gx0 + hw - 1, gy1 + 1));
        var mat = w == 'l' ? Material.Wood : w == 'm' ? l.Pick(Material.Wood, Material.Wood, Material.Brick) : l.Pick(Material.Brick, Material.Stone, Material.Stone);
        byte? paint = mat == Material.Wood ? HousePaint(l) : mat == Material.Brick ? BrickOf(l) : null;
        int doorX = gx0 + (wing ? 0 : hw / 2);
        Shell(dr, 0, 0, frames, storeys, mat, (doorX, gy1), paint, l.Variant * 3 + l.R.Next(4));
        if (storeys > 1) dr.Piece("stairs", Material.Wood, gx0 * 4, gy0 * 4, 0, 0);

        // The garage: a frame to the east, a door to the street, roofed with the house when it's one storey.
        double frontY = (gy1 + 1) * 4;
        if (garage)
        {
            int gg = gx0 + hw;
            dr.Piece("floor", Material.Concrete, gg * 4, gy1 * 4, 0, 0);
            foreach (int rot in new[] { 0, 1, 2 }) dr.Piece("wall", mat, gg * 4, gy1 * 4, 0, rot, paint is { } p ? c => c == Of(mat).Main ? p : c : null);
            dr.Box(gg * 4 + 0.5, frontY - 0.6, 0, gg * 4 + 3.5, frontY, 2.4, 0);
            dr.Box(gg * 4 + 0.5, frontY - 0.35, 0, gg * 4 + 3.5, frontY - 0.35 + dr.V, 2.4, Trim);
            for (double z = 0.5; z < 2.4; z += 0.5) dr.Box(gg * 4 + 0.5, frontY - 0.35, z, gg * 4 + 3.5, frontY - 0.3, z + dr.V, Concrete);
            if (storeys > 1) dr.Box(gg * 4, gy1 * 4, 2.8, gg * 4 + 4, frontY + 0.2, 3.0, ConcreteDark);
            Path(dr, gg * 4 + 0.3, frontY, gg * 4 + 3.7, l.D, w == 'h' ? Draw.Ramp(1, 8) : Concrete);
            dr.Prop("car", gg * 4 + 2, frontY + 3.5, 1, dr.V, dr.CarPaint());
        }
        else
        {
            // A gravel drive beside the house, and an old car on it.
            double dx = Math.Min(l.W - 3.5, (gx0 + hw) * 4 + 0.6);
            Path(dr, dx, gy0 * 4 + 2, dx + 3, l.D, w == 'l' ? Sand : Concrete);
            dr.Prop("car", dx + 1.5, frontY, 1, dr.V, c => c == Red ? l.Pick(Draw.Ramp(1, 4), Draw.Ramp(9, 4), MetalDark, Draw.Ramp(2, 6)) : c);
        }

        // A path to the door and a porch over it.
        double doorC = doorX * 4 + 2;
        Path(dr, doorC - 0.7, frontY, doorC + 0.7, l.D);
        if (w != 'h' && frontY + 2.5 < l.D)
        {
            dr.Box(doorC - 2, frontY, 0, doorC + 2, frontY + 2.2, 0.35, WoodDark);
            foreach (double px in new[] { doorC - 1.8, doorC + 1.6 }) dr.Box(px, frontY + 1.9, 0, px + 0.2, frontY + 2.1, 2.6, Trim);
            dr.Box(doorC - 2.2, frontY - 0.2, 2.6, doorC + 2.2, frontY + 2.4, 2.8, w == 'l' ? MetalDark : Of(mat).Trim);
        }

        // Roof: gable on most, hip on the villas.
        var style = w == 'h' ? l.Pick(RoofStyle.Hip, RoofStyle.Hip, RoofStyle.Gable) : l.Variant == 2 && w == 'm' ? RoofStyle.Hip : RoofStyle.Gable;
        byte cover = w == 'l' ? l.Pick(Asphalt, MetalDark, Asphalt) : w == 'm' ? l.Pick(Asphalt, RoofTile, Slate, WoodDark) : l.Pick(Slate, RoofTile, Slate);
        l.Roof = new RoofSpec(style, w == 'l' ? l.Between(22, 30) : l.Between(30, 42), w == 'l' ? 30 : 45, cover, l.R.Next(3) == 0);

        // The fence along the street, gardens and trees.
        for (int x = 0; x < mx; x++)
        {
            double fx = x * 4;
            bool gap = (fx <= doorC && doorC < fx + 4) || (garage && x == gx0 + hw);
            if (gap) continue;
            if (w == 'h') dr.Prop("hedge", fx + 2, l.D - 0.6, 0);
            else dr.Piece("fence", w == 'l' ? Material.Metal : Material.Wood, fx, (my - 1) * 4, 0, 2, w == 'm' ? c => c is Wood or WoodDark ? Trim : c : null);
        }
        Trees(l, dr, w == 'l' ? 1 : 2 + l.R.Next(2), 1.5, 1.5, l.W - 1.5, Math.Max(2, gy0 * 4 - 1), 0.35);
        if (w != 'l') dr.Prop("planter", doorC + 2.5, frontY + 1.2);
        if (w == 'm') dr.Tree(l.Between(1.5, Math.Max(2, gx0 * 4 - 1)), l.D - 3, false, 0.75);
        if (w == 'h' && l.D >= 32 || w == 'h' && l.Variant == 2)
        {
            // A pool in the back garden.
            double py = Math.Max(1, gy0 * 4 - 6), px = l.W / 2 - 4;
            if (py > 0.5)
            {
                dr.Box(px - 0.6, py - 0.6, 0, px + 8.6, py + 4.6, dr.V * 2, Trim);
                dr.Box(px, py, 0, px + 8, py + 4, dr.V * 2, Water);
            }
        }
        if (w == 'l') { dr.Box(0.6, 0.6, 0, 3.2, 2.6, 2.2, Metal); dr.Box(0.6, 0.6, 2.2, 3.2, 2.6, 2.4, MetalDark); }
    }

    /// <summary>
    /// A house of polygon walls and parts (RC part 5): brick, render, stone or clapboard by the street's wealth, with
    /// sash windows, a panelled door, a slate, tile or shingle roof by the auto-roof, a garden, a drive and trees.
    /// Its voxels, in its finishes' colours, are for when it's wrecked.
    /// </summary>
    static void WalledHouse(Lot l)
    {
        char w = l.Wealth;
        var dr = l.Begin(12, 8);
        dr.Ground(Grass);
        double hw = Math.Min(l.W - 6, w == 'h' ? l.Between(11, 13) : l.Between(9, 11)), hd = Math.Min(l.D - 7, w == 'h' ? 9 : 8);
        double x0 = Math.Round((l.W - hw) / 2 - (w == 'm' ? 1.5 : 0), 1), y1 = l.D - 5, y0 = y1 - hd;
        string walls = w == 'h' ? l.Pick("brick-buff", "stone-ashlar", "brick-red", "render-cream") : l.Pick("brick-red", "siding-white", "siding-blue", "siding-sage", "render-cream", "brick-painted");
        bool timber = walls.StartsWith("siding");
        var spec = new PolyHouseSpec(x0, y0, x0 + hw, y1, w == 'h' ? 2 : 1 + l.R.Next(2), walls)
        {
            Trim = walls == "brick-buff" ? "trim-black" : "trim-white",
            Dressing = timber ? "" : walls == "stone-ashlar" ? "concrete" : "stone-ashlar",
            Roof = timber ? l.Pick("shingles-grey", "shingles-brown") : l.Pick("slate", "tile-clay", "slate"),
            Style = w == 'h' && l.R.Next(2) == 0 ? RoofStyle.Hip : RoofStyle.Gable,
            Pitch = l.Between(32, 42), OverhangCm = timber ? 40 : 30,
            Chimney = !timber || l.R.Next(2) == 0,
        };
        spec = spec with { RoofColour = spec.Roof == "tile-clay" ? RoofTile : spec.Roof.StartsWith("shingles") ? Asphalt : Slate };
        l.Roof = PolyBuild.House(sh => dr.Add(sh), spec, dr.V);
        // The path to the front door, a drive to the east with a car, a hedge or fence along the street, trees behind.
        double doorC = (x0 + x0 + hw) / 2;
        dr.Add(Parts.Box(doorC - 0.7, y1 + 0.35, 0, 1.4, l.D - y1 - 0.35, 2 * dr.V, w == 'h' ? "stone-rubble" : "concrete"));
        double dx = Math.Min(l.W - 3.4, x0 + hw + 0.8);
        Path(dr, dx, y0 + 2, dx + 3, l.D, w == 'h' ? Draw.Ramp(1, 8) : Concrete);
        dr.Prop("car", dx + 1.5, y1 + 1.5, 1, dr.V, dr.CarPaint());
        for (double fx = 0; fx + 4 <= l.W; fx += 4)
        {
            if ((fx <= doorC && doorC < fx + 4) || (fx + 4 > dx && fx < dx + 3)) continue;
            if (w == 'h') dr.Prop("hedge", fx + 2, l.D - 0.6, 0);
            else dr.Piece("fence", Material.Wood, fx, l.D - 4, 0, 2, c => c is Wood or WoodDark ? Trim : c);
        }
        Trees(l, dr, 2 + l.R.Next(2), 1.5, 1.5, l.W - 1.5, Math.Max(2, y0 - 1), 0.35);
        dr.Prop("planter", doorC + 1.6, y1 + 1.0);
    }

    /// <summary>Clothesline walk-ups: three or four storeys of plain brick flats, fire escapes, washing on the roof.</summary>
    static void WalkUp(Lot l)
    {
        int storeys = 3 + l.R.Next(2);
        double sh = 3.1, h = storeys * sh;
        var dr = l.Begin(h + 4);
        dr.Ground(Grass);
        int n = l.W >= 32 ? 2 : 1;
        double gap = 4, bw = (l.W - 4 - gap * (n - 1)) / n, depth = Math.Min(l.D - 9, 16 + l.R.Next(3) * 2);
        byte wall = l.Pick(BrickOf(l), Cream, Draw.Ramp(2, 8));
        for (int i = 0; i < n; i++)
        {
            double x0 = 2 + i * (bw + gap), x1 = x0 + bw, y1 = l.D - 4, y0 = y1 - depth;
            double top = dr.Block(x0, y0, x1, y1, 0, storeys, sh, wall, Concrete, G.Punched, 2.6);
            dr.FlatRoof(x0, y0, x1, y1, top, Concrete, plant: false);
            // A fire escape down the east end: landings and ladders.
            for (int s = 1; s < storeys; s++)
            {
                double z = s * sh;
                dr.Box(x1, y0 + 2, z - 0.15, x1 + 1.2, y0 + 6, z, MetalDark);
                dr.Box(x1 + 1.1, y0 + 2, z, x1 + 1.2, y0 + 6, z + 1, MetalDark);
                dr.Box(x1 + 0.2, y0 + 3.5 + (s & 1) * 1.5, z - sh + 0.1, x1 + 0.6, y0 + 4 + (s & 1) * 1.5, z, Metal);
            }
            // Washing lines on the roof.
            for (double y = y0 + 2; y < y1 - 2; y += 2.5)
            {
                dr.Box(x0 + 1.5, y, top + 1.6, x1 - 1.5, y + dr.V, top + 1.6 + dr.V, Trim);
                for (double x = x0 + 2; x < x1 - 2.5; x += l.Between(1.2, 2.5))
                    dr.Box(x, y, top + 0.9, x + 0.6, y + dr.V, top + 1.6, l.Pick(Red, Blue, Yellow, Trim, Draw.Ramp(5, 9)));
            }
            Path(dr, (x0 + x1) / 2 - 1, y1, (x0 + x1) / 2 + 1, l.D);
            dr.Box((x0 + x1) / 2 - 1.2, y1, 0, (x0 + x1) / 2 + 1.2, y1 + 1, 0.4, Concrete);
        }
        // Parking and bins behind.
        if (l.D - depth - 4 > 7) dr.CarPark(2, 1, l.W - 2, l.D - depth - 5, 0.5);
        dr.Prop("bin", l.W - 2, l.D - 2.5);
        Trees(l, dr, 1 + l.R.Next(2), 1, l.D - 3.5, l.W - 1, l.D - 1, 0);
    }

    /// <summary>A brownstone row: narrow houses wall to wall, each with a stoop, a cornice and its own shade of brick.</summary>
    static void Brownstones(Lot l)
    {
        int storeys = 3 + l.R.Next(2);
        double sh = 3.3, h = storeys * sh + 1;
        var dr = l.Begin(h + 4);
        dr.Ground(Grass);
        double unit = l.Pick(5.4, 6.0, 6.6);
        int n = Math.Max(2, (int)((l.W - 1) / unit));
        double x0 = (l.W - n * unit) / 2, depth = Math.Min(l.D - 8, 14), y1 = l.D - 4, y0 = y1 - depth;
        byte[] browns = { Draw.Ramp(1, 4), Draw.Ramp(1, 3), Draw.Ramp(0, 4), Draw.Ramp(2, 4), Brick, Draw.Ramp(1, 5) };
        for (int i = 0; i < n; i++)
        {
            double a = x0 + i * unit, b = a + unit;
            int st = storeys + (l.R.Next(4) == 0 ? 1 : 0);
            byte wall = browns[l.R.Next(browns.Length)];
            double top = dr.Block(a, y0, b, y1, 1.2, st, sh, wall, Darker(wall), G.Punched, unit / 2.2, true, G.Punched, Glass, Trim);
            dr.Box(a, y0, 0, b, y1, 1.2, StoneDark);
            // A cornice.
            dr.Box(a, y0 - 0.3, top - 0.6, b, y1 + 0.5, top, Trim);
            dr.FlatRoof(a, y0, b, y1, top, Darker(wall), plant: false, rim: 0.5);
            // The stoop: steps up to the door, with railings.
            double c = (a + b) / 2;
            for (int s = 0; s < 4; s++) dr.Box(c - 1, y1 + 2.4 - s * 0.6, 0, c + 1, y1 + 2.4 - s * 0.6 + 0.6, 0.3 * (s + 1), StoneDark);
            dr.Box(c - 1.1, y1, 0.3, c - 1, y1 + 2.4, 1.5, Black);
            dr.Box(c + 1, y1, 0.3, c + 1.1, y1 + 2.4, 1.5, Black);
            if ((i & 1) == 0) dr.Prop("planter", a + 1, y1 + 2.4);
        }
        Path(dr, 0, l.D - 1.5, l.W, l.D, Concrete);
        for (double x = 3; x < l.W - 2; x += 8) dr.Tree(x, l.D - 0.8, false, 0.7);
        Trees(l, dr, n / 2 + 1, 1.5, 1.5, l.W - 1.5, Math.Max(2, y0 - 2), 0.2);
    }

    /// <summary>Garden courts: three wings of flats round a courtyard garden open to the street, under one hipped roof.</summary>
    static void GardenCourts(Lot l)
    {
        int storeys = 3;
        double sh = 3, h = storeys * sh;
        var dr = l.Begin(h + 3);
        dr.Ground(Grass);
        double x0 = 2, x1 = l.W - 2, y0 = 2, y1 = l.D - 3, wing = Math.Min(10, (x1 - x0) / 3);
        byte wall = l.Pick(Cream, BrickOf(l), Draw.Ramp(2, 10), Trim);
        dr.Block(x0, y0, x0 + wing, y1, 0, storeys, sh, wall, Concrete, G.Punched, 3);
        dr.Block(x1 - wing, y0, x1, y1, 0, storeys, sh, wall, Concrete, G.Punched, 3);
        dr.Block(x0, y0, x1, y0 + wing, 0, storeys, sh, wall, Concrete, G.Punched, 3);
        // The courtyard garden: paths, a lawn, trees and benches.
        double cx0 = x0 + wing, cx1 = x1 - wing, cy0 = y0 + wing;
        Path(dr, (cx0 + cx1) / 2 - 1, cy0, (cx0 + cx1) / 2 + 1, l.D, Sand);
        Path(dr, cx0, (cy0 + y1) / 2 - 0.8, cx1, (cy0 + y1) / 2 + 0.8, Sand);
        Trees(l, dr, 2 + (int)((cx1 - cx0) * (y1 - cy0) / 120), cx0 + 2, cy0 + 2, cx1 - 2, y1 - 1, 0.2, 0.9);
        dr.Prop("bench", (cx0 + cx1) / 2 + 2.5, (cy0 + y1) / 2 + 2);
        dr.Prop("bench", (cx0 + cx1) / 2 - 2.5, (cy0 + y1) / 2 - 2);
        l.Roof = new RoofSpec(RoofStyle.Hip, l.Between(26, 34), 50, l.Pick(RoofTile, Slate, RoofTile));
        for (double x = 1; x < l.W - 1; x += 3) if (Math.Abs(x + 1.5 - (cx0 + cx1) / 2) > 2) dr.Prop("hedge", x + 1.5, l.D - 1.2);
    }

    /// <summary>The concrete beehive: a brutalist slab of flats with balconies stacked like cells, and its car park.</summary>
    static void Beehive(Lot l)
    {
        int storeys = 11 + l.R.Next(8);
        double sh = 2.9, h = storeys * sh;
        var dr = l.Begin(h + 5);
        dr.Ground(Grass);
        double x0 = 3, x1 = l.W - 3, depth = Math.Min(l.D - 14, 15), y1 = l.D - 5, y0 = y1 - depth;
        byte wall = l.Pick(Concrete, Concrete, Draw.Ramp(2, 9));
        double top = dr.Block(x0, y0, x1, y1, 0, storeys, sh, wall, ConcreteDark, G.Punched, 3.2, true, G.Ribbon);
        Balconies(dr, x0, x1, y1, 0, storeys, sh, 3.2, ConcreteDark, Concrete, l.Variant);
        dr.FlatRoof(x0, y0, x1, y1, top, ConcreteDark);
        // Lift towers rising through the roof.
        dr.Box(x0 + (x1 - x0) * 0.3, y0 + 3, top, x0 + (x1 - x0) * 0.3 + 4, y0 + 7, top + 3, wall);
        if (y0 - 2 > 6) dr.CarPark(2, 1, l.W - 2, y0 - 1.5, 0.7);
        Path(dr, (x0 + x1) / 2 - 1.5, y1, (x0 + x1) / 2 + 1.5, l.D);
        Trees(l, dr, 2 + l.Cw, 1, y1 + 1.5, l.W - 1, l.D - 1, 0.2, 0.9);
    }

    /// <summary>Skyline flats: a slim tower of flats with corner balconies over a podium of shops.</summary>
    static void SkylineFlats(Lot l)
    {
        int storeys = 18 + l.R.Next(10);
        double sh = 3.0, h = 2 * 4.2 + storeys * sh;
        var dr = l.Begin(h + 6);
        dr.Ground(Concrete);
        double m = 2, side = Math.Min(l.W, l.D) - 2 * m;
        double x0 = (l.W - side) / 2, y0 = (l.D - side) / 2;
        byte wall = l.Pick(Trim, Cream, Draw.Ramp(9, 11));
        double inset = Math.Min(side / 4, 6);
        double top = Tower(dr, x0, y0, x0 + side, y0 + side, 2, storeys, sh, wall, Concrete, G.Ribbon, 3.5, 1, inset);
        Balconies(dr, x0 + inset, x0 + side - inset, y0 + side - inset, 8.4, storeys, sh, 3.5, Trim, Glass, 1);
        dr.Box(x0 + side / 2 - 3, y0 + side / 2 - 3, top, x0 + side / 2 + 3, y0 + side / 2 + 3, top + 4, wall);
        for (double x = 2; x < l.W - 1; x += 7) dr.Tree(x, l.D - 1, false, 0.6);
    }

    /// <summary>The penthouse spire: a glass tower in setback tiers with a lit crown and a mast.</summary>
    static void Spire(Lot l)
    {
        int storeys = 28 + l.R.Next(8);
        double sh = 3.2, h = Math.Min(MaxHeight - 12, storeys * sh);
        storeys = (int)(h / sh);
        var dr = l.Begin(MaxHeight);
        dr.Ground(Concrete);
        double side = Math.Min(l.W, l.D) - 6, x0 = (l.W - side) / 2, y0 = (l.D - side) / 2;
        byte glass = l.Pick(Glass, Draw.Ramp(8, 9), Draw.Ramp(8, 7));
        double top = Tower(dr, x0, y0, x0 + side, y0 + side, 0, storeys, sh, Frame, MetalDark, G.Curtain, 3, 3, 2.5, G.Shop, glass, Frame);
        double c = l.W / 2, cy = l.D / 2;
        dr.Box(c - 3, cy - 3, top, c + 3, cy + 3, top + 3, Lamp);
        dr.Cyl(c, cy, 0.6, top + 3, Math.Min(MaxHeight - 0.5, top + 12), Chrome, 0.15);
        Path(dr, 0, l.D - 3, l.W, l.D, Stone);
        for (double x = 2; x < l.W - 1; x += 6) dr.Prop("planter", x, l.D - 1.5);
    }

    // ---- commercial ------------------------------------------------------------------------------------------------

    /// <summary>Pawn &amp; pie: a row of one-storey shops with striped awnings and signs, parking beside.</summary>
    static void PawnShop(Lot l)
    {
        var dr = l.Begin(9);
        dr.Ground(Concrete);
        int n = Math.Max(1, (int)(l.W / 8));
        double depth = Math.Min(12, l.D - 6), y1 = l.D - 3, y0 = y1 - depth, unit = (l.W - 2) / n;
        for (int i = 0; i < n; i++)
        {
            double a = 1 + i * unit, b = a + unit;
            byte wall = l.Pick(BrickOf(l), Cream, Draw.Ramp(2, 9), Concrete);
            double top = dr.Block(a, y0, b, y1, 0, 1, 4.2, wall, Darker(wall), G.Shop, unit / 2, true, G.Shop);
            dr.FlatRoof(a, y0, b, y1, top, wall, plant: false, rim: 1.4);
            Sign(dr, a + 0.4, b - 0.4, y1, top - 0.2, 1.2, l.Pick(Red, Blue, Yellow, Draw.Ramp(5, 5), Black), l.Pick(Trim, Yellow));
            Awning(dr, a + 0.5, b - 0.5, y1, 2.9, l.Pick(Red, Draw.Ramp(5, 5), Blue, Draw.Ramp(1, 6)));
        }
        if (y0 > 7) dr.CarPark(1, 0.5, l.W - 1, y0 - 0.5, 0.5);
        dr.Prop("bin", l.W - 1.5, y1 + 1.2);
        dr.Prop("bench", 3, l.D - 1);
    }

    /// <summary>The corner bodega: two storeys of brick, a shop below with a green awning and a flat over it.</summary>
    static void Bodega(Lot l)
    {
        var dr = l.Begin(11);
        dr.Ground(Concrete);
        double x0 = 1, x1 = l.W - 1, y1 = l.D - 2.5, y0 = Math.Max(1, y1 - 14);
        byte wall = l.Pick(BrickOf(l), Draw.Ramp(2, 7), Cream);
        double top = dr.Block(x0, y0, x1, y1, 0, 2, 3.6, wall, Trim, G.Punched, 3, true, G.Shop);
        dr.Box(x0, y0 - 0.3, top - 0.5, x1, y1 + 0.4, top, Trim);
        dr.FlatRoof(x0, y0, x1, y1, top, wall, plant: true, rim: 0.8);
        Awning(dr, x0 + 0.3, x1 - 0.3, y1, 2.9, l.Pick(Draw.Ramp(5, 4), Red, Draw.Ramp(6, 4)));
        Sign(dr, x0 + 1, x1 - 1, y1 + 0.4, 3.25, 0.5, Black, Yellow);
        // Crates of fruit out front.
        for (double x = x0 + 1; x < Math.Min(x1 - 2, x0 + 7); x += 1.4)
            dr.Box(x, y1 + 0.3, 0, x + 1.1, y1 + 1.2, 0.8, l.Pick(Red, Yellow, Draw.Ramp(2, 8), Draw.Ramp(5, 6)));
        dr.Prop("lamp", x1 - 0.5, l.D - 0.8);
        if (y0 > 7) dr.CarPark(1, 0.5, l.W - 1, y0 - 0.5, 0.4);
    }

    /// <summary>The boutique cheese cellar: a gabled stone shop with a big window, café tables and planters.</summary>
    static void CheeseCellar(Lot l)
    {
        var dr = l.Begin(12);
        dr.Ground(Grass);
        double x0 = 2, x1 = Math.Min(l.W - 2, 2 + 12), y1 = l.D - 5, y0 = Math.Max(2, y1 - 10);
        byte wall = l.Pick(Stone, Cream, Stone);
        dr.Block(x0, y0, x1, y1, 0, 2, 3.2, wall, Darker(wall), G.Punched, 2.6, true, G.Shop, Glass, WoodDark);
        l.Roof = new RoofSpec(RoofStyle.Gable, l.Between(38, 48), 40, l.Pick(Slate, RoofTile), true);
        Path(dr, 0, y1, l.W, l.D, Draw.Ramp(1, 9));
        Awning(dr, x0 + 0.5, x1 - 0.5, y1, 2.7, Draw.Ramp(3, 6), stripes: false);
        for (double x = x0 + 1.5; x < x1 - 1; x += 3) dr.Prop("table", x, y1 + 3);
        dr.Prop("planter", x0 - 1, l.D - 1.5);
        dr.Prop("planter", x1 + 1, l.D - 1.5);
        if (l.W - x1 > 5) Trees(l, dr, 2, x1 + 2, 2, l.W - 2, y1 - 2, 0.2, 0.9);
        Trees(l, dr, 1, 2, 1, l.W - 2, Math.Max(1.5, y0 - 1), 0.2, 0.9);
    }

    /// <summary>The strip mall: a long row of shop units at the back of a car park, each with its own sign, and a pylon sign.</summary>
    static void StripMall(Lot l)
    {
        var dr = l.Begin(16);
        dr.Ground(Asphalt);
        double depth = Math.Min(16, l.D * 0.45), x0 = 1.5, x1 = l.W - 1.5, y0 = 1.5, y1 = y0 + depth;
        byte wall = l.Pick(Cream, Draw.Ramp(2, 10), Concrete);
        double top = dr.Block(x0, y0, x1, y1, 0, 1, 5, wall, Darker(wall), G.Shop, 5, false, G.Shop);
        dr.FlatRoof(x0, y0, x1, y1, top, wall, plant: true, rim: 1.6);
        int units = Math.Max(2, (int)((x1 - x0) / 9));
        double unit = (x1 - x0) / units;
        for (int i = 0; i < units; i++)
        {
            double a = x0 + i * unit;
            Sign(dr, a + 0.8, a + unit - 0.8, y1, top - 0.3, 1.4, l.Pick(Red, Blue, Yellow, Draw.Ramp(5, 5), Draw.Ramp(11, 5), Black));
            dr.Box(a + unit / 2 - 1, y1 - 0.3, 0, a + unit / 2 + 1, y1, 2.5, Glass);
            if (i > 0) dr.Box(a - 0.15, y1, 0, a + 0.15, y1 + 0.3, top, Darker(wall));
        }
        dr.Box(x0, y1, 3.9, x1, y1 + 2.5, 4.2, Darker(wall));
        dr.CarPark(1.5, y1 + 4, l.W - 1.5, l.D - 2, 0.6);
        // The pylon sign at the corner.
        double px = l.W - 3, py = l.D - 2;
        dr.Box(px - 0.3, py - 0.3, 0, px + 0.3, py + 0.3, 9, MetalDark);
        dr.Box(px - 2, py - 0.5, 9, px + 2, py + 0.5, 12.5, Red);
        dr.Box(px - 1.6, py + 0.5, 9.5, px + 1.6, py + 0.6, 12, Trim);
        for (double x = 3; x < l.W - 6; x += 12) dr.Prop("lamp", x, y1 + 9.5);
    }

    /// <summary>The office shoebox: five to eight storeys of concrete and ribbon windows over a car park.</summary>
    static void Shoebox(Lot l)
    {
        int storeys = 5 + l.R.Next(4);
        double sh = 3.6, h = storeys * sh;
        var dr = l.Begin(h + 4);
        dr.Ground(Grass);
        double x0 = 3, x1 = l.W - 3, depth = Math.Min(l.D - 12, 18), y1 = l.D - 4, y0 = y1 - depth;
        byte wall = l.Pick(Concrete, Cream, Draw.Ramp(2, 9), Stone);
        double top = dr.Block(x0, y0, x1, y1, 0, storeys, sh, wall, wall, G.Ribbon, 3, true, G.Curtain, l.Pick(Glass, Draw.Ramp(9, 7)), MetalDark);
        dr.FlatRoof(x0, y0, x1, y1, top, wall);
        // A canopy over the entrance.
        dr.Box(l.W / 2 - 3, y1, 3.2, l.W / 2 + 3, y1 + 2.5, 3.5, MetalDark);
        if (y0 > 7) dr.CarPark(2, 1, l.W - 2, y0 - 1, 0.7);
        Path(dr, l.W / 2 - 1.5, y1, l.W / 2 + 1.5, l.D);
        Trees(l, dr, 2 + l.Cw, 1, y1 + 1, l.W - 1, l.D - 0.5, 0.1, 0.8);
    }

    /// <summary>Glass atrium offices: a curtain-walled block and a tall glass atrium, a plaza of trees in front.</summary>
    static void Atrium(Lot l)
    {
        int storeys = 6 + l.R.Next(4);
        double sh = 3.8, h = storeys * sh;
        var dr = l.Begin(h + 4);
        dr.Ground(Stone);
        double x0 = 3, x1 = l.W - 3, depth = Math.Min(l.D - 12, 20), y1 = l.D - 7, y0 = y1 - depth;
        byte glass = l.Pick(Glass, Draw.Ramp(8, 9), Draw.Ramp(6, 9));
        double ax = x0 + (x1 - x0) * l.Between(0.25, 0.45);
        double top = dr.Block(x0, y0, x1, y1, 0, storeys, sh, Frame, MetalDark, G.Curtain, 2.5, false, G.Curtain, glass, Frame);
        dr.FlatRoof(x0, y0, x1, y1, top, MetalDark);
        // The atrium: a glass box in front of the block, its roof sloping back to the main block.
        double az = h * 0.6;
        dr.Box(ax, y1, 0, ax + 9, y1 + 4, az, glass);
        dr.Box(ax + 0.3, y1, 0, ax + 8.7, y1 + 3.7, az - 0.3, 0);
        for (double x = ax; x <= ax + 9; x += 1.5) dr.Box(x, y1 + 3.8, 0, x + 0.15, y1 + 4, az, Frame);
        for (double z = 3; z < az; z += 3) dr.Box(ax, y1 + 3.8, z, ax + 9, y1 + 4, z + 0.15, Frame);
        dr.Box(ax + 3.5, y1 + 3.7, 0, ax + 5.5, y1 + 4, 2.6, 0);
        if (y0 > 7) dr.CarPark(2, 1, l.W - 2, y0 - 1, 0.7);
        for (double x = 2.5; x < l.W - 2; x += 5) if (x < ax - 1 || x > ax + 10) dr.Tree(x, l.D - 3, false, 0.75);
        dr.Prop("bench", ax + 4.5, l.D - 1.5);
    }

    /// <summary>The discount tower: a stack of concrete grid floors over a big-box store with a bright sign.</summary>
    static void DiscountTower(Lot l)
    {
        int storeys = 14 + l.R.Next(7);
        double sh = 3.6, h = 2 * 4.2 + storeys * sh;
        var dr = l.Begin(h + 4);
        dr.Ground(Asphalt);
        double x0 = 2, x1 = l.W - 2, y0 = 2, y1 = l.D - 4;
        byte wall = l.Pick(Concrete, Cream, Draw.Ramp(2, 9));
        Tower(dr, x0, y0, x1, y1, 2, storeys, sh, wall, ConcreteDark, G.Punched, 2.4, 1, Math.Min(6, (x1 - x0) / 5));
        Sign(dr, x0 + 2, x1 - 2, y1, 5.4, 2.4, l.Pick(Red, Yellow, Draw.Ramp(5, 6)), l.Pick(Trim, Black));
        for (double x = 2; x < l.W - 1; x += 6) dr.Prop("lamp", x, l.D - 1);
    }

    /// <summary>The insurance monolith: a dark stone slab with narrow windows, a plinth and a granite plaza.</summary>
    static void Monolith(Lot l)
    {
        int storeys = 24 + l.R.Next(7);
        double sh = 3.8, h = Math.Min(MaxHeight - 4, 6 + storeys * sh);
        storeys = (int)((h - 6) / sh);
        var dr = l.Begin(h + 4);
        dr.Ground(StoneDark);
        double sx = (l.W - 8) * 0.8, sy = Math.Min(l.D - 10, sx * 0.6);
        double x0 = (l.W - sx) / 2, y0 = (l.D - sy) / 2 - 1;
        byte wall = l.Pick(StoneDark, Draw.Ramp(2, 4), Draw.Ramp(1, 3));
        dr.Block(x0 - 1, y0 - 1, x0 + sx + 1, y0 + sy + 1, 0, 1, 6, Stone, StoneDark, G.Curtain, 3, true, G.Curtain);
        double top = dr.Block(x0, y0, x0 + sx, y0 + sy, 6, storeys, sh, wall, wall, G.Punched, 2, false, G.Punched, Draw.Ramp(8, 8), wall);
        dr.FlatRoof(x0, y0, x0 + sx, y0 + sy, top, wall, plant: false, rim: 2.5);
        Path(dr, 0, y0 + sy + 1, l.W, l.D, Stone);
        for (double x = 3; x < l.W - 2; x += 7) dr.Prop("planter", x, l.D - 2);
    }

    /// <summary>The Starlight Exchange: a faceted glass tower in tiers with a glowing crown and a spire.</summary>
    static void Exchange(Lot l)
    {
        int storeys = 30;
        double sh = 3.4;
        var dr = l.Begin(MaxHeight);
        dr.Ground(Stone);
        double side = Math.Min(l.W, l.D) - 8, x0 = (l.W - side) / 2, y0 = (l.D - side) / 2;
        byte glass = l.Pick(Draw.Ramp(8, 6), Glass, Draw.Ramp(7, 7));
        double top = Tower(dr, x0, y0, x0 + side, y0 + side, 1, storeys, sh, Frame, Chrome, G.Curtain, 2.5, 4, 3, G.Shop, glass, Frame);
        double c = l.W / 2, cy = l.D / 2;
        dr.Cyl(c, cy, side / 5, top, top + 5, Lamp, side / 9);
        dr.Cyl(c, cy, 0.5, top + 5, MaxHeight - 0.5, Chrome, 0.1);
        for (double x = 2; x < l.W - 1; x += 5) dr.Tree(x, l.D - 2, false, 0.6);
    }

    // ---- industrial ------------------------------------------------------------------------------------------------

    /// <summary>The smelly tannery: a brick shed under a pitched roof, open vats of brown liquor, hides drying.</summary>
    static void Tannery(Lot l)
    {
        var dr = l.Begin(12);
        dr.Ground(Soil);
        double x0 = 2, x1 = l.W - 2, y0 = 2, y1 = Math.Min(l.D - 6, y0 + 9);
        Shed(dr, x0, y0, x1, y1, 4.5, BrickOf(l), 1, WoodDark);
        l.Roof = new RoofSpec(RoofStyle.Gable, 28, 40, l.Pick(MetalDark, RoofTile, Asphalt));
        for (double x = 3; x < l.W - 3; x += 3.5)
        {
            dr.Cyl(x, y1 + 3, 1.3, 0, 1.4, WoodDark, null, true);
            dr.Cyl(x, y1 + 3, 1.15, 0, 1.2, Draw.Ramp(2, 3));
        }
        dr.Cyl(x1 - 2, y0 + 2, 0.5, 4, 10, BrickOf(l));
        for (double x = 2; x < l.W - 2; x += 1.6) dr.Box(x, l.D - 1.2, 1, x + 1.2, l.D - 1.1, 2.4, Draw.Ramp(2, 6));
        dr.Box(1.5, l.D - 1.2, 2.4, l.W - 1.5, l.D - 1.1, 2.5, MetalDark);
    }

    /// <summary>Widget Works: a metal shed with sawtooth north lights, roller doors, a yard of crates and a lorry.</summary>
    static void WidgetWorks(Lot l)
    {
        var dr = l.Begin(12);
        dr.Ground(Concrete);
        double x0 = 1.5, x1 = l.W - 1.5, y0 = 1.5, y1 = Math.Min(l.D - 7, y0 + Math.Max(10, l.D * 0.6));
        byte wall = l.Pick(Metal, Draw.Ramp(9, 7), Cream);
        Shed(dr, x0, y0, x1, y1, 5.5, wall, Math.Max(1, (int)(l.W / 10)), l.Pick(Blue, MetalDark, Yellow));
        Sawtooth(dr, x0, y0, x1, y1, 5.5, 5, 2.4, Metal);
        Truck(dr, l, l.W / 2 - 4, y1 + 2.5);
        for (double x = 2; x < l.W / 2 - 5; x += 1.6) dr.Prop("crate", x, l.D - 1.5);
    }

    /// <summary>A lorry: a cab and a box trailer, nose to the east.</summary>
    static void Truck(Draw dr, Lot l, double x, double y)
    {
        byte cab = l.Pick(Red, Blue, Trim, Yellow);
        dr.Box(x, y, 0.5, x + 7, y + 2.5, 3.5, l.Pick(Trim, Metal, Cream));
        dr.Box(x + 7.2, y, 0.5, x + 9.3, y + 2.5, 2.9, cab);
        dr.Box(x + 8.4, y + 0.1, 1.8, x + 9.35, y + 2.4, 2.6, Glass);
        foreach (double wx in new[] { x + 1, x + 5.2, x + 8 })
        {
            dr.Box(wx, y - 0.05, 0, wx + 1, y + 0.4, 1, Tyre);
            dr.Box(wx, y + 2.1, 0, wx + 1, y + 2.55, 1, Tyre);
        }
    }

    /// <summary>The clean room garage: a white low-rise lab, ribbon windows, lawn and a tidy car park.</summary>
    static void CleanRoom(Lot l)
    {
        var dr = l.Begin(10);
        dr.Ground(Grass);
        double x0 = 2, x1 = l.W - 2, y1 = l.D - 4, y0 = Math.Max(2, y1 - 11);
        double top = dr.Block(x0, y0, x1, y1, 0, 1, 5, Trim, Concrete, G.Ribbon, 3, true, G.Ribbon, Draw.Ramp(8, 8), MetalDark);
        dr.FlatRoof(x0, y0, x1, y1, top, Trim);
        if (y0 > 7) dr.CarPark(2, 1, l.W - 2, y0 - 1, 0.5);
        Path(dr, l.W / 2 - 1, y1, l.W / 2 + 1, l.D);
        Trees(l, dr, 2, 1, y1 + 1, l.W - 1, l.D - 1, 0.5, 0.7);
    }

    /// <summary>Soot &amp; Sons Foundry: a tall brick casting hall, two chimneys, slag heaps and ingots.</summary>
    static void Foundry(Lot l)
    {
        double stack = 22 + l.R.Next(10);
        var dr = l.Begin(stack + 1);
        dr.Ground(Soil);
        double x0 = 2, x1 = l.W - 2, y0 = 2, y1 = Math.Min(l.D - 8, y0 + 16);
        byte wall = BrickOf(l);
        Shed(dr, x0, y0, x1, y1, 9, wall, 2, MetalDark);
        l.Roof = new RoofSpec(RoofStyle.Gable, 22, 50, MetalDark, l.W < l.D);
        for (int i = 0; i < 2; i++)
        {
            double cx = x0 + 3 + i * 5, cy = y0 - 0 + 2.5;
            dr.Cyl(cx, cy, 1.4, 9, stack, wall, 1.0);
            dr.Cyl(cx, cy, 1.05, stack - 1.5, stack, Black);
        }
        dr.Blob(l.W - 6, l.D - 4, 0, 4, 3, 2.5, StoneDark, 0.2, Black);
        for (double x = 3; x < l.W / 2; x += 1.5) dr.Box(x, l.D - 4, 0, x + 1.1, l.D - 2.5, 0.5, l.Pick(Draw.Ramp(1, 6), Metal));
    }

    /// <summary>The assembly hall: a big pitched metal shed with loading docks, lorries and a staff car park.</summary>
    static void AssemblyHall(Lot l)
    {
        var dr = l.Begin(16);
        dr.Ground(Concrete);
        double x0 = 2, x1 = l.W - 2, y0 = 2, y1 = Math.Max(14, l.D - 14);
        byte wall = l.Pick(Metal, Draw.Ramp(9, 6), Cream, Concrete);
        Shed(dr, x0, y0, x1, y1, 10, wall, Math.Max(2, (int)(l.W / 9)), MetalDark);
        dr.Box(x0, y1 - 0.3, 9, x1, y1 + 0.1, 9.6, Darker(wall));
        l.Roof = new RoofSpec(RoofStyle.Gable, 15, 60, l.Pick(MetalDark, Metal), true);
        Truck(dr, l, x0 + 2, y1 + 2);
        if (l.W > 30) Truck(dr, l, x0 + 16, y1 + 2);
        if (l.D - y1 > 10) dr.CarPark(2, y1 + 6, l.W - 2, l.D - 1, 0.6);
    }

    /// <summary>The chip fab: white boxes of clean rooms, a roof crowded with air plant, silver gas tanks, lawns.</summary>
    static void ChipFab(Lot l)
    {
        var dr = l.Begin(18);
        dr.Ground(Grass);
        double x0 = 3, x1 = l.W - 3, y0 = 3, y1 = l.D - 9;
        double top = dr.Block(x0, y0, x1, y1, 0, 2, 5.5, Trim, Concrete, G.Ribbon, 4, true, G.Ribbon, Draw.Ramp(8, 7), Concrete);
        dr.FlatRoof(x0, y0, x1, y1, top, Trim, plant: false);
        for (double x = x0 + 2; x < x1 - 4; x += 5)
            for (double y = y0 + 2; y < y1 - 4; y += 5)
            {
                dr.Box(x, y, top, x + 3.5, y + 3.5, top + 2.5, Metal);
                dr.Cyl(x + 1.75, y + 1.75, 1.2, top + 2.5, top + 3, MetalDark);
            }
        for (double x = 3; x < l.W - 3; x += 4) dr.Cyl(x, l.D - 5, 1.4, 0, 6, Chrome);
        Path(dr, 0, l.D - 2, l.W, l.D, Concrete);
    }

    /// <summary>The mega-smelter: blast furnaces with their stoves, a tall stack, ore heaps and a conveyor.</summary>
    static void Smelter(Lot l)
    {
        double stack = 50 + l.R.Next(15);
        var dr = l.Begin(stack + 1);
        dr.Ground(Soil);
        double cx = l.W * 0.35, cy = l.D * 0.45;
        Shed(dr, 2, 2, l.W - 2, l.D * 0.3, 12, MetalDark, 2, Black);
        l.Roof = new RoofSpec(RoofStyle.Gable, 18, 50, Draw.Ramp(1, 4));
        // A blast furnace and its stoves.
        dr.Cyl(cx, cy, 5, 0, 26, MetalDark, 3.5);
        dr.Cyl(cx, cy, 3.5, 26, 34, Metal, 2);
        for (int i = 0; i < 3; i++) dr.Cyl(cx + 9 + i * 4.5, cy - 1, 1.9, 0, 22, Metal);
        dr.Box(cx, cy - 0.5, 20, cx + 20, cy + 0.5, 21, MetalDark);
        dr.Cyl(l.W - 6, l.D * 0.45, 2.5, 0, stack, Draw.Ramp(0, 7), 1.6);
        dr.Box(l.W - 8.5, l.D * 0.45 - 0.2, stack - 6, l.W - 3.5, l.D * 0.45 + 0.2, stack - 3, Trim);
        dr.Blob(l.W * 0.3, l.D - 7, 0, 7, 5, 4.5, Draw.Ramp(1, 4), 0.25, Soil);
        dr.Blob(l.W * 0.7, l.D - 7, 0, 6, 5, 3.5, Black, 0.25, StoneDark);
        // A conveyor from the heaps up to the furnace top.
        for (int i = 0; i < 20; i++)
        {
            double f = i / 19.0;
            dr.Box(cx + 2 + f * 10, l.D - 10 - f * (l.D - 10 - cy - 5), f * 30, cx + 3.5 + f * 10, l.D - 8.5 - f * (l.D - 10 - cy - 5), f * 30 + 1.2, Metal);
        }
    }

    /// <summary>The robot plant: a vast flat-roofed shed under solar panels, a glass office in front.</summary>
    static void RobotPlant(Lot l)
    {
        var dr = l.Begin(18);
        dr.Ground(Concrete);
        double x0 = 3, x1 = l.W - 3, y0 = 3, y1 = l.D - 16;
        Shed(dr, x0, y0, x1, y1, 11, l.Pick(Trim, Metal, Draw.Ramp(9, 9)), Math.Max(2, (int)(l.W / 12)), Draw.Ramp(9, 5));
        dr.FlatRoof(x0, y0, x1, y1, 11, Trim, plant: false, rim: 0.6);
        for (double y = y0 + 2; y < y1 - 2; y += 3)
            for (int z = 0; z < 3; z++)
                dr.Box(x0 + 2, y + z * 0.6, 11.3 + z * 0.35, x1 - 2, y + z * 0.6 + 0.6, 11.6 + z * 0.35, Draw.Ramp(9, 2));
        double top = dr.Block(l.W / 2 - 12, y1 + 2, l.W / 2 + 12, y1 + 10, 0, 3, 3.8, Frame, Chrome, G.Curtain, 2.5, true, G.Curtain);
        dr.FlatRoof(l.W / 2 - 12, y1 + 2, l.W / 2 + 12, y1 + 10, top, Chrome, plant: false);
        Sign(dr, l.W / 2 - 6, l.W / 2 + 6, y1 + 10, top - 1.6, 1.2, Draw.Ramp(9, 6), Trim);
        Trees(l, dr, 4, 2, y1 + 11, l.W - 2, l.D - 1, 0.3, 0.7);
    }

    /// <summary>The quantum campus: glass and white labs round a lawn, a round lab drum, trees and paths.</summary>
    static void QuantumCampus(Lot l)
    {
        var dr = l.Begin(22);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        double top = dr.Block(3, 3, W * 0.45, D * 0.35, 0, 4, 4, Trim, Concrete, G.Curtain, 3);
        dr.FlatRoof(3, 3, W * 0.45, D * 0.35, top, Trim);
        top = dr.Block(W * 0.55, 3, W - 3, D * 0.4, 0, 3, 4, Frame, Chrome, G.Curtain, 2.5, true, G.Curtain, Draw.Ramp(7, 8));
        dr.FlatRoof(W * 0.55, 3, W - 3, D * 0.4, top, Chrome);
        double rx = W * 0.72, ry = D * 0.68, rr = Math.Min(W, D) * 0.14;
        dr.Cyl(rx, ry, rr, 0, 12, Draw.Ramp(7, 8));
        dr.Cyl(rx, ry, rr - 0.6, 0, 12, 0);
        for (double z = 0; z < 12; z += 4) dr.Cyl(rx, ry, rr + 0.1, z, z + 0.4, Trim, null, true);
        dr.Cyl(rx, ry, rr + 0.3, 12, 13, Trim);
        dr.Blob(rx, ry, 13, rr * 0.7, rr * 0.7, rr * 0.4, Chrome);
        Path(dr, W / 2 - 1.2, D * 0.35, W / 2 + 1.2, D);
        Path(dr, 3, D * 0.55 - 1, W - 3, D * 0.55 + 1);
        Trees(l, dr, 4 + l.Cw * 2, 3, D * 0.6, W * 0.55, D - 2, 0.3);
        for (double x = 4; x < W - 2; x += 8) dr.Prop("bench", x, D * 0.55 + 2);
    }
}
