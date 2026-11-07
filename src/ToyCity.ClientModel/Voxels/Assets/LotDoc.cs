using System.Text.Json;
using System.Text.Json.Serialization;
using static ToyCity.ClientModel.Voxels.Assets.AssetPalette;

namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>What a thing on a lot is.</summary>
public enum LotKind { Ground, Parking, Fence, Plant, Prop }

/// <summary>
/// One thing on a lot, in metres from the lot's north-west corner (x east, y south, the street side):
/// - Ground: a patch from (X0, Y0) to (X1, Y1) of a surface (<see cref="LotDoc.Grounds"/>): a drive, a path, a bed;
/// - Parking: a car park over that rectangle, its bays along the longer side, so many of them filled;
/// - Fence: a run from (X0, Y0) to (X1, Y1) in a style (<see cref="LotDoc.Fences"/>);
/// - Plant: a plant of the plant layer's kinds and more (<see cref="LotDoc.Plants"/>) at (X0, Y0);
/// - Prop: a library prop (<see cref="Pieces"/>) centred on (X0, Y0), turned a quarter at a time.
/// Seed picks a plant's size and which bays have cars, so a lot comes out the same every time.
/// </summary>
public sealed record LotItem(LotKind Kind, string Style, double X0, double Y0, double X1 = 0, double Y1 = 0, int Turn = 0, int Seed = 0)
{
    public LotItem Moved(double dx, double dy) => this with { X0 = X0 + dx, Y0 = Y0 + dy, X1 = X1 + dx, Y1 = Y1 + dy };
}

/// <summary>The building on a lot: where its model comes from (<see cref="LotDoc.Resolve"/>), its north-west corner in metres, and its turn.</summary>
public sealed record LotBuilding(string Source, double X, double Y, int Turn = 0);

/// <summary>A building's model as a lot uses it: cut to what's there (standing on z 0), its detail, roof and shapes.</summary>
public sealed record LotModel(VoxModel Model, int Detail, RoofSpec? Roof, IReadOnlyList<Shape> Shapes)
{
    public double Width => Model.SizeX * 0.5 / Detail;
    public double Depth => Model.SizeY * 0.5 / Detail;
    public double Height => Model.SizeZ * 0.5 / Detail;
}

/// <summary>
/// A lot being dressed in the lot editor (RC part 3), as SimCity 4's Lot Editor does: a building put on a lot one to
/// four cells each way, then ground (drives, paths, paving, beds), car parks, fences and hedges, plants of the plant
/// layer's kinds and the library's props round it. Every change is one step that can be undone and redone. It saves
/// as a small .lot.json of what's where, and <see cref="Compose"/> builds it into one model, the whole lot at the
/// finest detail that fits, which the game draws a building type with.
/// </summary>
public sealed class LotDoc
{
    public const double Cell = 16;
    /// <summary>The finest a lot is built at: 12.5 cm voxels, the finest the city draws.</summary>
    public const int MaxDetail = 4;
    public const int MaxUndo = 200;

    /// <summary>The surfaces a ground patch can be, with their names.</summary>
    public static readonly (string Id, string Name)[] Grounds =
    {
        ("lawn", "Lawn"), ("concrete", "Concrete"), ("asphalt", "Asphalt"), ("paving", "Paving slabs"), ("gravel", "Gravel"),
        ("sand", "Sand"), ("soil", "Soil"), ("flowerbed", "Flower bed"), ("water", "Pond"),
    };

    /// <summary>The fence styles.</summary>
    public static readonly (string Id, string Name)[] Fences =
    {
        ("picket", "Picket fence"), ("rail", "Post and rail"), ("brick", "Brick wall"), ("hedge", "Hedge"), ("chainlink", "Chain-link"),
    };

    /// <summary>The plants: the plant layer's kinds (broadleaf, conifer, scrub, reeds, an orchard's fruit tree) and a bed of flowers.</summary>
    public static readonly (string Id, string Name)[] Plants =
    {
        ("broadleaf", "Broadleaf tree"), ("conifer", "Conifer"), ("orchard", "Fruit tree"), ("scrub", "Shrub"), ("reeds", "Reeds"), ("flowers", "Flowers"),
    };

