using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// Goods. Factories make them and shops want them; trucks the city never sees carry them by road, as before. A
/// transit company that leases a freight depot can carry them instead, by truck or by rail: the goods made in the
/// depot's reach are ready to ship there, and the shops in its reach want some. Goods the company carries out to the
/// map edge are exports, and they raise industry's demand.
/// </summary>
public sealed partial class World
{
    /// <summary>How far a freight depot gathers goods from, in cells.</summary>
    public const int FreightReach = 10;

    /// <summary>Tons exported by company freight this month so far, and the daily average last month.</summary>
    public int FreightExportMonth, FreightExportDaily;
    /// <summary>Tons delivered to shops by company freight this month so far, and the daily average last month.</summary>
    public int FreightDeliveredMonth, FreightDeliveredDaily;

    /// <summary>A factory's goods a day: half a ton a job. A shop wants a quarter.</summary>
    int GoodsMade(Building b) => TypeOf(b).Zone == Zone.Industrial && b.Operating ? b.Jobs / 2 : 0;
    int GoodsWanted(Building b) => TypeOf(b).Zone == Zone.Commercial && b.Operating ? b.Jobs / 4 : 0;

    /// <summary>
    /// Goods at each leased freight depot today. Each factory and shop counts at its nearest depot in reach, any
    /// company's, so two depots never share the same goods.
    /// </summary>
    Dictionary<int, (int Ready, int Wanted)> FreightAtDepots()
    {
        var depots = Leases.Where(l => l.Kind == "freight-depot" && Buildings[l.BuildingId] is { } b && b.Operating).ToList();
        var at = depots.ToDictionary(l => l.Id, _ => (Ready: 0, Wanted: 0));
        if (depots.Count == 0) return at;
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            int made = GoodsMade(b), wanted = GoodsWanted(b);
            if (made == 0 && wanted == 0) continue;
            Lease? best = null;
            int bestGap = int.MaxValue;
            foreach (var l in depots)
            {
                int gap = RectGap(b, Buildings[l.BuildingId]!);
                if (gap <= FreightReach && gap < bestGap) { best = l; bestGap = gap; }
            }
            if (best == null) continue;
            var v = at[best.Id];
            at[best.Id] = (v.Ready + made, v.Wanted + wanted);
        }
        return at;
    }

    Dictionary<int, (int Ready, int Wanted)>? _freightToday;

    void FillFreight(HostedGame hg, CityToGame input)
    {
        _freightToday ??= FreightAtDepots();
        foreach (var l in Leases.Where(l => l.GameId == hg.Id && l.Kind == "freight-depot"))
            if (_freightToday.TryGetValue(l.Id, out var f)) input.Freight.Add(new FreightLot(l.Id, f.Ready, f.Wanted));
    }

    /// <summary>What the company carried today: picked up no more than was ready, dropped no more than was wanted, exports counted.</summary>
    void TakeFreight(HostedGame hg, GameToCity output)
    {
        if (_freightToday == null) return;
        int picked = 0;
        foreach (var (leaseId, tons) in output.FreightPicked)
            if (LeaseById(leaseId) is { Kind: "freight-depot" } l && l.GameId == hg.Id && _freightToday.TryGetValue(leaseId, out var f))
            {
                int take = Math.Clamp(tons, 0, f.Ready);
                _freightToday[leaseId] = (f.Ready - take, f.Wanted);
                picked += take;
            }
        int dropped = 0;
        foreach (var (leaseId, tons) in output.FreightDropped.OrderBy(d => d.Key))
        {
            int left = picked - dropped;
            if (left <= 0) break;
            if (leaseId == 0) { int x = Math.Clamp(tons, 0, left); FreightExportMonth += x; dropped += x; continue; }
            if (LeaseById(leaseId) is { Kind: "freight-depot" } l && l.GameId == hg.Id && _freightToday.TryGetValue(leaseId, out var f))
            {
                int give = Math.Clamp(tons, 0, Math.Min(left, f.Wanted));
                _freightToday[leaseId] = (f.Ready, f.Wanted - give);
                FreightDeliveredMonth += give;
                dropped += give;
            }
        }
    }

    /// <summary>First of the month: last month's freight becomes the daily figures demand reads.</summary>
    void CloseFreightMonth()
    {
        FreightExportDaily = FreightExportMonth / DaysPerMonth;
        FreightDeliveredDaily = FreightDeliveredMonth / DaysPerMonth;
        FreightExportMonth = FreightDeliveredMonth = 0;
    }

    /// <summary>The way a vehicle of this kind takes from a stop to the nearest map edge it can reach, or null.</summary>
    (int Cost, int[] Cells)? EdgeRoute(TransitKind kind, int from)
    {
        if (from < 0 || !Carries(kind, from)) return null;
        if (_rDist.Length < N) { _rDist = new int[N]; _rParent = new int[N]; _rStamp = new int[N]; _rGen = 0; }
        if (++_rGen == int.MaxValue) { Array.Clear(_rStamp); _rGen = 1; }
        _rPq.Clear();
        void Push(int c, int cost, int parent)
        {
            if (_rStamp[c] == _rGen && _rDist[c] <= cost) return;
            _rStamp[c] = _rGen; _rDist[c] = cost; _rParent[c] = parent;
            _rPq.Enqueue(c, (long)cost * N + c);
        }
        Push(from, 0, -1);
        while (_rPq.TryDequeue(out int c, out long pri))
        {
            int cost = (int)(pri / N);
            if (cost != _rDist[c]) continue;
            int x = c % Width, y = c / Width;
            if (x == 0 || y == 0 || x == Width - 1 || y == HeightCells - 1)
            {
                var cells = new List<int>();
                for (int p = c; p >= 0; p = _rParent[p]) cells.Add(p);
                cells.Reverse();
                return (cost, cells.ToArray());
            }
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int i = Idx(nx, ny);
                if (Carries(kind, i)) Push(i, cost + RideCost(kind, i), c);
            }
        }
        return null;
    }
}
