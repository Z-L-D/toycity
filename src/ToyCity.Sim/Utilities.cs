namespace ToyCity.Sim;

public sealed partial class World
{
    public const int PipeReach = 6;

    // Scratch buffers; never part of saved state.
    int[]? _comp;
    int[]? _waterComp;
    readonly HashSet<int> _shortGrids = new();
    bool _powerShortNotified, _waterShortNotified;

    /// <summary>Power flows through lines, buildings, zoned land and streets with frontage (as in SimCity 4), but not highways.</summary>
    bool ConductsPower(int i) => PowerLine[i] != 0 || BuildingAt[i] != 0 || ZoneCode[i] != 0 || ProfileAt(i) is { Frontage: true };

    /// <summary>Labels connected groups of cells with BFS in index order. Returns the number of groups.</summary>
    int LabelComponents(int[] comp, Func<int, bool> member)
    {
        Array.Fill(comp, -1);
        int count = 0;
        var queue = new Queue<int>();
        for (int s = 0; s < N; s++)
        {
            if (comp[s] >= 0 || !member(s)) continue;
            comp[s] = count;
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % Width, y = i / Width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + Dx4[k], ny = y + Dy4[k];
                    if (!InBounds(nx, ny)) continue;
                    int n = Idx(nx, ny);
                    if (comp[n] < 0 && member(n)) { comp[n] = count; queue.Enqueue(n); }
                }
            }
            count++;
        }
        return count;
    }

    static int FundingFactor(int funding) => Math.Min(funding, 100);

    void RunUtilities()
    {
        RebuildAccess();
        RunPower();
        RunWater();
        RunGarbage();
    }

    void RunPower()
    {
        _comp ??= new int[N];
        int groups = LabelComponents(_comp, ConductsPower);
        var capacity = new long[groups];
        var used = new long[groups];
        var demand = new long[groups];

        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (t.PowerOutKw > 0) capacity[_comp[Idx(b.X, b.Y)]] += t.PowerOutKw * FundingFactor(b.Funding) / 100;
        }

        PowerCapacity = capacity.Sum();
        PowerDemand = 0;
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            var t = TypeOf(b);
            int g = _comp[Idx(b.X, b.Y)];
            if (t.PowerOutKw > 0 || b.PowerKw == 0) { b.Powered = true; continue; }
            if (!b.Operating) { b.Powered = capacity[g] > 0; continue; }
            int kw = t.Placeable ? b.PowerKw : Scaled(b.PowerKw, ModKey.PowerUse);
            demand[g] += kw;
            PowerDemand += kw;
            if (capacity[g] - used[g] >= kw) { used[g] += kw; b.Powered = true; }
            else b.Powered = false;
        }

        _shortGrids.Clear();
        for (int g = 0; g < groups; g++) if (capacity[g] > 0 && demand[g] > capacity[g]) _shortGrids.Add(g);
        for (int i = 0; i < N; i++)
        {
            int g = _comp[i];
            Powered[i] = g < 0 || capacity[g] == 0 ? (byte)0 : _shortGrids.Contains(g) ? (byte)2 : (byte)1;
        }

        if (_shortGrids.Count > 0 && !_powerShortNotified)
            Notices.Add(new SimNotice("Brownout: the power grid can't keep up. Build another plant or connect the grids.", "warning"));
        _powerShortNotified = _shortGrids.Count > 0;
    }

    /// <summary>Pump output after funding and the water pollution at the pump.</summary>
    int PumpOutput(Building b)
    {
        var t = TypeOf(b);
        int pollution = WaterPollution[Idx(b.X, b.Y)];
        return t.WaterOut * FundingFactor(b.Funding) / 100 * (255 - pollution) / 255;
    }

    void RunWater()
    {
        _comp ??= new int[N];
        _waterComp ??= new int[N];
        var pipeComp = _comp; // reuse: power labels are no longer needed once RunPower has finished
        int groups = LabelComponents(pipeComp, i => WaterPipe[i] != 0);
        var capacity = new long[groups];
        var used = new long[groups];
        var demand = new long[groups];

        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating || TypeOf(b).WaterOut == 0 || !b.Powered) continue;
            int g = -1;
            for (int y = b.Y - 1; y <= b.Y + b.H && g < 0; y++)
            for (int x = b.X - 1; x <= b.X + b.W && g < 0; x++)
                if (InBounds(x, y) && pipeComp[Idx(x, y)] >= 0) g = pipeComp[Idx(x, y)];
            if (g >= 0) capacity[g] += PumpOutput(b);
        }
        WaterCapacity = capacity.Sum();

        // Each cell takes the network of the nearest pipe (8-way steps, so reach is a square) whose network has a pump.
        Array.Fill(_waterComp, -1);
        var dist = new byte[N];
        var queue = new Queue<int>();
        for (int i = 0; i < N; i++)
        {
            int g = pipeComp[i];
            if (g >= 0 && capacity[g] > 0) { _waterComp[i] = g; queue.Enqueue(i); }
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
                if (_waterComp[n] >= 0) continue;
                _waterComp[n] = _waterComp[i];
                dist[n] = (byte)(dist[i] + 1);
                queue.Enqueue(n);
            }
        }

        WaterDemand = 0;
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            var t = TypeOf(b);
            if (b.WaterUse == 0) { b.Watered = true; continue; }
            int g = -1;
            for (int y = b.Y; y < b.Y + b.H && g < 0; y++)
            for (int x = b.X; x < b.X + b.W && g < 0; x++)
                g = _waterComp[Idx(x, y)];
            if (!b.Operating) { b.Watered = g >= 0; continue; }
            if (g < 0) { b.Watered = false; continue; }
            int use = TypeOf(b).Placeable ? b.WaterUse : Scaled(b.WaterUse, ModKey.WaterUse);
            demand[g] += use;
            WaterDemand += use;
            if (capacity[g] - used[g] >= use) { used[g] += use; b.Watered = true; }
            else b.Watered = false;
        }

        bool anyShort = false;
        for (int i = 0; i < N; i++)
        {
            int g = _waterComp[i];
            if (g < 0) { Watered[i] = 0; continue; }
            bool shortage = demand[g] > capacity[g];
            anyShort |= shortage;
            Watered[i] = shortage ? (byte)2 : (byte)1;
        }
        if (anyShort && !_waterShortNotified)
            Notices.Add(new SimNotice("Water shortage: demand is more than the pumps supply. Add a pump or fund the ones you have.", "warning"));
        _waterShortNotified = anyShort;
    }

    /// <summary>How far a lot may sit from its road, in steps (Manhattan distance from the footprint). 1 = it must touch the road.</summary>
    public const int RoadReach = 1;

    /// <summary>
    /// The nearest path cell with frontage within <see cref="RoadReach"/> of a footprint: the lot's driveway.
    /// Nearest first, then index order. Returns -1 when there is none.
    /// </summary>
    public int FindAccess(int bx, int by, int bw, int bh)
    {
        int best = -1, bestD = int.MaxValue;
        for (int y = Math.Max(0, by - RoadReach); y <= Math.Min(HeightCells - 1, by + bh - 1 + RoadReach); y++)
        for (int x = Math.Max(0, bx - RoadReach); x <= Math.Min(Width - 1, bx + bw - 1 + RoadReach); x++)
        {
            int dx = Math.Max(0, Math.Max(bx - x, x - (bx + bw - 1)));
            int dy = Math.Max(0, Math.Max(by - y, y - (by + bh - 1)));
            int d = dx + dy;
            if (d == 0 || d > RoadReach || d >= bestD) continue;
            int i = Idx(x, y);
            if (ProfileAt(i) is { Frontage: true }) { best = i; bestD = d; }
        }
        return best;
    }

    bool HasFrontage(Building b) => b.Access >= 0;

    // Job sites keyed by their driveway cell, rebuilt every tick. Never saved.
    readonly Dictionary<int, List<int>> _sitesByAccess = new();

    /// <summary>Finds every building's driveway. Runs every tick before anything that needs road access.</summary>
    void RebuildAccess()
    {
        _sitesByAccess.Clear();
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            b.Access = FindAccess(b.X, b.Y, b.W, b.H);
            if (b.Access >= 0 && b.Jobs > 0)
            {
                if (!_sitesByAccess.TryGetValue(b.Access, out var list)) _sitesByAccess[b.Access] = list = new List<int>();
                list.Add(b.Id);
            }
        }
        RebuildTransit();
    }

    void RunGarbage()
    {
        GarbageCapacity = 0;
        GarbageProduced = 0;
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (t.GarbageCap > 0 && HasFrontage(b)) GarbageCapacity += t.GarbageCap * FundingFactor(b.Funding) / 100;
            GarbageProduced += t.Placeable ? b.Garbage : Scaled(b.Garbage, ModKey.Garbage);
        }
    }

    /// <summary>City-wide desirability penalty from uncollected garbage: 1 point per 100 units over capacity, up to 30.</summary>
    public int GarbagePenalty =>
        GarbageProduced <= GarbageCapacity ? 0 : (int)Math.Min(30, (GarbageProduced - GarbageCapacity) / 100);
}