    /// <summary>How full a car park is.</summary>
    public static readonly (string Id, string Name)[] Parkings = { ("full", "Car park, busy"), ("some", "Car park, half full"), ("empty", "Car park, empty") };

    /// <summary>The library's props, which go anywhere on a lot.</summary>
    public static IEnumerable<Piece> Props => Pieces.All.Where(p => !p.Snap);

    public string Name { get; set; }
    /// <summary>The building type it's for, if chosen.</summary>
    public string Type { get; set; } = "";
    /// <summary>The lot's size in cells.</summary>
    public int W { get; private set; }
    public int D { get; private set; }
    /// <summary>What the rest of the lot is: a surface of <see cref="Grounds"/>, lawn by default.</summary>
    public string Ground { get; private set; } = "lawn";
    public LotBuilding? Building { get; private set; }
    readonly List<LotItem> _items = new();
    public IReadOnlyList<LotItem> Items => _items;

    public double Width => W * Cell;
    public double Depth => D * Cell;

    /// <summary>Goes up with every change, so a view knows to build the lot again.</summary>
    public int Version { get; private set; }
    public bool Dirty { get; private set; }

    readonly List<string> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    int _seeds;

    /// <summary>Finds a building's model from its source; <see cref="Resolve"/> unless set otherwise.</summary>
    public Func<string, LotModel?> Resolver { get; set; } = Resolve;
    readonly Dictionary<string, LotModel?> _models = new();

    public LotDoc(string name, int w, int d)
    {
        Name = name;
        W = Math.Clamp(w, 1, 4);
        D = Math.Clamp(d, 1, 4);
    }

    // ---- changes -------------------------------------------------------------------------------------------------

    /// <summary>A change as one undo step.</summary>
    void Change(Action f)
    {
        string before = ToJson();
        f();
        if (ToJson() == before) return;
        _undo.Add(before);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        Version++;
        Dirty = true;
    }

    /// <summary>Starts the undo history again, and counts the lot as saved: for a lot just made or opened.</summary>
    public void ForgetHistory() { _undo.Clear(); _redo.Clear(); Dirty = false; }

    public void Undo() => Swap(_undo, _redo);
    public void Redo() => Swap(_redo, _undo);

    void Swap(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        to.Add(ToJson());
        Read(from[^1]);
        from.RemoveAt(from.Count - 1);
        Version++;
        Dirty = true;
    }

    /// <summary>The lot's size in cells. What's past the new edge stays in the list but isn't built.</summary>
    public void Resize(int w, int d) => Change(() => { W = Math.Clamp(w, 1, 4); D = Math.Clamp(d, 1, 4); });

    public void SetGround(string style) => Change(() => Ground = style);

    /// <summary>Puts a building on the lot (in place of any there), or takes it off with null.</summary>
    public void SetBuilding(LotBuilding? b) => Change(() => Building = b);

    /// <summary>Adds a thing to the lot and returns where it is in <see cref="Items"/>; one with no seed gets one.</summary>
    public int Add(LotItem item)
    {
        Change(() => _items.Add(item.Seed != 0 ? item : item with { Seed = NextSeed(item) }));
        return _items.Count - 1;
    }

    int NextSeed(LotItem it) => (int)((uint)(++_seeds * 2654435761u) ^ (uint)(it.X0 * 977 + it.Y0 * 7919)) | 1;

    public void Replace(int i, LotItem item) => Change(() => _items[i] = item);

    public void Remove(int i) => Change(() => _items.RemoveAt(i));

    /// <summary>Moves a thing, or the building with -1.</summary>
    public void Move(int i, double dx, double dy) => Change(() =>
    {
        if (i < 0) { if (Building is { } b) Building = b with { X = b.X + dx, Y = b.Y + dy }; }
        else _items[i] = _items[i].Moved(dx, dy);
    });

