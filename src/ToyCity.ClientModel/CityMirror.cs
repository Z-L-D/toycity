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
    public byte[] Zones = Array.Empty<byte>();
    public ushort[] Paths = Array.Empty<ushort>();
    public byte[] PowerLines = Array.Empty<byte>();
    public byte[] WaterPipes = Array.Empty<byte>();
    /// <summary>Building id + 1 per cell, 0 when empty. Roadside stops don't take a cell.</summary>
    public int[] BuildingAt = Array.Empty<int>();
    public readonly Dictionary<int, BuildingState> Buildings = new();
    public CityStats Stats { get; private set; } = new();
    public IReadOnlyList<PathProfileInfo> Profiles { get; private set; } = Array.Empty<PathProfileInfo>();
    public IReadOnlyList<BuildingTypeInfo> Types { get; private set; } = Array.Empty<BuildingTypeInfo>();
    public readonly Dictionary<string, BuildingTypeInfo> TypeById = new();
    public StreetNamer Streets { get; private set; } = new(new[] { "Main" }, new[] { "Street" });
    public bool Loaded { get; private set; }

    /// <summary>Bumped whenever heights change, so a renderer knows to rebuild the terrain.</summary>
    public int TerrainVersion { get; private set; }
    /// <summary>Bumped on any change at all.</summary>
    public int Version { get; private set; }
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
    public bool IsWater(int i) => Heights[i] < WaterLevel;

    public void ApplyFull(FullState f)
    {
        Width = f.Width;
        Height = f.Height;
        WaterLevel = f.WaterLevel;
        Seed = f.Seed;
        int n = N;
        Heights = f.Heights.ToByteArray();
        Zones = f.Zones.ToByteArray();
        var pb = f.Paths.Span;
        Paths = new ushort[n];
        for (int i = 0; i < n; i++) Paths[i] = (ushort)(pb[2 * i] | (pb[2 * i + 1] << 8));
        PowerLines = f.PowerLines.ToByteArray();
        WaterPipes = f.WaterPipes.ToByteArray();
        Profiles = f.Catalog.Profiles.ToList();
        Types = f.Catalog.BuildingTypes.ToList();
        TypeById.Clear();
        foreach (var t in Types) TypeById[t.Id] = t;
        if (f.Catalog.StreetNames.Count > 0) Streets = new StreetNamer(f.Catalog.StreetNames.ToList(), f.Catalog.StreetKinds.ToList());
        Buildings.Clear();
        BuildingAt = new int[n];
        foreach (var b in f.Buildings) PutBuilding(b);
        Stats = f.Stats ?? new CityStats();
        TerrainVersion++;
        Version++;
        _dirtyCells.Clear();
        _dirtyBuildings.Clear();
        _removed.Clear();
        AllDirty = true;
        Loaded = true;
    }

    public void ApplyDelta(TickDelta d)
    {
        bool terrain = false;
        foreach (var c in d.Cells)
        {
            int i = c.Index;
            if (Heights[i] != c.Height) terrain = true;
            Heights[i] = (byte)c.Height;
            Zones[i] = (byte)c.Zone;
            Paths[i] = (ushort)c.Path;
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
        if (terrain) TerrainVersion++;
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

    public PathProfileInfo? ProfileAt(int i) => Paths[i] is var p and > 0 && p <= Profiles.Count ? Profiles[p - 1] : null;

    /// <summary>The roadside stops on a road cell.</summary>
    public IEnumerable<BuildingState> StopsAt(int x, int y) =>
        Buildings.Values.Where(b => b.X == x && b.Y == y && IsRoadside(b));

    bool PathAt(int x, int y) => InBounds(x, y) && Paths[Idx(x, y)] != 0;
    public bool RoadEW(int x, int y) => PathAt(x - 1, y) || PathAt(x + 1, y);
    public bool RoadNS(int x, int y) => PathAt(x, y - 1) || PathAt(x, y + 1);

    /// <summary>The name of the road on a path cell, as the server names it (World.StreetAt).</summary>
    public string StreetAt(int x, int y) => !RoadEW(x, y) && RoadNS(x, y) ? Streets.RoadName(true, x) : Streets.RoadName(false, y);
}
