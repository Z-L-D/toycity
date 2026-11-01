namespace ToyCity.Sim;

/// <summary>Where demand comes from. Numbers match the protocol's DemandSourceKind.</summary>
public enum DemandSource : byte { Local = 0, Regional = 1, External = 2 }

/// <summary>
/// One line of a zone's demand: a source, an adjustment or a limit. <c>Points</c> has one value shared by all
/// wealth levels, or three (low, medium, high).
/// </summary>
public sealed record DemandLine(string Key, string Label, int[] Points, string Detail);

/// <summary>Demand for one zone, broken down so the player can see what drives it and what holds it back.</summary>
public sealed record ZoneDemandReport(
    Zone Zone,
    int[] Raw,
    int[] Final,
    List<DemandLine> Sources,
    List<DemandLine> Adjustments,
    List<DemandLine> Limits,
    string BindingKey,
    string Binding,
    int X,
    int Y,
    string Topic);

public sealed partial class World
{
    // Derived from buildings each tick by UpdateStats; never saved.
    public int PendingPopulation, JobsCommercial, JobsIndustrial, JobsCivic, Attraction;
    public readonly int[] DemandRaw = new int[9];

    /// <summary>Vehicles an hour that can cross the map edge on paths joined to the city. Saved.</summary>
    public int EdgeCapacity;
    /// <summary>One of the edge crossings, for "show me". -1 when there is none. Saved.</summary>
    public int EdgeCell = -1;
    bool _edgeDirty;

    /// <summary>Population at which regional pull has halved. Grows with the capacity of roads out to the region.</summary>
    public int RegionalScale => 2000 + Math.Min(EdgeCapacity, 20_000);

    /// <summary>Each source's share of demand, in index points, for R, C and I. Derived each tick.</summary>
    readonly int[,] _sourcePoints = new int[3, 3];
    readonly int[] _existingPoints = new int[3];
    readonly int[] _powerCap = { 100, 100, 100 };

