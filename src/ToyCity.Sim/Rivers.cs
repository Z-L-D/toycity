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
    /// <param name="channels">How the rivers are shaped (<see cref="ChannelsVersion"/>): 0 the old one-cell cut, 1
    /// channels with banks, 2 with meanders off the grid. A region saved before keeps its old rivers.</param>
    public static Ground GenerateGround(ulong seed, int width, int heightCells, int waterLevel = 8, int channels = ChannelsVersion)
    {
        var corners = GenerateRawCorners(seed, width, heightCells, waterLevel);
        var downstream = new int[width * heightCells];
        var rivers = CarveRivers(corners, width, heightCells, waterLevel, downstream, channels: channels, seed: seed);
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
    /// <param name="edgeOutlet">For a growing region's tile: whether water may run off the map at this edge cell (not
    /// where it meets ground already made). Null: everywhere on the edge.</param>
    /// <param name="inflow">For a growing region's tile: cells a river comes into from a neighbour, and how many cells'
    /// water it brings.</param>
    /// <param name="start">For a growing region's tile: the river's surface where it comes in, which it can't rise above.</param>
    public static byte[] CarveRivers(ushort[] corners, int width, int heightCells, int waterLevel, int[]? downstream = null,
        Func<int, bool>? edgeOutlet = null, int[]? inflow = null, byte[]? start = null, int channels = ChannelsVersion,
        ulong seed = 0, int originX = 0, int originY = 0)
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
            bool edge = x == 0 || y == 0 || x == width - 1 || y == heightCells - 1;
            if (step[i] < waterLevel || edge && (edgeOutlet == null || edgeOutlet(i)))
            {
                receiver[i] = -1;
                filled[i] = dm[i];
                queue.Enqueue(i, (dm[i], i));
            }
        }
        // Ground closed in on every side with no sea: it drains to its lowest cell, a lake.
        if (queue.Count == 0)
        {
            int low = 0;
            for (int i = 1; i < n; i++) if (dm[i] < dm[low]) low = i;
            receiver[low] = -1;
            filled[low] = dm[low];
            queue.Enqueue(low, (dm[low], low));
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
            acc[c] += 1 + (inflow?[c] ?? 0);
            if (receiver[c] >= 0) acc[receiver[c]] += acc[c];
        }

        // 3. The surface: down the river, never above the ground nor above where it came from.
        var surface = start != null ? (byte[])start.Clone() : new byte[n];
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

        // 4. Cut the channels: every corner of a river cell down to its bed. Meandering channels (version 2) are cut
        // along their own line instead, so the grid's trench doesn't show under them.
        for (int i = 0; i < n && channels < 2; i++)
        {
            if (surface[i] == 0) continue;
            int bed = Math.Max(0, surface[i] - 1) * StepDm;
            int x = i % width, y = i / width, c = y * cw + x;
            foreach (int k in (ReadOnlySpan<int>)[c, c + 1, c + cw, c + cw + 1])
                corners[k] = (ushort)Math.Min(corners[k], bed);
        }
        // 5. Shape them: banks along the river's smoothed line, wider downstream.
        if (channels > 0) Channels(corners, width, heightCells, waterLevel, surface, receiver, acc, downstream, channels >= 2, seed, originX, originY);
        return surface;
    }

    /// <summary>How far a river's banks reach past its water, in cells.</summary>
    public const float BankReach = 2f;
    /// <summary>How new regions' rivers are shaped: 2, channels with banks that meander off the grid.</summary>
    public const int ChannelsVersion = 2;

    /// <summary>
    /// A river's channel as a creek is in the Flint Hills project: a smoothed line down the valley with a width that
    /// grows with the water it carries, a bed, and banks that slope up from the water to the ground. The cells of the
    /// drainage step down the valley a cell at a time, in a staircase; the line through them is smoothed over a few
    /// cells each way, and the ground is cut to the channel's cross-section by how far each corner is from it.
    /// Cells the channel puts under water become the river's too, flowing on with the river cell they lie beside.
    /// It only ever cuts.
    /// </summary>
    static void Channels(ushort[] corners, int width, int heightCells, int waterLevel, byte[] surface, int[] receiver, int[] acc, int[]? downstream,
        bool meander = false, ulong seed = 0, int originX = 0, int originY = 0)
    {
        int n = width * heightCells, cw = width + 1;
        var river = new List<int>();
        for (int i = 0; i < n; i++) if (surface[i] != 0) river.Add(i);
        if (river.Count == 0) return;

        // The main stem upstream of each river cell: the river cell flowing into it that carries the most.
        var up = new int[n];
        Array.Fill(up, -1);
        foreach (int c in river)
        {
            int r = receiver[c];
            if (r >= 0 && surface[r] != 0 && (up[r] < 0 || acc[c] > acc[up[r]] || acc[c] == acc[up[r]] && c < up[r])) up[r] = c;
        }
        // The smoothed line: each river cell's centre averaged with the cells three up and three down its river.
        ReadOnlySpan<float> weight = [1f, 2f, 3f, 4f];
        var px = new float[n];
        var py = new float[n];
        foreach (int c in river)
        {
            float sx = (c % width + 0.5f) * weight[3], sy = (c / width + 0.5f) * weight[3], sw = weight[3];
            int d = c, u = c;
            for (int k = 2; k >= 0; k--)
            {
                d = d >= 0 && receiver[d] >= 0 && surface[receiver[d]] != 0 ? receiver[d] : -1;
                u = u >= 0 ? up[u] : -1;
                if (d >= 0) { sx += (d % width + 0.5f) * weight[k]; sy += (d / width + 0.5f) * weight[k]; sw += weight[k]; }
                if (u >= 0) { sx += (u % width + 0.5f) * weight[k]; sy += (u / width + 0.5f) * weight[k]; sw += weight[k]; }
            }
            px[c] = sx / sw;
            py[c] = sy / sw;
        }
        // Meanders: the line wanders to either side of the drainage cells, by slow noise over the map (in the region's
        // own cells, so a river runs on smoothly into the next tile), and each bank's width wobbles on its own.
        var wobble = new float[n];
        if (meander)
        {
            var mx = new float[n];
            var my = new float[n];
            foreach (int c in river)
            {
                int d = receiver[c] >= 0 && surface[receiver[c]] != 0 ? receiver[c] : -1, u = up[c];
                float ax = u >= 0 ? px[u] : px[c], ay = u >= 0 ? py[u] : py[c];
                float bx = d >= 0 ? px[d] : px[c], by = d >= 0 ? py[d] : py[c];
                float tx = bx - ax, ty = by - ay, tl = MathF.Sqrt(tx * tx + ty * ty);
                float hw = HalfWidth(acc[c]);
                float gx = originX + px[c], gy = originY + py[c];
                wobble[c] = 0.85f + 0.3f * SmoothNoise(seed ^ 0x5EEDu, gx / 3.5f, gy / 3.5f);
                if (tl < 1e-3f) { mx[c] = px[c]; my[c] = py[c]; continue; }
                // Up to half the channel's width, so the drainage cell stays under the water, and less at the sea.
                float off = (SmoothNoise(seed ^ 0xBE4Du, gx / 7f, gy / 7f) * 2 - 1) * MathF.Min(0.5f * hw + 0.15f, 0.9f);
                mx[c] = px[c] - ty / tl * off;
                my[c] = py[c] + tx / tl * off;
            }
            foreach (int c in river) { px[c] = mx[c]; py[c] = my[c]; }
        }

        // Cut each stretch of the line, from one river cell to the next, into the corners near it; and note for each
        // cell the stretch nearest its middle.
        var cut = new int[corners.Length];
        Array.Fill(cut, int.MaxValue);
        var near = new float[n];
        Array.Fill(near, float.MaxValue);
        var owner = new int[n];
        Array.Fill(owner, -1);
        var level = new float[n];
        foreach (int c in river)
        {
            int d = receiver[c];
            float ax = px[c], ay = py[c], bx, by;
            if (d >= 0 && surface[d] != 0) (bx, by) = (px[d], py[d]);
            else if (d >= 0) (bx, by) = (d % width + 0.5f, d / width + 0.5f);  // into the sea
            else (bx, by) = (ax, ay);
            float hw = HalfWidth(acc[c]) * (meander ? wobble[c] : 1f);
            // The surface along the stretch, from this cell's to the next's (where it steps down a rapid).
            float sa = surface[c], sb = d >= 0 && surface[d] != 0 ? surface[d] : surface[c];
            float reach = hw + BankReach;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, bx) - reach)), x1 = Math.Min(width, (int)MathF.Ceiling(MathF.Max(ax, bx) + reach));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, by) - reach)), y1 = Math.Min(heightCells, (int)MathF.Ceiling(MathF.Max(ay, by) + reach));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float t = Distance(x, y, ax, ay, bx, by, out float along);
                    if (t >= reach) continue;
                    float sf = sa + (sb - sa) * along;
                    float water = sf * StepDm - 0.35f * StepDm, bed = (sf - 1) * StepDm;
                    // The cross-section: the bed across the middle half, rising to just under the water at the
                    // channel's edge; a cut bank a step high over the next half cell (so a bridge's deck still
                    // meets a road along the bank); then the bank, a step up for each cell and a quarter out.
                    float h = t <= hw * 0.5f ? bed
                        : t <= hw ? bed + (water - 2 - bed) * (t - hw * 0.5f) / (hw * 0.5f)
                        : t <= hw + 0.5f ? water - 2 + (StepDm + 2) * (t - hw) / 0.5f
                        : water + StepDm + (t - hw - 0.5f) * StepDm / 1.25f;
                    int k = y * cw + x;
                    cut[k] = Math.Min(cut[k], (int)h);
                    if (x < width && y < heightCells)
                    {
                        float m = Distance(x + 0.5f, y + 0.5f, ax, ay, bx, by, out float mu);
                        int i = y * width + x;
                        if (m < near[i] || m == near[i] && c < owner[i]) { near[i] = m; owner[i] = c; level[i] = sa + (sb - sa) * mu; }
                    }
                }
        }
        // The cells that were sea or lake before the cut stay so; the rest of the cut ground becomes the river's.
        var wasWater = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int k = i / width * cw + i % width;
            wasWater[i] = StepOf(corners[k], corners[k + 1], corners[k + cw + 1], corners[k + cw]) < waterLevel;
        }
        for (int k = 0; k < corners.Length; k++)
            if (cut[k] < corners[k]) corners[k] = (ushort)Math.Max(0, cut[k]);
        // A drainage cell the meandering line left on its bank still carries the river: cut it to its bed, as the
        // old channels were.
        if (meander)
            foreach (int i in river)
            {
                int k = i / width * cw + i % width;
                if (StepOf(corners[k], corners[k + 1], corners[k + cw + 1], corners[k + cw]) < surface[i]) continue;
                int bed = Math.Max(0, surface[i] - 1) * StepDm;
                foreach (int q in (ReadOnlySpan<int>)[k, k + 1, k + cw, k + cw + 1]) corners[q] = (ushort)Math.Min(corners[q], bed);
            }

        // Cells now under the channel's water are the river's, at the river's level beside them, and shallow: a step
        // deep at most.
        for (int i = 0; i < n; i++)
        {
            int c = owner[i];
            if (c < 0 || surface[i] != 0 || wasWater[i]) continue;
            int x = i % width, y = i / width, k = y * cw + x;
            int step = StepOf(corners[k], corners[k + 1], corners[k + cw + 1], corners[k + cw]);
            int s = Math.Min((int)MathF.Round(level[i]), step + 1);
            // Ground cut under the sea's level is the river's too, however shallow, or it would read as a lake no
            // path could bridge.
            if (step >= s && step >= waterLevel) continue;
            s = Math.Max(s, step + 1);
            // It flows on into the river beside it: the nearest of the next few cells down from there no higher than
            // its own water, the furthest down of those as near, so it doesn't run back upstream. Failing one of
            // those (a cell cut deeper than the rapid beside it), the nearest of them, and its water stands at that
            // cell's level.
            int to = -1, fallback = -1, hops = 0;
            float best = float.MaxValue, nearest = float.MaxValue;
            for (int at = c; at >= 0 && hops < 64; at = receiver[at] >= 0 && surface[receiver[at]] != 0 ? receiver[at] : -1, hops++)
            {
                float ddx = at % width - x, ddy = at / width - y, dd = ddx * ddx + ddy * ddy;
                if (dd < nearest) { nearest = dd; fallback = at; }
                if (surface[at] > s) continue;
                if (dd <= best) { best = dd; to = at; }
                else if (to >= 0) break;
            }
            if (to < 0) { to = fallback; s = Math.Max(s, surface[to]); }
            surface[i] = (byte)s;
            if (downstream != null) downstream[i] = to;
        }
    }

    /// <summary>A channel's half-width in cells from the cells draining into it: ¾ of a cell for a stream, up to 2.</summary>
    static float HalfWidth(int acc) => Math.Clamp(0.45f + 0.3f * MathF.Sqrt(acc / (float)RiverCatchment), 0.75f, 2f);

    /// <summary>Smooth value noise in 0..1 at a point, from a seed: the same everywhere for the same seed and point.</summary>
    static float SmoothNoise(ulong seed, float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float u = x - x0, v = y - y0;
        u = u * u * (3 - 2 * u);
        v = v * v * (3 - 2 * v);
        float H(int a, int b)
        {
            ulong h = seed ^ ((ulong)(uint)a * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL);
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return (h >> 40) / (float)(1 << 24);
        }
        return (H(x0, y0) * (1 - u) + H(x0 + 1, y0) * u) * (1 - v) + (H(x0, y0 + 1) * (1 - u) + H(x0 + 1, y0 + 1) * u) * v;
    }

    /// <summary>How far a point is from the stretch of line from a to b, in cells, and how far along it (0 to 1) the nearest point is.</summary>
    static float Distance(float x, float y, float ax, float ay, float bx, float by, out float t)
    {
        float vx = bx - ax, vy = by - ay, l = vx * vx + vy * vy;
        t = l > 0 ? Math.Clamp(((x - ax) * vx + (y - ay) * vy) / l, 0f, 1f) : 0f;
        float dx = x - (ax + vx * t), dy = y - (ay + vy * t);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
