namespace ToyCity.Sim;

/// <summary>
/// How much a river carries and how high its water stands (river depth from the month's rain): shared with the client
/// model, which draws the water and the floods by the same sums as the sim damages buildings by.
/// </summary>
public static class RiverFlow
{
    /// <summary>A height step in decimetres (as World.StepDm).</summary>
    const int StepDm = 20;
    /// <summary>How far, in cells, floodwater spreads from a river over ground low enough to take it.</summary>
    public const int Reach = 5;
    /// <summary>The least and most a river carries, as a share of its usual flow: a drought's trickle to a great flood.</summary>
    public const int Least = 25, Most = 600;

    /// <summary>The flood stage a flow brings: in its banks under twice the usual, up to 3 at four and a half times.</summary>
    public static int StageOf(int pct) => pct >= 450 ? 3 : pct >= 300 ? 2 : pct >= 200 ? 1 : 0;
    /// <summary>The flow a stage stands for, for logs and saves from before flows.</summary>
    public static int OfStage(int stage) => stage switch { <= 0 => 100, 1 => 230, 2 => 350, _ => 500 };

    /// <summary>
    /// How big the water is around each cell: the cells of river, pond or lake within two cells (up to 25), 0 where
    /// there is none. A wide river runs deeper than a stream, and rises further in a flood.
    /// </summary>
    public static byte[] Sizes(int width, int height, byte[] ground, byte[] river)
    {
        int n = width * height;
        var wet = new byte[n];
        for (int i = 0; i < n; i++) wet[i] = (byte)(river[i] > ground[i] ? 1 : 0);
        var size = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (wet[i] == 0) continue;
            int x = i % width, y = i / width, k = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height) k += wet[ny * width + nx];
                }
            size[i] = (byte)k;
        }
        return size;
    }

    /// <summary>A river's usual depth in decimetres, from how big it is: a metre for a stream, up to three for a wide river.</summary>
    public static int UsualDepthDm(int size) => Math.Clamp(8 + size, 10, 30);

    /// <summary>
    /// How far a river's water stands over (or under) its usual level at a flow, in decimetres: its depth goes as the
    /// flow to the 0.6 (as a channel's does), so twice the flow is half as deep again and a drought leaves it shallow.
    /// </summary>
    public static int RiseDm(int size, int flowPct) =>
        (int)MathF.Round(UsualDepthDm(size) * (MathF.Pow(Math.Clamp(flowPct, Least, Most) / 100f, 0.6f) - 1f));

    /// <summary>
    /// A river cell's water surface in decimetres at a flow: its usual level (a third of a step under its surface
    /// step) and the rise, never down to its bed.
    /// </summary>
    public static int WaterDm(int river, int size, int flowPct) =>
        Math.Max((river - 1) * StepDm + 3, river * StepDm - 7 + RiseDm(size, flowPct));

    /// <summary>
    /// The floodwater for a map at a flow (the client works it out the same way from what it's sent): none in its
    /// banks (under twice the usual flow); otherwise from each river cell outward over dry land, at most
    /// <see cref="Reach"/> cells, onto every cell whose ground lies under the raised water, through cells that are
    /// themselves under it. Each flooded cell's surface is the step its water reaches.
    /// </summary>
    public static byte[] Flooded(int width, int height, byte[] ground, byte[] river, int waterLevel, int flowPct, byte[]? dams = null)
    {
        int n = width * height;
        var water = new byte[n];
        if (StageOf(flowPct) <= 0) return water;
        var size = Sizes(width, height, ground, river);
        var held = dams == null ? null : Held(width, height, ground, river, dams);
        var dm = new int[n];
        var dist = new int[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (river[i] > ground[i])
            {
                dm[i] = WaterDm(river[i], size[i], held == null ? flowPct : FlowAt(flowPct, held[i]));
                queue.Enqueue(i);
            }
        ReadOnlySpan<int> dx = [0, 1, 0, -1], dy = [-1, 0, 1, 0];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (dist[c] >= Reach) continue;
            int x = c % width, y = c / width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int j = ny * width + nx;
                // Ground most of a cell under the water (its step a third of one under it) takes it.
                if (dm[j] != 0 || ground[j] < waterLevel || river[j] > ground[j] || ground[j] * StepDm + 7 > dm[c]) continue;
                dm[j] = dm[c];
                dist[j] = dist[c] + 1;
                queue.Enqueue(j);
            }
        }
        // The river cells themselves are rivers, not flood.
        for (int i = 0; i < n; i++)
            if (dm[i] != 0 && river[i] <= ground[i]) water[i] = (byte)Math.Min(255, (dm[i] + StepDm - 1) / StepDm);
        return water;
    }

    /// <summary>A reservoir's water, held at its dam's spillway whatever the rain.</summary>
    public const byte Reservoir = 2;
    /// <summary>A river below a dam: the reservoir evens its flow out.</summary>
    public const byte Regulated = 1;
    /// <summary>The least and most a dam lets through below it, as a share of the usual flow: no floods, no trickle.</summary>
    public const int HeldLeast = 60, HeldMost = 150;

    /// <summary>
    /// What the dams do to each river cell (river brush and dams): <see cref="Reservoir"/> for a reservoir's water
    /// (river cells joined to a dam at its spillway's level, a step under its crest), <see cref="Regulated"/> for the
    /// river below a dam (river cells joined to the dam's foot at its level or lower), 0 for a free river. dams holds
    /// each dam cell's crest in steps, 0 elsewhere.
    /// </summary>
    public static byte[] Held(int width, int height, byte[] ground, byte[] river, byte[] dams)
    {
        int n = width * height;
        var held = new byte[n];
        int[] dx = [0, 1, 0, -1], dy = [-1, 0, 1, 0];
        var queue = new Queue<int>();
        void Spread(int from, byte mark, int level, bool exact)
        {
            queue.Clear();
            queue.Enqueue(from);
            held[from] = Math.Max(held[from], mark);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue(), x = c % width, y = c / width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int j = ny * width + nx;
                    if (held[j] >= mark || dams[j] != 0 || river[j] <= ground[j]) continue;
                    if (exact ? river[j] != level : river[j] > level) continue;
                    held[j] = mark;
                    queue.Enqueue(j);
                }
            }
        }
        // Reservoirs first, so the river below never claims one.
        for (int pass = 0; pass < 2; pass++)
        for (int d = 0; d < n; d++)
        {
            if (dams[d] == 0) continue;
            int x = d % width, y = d / width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                int j = ny * width + nx;
                if (dams[j] != 0 || river[j] <= ground[j]) continue;
                bool pool = river[j] == dams[d] - 1;
                if (pass == 0 && pool && held[j] != Reservoir) Spread(j, Reservoir, river[j], true);
                if (pass == 1 && !pool && held[j] == 0) Spread(j, Regulated, river[j], false);
            }
        }
        return held;
    }

    /// <summary>The flow a river cell runs at, given what the dams do to it.</summary>
    public static int FlowAt(int flowPct, byte held) =>
        held == Reservoir ? 100 : held == Regulated ? Math.Clamp(flowPct, HeldLeast, HeldMost) : flowPct;
}
