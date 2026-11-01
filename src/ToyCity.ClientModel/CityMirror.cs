using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// The client's mirror of the city, kept current from FullState and TickDelta messages. The C# twin of the web
/// client's City (clients/web/src/state.ts).
/// </summary>
public sealed class CityMirror
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int WaterLevel { get; private set; } = 8;
    public ulong Seed { get; private set; }
    public byte[] Heights = Array.Empty<byte>();
    /// <summary>The ground at every grid corner in decimetres, (Width + 1) by (Height + 1): what the terrain is drawn from.</summary>
    public ushort[] Corners = Array.Empty<ushort>();
    public int CornerIdx(int x, int y) => y * (Width + 1) + x;
    /// <summary>A corner's height in height steps (2 m), the unit the views scale by.</summary>
    public float CornerSteps(int x, int y) => Corners[CornerIdx(Math.Clamp(x, 0, Width), Math.Clamp(y, 0, Height))] / 20f;
    public byte[] Zones = Array.Empty<byte>();
    public ushort[] Paths = Array.Empty<ushort>();
    /// <summary>Each cell's path surface: index into <see cref="Surfaces"/> + 1, 0 for none (Beta 3.2).</summary>
    public byte[] PathSurfaces = Array.Empty<byte>();
    /// <summary>The road's level in dm where it isn't on the ground: a bridge's deck or a tunnel's floor; 0 on the ground.</summary>
    public ushort[] PathDecks = Array.Empty<ushort>();
    /// <summary>A path cell that runs in a tunnel under the ground (Beta 3.2).</summary>
    public bool IsTunnel(int i) => PathDecks.Length > i && PathDecks[i] > 0 && Paths[i] != 0 && !IsRiver(i);
    public List<SurfaceInfo> Surfaces = new();
    public byte[] PowerLines = Array.Empty<byte>();
    public byte[] WaterPipes = Array.Empty<byte>();
    /// <summary>Building id + 1 per cell, 0 when empty. Roadside stops don't take a cell.</summary>
    public int[] BuildingAt = Array.Empty<int>();
    public readonly Dictionary<int, BuildingState> Buildings = new();
    public CityStats Stats { get; private set; } = new();
    public IReadOnlyList<PathProfileInfo> Profiles { get; private set; } = Array.Empty<PathProfileInfo>();
    public IReadOnlyList<BuildingTypeInfo> Types { get; private set; } = Array.Empty<BuildingTypeInfo>();
    public readonly Dictionary<string, BuildingTypeInfo> TypeById = new();
    /// <summary>The deepest lot a zoned building grows on, by density.</summary>
    public int[] LotDepth { get; private set; } = { 2, 3, 4 };
    /// <summary>What the neighbouring cities of the region show across this one's sides, from the region state.</summary>
    public IReadOnlyList<NeighbourEdge> Borders = Array.Empty<NeighbourEdge>();
    /// <summary>The landmarks and reward lots this city has unlocked, from the server's civic state.</summary>
    public HashSet<string> Unlocked = new();
    /// <summary>The arcs and diagonals as they were drawn, and which cells they cover (drawn by the strip, not the cell).</summary>
    public IReadOnlyList<RoadStrip> Strips { get; private set; } = Array.Empty<RoadStrip>();
    public HashSet<int> StripCells { get; private set; } = new();
    public HashSet<int> StripEnds { get; private set; } = new();
    public StreetNamer Streets { get; private set; } = new(new[] { "Main" }, new[] { "Street" });
    public bool Loaded { get; private set; }

    /// <summary>Bumped whenever heights change, so a renderer knows to rebuild the terrain.</summary>
    public int TerrainVersion { get; private set; }
    /// <summary>Bumped on any change at all.</summary>
    public int Version { get; private set; }
    /// <summary>Goes up when a road or the ground under one may have changed.</summary>
    public int RoadVersion { get; private set; }
    /// <summary>Cells changed since the last <see cref="TakeDirtyCells"/>, for renderers that update in chunks.</summary>
    readonly HashSet<int> _dirtyCells = new();
    readonly List<BuildingState> _removed = new();

    /// <summary>The buildings knocked down, burnt out or demolished since the last call, as they last stood.</summary>
    public List<BuildingState> TakeRemoved()
    {
        var r = _removed.ToList();
        _removed.Clear();
        return r;
    }
    readonly HashSet<int> _dirtyBuildings = new();

    public int N => Width * Height;
    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public int Idx(int x, int y) => y * Width + x;
    public bool IsWater(int i) => Heights[i] < WaterLevel || Rivers[i] > Heights[i];

    /// <summary>Each cell's river surface in steps, 0 for none (Beta 3.1 part 5).</summary>
    public byte[] Rivers = Array.Empty<byte>();
    public bool IsRiver(int i) => Rivers[i] > Heights[i];

    /// <summary>
    /// Each cell's floodwater surface in steps, 0 where dry, from the stats' flood stage: the same rule the server
    /// uses (World.FloodedCells), so the views show the water the sim damages buildings with.
    /// </summary>
    public byte[] Flooded => _flooded is { } f && _floodedFor == (FloodStage, TerrainVersion) ? f : (_flooded = FloodedCells(), _floodedFor = (FloodStage, TerrainVersion)).Item1;
    byte[]? _flooded;
    (int, int) _floodedFor = (-1, -1);
    public int FloodStage => Stats.FloodStage;

    byte[] FloodedCells()
    {
        const int Reach = 5;
        int n = N, stage = FloodStage;
        var water = new byte[n];
        if (stage <= 0) return water;
        var dist = new int[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (IsRiver(i)) { water[i] = (byte)Math.Min(255, Rivers[i] + stage); queue.Enqueue(i); }
        int[] dx = { 0, 1, 0, -1 }, dy = { -1, 0, 1, 0 };
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (dist[c] >= Reach) continue;
            int x = c % Width, y = c / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (!InBounds(nx, ny)) continue;
                int j = Idx(nx, ny);
                if (water[j] != 0 || Heights[j] < WaterLevel || IsRiver(j) || Heights[j] >= water[c]) continue;
                water[j] = water[c];
                dist[j] = dist[c] + 1;
                queue.Enqueue(j);
            }
        }
        for (int i = 0; i < n; i++) if (IsRiver(i)) water[i] = 0;
        return water;
    }

    public void ApplyFull(FullState f)
    {
        Width = f.Width;
        Height = f.Height;
        WaterLevel = f.WaterLevel;
        Seed = f.Seed;
        int n = N;
        Heights = f.Heights.ToByteArray();
        Rivers = f.Rivers.Length == Width * Height ? f.Rivers.ToByteArray() : new byte[Width * Height];
        var cb = f.Corners.Span;
        Corners = new ushort[(Width + 1) * (Height + 1)];
        if (cb.Length == Corners.Length * 2)
            for (int i = 0; i < Corners.Length; i++) Corners[i] = (ushort)(cb[2 * i] | (cb[2 * i + 1] << 8));
        else
            // A server from before corners: each corner the average of the cells around it.
            for (int y = 0; y <= Height; y++)
            for (int x = 0; x <= Width; x++)
            {
                int sum = 0, k = 0;
                for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                    if (InBounds(x + dx, y + dy)) { sum += Heights[Idx(x + dx, y + dy)]; k++; }
                Corners[CornerIdx(x, y)] = (ushort)(sum * 20 / k);
            }
        Zones = f.Zones.ToByteArray();
        var pb = f.Paths.Span;
        Paths = new ushort[n];
        for (int i = 0; i < n; i++) Paths[i] = (ushort)(pb[2 * i] | (pb[2 * i + 1] << 8));
        PathSurfaces = f.PathSurfaces.Length == n ? f.PathSurfaces.ToByteArray() : new byte[n];
        PathDecks = new ushort[n];
        var db = f.PathDecks.Span;
        if (db.Length == 2 * n) for (int i = 0; i < n; i++) PathDecks[i] = (ushort)(db[2 * i] | (db[2 * i + 1] << 8));
        PowerLines = f.PowerLines.ToByteArray();
        WaterPipes = f.WaterPipes.ToByteArray();
        Profiles = f.Catalog.Profiles.ToList();
        Types = f.Catalog.BuildingTypes.ToList();
        Surfaces = f.Catalog.Surfaces.ToList();
        TypeById.Clear();
        foreach (var t in Types) TypeById[t.Id] = t;
        if (f.Catalog.LotDepth.Count == 3) LotDepth = f.Catalog.LotDepth.ToArray();
        if (f.Catalog.StreetNames.Count > 0) Streets = new StreetNamer(f.Catalog.StreetNames.ToList(), f.Catalog.StreetKinds.ToList());
        Buildings.Clear();
        BuildingAt = new int[n];
        foreach (var b in f.Buildings) PutBuilding(b);
        Stats = f.Stats ?? new CityStats();
        SetStrips(f.Strips);
        TerrainVersion++;
        RoadVersion++;
        Version++;
        _dirtyCells.Clear();
        _dirtyBuildings.Clear();
        _removed.Clear();
        AllDirty = true;
        Loaded = true;
    }

    void SetStrips(IEnumerable<RoadStrip> strips)
    {
        Strips = strips.ToList();
        StripCells = Strips.SelectMany(s => s.Cells).ToHashSet();
        StripEnds = Strips.Where(s => s.Cells.Count > 0).SelectMany(s => new[] { s.Cells[0], s.Cells[^1] }).ToHashSet();
    }

    public void ApplyDelta(TickDelta d)
    {
        bool terrain = d.Corners.Count > 0, roads = false;
        foreach (var c in d.Corners)
        {
            Corners[c.Index] = (ushort)c.Dm;
            // The cells around a moved corner are drawn again.
            int cx = c.Index % (Width + 1), cy = c.Index / (Width + 1);
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
                if (InBounds(cx + dx, cy + dy)) _dirtyCells.Add(Idx(cx + dx, cy + dy));
        }
        foreach (var c in d.Cells)
        {
            int i = c.Index;
            if (Heights[i] != c.Height) terrain = true;
            if (Paths[i] != c.Path) roads = true;
            Heights[i] = (byte)c.Height;
            Zones[i] = (byte)c.Zone;
            if (PathSurfaces[i] != c.Surface) roads = true;
            Paths[i] = (ushort)c.Path;
            PathSurfaces[i] = (byte)c.Surface;
            if (PathDecks[i] != c.Deck) roads = true;
            PathDecks[i] = (ushort)c.Deck;
            PowerLines[i] = (byte)(c.PowerLine ? 1 : 0);
            WaterPipes[i] = (byte)(c.WaterPipe ? 1 : 0);
            _dirtyCells.Add(i);
        }
        foreach (int id in d.RemovedBuildings)
        {
            if (Buildings.TryGetValue(id, out var gone)) _removed.Add(gone);
            RemoveBuilding(id);
        }
        foreach (var b in d.Buildings)
        {
            // Most updates are a building's numbers changing; only a new footprint touches the cells.
            if (Buildings.TryGetValue(b.Id, out var old) && (old.X, old.Y, old.Width, old.Height, old.TypeId) == (b.X, b.Y, b.Width, b.Height, b.TypeId))
            {
                Buildings[b.Id] = b;
                _dirtyBuildings.Add(b.Id);
                continue;
            }
            RemoveBuilding(b.Id);
            PutBuilding(b);
        }
        if (d.Stats != null) Stats = d.Stats;
        if (d.StripsChanged)
        {
            foreach (int i in StripCells) _dirtyCells.Add(i);
            SetStrips(d.Strips);
            foreach (int i in StripCells) _dirtyCells.Add(i);
            roads = true;
        }
        if (terrain) TerrainVersion++;
        if (terrain || roads) RoadVersion++;
        Version++;
    }

    /// <summary>True after a full state: everything needs drawing again.</summary>
    public bool AllDirty { get; private set; }

    /// <summary>The cells and buildings changed since the last call, and whether everything did.</summary>
    public (bool All, int[] Cells, int[] Buildings) TakeDirty()
    {
        var r = (AllDirty, _dirtyCells.ToArray(), _dirtyBuildings.ToArray());
        AllDirty = false;
        _dirtyCells.Clear();
        _dirtyBuildings.Clear();
        return r;
    }

    public bool IsRoadside(BuildingState b) => TypeById.TryGetValue(b.TypeId, out var t) && t.Roadside;

    void PutBuilding(BuildingState b)
    {
        Buildings[b.Id] = b;
        _dirtyBuildings.Add(b.Id);
        if (IsRoadside(b)) { if (InBounds(b.X, b.Y)) _dirtyCells.Add(Idx(b.X, b.Y)); return; }
        for (int y = b.Y; y < b.Y + b.Height; y++)
            for (int x = b.X; x < b.X + b.Width; x++)
                if (InBounds(x, y)) { BuildingAt[Idx(x, y)] = b.Id + 1; _dirtyCells.Add(Idx(x, y)); }
    }

    void RemoveBuilding(int id)
    {
        if (!Buildings.TryGetValue(id, out var b)) return;
        _dirtyBuildings.Add(id);
        Buildings.Remove(id);
        if (IsRoadside(b)) { if (InBounds(b.X, b.Y)) _dirtyCells.Add(Idx(b.X, b.Y)); return; }
        for (int y = b.Y; y < b.Y + b.Height; y++)
            for (int x = b.X; x < b.X + b.Width; x++)
                if (InBounds(x, y) && BuildingAt[Idx(x, y)] == id + 1) { BuildingAt[Idx(x, y)] = 0; _dirtyCells.Add(Idx(x, y)); }
    }

    public BuildingState? BuildingAtCell(int x, int y) =>
        InBounds(x, y) && BuildingAt[Idx(x, y)] is var id and > 0 && Buildings.TryGetValue(id - 1, out var b) ? b : null;

    public SurfaceInfo? SurfaceAt(int i) => PathSurfaces.Length > i && PathSurfaces[i] is var s and > 0 && s <= Surfaces.Count ? Surfaces[s - 1] : null;

    /// <summary>The surface code (index + 1) a profile is built in when asked for <paramref name="surface"/>, as the server picks it; -1 if not allowed.</summary>
    public int SurfaceCode(PathProfileInfo p, string? surface)
    {
        if (p.Surfaces.Count == 0) return string.IsNullOrEmpty(surface) ? 0 : -1;
        string id = string.IsNullOrEmpty(surface) ? p.Surfaces[0] : surface;
        if (!p.Surfaces.Contains(id)) return -1;
        return Surfaces.FindIndex(s => s.Id == id) + 1;
    }

    /// <summary>The steepest a profile in a surface code climbs, in percent: as the server's ContentDb.GradePct.</summary>
    public int GradePct(PathProfileInfo p, int surfaceCode) =>
        ToyCity.Sim.PathGrade.GradePct(p.MaxGradePct, p.Stairs, surfaceCode > 0 && surfaceCode <= Surfaces.Count ? Surfaces[surfaceCode - 1].MaxGradePct : -1);

    public PathProfileInfo? ProfileAt(int i) => Paths[i] is var p and > 0 && p <= Profiles.Count ? Profiles[p - 1] : null;

    /// <summary>Stops on the kerbs of the roads beside a cell that face it.</summary>
    public IEnumerable<BuildingState> StopsFacing(int x, int y)
    {
        int[] dx = { 0, 1, 0, -1 }, dy = { -1, 0, 1, 0 };
        for (int k = 0; k < 4; k++)
            foreach (var s in StopsAt(x - dx[k], y - dy[k]))
                if (s.Side == k) yield return s;
    }

    /// <summary>The roadside stops on a road cell.</summary>
    public IEnumerable<BuildingState> StopsAt(int x, int y) =>
        Buildings.Values.Where(b => b.X == x && b.Y == y && IsRoadside(b));

    bool PathAt(int x, int y) => InBounds(x, y) && Paths[Idx(x, y)] != 0;
    public bool RoadEW(int x, int y) => PathAt(x - 1, y) || PathAt(x + 1, y);
    public bool RoadNS(int x, int y) => PathAt(x, y - 1) || PathAt(x, y + 1);

    /// <summary>The name of the road on a path cell, as the server names it (World.StreetAt).</summary>
    public string StreetAt(int x, int y) => !RoadEW(x, y) && RoadNS(x, y) ? Streets.RoadName(true, x) : Streets.RoadName(false, y);

    static readonly (int Dx, int Dy)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>The block of open land around a cell, enclosed by paths, or null when it isn't enclosed. Mirrors World.BlockAt.</summary>
    public List<int>? BlockAt(int x, int y, int max = 1600)
    {
        if (!InBounds(x, y)) return null;
        bool Open(int i) => Paths[i] == 0 && !IsWater(i) && !(BuildingAt[i] > 0 && Buildings.TryGetValue(BuildingAt[i] - 1, out var b) && TypeById.TryGetValue(b.TypeId, out var t) && t.Placeable);
        int start = Idx(x, y);
        if (!Open(start)) return null;
        var cells = new List<int> { start };
        var seen = new HashSet<int> { start };
        for (int q = 0; q < cells.Count; q++)
        {
            int c = cells[q], cx = c % Width, cy = c / Width;
            foreach (var (dx, dy) in Dirs)
            {
                if (!InBounds(cx + dx, cy + dy)) return null;
                int n = Idx(cx + dx, cy + dy);
                if (!Open(n) || !seen.Add(n)) continue;
                cells.Add(n);
                if (cells.Count > max) return null;
            }
        }
        return cells;
    }

    /// <summary>Steps from the nearest frontage path for each block cell (1 touches the road), 0 when no road reaches it.</summary>
    public Dictionary<int, int> FrontageDepth(List<int> block)
    {
        var inBlock = block.ToHashSet();
        var depth = new Dictionary<int, int>();
        var queue = new List<int>();
        foreach (int c in block)
        {
            int cx = c % Width, cy = c / Width;
            if (Dirs.Any(d => InBounds(cx + d.Dx, cy + d.Dy) && ProfileAt(Idx(cx + d.Dx, cy + d.Dy)) is { Frontage: true })) { depth[c] = 1; queue.Add(c); }
        }
        for (int q = 0; q < queue.Count; q++)
        {
            int c = queue[q], cx = c % Width, cy = c / Width;
            foreach (var (dx, dy) in Dirs)
            {
                if (!InBounds(cx + dx, cy + dy)) continue;
                int n = Idx(cx + dx, cy + dy);
                if (!inBlock.Contains(n) || depth.ContainsKey(n)) continue;
                depth[n] = depth[c] + 1;
                queue.Add(n);
            }
        }
        foreach (int c in block) depth.TryAdd(c, 0);
        return depth;
    }
}