    /// <summary>Turns a prop or the building (-1) a quarter clockwise seen from above, about its middle.</summary>
    public void TurnAt(int i) => Change(() =>
    {
        if (i < 0)
        {
            if (Building is not { } b || Model(b.Source) is not { } m) return;
            // About its middle: the footprint swaps its sides.
            var (w0, d0) = Footprint(m, b.Turn);
            var (w1, d1) = Footprint(m, b.Turn + 1);
            Building = b with { X = b.X + (w0 - w1) / 2, Y = b.Y + (d0 - d1) / 2, Turn = (b.Turn + 1) & 3 };
        }
        else if (_items[i].Kind is LotKind.Prop) _items[i] = _items[i] with { Turn = (_items[i].Turn + 1) & 3 };
    });

    public void Clear() => Change(() => { _items.Clear(); Building = null; Ground = "lawn"; });

    // ---- where things are ----------------------------------------------------------------------------------------

    /// <summary>The building's model, found once for each source.</summary>
    public LotModel? Model(string source)
    {
        if (!_models.TryGetValue(source, out var m)) _models[source] = m = Resolver(source);
        return m;
    }

    static (double W, double D) Footprint(LotModel m, int turn) => (turn & 1) == 0 ? (m.Width, m.Depth) : (m.Depth, m.Width);

    /// <summary>What a thing covers, in metres: (x0, y0) to (x1, y1), or the building's with -1.</summary>
    public (double X0, double Y0, double X1, double Y1) BoundsOf(int i)
    {
        if (i < 0)
        {
            if (Building is not { } b || Model(b.Source) is not { } m) return (0, 0, 0, 0);
            var (w, d) = Footprint(m, b.Turn);
            return (b.X, b.Y, b.X + w, b.Y + d);
        }
        var it = _items[i];
        switch (it.Kind)
        {
            case LotKind.Ground or LotKind.Parking:
                return (Math.Min(it.X0, it.X1), Math.Min(it.Y0, it.Y1), Math.Max(it.X0, it.X1), Math.Max(it.Y0, it.Y1));
            case LotKind.Fence:
                double t = FenceStyle(it.Style).Thick / 2 + 0.2;
                return (Math.Min(it.X0, it.X1) - t, Math.Min(it.Y0, it.Y1) - t, Math.Max(it.X0, it.X1) + t, Math.Max(it.Y0, it.Y1) + t);
            case LotKind.Plant:
                double r = PlantRadius(it.Style);
                return (it.X0 - r, it.Y0 - r, it.X0 + r, it.Y0 + r);
            default:
                var (pw, pd) = PropSize(it.Style, it.Turn);
                return (it.X0 - pw / 2, it.Y0 - pd / 2, it.X0 + pw / 2, it.Y0 + pd / 2);
        }
    }