    /// <summary>
    /// Finds paths that leave the map and are joined to the city: neighbour connections. People, shoppers and
    /// freight from the region come in over them, so they raise regional and external demand.
    /// </summary>
    void RunNeighborConnections()
    {
        _edgeDirty = false;
        var seen = new bool[N];
        var queue = new Queue<int>();
        foreach (var b in Buildings)
        {
            if (b == null || b.Access < 0 || seen[b.Access]) continue;
            seen[b.Access] = true;
            queue.Enqueue(b.Access);
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % Width, y = i / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int n = Idx(nx, ny);
                if (!seen[n] && Path[n] != 0) { seen[n] = true; queue.Enqueue(n); }
            }
        }
        int cap = 0, first = -1;
        Array.Clear(SideCapacity);
        for (int i = 0; i < N; i++)
        {
            if (!seen[i]) continue;
            int x = i % Width, y = i / Width;
            // A crossing is a border cell whose path runs off the map: the next cell inward is a path too.
            int inward = x == 0 ? i + 1 : x == Width - 1 ? i - 1 : y == 0 ? i + Width : y == HeightCells - 1 ? i - Width : -1;
            if (inward < 0 || Path[inward] == 0) continue;
            // Toward a neighbouring city, only where its road meets this one.
            if (!CrossingMeets(i)) continue;
            int c = Content.Profiles[Path[i] - 1].CapacityPerHour;
            cap += c;
            if (SideOf(i).Side is >= 0 and var side) SideCapacity[side] += c;
            if (first < 0) first = i;
        }
        // A shuttle adds its seats to a side whose roads meet.
        foreach (var d in Deals)
            if (d.Kind == DealKind.Transit && SideCapacity[d.Side] > 0) { SideCapacity[d.Side] += d.Amount; cap += d.Amount; }
        // So does a company line running on over it (Beta 3.1).
        for (int s = 0; s < 4; s++)
            if (SideCapacity[s] > 0 && ThroughSeats(s) is var seats and > 0) { SideCapacity[s] += seats; cap += seats; }
        EdgeCapacity = cap;
        EdgeCell = first;
    }

    /// <summary>
    /// Computes demand from its sources, then applies per-wealth adjustments and capacity limits. Uses the city
    /// totals UpdateStats just counted. The demand panel reads the same numbers.
    /// </summary>
    void ComputeDemand()
    {
        int p = Population + PendingPopulation;
        int l = p / 2;
        int jobs = JobsCommercial + JobsIndustrial + JobsCivic;
        bool connected = EdgeCapacity > 0;
        // Roads to open land and past the region's edge reach the abstract region; roads to a neighbouring city carry
        // its real commuters, shoppers, visitors and goods instead (BorderFlows).
        int openCap = EdgeCapacity - Enumerable.Range(0, 4).Where(s => NextDoor[s] != null).Sum(s => SideCapacity[s]);
        int jobsNextDoor = 0, shoppersIn = 0, shoppersOut = 0, freightOut = 0;
        for (int s = 0; s < 4; s++)
        {
            var f = Flows[s];
            if (f.Capacity == 0 || NextDoor[s] is not { Stats.Length: PubCount } n) continue;
            jobsNextDoor += Math.Min(n.Stats[PubSpareJobs] / 2, f.Capacity / 4);
            shoppersIn += f.ShoppersIn + f.TouristsIn;
            shoppersOut += f.ShoppersOut;
            freightOut += f.FreightOut;
        }

        // Residential: points straight from each source. Residents who work next door have jobs; jobs here taken by
        // the neighbours' commuters don't.
        _sourcePoints[0, 0] = 60 * (jobs - CommuteInTotal + CommuteOutTotal - l) / Math.Max(l, 100);
        _sourcePoints[0, 1] = 40 * RegionalScale / (RegionalScale + p) + Math.Min(30, 60 * Math.Max(0, jobsNextDoor - CommuteOutTotal) / Math.Max(l, 100));
        _sourcePoints[0, 2] = Math.Min(15, Attraction);
        _existingPoints[0] = 0;

        // Commercial and industrial: each source asks for a number of jobs; demand is the gap to jobs that exist.
        int cLocal = Math.Max(0, p / 4 - shoppersOut / 2);
        int cRegional = (connected ? Math.Min(p / 10, Math.Max(0, openCap) / 4) : 0) + shoppersIn / 2;
        int cExternal = Attraction * 10;
        SplitTargets(1, cLocal, cRegional, cExternal, JobsCommercial, 40);

        int iLocal = (l + CommuteInTotal) * 45 / 100;
        int iExternal = 50 + (connected ? Math.Min(Math.Max(0, openCap) / 8, 1000) : 0) + Math.Min(freightOut * 2, 1000);
        // Goods a transit company carries out to the edge are sold abroad, and factories want to make more of them.
        iExternal += Math.Min(FreightExportDaily * 2, 600);
        SplitTargets(2, iLocal, 0, iExternal, JobsIndustrial, 50);

        // Power headroom: with less than a quarter of capacity spare, demand is held down so the grid isn't swamped.
        int powerCap = PowerCapacity <= 0 ? 0
            : (int)Math.Clamp((PowerCapacity - PowerDemand) * 400 / PowerCapacity, 0, 100);

        int edu = EducationIndex;
        int[] bias = WealthBias(edu, p);
        int[] ord = OrdinanceDemand();
        for (int k = 0; k < 9; k++)
        {
            int z = k / 3;
            int raw = _sourcePoints[z, 0] + _sourcePoints[z, 1] + _sourcePoints[z, 2] + _existingPoints[z]
                + bias[k] - (TaxTenths[k] - 90) * 6 / 10 + ord[k];
            raw = Math.Clamp(raw, -100, 100);
            DemandRaw[k] = raw;
            Demand[k] = Math.Min(raw, powerCap);
        }
        _powerCap[0] = _powerCap[1] = _powerCap[2] = powerCap;
    }

    /// <summary>Demand changes from enacted ordinances, by zone and wealth.</summary>
    int[] OrdinanceDemand()
    {
        var d = new int[9];
        for (int w = 0; w < 3; w++)
        {
            d[w] += (int)Mod(ModKey.DemandR);
            d[3 + w] += (int)Mod(ModKey.DemandC);
            d[6 + w] += (int)Mod(ModKey.DemandI);
        }
        // People like living in a well-run city: approval above or below 50 moves residential demand up to 10.
        for (int w = 0; w < 3; w++) d[w] += (Approval - 50) / 5;
        d[6] += (int)Mod(ModKey.DemandDirty);
        d[8] += (int)Mod(ModKey.DemandHighTech);
        return d;
    }

    void SplitTargets(int z, int local, int regional, int external, int existing, int floor)
    {
        int target = local + regional + external;
        int denom = Math.Max(target, floor);
        _sourcePoints[z, 0] = 100 * local / denom;
        _sourcePoints[z, 1] = 100 * regional / denom;
        _sourcePoints[z, 2] = 100 * external / denom;
        _existingPoints[z] = -100 * existing / denom;
    }

    /// <summary>Wealth-level adjustments: education pulls toward rich homes and high-tech; big cities draw offices.</summary>
    static int[] WealthBias(int edu, int p) => new[]
    {
        0, -5 + edu * 20 / 100, -20 + edu * 40 / 100,
        0, -5 + Math.Min(20, p / 500), -25 + Math.Min(40, p / 250),
        5 - edu * 30 / 100, 0, -40 + edu * 70 / 100,
    };

    /// <summary>The demand panel: sources, adjustments, limits and the one thing holding each zone back.</summary>
    public List<ZoneDemandReport> DemandReport()
    {
        var reports = new List<ZoneDemandReport>();
        int p = Population + PendingPopulation;
        int l = p / 2;
        int jobs = JobsCommercial + JobsIndustrial + JobsCivic;
        string edgeNote = EdgeCapacity > 0
            ? $"Roads out to the region carry {EdgeCapacity:N0} vehicles an hour."
            : "No road leaves the map, so the region reaches you only by back roads. Build a road to the map edge.";
        var room = GrowableLots();
        int[] bias = WealthBias(EducationIndex, p);
        // What crosses each border to a neighbouring city, named, for the panel's lines.
        string Across(Func<BorderFlow, int> pick, string what)
        {
            var parts = Enumerable.Range(0, 4).Where(s => NextDoor[s] != null && pick(Flows[s]) > 0)
                .Select(s => $"{pick(Flows[s]):N0} {what} {NextDoor[s]!.Name}").ToList();
            return parts.Count == 0 ? "" : " " + string.Join("; ", parts) + ".";
        }

        for (int z = 0; z < 3; z++)
        {
            var zone = (Zone)(z + 1);
            var sources = new List<DemandLine>();
            var adjustments = new List<DemandLine>();
            var limits = new List<DemandLine>();
            int[] Same(int v) => new[] { v };

            switch (zone)
            {
                case Zone.Residential:
                    sources.Add(new DemandLine("local", "Jobs in town", Same(_sourcePoints[0, 0]),
                        jobs >= l ? $"{jobs:N0} jobs for {l:N0} workers: people move in to fill them."
                                  : $"{l:N0} workers for {jobs:N0} jobs: newcomers would have nowhere to work."));
                    sources.Add(new DemandLine("regional", "Pull from the region", Same(_sourcePoints[0, 1]),
                        $"Small towns draw people from the region; the pull halves by {RegionalScale:N0} people. {edgeNote}"
                        + Across(f => f.CommuteOut, "of your residents work in") + Across(f => f.CommuteIn, "people commute here from")));
                    sources.Add(new DemandLine("external", "Sights", Same(_sourcePoints[0, 2]),
                        Attraction > 0 ? $"Parks and landmarks (attraction {Attraction}) draw people who stay." : "No parks or landmarks to draw people yet."));
                    adjustments.Add(new DemandLine("education", "Education", new[] { bias[0], bias[1], bias[2] },
                        $"Education at homes is {EducationIndex}%. Richer people want schools nearby."));
                    break;
                case Zone.Commercial:
                    sources.Add(new DemandLine("local", "Shoppers in town", Same(_sourcePoints[1, 0]), $"{p:N0} residents support about {p / 4:N0} shop jobs."));
                    sources.Add(new DemandLine("regional", "Shoppers from the region", Same(_sourcePoints[1, 1]),
                        (EdgeCapacity > 0 ? $"Neighbours drive in to shop. {edgeNote}" : edgeNote)
                        + Across(f => f.ShoppersIn, "shoppers a day come from") + Across(f => f.TouristsIn, "visitors a day come from") + Across(f => f.ShoppersOut, "of your residents shop in")));
                    sources.Add(new DemandLine("external", "Tourists", Same(_sourcePoints[1, 2]),
                        Attraction > 0 ? $"Visitors come for the sights (attraction {Attraction}) and spend money." : "Build parks and landmarks to bring tourists."));
                    sources.Add(new DemandLine("existing", "Shops already open", Same(_existingPoints[1]), $"{JobsCommercial:N0} shop jobs exist or are being built."));
                    adjustments.Add(new DemandLine("size", "City size", new[] { bias[3], bias[4], bias[5] }, "Offices and upscale shops want a bigger city."));
                    break;
                default:
                    sources.Add(new DemandLine("local", "Workers in town", Same(_sourcePoints[2, 0]), $"Industry wants to hire about 45% of the {l:N0} workers."));
                    sources.Add(new DemandLine("external", "Exports", Same(_sourcePoints[2, 2]),
                        (EdgeCapacity > 0 ? $"Freight leaves by road to the region. {edgeNote}" : $"Some goods sell by back roads. {edgeNote}")
                        + (FreightExportDaily > 0 ? $" Company freight carries {FreightExportDaily:N0} tons a day out of the city." : "")
                        + Across(f => f.FreightOut, "tons of goods a day go to")));
                    sources.Add(new DemandLine("existing", "Factories already open", Same(_existingPoints[2]), $"{JobsIndustrial:N0} industrial jobs exist or are being built."));
                    adjustments.Add(new DemandLine("education", "Education", new[] { bias[6], bias[7], bias[8] },
                        $"Education is {EducationIndex}%: high-tech needs it, dirty industry avoids it."));
                    break;
            }
            int[] tax = new int[3];
            for (int w = 0; w < 3; w++) tax[w] = -(TaxTenths[z * 3 + w] - 90) * 6 / 10;
            adjustments.Add(new DemandLine("tax", "Taxes", tax, "Each point of tax above 9% costs 6 points of demand; each point below adds 6."));
            var ord = OrdinanceDemand();
            if (ord[z * 3] != 0 || ord[z * 3 + 1] != 0 || ord[z * 3 + 2] != 0)
                adjustments.Add(new DemandLine("ordinances", z == 0 ? "Laws and approval" : "Ordinances", new[] { ord[z * 3], ord[z * 3 + 1], ord[z * 3 + 2] },
                    z == 0 ? $"Enacted ordinances, plus mayor approval ({Approval}%): every 5 points above or below 50 moves demand by 1." : "Enacted ordinances that change this zone's demand. See Laws."));

            string powerDetail = PowerCapacity <= 0 ? "There is no power plant. Nothing grows without power."
                : $"The grid uses {PowerDemand:N0} of {PowerCapacity:N0} kW. Below 25% spare, demand is held down.";
            limits.Add(new DemandLine("power", "Power headroom", Same(_powerCap[z]), powerDetail));
            int lots = room[z];
            limits.Add(new DemandLine("room", "Room to grow", Same(lots),
                lots > 0 ? $"{lots:N0} empty {ZoneName(zone)} cells can take a building." : $"No empty {ZoneName(zone)} cell with power and road access."));

            var raw = new int[3];
            var fin = new int[3];
            for (int w = 0; w < 3; w++) { raw[w] = DemandRaw[z * 3 + w]; fin[w] = Demand[z * 3 + w]; }
            var (key, text, bx, by, topic) = Binding(zone, raw, fin, sources, adjustments, lots);
            reports.Add(new ZoneDemandReport(zone, raw, fin, sources, adjustments, limits, key, text, bx, by, topic));
        }
        return reports;
    }

    (string Key, string Text, int X, int Y, string Topic) Binding(Zone zone, int[] raw, int[] fin, List<DemandLine> sources, List<DemandLine> adjustments, int lots)
    {
        string name = ZoneName(zone);
        int bestRaw = raw.Max();
        int bestFin = fin.Max();
        if (PowerCapacity <= 0)
        {
            var site = FirstZoned(zone);
            return ("power", "No power plant: build one and connect it, and demand opens up.", site.X, site.Y, "power");
        }
        if (bestFin < bestRaw && bestRaw > 0)
        {
            var plant = Buildings.FirstOrDefault(b => b != null && TypeOf(b).PowerOutKw > 0);
            return ("power", $"Power is the limit: demand would be {bestRaw:+#;-#;0} but the grid is nearly full. Build another plant.",
                plant?.X ?? -1, plant?.Y ?? -1, "power");
        }
        if (bestRaw <= 0)
        {
            // Name the biggest drag, whether a source line or an adjustment.
            DemandLine? worst = null;
            int worstV = 0;
            foreach (var d in sources.Concat(adjustments))
            {
                int v = d.Points.Length == 1 ? d.Points[0] : d.Points.Max();
                if (v < worstV) { worstV = v; worst = d; }
            }
            string why = worst == null ? "Nothing is pulling people or businesses in." : $"{worst.Label} ({worstV:+#;-#;0}): {worst.Detail}";
            return (worst?.Key ?? "none", $"No {name} demand. {why}", -1, -1, "demand");
        }
        if (lots == 0)
        {
            var site = FirstZoned(zone);
            return ("room", site.X >= 0
                ? $"Demand is {bestRaw:+#;-#;0} but there's nowhere to grow: every {name} lot is built, unpowered or out of road reach. Zone more, or fix the lots shown."
                : $"Demand is {bestRaw:+#;-#;0} but there is no {name} zoning. Zone some land near a road.", site.X, site.Y, "zones");
        }
        // Growing: point at the biggest source so the player knows what keeps it going.
        var top = sources.Where(s => s.Points[0] > 0).OrderByDescending(s => s.Points[0]).FirstOrDefault();
        return ("growing", top != null ? $"Growing. Mostly {top.Label.ToLowerInvariant()}." : "Growing.", -1, -1, "demand");
    }

    (int X, int Y) FirstZoned(Zone zone)
    {
        for (int i = 0; i < N; i++)
            if (ZoneCode[i] != 0 && ZoneOf(ZoneCode[i]) == zone && BuildingAt[i] == 0) return (i % Width, i / Width);
        return (-1, -1);
    }

    /// <summary>Empty zoned cells, by zone, that have power and some lot facing a road: where growth can happen now.</summary>
    public int[] GrowableLots()
    {
        var room = new int[3];
        for (int i = 0; i < N; i++)
        {
            byte code = ZoneCode[i];
            if (code == 0 || BuildingAt[i] != 0 || Powered[i] != 1) continue;
            if (LotPlacements(i, 1).Count == 0) continue;
            room[(int)ZoneOf(code) - 1]++;
        }
        return room;
    }
}
