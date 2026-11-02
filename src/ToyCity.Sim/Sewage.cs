namespace ToyCity.Sim;

/// <summary>
/// Sewers (Beta 3.2, part 4). Every pipe is a water main with a sewer laid beside it, so one tool lays both and the
/// sewers run wherever the water does. What a building uses it sends back down the sewer. A treatment plant beside
/// the pipes treats what reaches it, up to its capacity; a small town's septic tanks cope with the rest, and past
/// them the raw sewage seeps into the ground and the water near the buildings that made it, cutting the pumps' output
/// and making the place less healthy to live in, as a city outgrows doing without.
/// </summary>
public sealed partial class World
{
    /// <summary>Sewage a day that a town's septic tanks take with no plant: about what one pump supplies, a town of three or four thousand.</summary>
    public const int SepticCapacity = 6000;

    /// <summary>
    /// Per cell, how its sewage is treated: 0 when no sewer with a working plant reaches it, else 1 plus the percent
    /// of its network's sewage that the plants treat (1 to 101).
    /// </summary>
    public readonly byte[] Sewered;

    /// <summary>Treatment capacity, sewage made, and sewage left raw after the plants and the septic tanks, a day.</summary>
    public long SewageCapacity, SewageProduced, SewageUntreated;

    int[]? _sewerComp, _sewerReach;
    bool _sewageNotified;

    /// <summary>A building's sewage: what it uses of water, without the weather's swing.</summary>
    int SewageOf(Building b) => !b.Operating || b.WaterUse == 0 ? 0 : TypeOf(b).Placeable ? b.WaterUse : Scaled(b.WaterUse, ModKey.WaterUse);

    void RunSewage()
    {
        _sewerComp ??= new int[N];
        _sewerReach ??= new int[N];
        int groups = LabelComponents(_sewerComp, i => WaterPipe[i] != 0);
        var capacity = new long[groups];
        var made = new long[groups];

        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating || TypeOf(b).SewageCap == 0 || !b.Powered) continue;
            int g = -1;
            for (int y = b.Y - 1; y <= b.Y + b.H && g < 0; y++)
            for (int x = b.X - 1; x <= b.X + b.W && g < 0; x++)
                if (InBounds(x, y) && _sewerComp[Idx(x, y)] >= 0) g = _sewerComp[Idx(x, y)];
            if (g >= 0) capacity[g] += TypeOf(b).SewageCap * FundingFactor(b.Funding) / 100;
        }
        SewageCapacity = capacity.Sum();

        // Sewers reach the buildings near them as the water mains do.
        Array.Fill(_sewerReach, -1);
        var dist = new byte[N];
        var queue = new Queue<int>();
        for (int i = 0; i < N; i++)
        {
            int g = _sewerComp[i];
            if (g >= 0 && capacity[g] > 0) { _sewerReach[i] = g; queue.Enqueue(i); }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (dist[i] >= PipeReach) continue;
            int x = i % Width, y = i / Width;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (!InBounds(nx, ny)) continue;
                int n = Idx(nx, ny);
                if (_sewerReach[n] >= 0) continue;
                _sewerReach[n] = _sewerReach[i];
                dist[n] = (byte)(dist[i] + 1);
                queue.Enqueue(n);
            }
        }

        // What each network is sent, and so the share its plants treat.
        SewageProduced = 0;
        long unsewered = 0;
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            int s = SewageOf(b);
            if (s == 0) continue;
            SewageProduced += s;
            int g = SewerOf(b);
            if (g >= 0) made[g] += s;
            else unsewered += s;
        }
        long raw = unsewered;
        for (int g = 0; g < groups; g++) raw += Math.Max(0, made[g] - capacity[g]);
        SewageUntreated = Math.Max(0, raw - SepticCapacity);
        for (int i = 0; i < N; i++)
        {
            int g = _sewerReach[i];
            Sewered[i] = g < 0 ? (byte)0 : (byte)(1 + (made[g] <= capacity[g] ? 100 : capacity[g] * 100 / made[g]));
        }

        if (SewageUntreated > 0 && !_sewageNotified)
            Notices.Add(new SimNotice("Raw sewage: the septic tanks can't cope with a town this size. Build a sewage plant beside the pipes.", "warning"));
        _sewageNotified = SewageUntreated > 0;
    }

    /// <summary>
    /// Raw sewage seeps into the water near the buildings that made it: each building's untreated share, scaled by
    /// how much of the city's raw sewage is past what its septic tanks take.
    /// </summary>
    void SpreadSewage()
    {
        if (SewageUntreated <= 0) return;
        long raw = 0;
        foreach (var b in Buildings) if (b != null) raw += RawSewageOf(b);
        if (raw <= 0) return;
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            long r = RawSewageOf(b) * SewageUntreated / raw;
            if (r <= 0) continue;
            int v = (int)Math.Min(40, r / 4 + 1);
            Spread(WaterPollution, b.X, b.Y, b.W, b.H, v, 1 + v / 12, false);
        }
    }

    /// <summary>The sewer network that reaches a building's footprint, as for water; -1 when none does.</summary>
    int SewerOf(Building b)
    {
        int g = -1;
        for (int y = b.Y; y < b.Y + b.H && g < 0; y++)
        for (int x = b.X; x < b.X + b.W && g < 0; x++)
            g = _sewerReach![Idx(x, y)];
        return g;
    }

    /// <summary>A building's sewage that its plants don't treat, a day.</summary>
    public int RawSewageOf(Building b)
    {
        int s = SewageOf(b);
        if (s == 0) return 0;
        int share = 0;
        for (int y = b.Y; y < b.Y + b.H && share == 0; y++)
        for (int x = b.X; x < b.X + b.W && share == 0; x++)
            share = Sewered[Idx(x, y)];
        return share == 0 ? s : s * (101 - share) / 100;
    }

    /// <summary>
    /// What raw sewage costs a neighbourhood's appeal to live in, city-wide like uncollected garbage: a point for each
    /// 150 units a day past the septic tanks, up to 20.
    /// </summary>
    public int SewagePenalty => (int)Math.Min(20, SewageUntreated / 150);
}
