namespace ToyCity.Sim;

/// <summary>
/// An arc or diagonal as the player drew it (part 9): the line itself, and the grid cells the sim gave it. Traffic,
/// zoning and everything else still use the cells; clients draw the line, SC4-style, over them.
/// </summary>
public sealed class RoadStrip
{
    /// <summary>The path profile's code on its cells (profile index + 1).</summary>
    public ushort Code;
    /// <summary>An arc bent toward (CX, CY); otherwise a straight line from end to end.</summary>
    public bool Curved;
    public int X0, Y0, X1, Y1, CX, CY;
    /// <summary>Its cells, as indexes, in order from (X0, Y0).</summary>
    public int[] Cells = Array.Empty<int>();
}

public sealed partial class World
{
    /// <summary>The arcs and diagonals laid in this city that are still whole.</summary>
    public readonly List<RoadStrip> Strips = new();
    /// <summary>Goes up whenever <see cref="Strips"/> changes, so the server knows to send them again. Not saved.</summary>
    public int StripsVersion { get; private set; }

    /// <summary>Remembers the line of an arc or diagonal just laid; a straight or L-shaped run needs nothing.</summary>
    void AddStrip(PlacePathAction a, ushort code, List<(int X, int Y)> cells)
    {
        if (!a.Curved && !(a.Diagonal && a.X0 != a.X1 && a.Y0 != a.Y1)) return;
        if (cells.Count < 3) return;
        var idx = cells.Select(c => Idx(c.X, c.Y)).ToArray();
        // The same line laid again replaces the old one.
        Strips.RemoveAll(s => s.Cells.AsSpan().SequenceEqual(idx));
        Strips.Add(new RoadStrip { Code = code, Curved = a.Curved, X0 = a.X0, Y0 = a.Y0, X1 = a.X1, Y1 = a.Y1, CX = a.CX, CY = a.CY, Cells = idx });
        StripsVersion++;
    }

    /// <summary>
    /// Forgets strips that are no longer whole: one of their cells was bulldozed or laid over with another kind of
    /// path. What's left of them stays as ordinary path cells.
    /// </summary>
    void CheckStrips()
    {
        if (Strips.RemoveAll(s => s.Cells.Any(i => Path[i] != s.Code)) > 0) StripsVersion++;
    }

    void WriteStrips(BinaryWriter w)
    {
        w.Write(Strips.Count);
        foreach (var s in Strips)
        {
            w.Write(s.Code); w.Write(s.Curved);
            w.Write(s.X0); w.Write(s.Y0); w.Write(s.X1); w.Write(s.Y1); w.Write(s.CX); w.Write(s.CY);
            w.Write(s.Cells.Length);
            foreach (var c in s.Cells) w.Write(c);
        }
    }

    void ReadStrips(BinaryReader r)
    {
        Strips.Clear();
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++)
        {
            var s = new RoadStrip
            {
                Code = r.ReadUInt16(), Curved = r.ReadBoolean(),
                X0 = r.ReadInt32(), Y0 = r.ReadInt32(), X1 = r.ReadInt32(), Y1 = r.ReadInt32(), CX = r.ReadInt32(), CY = r.ReadInt32(),
            };
            s.Cells = new int[r.ReadInt32()];
            for (int c = 0; c < s.Cells.Length; c++) s.Cells[c] = r.ReadInt32();
            Strips.Add(s);
        }
        StripsVersion++;
    }
}