    /// <summary>The thing at a point on the lot, the top one first (props, plants and fences over the building over the ground): its index, -1 for the building, or null.</summary>
    public int? At(double x, double y)
    {
        bool In(int i, double grow = 0)
        {
            var (x0, y0, x1, y1) = BoundsOf(i);
            return x >= x0 - grow && x <= x1 + grow && y >= y0 - grow && y <= y1 + grow;
        }
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Kind is LotKind.Prop or LotKind.Plant && In(i, 0.2)) return i;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Kind is LotKind.Fence && NearFence(_items[i], x, y)) return i;
        if (Building != null && In(-1)) return -1;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Kind is LotKind.Ground or LotKind.Parking && In(i)) return i;
        return null;
    }

    static bool NearFence(LotItem f, double x, double y)
    {
        double dx = f.X1 - f.X0, dy = f.Y1 - f.Y0, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((x - f.X0) * dx + (y - f.Y0) * dy) / len2, 0, 1);
        double ex = f.X0 + t * dx - x, ey = f.Y0 + t * dy - y;
        return ex * ex + ey * ey <= Math.Pow(FenceStyle(f.Style).Thick / 2 + 0.5, 2);
    }

    static double PlantRadius(string style) => style switch
    {
        "broadleaf" => 2.6, "conifer" => 2.3, "orchard" => 1.5, "scrub" => 0.8, "reeds" => 0.7, _ => 0.6,
    };

    static readonly Dictionary<string, (int X, int Y)> PropVoxels = new();

    /// <summary>A prop's footprint in metres once turned.</summary>
    public static (double W, double D) PropSize(string id, int turn)
    {
        if (!PropVoxels.TryGetValue(id, out var s))
        {
            var m = Pieces.Find(id)?.Build(Material.Wood, 2);
            PropVoxels[id] = s = m == null ? (2, 2) : (m.SizeX, m.SizeY);
        }
        var (w, d) = (s.X * 0.25, s.Y * 0.25);
        return (turn & 1) == 0 ? (w, d) : (d, w);
    }

    // ---- building it -------------------------------------------------------------------------------------------

    /// <summary>The detail a lot is built at: the finest that fits it and its building in 256 voxels, at most 12.5 cm.</summary>
    public int DetailFor(double height) => RealSet.DetailFor(W, D, height, MaxDetail);

    /// <summary>
    /// Builds the lot into one model the size of the whole lot: the ground, its patches and car parks, the building
    /// at its place and turn with its roof and smooth shapes, then fences, plants and props, each clipped to the lot.
    /// </summary>
    public RealSet.Result Compose()
    {
        var b = Building is { } lb ? Model(lb.Source) : null;
        double height = Math.Max(12, (b?.Height ?? 0) + 1);
        int k = DetailFor(height);
        var m = new VoxModel(W * VoxModel.PerCell * k, D * VoxModel.PerCell * k, Math.Min(256, (int)Math.Ceiling(height * 2 * k) + 1));
        Apply(m);
        m.Smooth[Leaf] = m.Smooth[LeafDark] = true;
        var shapes = new List<Shape>();
        var ground = new Draw(m, k, 1);
        Patch(ground, Ground, 0, 0, Width, Depth, 1);
        Draw For(LotItem it) => new(m, k, it.Seed);
        foreach (var it in _items.Where(i => i.Kind == LotKind.Ground))
        {
            var (x0, y0, x1, y1) = BoundsOf(_items.IndexOf(it));
            Patch(For(it), it.Style, x0, y0, x1, y1, it.Seed);
        }
        foreach (var it in _items.Where(i => i.Kind == LotKind.Parking))
        {
            var (x0, y0, x1, y1) = BoundsOf(_items.IndexOf(it));
            CarPark(For(it), it.Style, x0, y0, x1, y1);
        }
        if (b != null) PlaceBuilding(m, k, b, Building!, shapes);
        foreach (var it in _items.Where(i => i.Kind == LotKind.Fence)) Fence(For(it), it);
        foreach (var it in _items.Where(i => i.Kind is LotKind.Plant or LotKind.Prop))
        {
            var dr = For(it);
            if (it.Kind == LotKind.Plant) Plant(dr, it.Style, it.X0, it.Y0);
            else if (Pieces.Find(it.Style) is { Snap: false }) dr.Prop(it.Style, it.X0, it.Y0, it.Turn);
            shapes.AddRange(dr.Shapes);
        }
        return new RealSet.Result(m, k, b?.Roof, shapes);
    }

    static void PlaceBuilding(VoxModel m, int k, LotModel b, LotBuilding at, List<Shape> shapes)
    {
        var src = AssetDoc.Rescale(b.Model, b.Detail, k);
        double v = 0.5 / k;
        int ox = (int)Math.Round(at.X / v), oy = (int)Math.Round(at.Y / v), turn = at.Turn & 3;
        for (int z = 0; z < src.SizeZ && z < m.SizeZ; z++)
            for (int y = 0; y < src.SizeY; y++)
                for (int x = 0; x < src.SizeX; x++)
                {
                    byte c = src[x, y, z];
                    if (c == 0) continue;
                    var (tx, ty) = AssetDoc.Turn(x, y, src.SizeX, src.SizeY, turn);
                    if (m.In(ox + tx, oy + ty, z)) m[ox + tx, oy + ty, z] = c;
                }
        // Its shapes turn with it, about the same corner, and move to its place.
        double sx = src.SizeX * v, sy = src.SizeY * v;
        foreach (var s0 in b.Shapes)
        {
            var s = s0.Clone();
            var (px, py) = turn switch { 1 => (sy - s.Y, s.X), 2 => (sx - s.X, sy - s.Y), 3 => (s.Y, sx - s.X), _ => (s.X, s.Y) };
            s.X = ox * v + px;
            s.Y = oy * v + py;
            s.Turn += turn * Math.PI / 2;
            shapes.Add(s);
        }
    }

    /// <summary>A patch of ground, a voxel thick: plain, or laid in slabs, gravel or a bed of flowers.</summary>
    static void Patch(Draw dr, string style, double x0, double y0, double x1, double y1, int seed)
    {
        var m = dr.M;
        int a0 = Math.Max(0, dr.I(x0)), b0 = Math.Max(0, dr.I(y0)), a1 = Math.Min(m.SizeX, dr.I(x1)), b1 = Math.Min(m.SizeY, dr.I(y1));
        int slab = dr.I(0.6);
        for (int y = b0; y < b1; y++)
            for (int x = a0; x < a1; x++)
            {
                int h = Hash(x, y, seed);
                byte c = style switch
                {
                    "concrete" => Concrete, "asphalt" => Asphalt, "sand" => Sand, "soil" => Soil, "water" => Water,
                    "paving" => slab >= 3 && (x % slab == 0 || y % slab == 0) ? Mortar : Stone,
                    "gravel" => (h & 3) switch { 0 => StoneDark, 1 => Concrete, _ => Stone },
                    "flowerbed" => Soil,
                    _ => (h & 7) == 0 ? Draw.Ramp(5, 6) : Grass,
                };
                m[x, y, 0] = c;
                if (style == "flowerbed" && (h & 3) == 0 && m.SizeZ > 1)
                {
                    byte f = ((h >> 3) % 5) switch { 0 => Red, 1 => Yellow, 2 => Blue, 3 => Trim, _ => Leaf };
                    m[x, y, 1] = f;
                    if (dr.K >= 4 && (h & 16) != 0 && m.SizeZ > 2) m[x, y, 2] = f;
                }
            }
    }

    static int Hash(int x, int y, int seed) => (int)(((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791)) * 2654435761u >> 8);

    /// <summary>
    /// A car park: asphalt, rows of bays 2.5 m wide and 5 m deep along its longer side, an aisle between each pair of
    /// rows (6 m, or down to 4 m to fit a second row in), cars in some.
    /// </summary>
    static void CarPark(Draw dr, string style, double x0, double y0, double x1, double y1)
    {
        dr.Box(x0, y0, 0, x1, y1, dr.V, Asphalt);
        double fill = style switch { "full" => 0.9, "empty" => 0, _ => 0.5 };
        bool alongX = x1 - x0 >= y1 - y0;
        double len = alongX ? x1 - x0 : y1 - y0, across = alongX ? y1 - y0 : x1 - x0;
        const double bay = 2.5, deep = 5;
        double aisle = across >= 2 * deep + 6 ? 6 : Math.Max(4, across - 2 * deep);
        int bays = (int)Math.Floor((len - 0.2) / bay);
        if (bays < 1 || across < deep) return;
        double start = (len - bays * bay) / 2;
        int row = 0;
        for (double o = 0; o + deep <= across + 0.01; o += row++ % 2 == 0 ? deep + aisle : deep)
        {
            bool facesIn = row % 2 == 0;
            for (int i = 0; i <= bays; i++)
            {
                double s = start + i * bay;
                // The painted lines between bays.
                if (alongX) dr.Box(x0 + s, y0 + o, dr.V * 0.5, x0 + s + Math.Max(dr.V, 0.12), y0 + o + deep, dr.V, Trim);
                else dr.Box(x0 + o, y0 + s, dr.V * 0.5, x0 + o + deep, y0 + s + Math.Max(dr.V, 0.12), dr.V, Trim);
                if (i == bays || dr.R.NextDouble() >= fill) continue;
                double c = s + bay / 2, d = o + deep / 2;
                byte paint = dr.CarColour();
                if (alongX) dr.Prop("car", x0 + c, y0 + d, facesIn ? 1 : 3, dr.V, col => col == Red ? paint : col);
                else dr.Prop("car", x0 + d, y0 + c, facesIn ? 0 : 2, dr.V, col => col == Red ? paint : col);
            }
        }
    }

    /// <summary>A fence style's colour, height and thickness in metres, and how far apart its posts are.</summary>
    static (byte Colour, double Height, double Thick, double Posts) FenceStyle(string style) => style switch
    {
        "rail" => (Wood, 1.15, 0.12, 2.4),
        "brick" => (Brick, 1.2, 0.25, 0),
        "hedge" => (LeafDark, 1.4, 0.7, 0),
        "chainlink" => (Metal, 1.8, 0.06, 3),
        _ => (Trim, 1.0, 0.06, 2),
    };

    /// <summary>A fence along a run, a column of voxels at a time, at any angle.</summary>
    static void Fence(Draw dr, LotItem f)
    {
        var m = dr.M;
        var (colour, height, thick, posts) = FenceStyle(f.Style);
        double dx = f.X1 - f.X0, dy = f.Y1 - f.Y0, len = Math.Sqrt(dx * dx + dy * dy), v = dr.V;
        if (len < v) return;
        double ux = dx / len, uy = dy / len;
        int across = Math.Max(1, (int)Math.Round(thick / v)), top = Math.Min(m.SizeZ - 1, Math.Max(1, dr.I(height)));
        var done = new HashSet<(int, int)>();
        for (double s = 0; s <= len + 1e-6; s += v / 2)
            for (int a = 0; a < across; a++)
            {
                double t = (a - (across - 1) / 2.0) * v;
                int x = (int)Math.Floor((f.X0 + ux * s - uy * t) / v), y = (int)Math.Floor((f.Y0 + uy * s + ux * t) / v);
                if (!m.In(x, y, 0) || !done.Add((x, y))) continue;
                bool post = posts > 0 && (s % posts < v * 1.5 || len - s < v * 1.5);
                int step = (int)Math.Floor(s / v);
                for (int z = 0; z <= top; z++)
                {
                    double zm = (z + 0.5) * v;
                    byte c = f.Style switch
                    {
                        "picket" => post || step % 2 == 0 ? (zm < (post ? height : height * 0.9) ? colour : (byte)0)
                            : Math.Abs(zm - 0.3) < Math.Max(v / 2, 0.05) || Math.Abs(zm - 0.75) < Math.Max(v / 2, 0.05) ? colour : (byte)0,
                        "rail" => post || Math.Abs(zm - 0.5) < Math.Max(v / 2, 0.06) || Math.Abs(zm - 1.0) < Math.Max(v / 2, 0.06) ? colour : (byte)0,
                        "brick" => z == top ? Concrete : colour,
                        "hedge" => z == 0 ? Bark : (Hash(x, y, z) & 3) == 0 ? Leaf : LeafDark,
                        "chainlink" => post || z == top ? colour : ((x + y + z) & 1) == 0 ? MetalDark : (byte)0,
                        _ => colour,
                    };
                    if (c != 0) m[x, y, z] = c;
                }
            }
    }

    /// <summary>A plant at (x, y) metres.</summary>
    static void Plant(Draw dr, string style, double x, double y)
    {
        switch (style)
        {
            case "broadleaf": dr.Tree(x, y); break;
            case "conifer": dr.Tree(x, y, conifer: true); break;
            case "orchard":
                dr.Tree(x, y, false, 0.55);
                // Fruit dotted on the crown's outside.
                var m = dr.M;
                for (int i = 0; i < 18; i++)
                {
                    double a = dr.R.NextDouble() * Math.PI * 2, e = dr.R.NextDouble() * 1.1 - 0.2, r = 1.3;
                    int fx = dr.I(x + Math.Cos(a) * Math.Cos(e) * r), fy = dr.I(y + Math.Sin(a) * Math.Cos(e) * r), fz = dr.I(2.3 + Math.Sin(e) * r);
                    if (m.In(fx, fy, fz)) m[fx, fy, fz] = Red;
                }
                break;
            case "scrub":
                double s = 0.6 + dr.R.NextDouble() * 0.3;
                dr.Blob(x, y, s * 0.7, s, s * 0.9, s * 0.8, LeafDark, 0.2, Draw.Ramp(5, 3));
                break;
            case "reeds":
                for (int i = 0; i < 40; i++)
                {
                    double a = dr.R.NextDouble() * Math.PI * 2, r = Math.Sqrt(dr.R.NextDouble()) * 0.6, h = 1.0 + dr.R.NextDouble() * 0.8;
                    dr.Box(x + Math.Cos(a) * r, y + Math.Sin(a) * r, 0, x + Math.Cos(a) * r + dr.V, y + Math.Sin(a) * r + dr.V, h, Draw.Ramp(4, 5 + dr.R.Next(4)));
                }
                break;
            default:
                Patch(dr, "flowerbed", x - 0.5, y - 0.5, x + 0.5, y + 0.5, dr.R.Next());
                break;
        }
    }

    // ---- building sources ----------------------------------------------------------------------------------------

    /// <summary>
    /// A building's model from its source: "example:house" (the editor's brick house, without its garden);
    /// "set:{type}:{w}x{d}:{variant}", the realistic set's model with its ground taken up (its own trees stay);
    /// or the path of a .vox asset. Each is cut to what's there; null when there's nothing.
    /// </summary>
    public static LotModel? Resolve(string source)
    {
        AssetDoc? doc = null;
        if (source == "example:house") doc = DemoAsset.House(MaxDetail, garden: false);
        else if (source.StartsWith("set:"))
        {
            var p = source.Split(':');
            var size = p.Length > 2 ? p[2].Split('x') : new[] { "1", "1" };
            if (p.Length < 2 || !int.TryParse(size[0], out int w) || !int.TryParse(size.Length > 1 ? size[1] : "1", out int d)) return null;
            int variant = p.Length > 3 && int.TryParse(p[3], out int vv) ? vv : 0;
            if (RealSet.Build(p[1], w, d, variant) is not { } r) return null;
            var m = r.Model;
            // Take up the set's own ground: the bottom layer wherever nothing stands on it.
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                    if (m.SizeZ < 2 || m[x, y, 1] == 0) m[x, y, 0] = 0;
            doc = AssetDoc.FromModel(p[1], r.Detail, m, r.Roof, r.Shapes);
        }
        else if (File.Exists(source)) doc = AssetDoc.Load(source);
        if (doc?.Bounds() is not { } b) return null;
        double v = 0.5 / doc.Detail;
        var shapes = doc.Shapes.Select(s => { var c = s.Clone(); c.X -= b.X0 * v; c.Y -= b.Y0 * v; return c; }).ToList();
        return new LotModel(doc.Cropped(), doc.Detail, doc.Roof, shapes);
    }

    // ---- files ---------------------------------------------------------------------------------------------------

    sealed record File_(string Name, string Type, int W, int D, string Ground, LotBuilding? Building, List<LotItem> Items, int Seeds);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new File_(Name, Type, W, D, Ground, Building, _items, _seeds),
        indented ? new JsonSerializerOptions(Json) { WriteIndented = true } : Json);

    void Read(string json)
    {
        var f = JsonSerializer.Deserialize<File_>(json, Json) ?? throw new JsonException("not a lot");
        Name = f.Name ?? "";
        Type = f.Type ?? "";
        W = Math.Clamp(f.W, 1, 4);
        D = Math.Clamp(f.D, 1, 4);
        Ground = f.Ground ?? "lawn";
        Building = f.Building;
        _items.Clear();
        if (f.Items != null) _items.AddRange(f.Items);
        _seeds = f.Seeds;
    }

    public static LotDoc FromJson(string json)
    {
        var doc = new LotDoc("", 1, 1);
        doc.Read(json);
        return doc;
    }

    /// <summary>Saves what's where as a .lot.json file.</summary>
    public void Save(string path)
    {
        File.WriteAllText(path, ToJson(indented: true));
        Dirty = false;
    }

    public static LotDoc Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>The lot built as an asset, to open in the asset editor or save as one.</summary>
    public AssetDoc ToAsset()
    {
        var r = Compose();
        return AssetDoc.FromModel(Name, r.Detail, r.Model, r.Roof, r.Shapes);
    }

    /// <summary>Builds the lot and saves it as a building type's model for a lot its size, {type}-{w}x{d}.vox, the whole lot so it lines up.</summary>
    public string UseForBuilding(string folder, string typeId) => ToAsset().UseForBuilding(folder, typeId, W, D, crop: false);
}
