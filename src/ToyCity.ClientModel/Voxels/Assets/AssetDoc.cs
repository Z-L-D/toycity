using System.Text.Json;

namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// One asset being made in the editor (RC part 1): a voxel model on the asset palette at a detail, built from the
/// library's snap pieces and props and by hand, voxel by voxel or a box at a time. Every change is one step that
/// can be undone and redone. It saves as a MagicaVoxel .vox file, so it opens in MagicaVoxel and back, with a small
/// .asset.json beside it for its name and detail.
/// </summary>
public sealed class AssetDoc
{
    /// <summary>
    /// Voxels for every half metre: 1 is the placeholder buildings' scale (0.5 m voxels), 2 is 0.25 m, 4 is 0.125 m,
    /// and 8 is 6.25 cm, about 2.5 inches, the default for houses: the finest at which a whole 16 m lot still fits in
    /// one .vox model of 256 voxels a side.
    /// </summary>
    public int Detail { get; private set; }
    public string Name { get; set; }
    public VoxModel Model { get; private set; }
    /// <summary>Goes up with every change, so a view knows to mesh the model again.</summary>
    public int Version { get; private set; }
    /// <summary>Changed since it was last saved.</summary>
    public bool Dirty { get; private set; }

    RoofSpec? _roof;
    /// <summary>The auto-roof over the walls, or null for none: settings only, the roof is made from the walls when drawn.</summary>
    public RoofSpec? Roof
    {
        get => _roof;
        set { if (_roof == value) return; _roof = value; Version++; Dirty = true; }
    }

    /// <summary>The auto-roof's mesh for the model as it is now, in <see cref="VoxMesher.Mesh"/>'s units; empty with no roof.</summary>
    public MeshData RoofMesh(float scale) => Roof is { } r ? AutoRoof.Build(Model, Detail, r, scale) : new MeshData();

    /// <summary>One grid module across, in this model's voxels.</summary>
    public int ModuleVoxels => Pieces.Module * Detail;
    /// <summary>One storey, in this model's voxels.</summary>
    public int StoreyVoxels => Pieces.Storey * Detail;

    public const int MaxUndo = 200;

    /// <summary>The details an asset can be made at.</summary>
    public static readonly int[] Details = { 1, 2, 4, 8 };

    /// <summary>A new asset's detail unless another is chosen: 6.25 cm voxels.</summary>
    public const int DefaultDetail = 8;

    readonly record struct Change(int Index, byte Before, byte After);
    readonly List<Change[]> _undo = new(), _redo = new();
    List<Change>? _open;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public AssetDoc(string name, int detail, int sizeX, int sizeY, int sizeZ)
    {
        if (!Details.Contains(detail)) throw new ArgumentOutOfRangeException(nameof(detail), "detail is 1, 2, 4 or 8");
        Name = name;
        Detail = detail;
        Model = new VoxModel(Math.Clamp(sizeX, 1, 256), Math.Clamp(sizeY, 1, 256), Math.Clamp(sizeZ, 1, 256));
        AssetPalette.Apply(Model);
    }

    /// <summary>A new asset sized in grid modules and storeys, as much as fits in 256 voxels a side.</summary>
    public static AssetDoc New(string name, int detail, int modulesX = 4, int modulesY = 4, int storeys = 4)
    {
        int m = Pieces.Module * detail, s = Pieces.Storey * detail;
        return new AssetDoc(name, detail, Math.Min(256 / m, modulesX) * m, Math.Min(256 / m, modulesY) * m, Math.Min(256 / s, storeys) * s);
    }

    AssetDoc(string name, int detail, VoxModel model) { Name = name; Detail = detail; Model = model; }

    // ---- changes -------------------------------------------------------------------------------------------------

    /// <summary>Several changes as one undo step: everything until <see cref="End"/> undoes together.</summary>
    public void Begin() => _open ??= new List<Change>();

    public void End()
    {
        if (_open == null) return;
        var step = _open;
        _open = null;
        if (step.Count == 0) return;
        _undo.Add(step.ToArray());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        Version++;
        Dirty = true;
    }

    void Put(int x, int y, int z, byte c)
    {
        if (!Model.In(x, y, z)) return;
        int i = x + Model.SizeX * (y + Model.SizeY * z);
        byte was = Model.Voxels[i];
        if (was == c) return;
        Model.Voxels[i] = c;
        _open!.Add(new Change(i, was, c));
    }

    T Step<T>(Func<T> f)
    {
        bool mine = _open == null;
        if (mine) Begin();
        try { return f(); }
        finally { if (mine) End(); }
    }

    void Step(Action f) => Step(() => { f(); return 0; });

    public bool Undo() => Swap(_undo, _redo, back: true);
    public bool Redo() => Swap(_redo, _undo, back: false);

