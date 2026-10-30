namespace ToyCity.Sim;

/// <summary>
/// What a neighbouring city shows across one edge of this one: its name and mayor, and which of its edge cells carry
/// a path, a power line or a pipe. Cell k runs along the edge: x on the north and south edges, y on the east and west.
/// </summary>
public sealed class NeighbourEdge
{
    public string Name = "";
    public string Mayor = "";
    /// <summary>Profile index + 1 of the neighbour's path at each edge cell, 0 for none.</summary>
    public ushort[] Path = Array.Empty<ushort>();
    public bool[] Power = Array.Empty<bool>();
    public bool[] Pipe = Array.Empty<bool>();
    /// <summary>The neighbour's published numbers (<see cref="World.Published"/>), as of the start of its month.</summary>
    public int[] Stats = Array.Empty<int>();
    /// <summary>Edge cells where the neighbour has a building on fire, right at the border.</summary>
    public bool[] Fire = Array.Empty<bool>();
    /// <summary>Equal signatures, equal edges: the region sends an edge again only when it changed.</summary>
    public string Sig = "";
}

/// <summary>
/// The region tells a city what its neighbour shows across one edge (0 north, 1 east, 2 south, 3 west), as of the
/// neighbour's previous day. Paths are pairs of (cell, profile index + 1); Power and Pipes list cells. An empty Name
/// means no city there (open land, or the region's edge). Fire lists cells burning at the border; Movers are
/// disasters walking over it into this city, five numbers each (kind, cell, dx, dy, days left). Logged, so a city's
/// log replays it without its neighbours.
/// </summary>
public sealed record NeighbourEdgeAction(int Side, string Name, string Mayor, int[] Paths, int[] Power, int[] Pipes, int[]? Stats = null,
    int[]? Fire = null, int[]? Movers = null) : SimAction;

public sealed partial class World
{
    /// <summary>The neighbouring cities, by side (0 north, 1 east, 2 south, 3 west); null where there's none.</summary>
    public readonly NeighbourEdge?[] NextDoor = new NeighbourEdge?[4];
    /// <summary>Vehicles an hour that cross each side into a working connection. Derived with <see cref="EdgeCapacity"/>.</summary>
    public readonly int[] SideCapacity = new int[4];

    public static int Opposite(int side) => (side + 2) % 4;

    /// <summary>Cells along a side.</summary>
    public int SideLength(int side) => side is 0 or 2 ? Width : HeightCells;

    /// <summary>The cell index of position k along a side.</summary>
    public int SideCell(int side, int k) => side switch
    {
        0 => Idx(k, 0),
        1 => Idx(Width - 1, k),
        2 => Idx(k, HeightCells - 1),
        _ => Idx(0, k),
    };

    /// <summary>The side a border cell lies on and its place along it, or (-1, -1) for an inner cell or a corner.</summary>
    public (int Side, int K) SideOf(int i)
    {
        int x = i % Width, y = i / Width;
        bool w = x == 0, e = x == Width - 1, n = y == 0, s = y == HeightCells - 1;
        if ((w || e) && (n || s)) return (-1, -1);
        return n ? (0, x) : e ? (1, y) : s ? (2, x) : w ? (3, y) : (-1, -1);
    }

    /// <summary>What this city shows its neighbour across a side: the action the region sends that neighbour.</summary>
    public NeighbourEdgeAction EdgeOut(int side)
    {
        var paths = new List<int>();
        var power = new List<int>();
        var pipes = new List<int>();
        var fire = new List<int>();
        int inward = side switch { 0 => Width, 1 => -1, 2 => -Width, _ => 1 };
        for (int k = 0; k < SideLength(side); k++)
        {
            int i = SideCell(side, k);
            if (Path[i] != 0) { paths.Add(k); paths.Add(Path[i]); }
            if (PowerLine[i] != 0) power.Add(k);
            if (WaterPipe[i] != 0) pipes.Add(k);
            if (BuildingAtCell(i) is { BurningDays: > 0 } || BuildingAtCell(i + inward) is { BurningDays: > 0 }) fire.Add(k);
        }
        var movers = new List<int>();
        foreach (var d in Departures.Where(d => d.Side == side))
            movers.AddRange(new[] { (int)d.Kind, d.K, d.Dx, d.Dy, d.DaysLeft });
        // The neighbour hears of this city across its own opposite side.
        return new NeighbourEdgeAction(Opposite(side), Name, MayorSeat.Owner, paths.ToArray(), power.ToArray(), pipes.ToArray(), (int[])Published.Clone(),
            fire.ToArray(), movers.ToArray());
    }

