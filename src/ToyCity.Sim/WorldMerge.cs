namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>While set, the per-cell layers <see cref="Snapshot"/> writes, in the order it writes them.</summary>
    List<Array>? _layers;

    /// <summary>
    /// Another city on a map the same size as this one, whose part of it (the rectangle) <see cref="Snapshot"/> takes
    /// in place of this city's own while <see cref="Merged"/> writes it out; null otherwise.
    /// </summary>
    sealed class Merge(World other, List<Array> layers, int x0, int y0, int w, int h, int buildingOffset)
    {
        public readonly World Other = other;
        public readonly List<Array> Layers = layers;
        public readonly int X0 = x0, Y0 = y0, W = w, H = h, BuildingOffset = buildingOffset;
        public int Next;
    }
    Merge? _merge;

    /// <summary>A per-cell layer as the snapshot writes it: noted while recording, and with the other city's part when merging.</summary>
    T[] Layer<T>(T[] a, bool corners)
    {
        _layers?.Add(a);
        if (_merge is not { } m) return a;
        var theirs = (T[])m.Layers[m.Next++];
        var b = (T[])a.Clone();
        if (!corners)
        {
            for (int y = m.Y0; y < m.Y0 + m.H; y++) Array.Copy(theirs, y * Width + m.X0, b, y * Width + m.X0, m.W);
            // Their buildings come after this city's.
            if (ReferenceEquals(a, BuildingAt))
            {
                var at = (int[])(object)b;
                for (int y = m.Y0; y < m.Y0 + m.H; y++)
                for (int x = m.X0; x < m.X0 + m.W; x++)
                    if (at[y * Width + x] > 0) at[y * Width + x] += m.BuildingOffset;
            }
            return b;
        }
        // Corners: theirs, except along the seam where the two cities meet, which stays this city's (its buildings
        // there stand on it).
        int cw = Width + 1;
        for (int y = m.Y0; y <= m.Y0 + m.H; y++)
        for (int x = m.X0; x <= m.X0 + m.W; x++)
        {
            bool seam = (x == m.X0 && x > 0) || (x == m.X0 + m.W && x < Width) || (y == m.Y0 && y > 0) || (y == m.Y0 + m.H && y < HeightCells);
            if (!seam) b[y * cw + x] = theirs[y * cw + x];
        }
        return b;
    }

    /// <summary>
    /// This city and another as one (Beta 4, part 6: a city absorbs its neighbour). Both are on maps of the same size
    /// already (<see cref="Resized"/>), each where it will stand; the other's part is the rectangle at
    /// <paramref name="x0"/>, <paramref name="y0"/>. The ground, roads, zones, pipes and the rest of the map come from
    /// whichever city had that cell; the other's buildings join with their people and jobs, its treasury and debt with
    /// this one's. Its side games' lines and units don't come: this city's go on. Both worlds are spent after.
    /// </summary>
    public World Merged(World other, int x0, int y0, int w, int h)
    {
        if (other.Width != Width || other.HeightCells != HeightCells) throw new InvalidOperationException("Both cities have to be on maps the same size.");
        ApplyPending();
        other.ApplyPending();
        other._layers = new List<Array>();
        other.Snapshot();
        var layers = other._layers;
        other._layers = null;
        _merge = new Merge(other, layers, x0, y0, w, h, Buildings.Count);
        try
        {
            var bytes = Snapshot();
            if (_merge.Next != layers.Count) throw new InvalidOperationException("The two cities' maps didn't line up.");
            var m = FromSnapshot(Content, bytes);
            m.Log.AddRange(Log);
            return m;
        }
        finally { _merge = null; }
    }

    /// <summary>The buildings the snapshot writes: this city's, then the other's when merging.</summary>
    IEnumerable<(Building? B, int Offset, bool Theirs)> SnapshotBuildings()
    {
        foreach (var b in Buildings) yield return (b, 0, false);
        if (_merge is { } m) foreach (var b in m.Other.Buildings) yield return (b, m.BuildingOffset, true);
    }

    int SnapshotBuildingCount => Buildings.Count + (_merge?.Other.Buildings.Count ?? 0);

    /// <summary>The road strips the snapshot writes: this city's, then the other's when merging.</summary>
    IEnumerable<RoadStrip> SnapshotStrips => _merge is { } m ? Strips.Concat(m.Other.Strips) : Strips;

    /// <summary>The city pays for its neighbour, and hears of it.</summary>
    public void PayForCity(long price, string them, string seller, int cols, int rows)
    {
        Funds -= price;
        string text = $"{Name} took in {them} for ${price:N0}, paid to {seller}: the city is {cols} × {rows} tiles now.";
        Notices.Add(new SimNotice(text, "info"));
        AddHeadline(text, "good");
    }

    /// <summary>
    /// What a computer mayor asks to sell its city: what's built there and its money, more for a temperament that
    /// would rather grow than sell.
    /// </summary>
    public long AskingPrice(int tiles, long perTile)
    {
        long built = Buildings.Count(b => b != null) * 400L + Population * 40L + Math.Max(0, Funds - Debt);
        double mood = MayorSeat.Style switch
        {
            AutopilotStyle.Growth => 1.5,
            AutopilotStyle.Green => 1.25,
            AutopilotStyle.Profit => 1.0,
            _ => 1.75,
        };
        return Math.Max(perTile * tiles, (long)(built * mood) / 1000 * 1000);
    }
}
