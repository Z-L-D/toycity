namespace ToyCity.Sim;

/// <summary>
/// Beta 3.1 part 5: rivers. The generator finds where rain gathers and runs off, and the larger streams become rivers
/// with their own water surface, stepping down to the sea or off the map. A region makes them on its whole map, so a
/// river runs on from one tile into the next.
/// </summary>
public sealed partial class World
{
    /// <summary>How many cells must drain through a cell for it to carry a river.</summary>
    public const int RiverCatchment = 700;

    /// <summary>
    /// The water surface of the river on each cell, in steps, or 0 where there's no river. The river's bed lies a
    /// step under it. Saved.
    /// </summary>
    public byte[] River = Array.Empty<byte>();

    /// <summary>Whether a cell is a river's (or its flood plain's water is on top of it: see <see cref="Flooded"/>).</summary>
    public bool IsRiver(int i) => River[i] > Height[i];

    /// <summary>
    /// The ground and its rivers for a map: the generated corners (see <see cref="GenerateCorners"/>) with the
    /// rivers' channels cut into them, and each cell's river surface.
    /// </summary>
    public static Ground GenerateGround(ulong seed, int width, int heightCells, int waterLevel = 8)
    {
        var corners = GenerateRawCorners(seed, width, heightCells, waterLevel);
        var downstream = new int[width * heightCells];
        var rivers = CarveRivers(corners, width, heightCells, waterLevel, downstream);
        return new Ground(corners, rivers, downstream);
    }

    /// <summary>Generated ground: corners in decimetres, each cell's river surface (0 for none), and for a river
    /// cell the cell it flows on into (-1 off the map or into the sea).</summary>
    public sealed record Ground(ushort[] Corners, byte[] Rivers, int[] Downstream);

    /// <summary>
    /// Finds the rivers on the ground and cuts their channels into the corners. Returns each cell's water surface in
    /// steps (0 for none).
    /// <list type="number">
    /// <item>Water runs off every cell to the neighbour it drains to. Which one is found by flooding inward from the
    /// sea and the map's edges, lowest first, so every cell, even one in a hollow, has a way out.</item>
    /// <item>Each cell counts the cells that drain through it. Where more than <see cref="RiverCatchment"/> do,
    /// it carries a river.</item>
    /// <item>The river's surface follows the ground down and never climbs: where the ground rises across its way
    /// (the rim of a hollow), it cuts a gorge through at the level it reached.</item>
    /// <item>Its bed is a step under the surface; the banks slope down to it.</item>
    /// </list>
    /// </summary>
    public static byte[] CarveRivers(ushort[] corners, int width, int heightCells, int waterLevel, int[]? downstream = null)
    {
        int cw = width + 1, n = width * heightCells;
        var step = StepsFromCorners(corners, width, heightCells);
        var dm = new int[n];
        for (int y = 0; y < heightCells; y++)
            for (int x = 0; x < width; x++)
            {
                int c = y * cw + x;
                dm[y * width + x] = (corners[c] + corners[c + 1] + corners[c + cw] + corners[c + cw + 1]) / 4;
            }

        // 1. Drainage: flood inward from the outlets, lowest first. Ties go by cell, so it's the same every time.
        var receiver = new int[n];
        Array.Fill(receiver, -2);
        var order = new List<int>(n);
        var queue = new PriorityQueue<int, (int, int)>();
        var filled = new int[n];
        for (int i = 0; i < n; i++)
        {
            int x = i % width, y = i / width;
            if (step[i] < waterLevel || x == 0 || y == 0 || x == width - 1 || y == heightCells - 1)
            {
                receiver[i] = -1;
                filled[i] = dm[i];
                queue.Enqueue(i, (dm[i], i));
            }
        }
        ReadOnlySpan<int> dx = [0, 1, 0, -1], dy = [-1, 0, 1, 0];
        while (queue.TryDequeue(out int c, out _))
        {
            order.Add(c);
            int x = c % width, y = c / width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= width || ny >= heightCells) continue;
                int j = ny * width + nx;
                if (receiver[j] != -2) continue;
                receiver[j] = c;
                filled[j] = Math.Max(dm[j], filled[c] + 1);
                queue.Enqueue(j, (filled[j], j));
            }
        }

        // 2. How much drains through each cell, from the heads of the streams down.
        var acc = new int[n];
        for (int k = order.Count - 1; k >= 0; k--)
        {
            int c = order[k];
            acc[c] += 1;
            if (receiver[c] >= 0) acc[receiver[c]] += acc[c];
        }

        // 3. The surface: down the river, never above the ground nor above where it came from.
        var surface = new byte[n];
        for (int k = order.Count - 1; k >= 0; k--)
        {
            int c = order[k];
            if (acc[c] < RiverCatchment || step[c] < waterLevel) continue;
            int s = surface[c] == 0 ? step[c] : Math.Min(surface[c], step[c]);
            s = Math.Max(s, waterLevel);
            surface[c] = (byte)s;
            int r = receiver[c];
            if (r >= 0 && step[r] >= waterLevel)
                surface[r] = surface[r] == 0 ? (byte)Math.Min(s, step[r]) : (byte)Math.Min(surface[r], s);
        }

        if (downstream != null)
            for (int i = 0; i < n; i++) downstream[i] = surface[i] == 0 ? -1 : receiver[i] >= 0 && surface[receiver[i]] != 0 ? receiver[i] : -1;

        // 4. Cut the channels: every corner of a river cell down to its bed.
        for (int i = 0; i < n; i++)
        {
            if (surface[i] == 0) continue;
            int bed = Math.Max(0, surface[i] - 1) * StepDm;
            int x = i % width, y = i / width, c = y * cw + x;
            foreach (int k in (ReadOnlySpan<int>)[c, c + 1, c + cw, c + cw + 1])
                corners[k] = (ushort)Math.Min(corners[k], bed);
        }
        return surface;
    }
}