    static string EdgeSig(NeighbourEdgeAction a) =>
        $"{a.Name}|{a.Mayor}|{string.Join(",", a.Paths)}|{string.Join(",", a.Power)}|{string.Join(",", a.Pipes)}|{string.Join(",", a.Stats ?? Array.Empty<int>())}" +
        $"|{string.Join(",", a.Fire ?? Array.Empty<int>())}|{string.Join(",", a.Movers ?? Array.Empty<int>())}";

    /// <summary>True when this action would change what the city knows of that side.</summary>
    public bool EdgeDiffers(NeighbourEdgeAction a) =>
        a.Name.Length == 0 ? NextDoor[a.Side] != null : NextDoor[a.Side]?.Sig != EdgeSig(a);

    void ApplyNeighbourEdge(NeighbourEdgeAction a)
    {
        if (a.Name.Length == 0) { NextDoor[a.Side] = null; _edgeDirty = true; return; }
        int len = SideLength(a.Side);
        var e = new NeighbourEdge { Name = a.Name, Mayor = a.Mayor, Path = new ushort[len], Power = new bool[len], Pipe = new bool[len], Fire = new bool[len], Sig = EdgeSig(a), Stats = (int[]?)a.Stats?.Clone() ?? Array.Empty<int>() };
        foreach (int k in a.Fire ?? Array.Empty<int>()) if (k >= 0 && k < len) e.Fire[k] = true;
        for (int j = 0; j + 4 < (a.Movers?.Length ?? 0); j += 5)
            Arrive(a.Side, a.Name, (DisasterKind)a.Movers![j], a.Movers[j + 1], a.Movers[j + 2], a.Movers[j + 3], a.Movers[j + 4]);
        for (int j = 0; j + 1 < a.Paths.Length; j += 2)
            if (a.Paths[j] >= 0 && a.Paths[j] < len) e.Path[a.Paths[j]] = (ushort)a.Paths[j + 1];
        foreach (int k in a.Power) if (k >= 0 && k < len) e.Power[k] = true;
        foreach (int k in a.Pipes) if (k >= 0 && k < len) e.Pipe[k] = true;
        NextDoor[a.Side] = e;
        _edgeDirty = true;
    }

    /// <summary>
    /// Whether a path leaving the map at this border cell is a working connection: always, toward open land or past
    /// the region's edge (the wider world takes it); toward a neighbouring city, only where its side has a path too.
    /// </summary>
    bool CrossingMeets(int i)
    {
        var (side, k) = SideOf(i);
        if (side < 0) return true;
        var n = NextDoor[side];
        return n == null || n.Path[k] != 0;
    }

    /// <summary>Paths that reach a side where the neighbour has none: half a connection, for the panels to point out.</summary>
    public List<(int Side, int K)> UnmetCrossings()
    {
        var list = new List<(int, int)>();
        for (int side = 0; side < 4; side++)
        {
            if (NextDoor[side] is not { } n) continue;
            for (int k = 1; k < SideLength(side) - 1; k++)
                if (Path[SideCell(side, k)] != 0 && n.Path[k] == 0) list.Add((side, k));
        }
        return list;
    }

    void WriteNeighbours(BinaryWriter w)
    {
        for (int s = 0; s < 4; s++)
        {
            var n = NextDoor[s];
            w.Write(n != null);
            if (n == null) continue;
            w.Write(n.Name); w.Write(n.Mayor); w.Write(n.Sig);
            foreach (var v in n.Path) w.Write(v);
            foreach (var v in n.Power) w.Write(v);
            foreach (var v in n.Pipe) w.Write(v);
            foreach (var v in n.Fire) w.Write(v);
            w.Write(n.Stats.Length);
            foreach (var v in n.Stats) w.Write(v);
        }
        foreach (var c in SideCapacity) w.Write(c);
        WriteFlows(w);
    }

    void ReadNeighbours(BinaryReader r)
    {
        for (int s = 0; s < 4; s++)
        {
            if (!r.ReadBoolean()) continue;
            int len = SideLength(s);
            var n = new NeighbourEdge { Name = r.ReadString(), Mayor = r.ReadString(), Sig = r.ReadString(), Path = new ushort[len], Power = new bool[len], Pipe = new bool[len], Fire = new bool[len] };
            for (int k = 0; k < len; k++) n.Path[k] = r.ReadUInt16();
            for (int k = 0; k < len; k++) n.Power[k] = r.ReadBoolean();
            for (int k = 0; k < len; k++) n.Pipe[k] = r.ReadBoolean();
            for (int k = 0; k < len; k++) n.Fire[k] = r.ReadBoolean();
            n.Stats = new int[r.ReadInt32()];
            for (int k = 0; k < n.Stats.Length; k++) n.Stats[k] = r.ReadInt32();
            NextDoor[s] = n;
        }
        for (int s = 0; s < 4; s++) SideCapacity[s] = r.ReadInt32();
        ReadFlows(r);
    }
}