    bool Swap(List<Change[]> from, List<Change[]> to, bool back)
    {
        if (from.Count == 0) return false;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        if (back) for (int i = step.Length - 1; i >= 0; i--) Model.Voxels[step[i].Index] = step[i].Before;
        else foreach (var c in step) Model.Voxels[c.Index] = c.After;
        to.Add(step);
        Version++;
        Dirty = true;
        return true;
    }

    // ---- by hand -------------------------------------------------------------------------------------------------

    public byte this[int x, int y, int z] => Model[x, y, z];

    /// <summary>Sets one voxel (0 erases it).</summary>
    public void Set(int x, int y, int z, byte c) => Step(() => Put(x, y, z, c));

    /// <summary>Recolours one voxel if there is one there.</summary>
    public void Paint(int x, int y, int z, byte c) => Step(() => { if (Model[x, y, z] != 0) Put(x, y, z, c); });

    /// <summary>Fills a box between two corners, inclusive (0 empties it); <paramref name="paintOnly"/> recolours only what's there.</summary>
    public void Box(int x0, int y0, int z0, int x1, int y1, int z1, byte c, bool paintOnly = false) => Step(() =>
    {
        (x0, x1) = (Math.Min(x0, x1), Math.Max(x0, x1));
        (y0, y1) = (Math.Min(y0, y1), Math.Max(y0, y1));
        (z0, z1) = (Math.Min(z0, z1), Math.Max(z0, z1));
        for (int z = Math.Max(0, z0); z <= Math.Min(Model.SizeZ - 1, z1); z++)
            for (int y = Math.Max(0, y0); y <= Math.Min(Model.SizeY - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(Model.SizeX - 1, x1); x++)
                    if (!paintOnly || Model[x, y, z] != 0) Put(x, y, z, c);
    });

    /// <summary>Recolours every voxel of one colour joined to this one (faces touching), like a paint bucket.</summary>
    public int FillColour(int x, int y, int z, byte c) => Step(() =>
    {
        byte from = Model[x, y, z];
        if (from == 0 || from == c) return 0;
        var todo = new Stack<(int, int, int)>();
        todo.Push((x, y, z));
        int n = 0;
        while (todo.Count > 0)
        {
            var (a, b, d) = todo.Pop();
            if (Model[a, b, d] != from) continue;
            Put(a, b, d, c);
            n++;
            todo.Push((a + 1, b, d)); todo.Push((a - 1, b, d)); todo.Push((a, b + 1, d));
            todo.Push((a, b - 1, d)); todo.Push((a, b, d + 1)); todo.Push((a, b, d - 1));
        }
        return n;
    });

    /// <summary>Clears the whole model.</summary>
    public void Clear() => Step(() => { for (int i = 0; i < Model.Voxels.Length; i++) if (Model.Voxels[i] != 0) { byte was = Model.Voxels[i]; Model.Voxels[i] = 0; _open!.Add(new Change(i, was, 0)); } });

    /// <summary>Flips the model east to west, in place.</summary>
    public void MirrorX() => Step(() =>
    {
        var copy = (byte[])Model.Voxels.Clone();
        for (int z = 0; z < Model.SizeZ; z++)
            for (int y = 0; y < Model.SizeY; y++)
                for (int x = 0; x < Model.SizeX; x++)
                    Put(x, y, z, copy[Model.SizeX - 1 - x + Model.SizeX * (y + Model.SizeY * z)]);
    });

    /// <summary>Copies the west half onto the east half, flipped, so a front can be made symmetrical.</summary>
    public void SymmetriseX() => Step(() =>
    {
        int w = Model.SizeX;
        for (int z = 0; z < Model.SizeZ; z++)
            for (int y = 0; y < Model.SizeY; y++)
                for (int x = 0; x < w / 2; x++)
                    Put(w - 1 - x, y, z, Model[x, y, z]);
    });

    // ---- the library ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Places a snap piece in the grid frame (gx, gy) at storey gz, turned a quarter at a time clockwise seen from
    /// above about the frame's middle: a wall on the north edge goes to the east, south, then west edge. Only the
    /// piece's solid voxels are set, so a wall and a floor in one frame both stand.
    /// </summary>
    public void PlacePiece(Piece p, Material mat, int gx, int gy, int gz, int rotation)
    {
        var model = p.Build(mat, Detail);
        if (p.Snap) Stamp(model, gx * ModuleVoxels, gy * ModuleVoxels, gz * StoreyVoxels, rotation);
        else Stamp(model, gx, gy, gz, rotation);
    }

    /// <summary>Places a prop with its turned footprint's north-west bottom corner at (x, y, z) in this model's voxels.</summary>
    public void PlaceProp(Piece p, int x, int y, int z, int rotation) => Stamp(p.Build(Material.Wood, Detail), x, y, z, rotation);

    /// <summary>
    /// Places another model (a saved asset, say) with its turned footprint's corner at (x, y, z): made at another
    /// detail, it is scaled to this one first, and its colours matched to this palette's nearest.
    /// </summary>
    public void PlaceModel(VoxModel other, int otherDetail, int x, int y, int z, int rotation)
    {
        var m = Rescale(other, otherDetail, Detail);
        Stamp(m, x, y, z, rotation, remap: !SamePalette(m.Palette, Model.Palette));
    }

    /// <summary>The size of a model once turned.</summary>
    public static (int X, int Y) Turned(VoxModel m, int rotation) => (rotation & 1) == 0 ? (m.SizeX, m.SizeY) : (m.SizeY, m.SizeX);

    /// <summary>Where a voxel of a model of footprint (sx, sy) lands once turned.</summary>
    public static (int X, int Y) Turn(int x, int y, int sx, int sy, int rotation) => (rotation & 3) switch
    {
        0 => (x, y),
        1 => (sy - 1 - y, x),
        2 => (sx - 1 - x, sy - 1 - y),
        _ => (y, sx - 1 - x),
    };

    void Stamp(VoxModel m, int ox, int oy, int oz, int rotation, bool remap = false) => Step(() =>
    {
        var map = new byte[256];
        for (int c = 1; c < 256; c++) map[c] = remap ? Nearest(Model.Palette, m.Palette[c]) : (byte)c;
        for (int z = 0; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                {
                    byte c = m[x, y, z];
                    if (c == 0) continue;
                    var (tx, ty) = Turn(x, y, m.SizeX, m.SizeY, rotation);
                    Put(ox + tx, oy + ty, oz + z, map[c]);
                }
    });

    /// <summary>The voxels a snap piece or prop would fill if placed now, for a preview.</summary>
    public IEnumerable<(int X, int Y, int Z, byte C)> Ghost(VoxModel m, int ox, int oy, int oz, int rotation)
    {
        for (int z = 0; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                {
                    byte c = m[x, y, z];
                    if (c == 0) continue;
                    var (tx, ty) = Turn(x, y, m.SizeX, m.SizeY, rotation);
                    if (Model.In(ox + tx, oy + ty, oz + z)) yield return (ox + tx, oy + ty, oz + z, c);
                }
    }

    /// <summary>A model at another detail: grown voxel by voxel, or shrunk keeping each block's commonest colour.</summary>
    public static VoxModel Rescale(VoxModel m, int from, int to)
    {
        if (from == to) return m;
        if (from > to) return m.Downsample(from / to);
        int f = to / from;
        var big = new VoxModel(Math.Min(256, m.SizeX * f), Math.Min(256, m.SizeY * f), Math.Min(256, m.SizeZ * f));
        Array.Copy(m.Palette, big.Palette, 256);
        Array.Copy(m.Smooth, big.Smooth, 256);
        for (int z = 0; z < big.SizeZ; z++)
            for (int y = 0; y < big.SizeY; y++)
                for (int x = 0; x < big.SizeX; x++)
                    big[x, y, z] = m[x / f, y / f, z / f];
        return big;
    }

    static bool SamePalette(uint[] a, uint[] b) { for (int i = 1; i < 256; i++) if (a[i] != b[i]) return false; return true; }

    static byte Nearest(uint[] palette, uint rgba)
    {
        var (r, g, b, _) = VoxModel.Unpack(rgba);
        int best = 1, bestD = int.MaxValue;
        for (int i = 1; i < 256; i++)
        {
            var (pr, pg, pb, _) = VoxModel.Unpack(palette[i]);
            int d = (pr - r) * (pr - r) + (pg - g) * (pg - g) + (pb - b) * (pb - b);
            if (d < bestD) (best, bestD) = (i, d);
        }
        return (byte)best;
    }

    // ---- shape ---------------------------------------------------------------------------------------------------

    /// <summary>The smallest box holding every solid voxel, or null if the model is empty.</summary>
    public (int X0, int Y0, int Z0, int X1, int Y1, int Z1)? Bounds()
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1;
        for (int z = 0; z < Model.SizeZ; z++)
            for (int y = 0; y < Model.SizeY; y++)
                for (int x = 0; x < Model.SizeX; x++)
                    if (Model[x, y, z] != 0)
                    {
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); z0 = Math.Min(z0, z);
                        x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); z1 = Math.Max(z1, z);
                    }
        return x1 < 0 ? null : (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>
    /// A copy cut down to what's there. With <paramref name="keepGround"/> the bottom stays at z 0, so a building
    /// keeps standing on its lot rather than sinking to its lowest voxel.
    /// </summary>
    public VoxModel Cropped(bool keepGround = true)
    {
        var b = Bounds() ?? (0, 0, 0, 0, 0, 0);
        int z0 = keepGround ? 0 : b.Z0;
        var m = new VoxModel(b.X1 - b.X0 + 1, b.Y1 - b.Y0 + 1, b.Z1 - z0 + 1);
        Array.Copy(Model.Palette, m.Palette, 256);
        Array.Copy(Model.Smooth, m.Smooth, 256);
        for (int z = 0; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                    m[x, y, z] = Model[b.X0 + x, b.Y0 + y, z0 + z];
        return m;
    }

    /// <summary>Makes the model another size, keeping what's there from the north-west bottom corner.</summary>
    public void Resize(int sx, int sy, int sz)
    {
        var old = Model;
        var m = new VoxModel(Math.Clamp(sx, 1, 256), Math.Clamp(sy, 1, 256), Math.Clamp(sz, 1, 256));
        Array.Copy(old.Palette, m.Palette, 256);
        Array.Copy(old.Smooth, m.Smooth, 256);
        for (int z = 0; z < Math.Min(sz, old.SizeZ); z++)
            for (int y = 0; y < Math.Min(sy, old.SizeY); y++)
                for (int x = 0; x < Math.Min(sx, old.SizeX); x++)
                    m[x, y, z] = old[x, y, z];
        Model = m;
        // Undo steps index the old model's voxels, so a resize starts the history again.
        _undo.Clear(); _redo.Clear();
        Version++;
        Dirty = true;
    }

    // ---- files ---------------------------------------------------------------------------------------------------

    sealed record Sidecar(string Name, int Detail, RoofSpec? Roof = null);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The sidecar beside a .vox file: name.asset.json.</summary>
    public static string SidecarOf(string voxPath) => Path.ChangeExtension(voxPath, null) + ".asset.json";

    /// <summary>Saves the model as a .vox file and its name and detail beside it.</summary>
    public void Save(string voxPath)
    {
        VoxFile.Write(Model, voxPath);
        File.WriteAllText(SidecarOf(voxPath), JsonSerializer.Serialize(new Sidecar(Name, Detail, Roof), Json));
        Dirty = false;
    }

    /// <summary>Opens a .vox file: one made here keeps its name and detail; any other is taken as detail 1 and named after the file.</summary>
    public static AssetDoc Load(string voxPath)
    {
        var m = VoxFile.Read(voxPath);
        var (name, detail) = ReadSidecar(voxPath) ?? (Path.GetFileNameWithoutExtension(voxPath), 1);
        string side = SidecarOf(voxPath);
        var roof = File.Exists(side) ? ParseRoof(File.ReadAllText(side)) : null;
        return new AssetDoc(name, detail, m) { _roof = roof };
    }

    /// <summary>The name and detail saved beside a .vox file, if there are any.</summary>
    public static (string Name, int Detail)? ReadSidecar(string voxPath)
    {
        string path = SidecarOf(voxPath);
        if (!File.Exists(path)) return null;
        try { return ParseSidecar(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    /// <summary>A sidecar's text as a name and a detail (1, 2, 4 or 8).</summary>
    public static (string Name, int Detail)? ParseSidecar(string text)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Sidecar>(text, Json);
            if (s == null) return null;
            return (s.Name ?? "", Details.Contains(s.Detail) ? s.Detail : 1);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A sidecar's auto-roof, if it has one.</summary>
    public static RoofSpec? ParseRoof(string text)
    {
        try { return JsonSerializer.Deserialize<Sidecar>(text, Json)?.Roof; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Saves the asset as a building type's model in a folder the game reads models from: {type}.vox cut down to
    /// what's there (standing on z 0) and its detail beside it, so the game shrinks a fine model to the lot.
    /// </summary>
    public string UseForBuilding(string folder, string typeId, int lotW = 0, int lotD = 0)
    {
        Directory.CreateDirectory(folder);
        string name = lotW > 0 && lotD > 0 ? $"{typeId}-{lotW}x{lotD}" : typeId;
        string path = Path.Combine(folder, name + ".vox");
        VoxFile.Write(Cropped(), path);
        File.WriteAllText(SidecarOf(path), JsonSerializer.Serialize(new Sidecar(Name, Detail, Roof), Json));
        return path;
    }

    /// <summary>The cells a model covers at a detail: its footprint in 16 m cells, rounded up.</summary>
    public static (int W, int D) CellsOf(VoxModel m, int detail)
    {
        int per = VoxModel.PerCell * detail;
        return ((m.SizeX + per - 1) / per, (m.SizeY + per - 1) / per);
    }
}
