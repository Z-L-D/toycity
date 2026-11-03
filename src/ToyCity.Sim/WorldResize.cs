using ToyCity.Contract;

namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>A move of the whole city onto a bigger map, while <see cref="Resized"/> writes it out; null otherwise.</summary>
    sealed record Shift(int OldW, int OldH, int NewW, int NewH, int Dx, int Dy);
    Shift? _shift;

    /// <summary>A cell index, on the map being written.</summary>
    int CI(int i) => _shift is not { } s || i < 0 || i >= N ? i : (i / s.OldW + s.Dy) * s.NewW + i % s.OldW + s.Dx;
    /// <summary>A search state (layer * N + cell), on the map being written.</summary>
    int CL(int v) => _shift is not { } s || v < 0 ? v : v / N * (s.NewW * s.NewH) + CI(v % N);
    /// <summary>An x or y on the map being written; a negative one is "nowhere" and stays so.</summary>
    int CX(int x) => _shift is { } s && x >= 0 ? x + s.Dx : x;
    int CY(int y) => _shift is { } s && y >= 0 ? y + s.Dy : y;

    /// <summary>A district, on the map being written: districts are a grid of 32 cells, and a city grows by whole tiles.</summary>
    int DI(int d)
    {
        if (_shift is not { } s || d < 0 || d >= DistrictCount) return d;
        int across = (s.NewW + DistrictSize - 1) / DistrictSize;
        return (d / DistrictsAcross + s.Dy / DistrictSize) * across + d % DistrictsAcross + s.Dx / DistrictSize;
    }

    /// <summary>A cell index mapped the way <see cref="CI"/> maps it, for a side game moving with its city.</summary>
    static Func<int, int> CellMap(int oldW, int n, int newW, int dx, int dy) => i => i < 0 || i >= n ? i : (i / oldW + dy) * newW + i % oldW + dx;

    // Per-cell layers: as they are, or laid out on the bigger map with the new land empty, or with another city's
    // part of the map taken from it (WorldMerge.cs).
    void WC(BinaryWriter w, byte[] a)
    {
        a = Layer(a, false);
        if (_shift is not { } s) { w.Write(a); return; }
        var b = new byte[s.NewW * s.NewH];
        for (int y = 0; y < s.OldH; y++) Array.Copy(a, y * s.OldW, b, (y + s.Dy) * s.NewW + s.Dx, s.OldW);
        w.Write(b);
    }

    void WC(BinaryWriter w, ushort[] a) { a = Layer(a, false); if (_shift == null) WriteU16(w, a); else WriteU16(w, Lay(a, _shift, false)); }
    void WC(BinaryWriter w, short[] a) { a = Layer(a, false); if (_shift == null) WriteI16(w, a); else WriteI16(w, Lay(a, _shift, false)); }
    void WC(BinaryWriter w, int[] a) { a = Layer(a, false); if (_shift == null) WriteI32(w, a); else WriteI32(w, Lay(a, _shift, false)); }
    /// <summary>A layer of corners, one more each way than cells.</summary>
    void WK(BinaryWriter w, ushort[] a) { a = Layer(a, true); if (_shift == null) WriteU16(w, a); else WriteU16(w, Lay(a, _shift, true)); }

    static T[] Lay<T>(T[] a, Shift s, bool corners)
    {
        int k = corners ? 1 : 0, ow = s.OldW + k, nw = s.NewW + k;
        var b = new T[nw * (s.NewH + k)];
        for (int y = 0; y < s.OldH + k; y++) Array.Copy(a, y * ow, b, (y + s.Dy) * nw + s.Dx, ow);
        return b;
    }

    /// <summary>Why this city can't move onto a bigger map, or null.</summary>
    public string? CantResize(int newW, int newH, int dx, int dy)
    {
        if (dx < 0 || dy < 0 || dx + Width > newW || dy + HeightCells > newH) return "The bigger map has to hold the city as it is.";
        if (dx % DistrictSize != 0 || dy % DistrictSize != 0 || newW % DistrictSize != 0 || newH % DistrictSize != 0) return "A city grows by whole tiles.";
        if ((long)newW * newH > 1024L * 1024) return "That's bigger than any city can be.";
        if (SideGames.FirstOrDefault(g => g.Game is not IRelocatable) is { } hg) return $"{hg.Game.Manifest.Name} is hosted elsewhere and can't follow the city onto a bigger map.";
        return null;
    }

    /// <summary>
    /// This city on a bigger map (Beta 4, part 6: annexing open land): <paramref name="newW"/> by <paramref name="newH"/>
    /// cells, with the city as it stands moved <paramref name="dx"/>, <paramref name="dy"/> cells in from the new map's
    /// corner. Everything it holds moves with it: buildings, roads and their strips, people's trips, side games. The new
    /// land comes empty and flat: put its ground in with <see cref="PlaceGround"/>. Neighbours' edges are dropped, for
    /// the region to tell again. This world is spent: its side games have moved with the new one.
    /// </summary>
    public World Resized(int newW, int newH, int dx, int dy)
    {
        if (CantResize(newW, newH, dx, dy) is { } why) throw new InvalidOperationException(why);
        ApplyPending();
        var cell = CellMap(Width, N, newW, dx, dy);
        var district = (Func<int, int>)DI;
        _shift = new Shift(Width, HeightCells, newW, newH, dx, dy);
        try
        {
            // Districts are read through the shift; side games take the same maps.
            foreach (var hg in SideGames) ((IRelocatable)hg.Game).Relocate(cell, district, dx, dy);
            var bytes = Snapshot();
            var w = FromSnapshot(Content, bytes);
            w.Log.AddRange(Log);
            return w;
        }
        finally { _shift = null; }
    }

    /// <summary>
    /// Lays ground on land this city has just taken on (<see cref="Resized"/>): a tile's corners, rivers and rock, with
    /// its top-left cell at <paramref name="x0"/>, <paramref name="y0"/>. Corners the city already has (its old edge)
    /// are kept, so the ground meets what the city has made of its own.
    /// </summary>
    public void PlaceGround(int x0, int y0, int size, ushort[] corners, byte[] rivers, ushort[] bedrock, byte[] rock, Func<int, int, bool> keepCorner)
    {
        int cw = size + 1, ww = Width + 1;
        for (int y = 0; y <= size; y++)
        for (int x = 0; x <= size; x++)
        {
            if (keepCorner(x0 + x, y0 + y)) continue;
            Corner[(y0 + y) * ww + x0 + x] = corners[y * cw + x];
            Bedrock[(y0 + y) * ww + x0 + x] = bedrock[y * cw + x];
        }
        for (int y = 0; y < size; y++)
        {
            Array.Copy(rivers, y * size, River, (y0 + y) * Width + x0, size);
            Array.Copy(rock, y * size, Rock, (y0 + y) * Width + x0, size);
        }
        DeriveHeights(x0, y0, x0 + size - 1, y0 + size - 1);
    }

    /// <summary>The city pays for land it has annexed, and hears of it.</summary>
    public void PayForLand(long price, int cols, int rows)
    {
        Funds -= price;
        string text = $"{Name} annexed the land beside it for ${price:N0}: the city is {cols} × {rows} tiles now.";
        Notices.Add(new SimNotice(text, "info"));
        AddHeadline(text, "good");
    }

    /// <summary>After the new land's ground is in: water's reach and the city's services worked out again.</summary>
    public void Settle()
    {
        RecomputeWaterDistance();
        RebuildAccess();
        RunUtilities();
        UpdateStats();
    }
}
