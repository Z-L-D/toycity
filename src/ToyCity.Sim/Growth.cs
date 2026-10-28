namespace ToyCity.Sim;

/// <summary>One check a lot must pass to grow or to stay healthy. The why-panel shows these as-is.</summary>
public readonly record struct Requirement(string Key, string Label, bool Met, string Detail);

/// <summary>A signed contribution, for example to desirability.</summary>
public readonly record struct Factor(string Label, int Value);

public readonly record struct LotVerdict(bool AllMet, Level Wealth, int Desirability, int Threshold);

public sealed partial class World
{
    public const int MonthsToAbandon = 6;
    public const int MonthsToRecover = 3;
    public const int MonthsToCrumble = 30;

    public static int DesirabilityThreshold(Level density) => density switch { Level.Low => 5, Level.Medium => 25, _ => 45 };

    public short[] DesirabilityLayer(Zone z) => z switch { Zone.Residential => DesR, Zone.Commercial => DesC, _ => DesI };

    public int DemandFor(Zone z, Level wealth) => Demand[((int)z - 1) * 3 + (int)wealth];

    /// <summary>Highest wealth a lot's land value supports. Industry is not picky about land value.</summary>
    public Level WealthBracket(Zone z, int landValue)
    {
        if (z == Zone.Industrial) return Level.High;
        return landValue >= 110 ? Level.High : landValue >= 60 ? Level.Medium : Level.Low;
    }

    static string ZoneName(Zone z) => z switch { Zone.Residential => "residential", Zone.Commercial => "commercial", Zone.Industrial => "industrial", _ => "unzoned" };
    static string WealthTag(Zone z, Level w) => z switch
    {
        Zone.Industrial => w switch { Level.Low => "dirty industry", Level.Medium => "manufacturing", _ => "high-tech" },
        _ => $"{"RCI"[(int)z - 1]}{new string('$', (int)w + 1)}",
    };

    /// <summary>A lot a zoned building could grow on: top-left corner and size.</summary>
    public readonly record struct Placement(int X, int Y, int W, int H);

    /// <summary>True when every cell of the rectangle is empty land zoned exactly <paramref name="code"/>.</summary>
    bool LotFree(int x0, int y0, int w, int h, byte code)
    {
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            if (!InBounds(x, y)) return false;
            int i = Idx(x, y);
            if (ZoneCode[i] != code || BuildingAt[i] != 0 || Path[i] != 0 || IsWater(i)) return false;
        }
        return true;
    }

    /// <summary>
    /// Every lot that contains cell i, fits the zone and faces a road, for the sizes the zone's density allows.
    /// Sizes come largest first, both orientations, then positions in scan order, so the list is deterministic.
    /// </summary>
    public List<Placement> LotPlacements(int i, int limit = int.MaxValue)
    {
        var found = new List<Placement>();
        byte code = ZoneCode[i];
        if (code == 0) return found;
        int cx = i % Width, cy = i / Width;
        foreach (var (fw, fd) in Content.LotSizes[(int)DensityOf(code)])
        {
            for (int o = 0; o < (fw == fd ? 1 : 2); o++)
            {
                int w = o == 0 ? fw : fd, h = o == 0 ? fd : fw;
                for (int y0 = cy - h + 1; y0 <= cy; y0++)
                for (int x0 = cx - w + 1; x0 <= cx; x0++)
                {
                    if (!LotFree(x0, y0, w, h, code) || FindAccess(x0, y0, w, h) < 0) continue;
                    found.Add(new Placement(x0, y0, w, h));
                    if (found.Count >= limit) return found;
                }
            }
        }
        return found;
    }

    int LaborOf(Building b) => (b.Residents + 1) / 2;

    /// <summary>
    /// The single rule set for zoned lots. Growth and decline call this with null lists; the why-query passes
    /// lists to get the same checks in words. If the why-panel says a lot is fine, growth agrees.
    /// </summary>
    public LotVerdict EvaluateLot(int i, List<Requirement>? reqs, List<Factor>? factors)
    {
        byte code = ZoneCode[i];
        var zone = ZoneOf(code);
        var density = DensityOf(code);
        var b = BuildingAtCell(i);
        if (b != null && TypeOf(b).Placeable) b = null;
        var bt = b != null ? TypeOf(b) : null;
        bool existing = b != null;
        bool all = true;

        void Check(string key, string label, bool met, string detail)
        {
            all &= met;
            reqs?.Add(new Requirement(key, label, met, detail));
        }

        if (zone == Zone.None && bt == null) return new LotVerdict(false, Level.Low, 0, 0);
        if (bt != null) zone = bt.Zone;
        var needDensity = bt?.Density ?? density;

        // Road access.
        if (existing)
        {
            bool road = HasFrontage(b!);
            Check("road", "Road access", road, road ? "Faces a path with frontage."
                : "No street or road touches this lot any more. Rebuild the road it faced.");
        }
        else
        {
            var lot = LotPlacements(i, 1);
            string sizes = string.Join(", ", Content.LotSizes[(int)density].Select(z => $"{z.W}×{z.D}"));
            Check("road", "Road access", lot.Count > 0, lot.Count > 0
                ? $"Part of a {lot[0].W}×{lot[0].H} lot that faces a road."
                : $"No {density.ToString().ToLowerInvariant()}-density lot ({sizes}) that includes this cell can reach a street or road. Lots need one side on a path with frontage (highways have none): build a road closer or zone a shallower strip.");
        }

        // Power.
        bool power = existing ? b!.Powered : Powered[i] == 1;
        string powerDetail = power ? "Connected to a working power plant."
            : Powered[i] == 2 ? "The grid is overloaded (brownout). Build another plant or connect to a bigger grid."
            : "Not connected to a power plant. Run a power line here, or zone next to powered land.";
        Check("power", "Power", power, powerDetail);

        // Water: medium and high density only.
        if (needDensity != Level.Low)
        {
            bool water = existing ? b!.Watered : Watered[i] == 1;
            string waterDetail = water ? "Within reach of a water pipe with a working pump."
                : Watered[i] == 2 ? "There are pipes here, but the pumps can't keep up. Add a pump."
                : $"No water pipe within {PipeReach} cells, or the pipes have no working (powered) pump. Medium and high density need water.";
            Check("water", "Water", water, waterDetail);
        }

        // Desirability.
        int des = DesirabilityOf(i, zone, factors);
        int threshold = DesirabilityThreshold(needDensity) - (existing ? 30 : 0);
        Check("desirability", "Desirability", des >= threshold, des >= threshold
            ? $"Desirability {des} meets the {threshold} this lot needs."
            : $"Desirability {des} is below the {threshold} this lot needs. See the factors below for what drags it down.");

        // Demand, and the wealth level this lot would grow at.
        Level wealth = Level.Low;
        if (existing)
        {
            int d = DemandFor(zone, bt!.Wealth);
            bool ok = d > -60;
            Check("demand", "Demand", ok, ok
                ? $"{WealthTag(zone, bt.Wealth)} demand is {d:+0;-0;0}."
                : $"{WealthTag(zone, bt.Wealth)} demand has collapsed ({d}). Lower taxes or grow what this zone depends on.");
            wealth = bt.Wealth;
        }
        else
        {
            var bracket = WealthBracket(zone, LandValue[i]);
            bool found = false;
            for (int w = (int)bracket; w >= 0 && !found; w--)
            {
                if (DemandFor(zone, (Level)w) > 0) { wealth = (Level)w; found = true; }
            }
            string list = string.Join(", ", Enumerable.Range(0, (int)bracket + 1).Select(w => $"{WealthTag(zone, (Level)w)} {DemandFor(zone, (Level)w):+0;-0;0}"));
            Check("demand", "Demand", found, found
                ? $"{WealthTag(zone, wealth)} demand is {DemandFor(zone, wealth):+0;-0;0} (land value {LandValue[i]} allows up to {WealthTag(zone, bracket)})."
                : $"No {ZoneName(zone)} demand at the wealth levels this land value allows ({list}).");
        }

        // Ongoing needs once people live or work here.
        if (existing && b!.ConstructionLeft == 0 && !b.Abandoned)
        {
            if (b.Residents > 0)
            {
                // Only unreachable jobs count against a home. Plain unemployment lowers residential demand instead.
                bool freeJobs = Jobs > Workers;
                bool jobs = b.Employed > 0 || !freeJobs;
                Check("jobs", "Jobs within reach", jobs, b.Employed > 0
                    ? $"{b.Employed} of {LaborOf(b)} workers have jobs, average commute {b.CommuteMinutes} min."
                    : !freeJobs ? "Nobody here has a job, but neither does anyone else: the city needs more jobs (watch the demand bars)."
                    : "There are free jobs in the city, but none within a 60 minute commute. Connect roads to job areas or zone jobs closer.");
            }
            else if (b.Jobs > 0)
            {
                // Only unreachable workers count against a workplace. A plain labor shortage shows up as low demand.
                bool jobless = LaborForce > Workers;
                bool staffed = b.Workers > 0 || !jobless;
                Check("workers", "Workers", staffed, b.Workers > 0
                    ? $"{b.Workers} of {b.Jobs} jobs filled."
                    : !jobless ? "No workers yet: everyone in town already has a job. More homes will help."
                    : "People are looking for work, but none can reach this workplace within a 60 minute commute. Connect it by road to homes.");
            }
        }

        return new LotVerdict(all, wealth, des, threshold);
    }

    void RunConstruction()
    {
        foreach (var b in Buildings)
        {
            if (b == null || b.ConstructionLeft == 0) continue;
            b.ConstructionLeft--;
            if (b.ConstructionLeft == 0 && b.Residents > 0) MatchCommutes(b);
        }
    }

    /// <summary>Staggered growth: each day checks the cells whose index falls on that day of the month.</summary>
    void RunGrowth()
    {
        int slot = Day - 1;
        for (int i = slot; i < N; i += DaysPerMonth)
        {
            byte code = ZoneCode[i];
            if (code == 0 || Path[i] != 0) continue;
            var zone = ZoneOf(code);
            var density = DensityOf(code);
            var b = BuildingAtCell(i);
            if (b == null)
            {
                var v = EvaluateLot(i, null, null);
                if (!v.AllMet) continue;
                int d = DemandFor(zone, v.Wealth);
                if (!Rng.Chance(Math.Clamp(5 + d / 4, 5, 30))) continue;
                // Pick a lot size at random among those that fit, then the first position for it.
                var lots = LotPlacements(i);
                var sizes = lots.Select(l => (l.W, l.H)).Distinct().ToList();
                var pick = sizes[Rng.Next(sizes.Count)];
                var lotPick = lots.First(l => (l.W, l.H) == pick);
                var t = Content.Zoned(zone, density, v.Wealth);
                AddBuilding(t, lotPick.X, lotPick.Y, t.BuildDays + (lotPick.W * lotPick.H - 1) * 2, lotPick.W, lotPick.H);
            }
            else if (!TypeOf(b).Placeable && b.Operating && Idx(b.X, b.Y) == i)
            {
                // Redevelop when the zone was upzoned or the land became valuable enough for a richer building.
                var t = TypeOf(b);
                if (t.Zone != zone) continue;
                var bracket = WealthBracket(zone, LandValue[i]);
                Level targetWealth = t.Wealth;
                if (bracket > t.Wealth && DemandFor(zone, bracket) > 10) targetWealth = bracket;
                if (density == t.Density && targetWealth == t.Wealth) continue;
                if (density < t.Density) continue; // downzoning keeps the bigger building
                if (DemandFor(zone, targetWealth) <= 0) continue;
                if (DesirabilityLayer(zone)[i] < DesirabilityThreshold(density)) continue;
                if (density != Level.Low && Watered[i] != 1) continue;
                if (Powered[i] != 1 || !HasFrontage(b)) continue;
                if (!Rng.Chance(15)) continue;
                var nt = Content.Zoned(zone, density, targetWealth);
                int bx = b.X, by = b.Y, bw = b.W, bh = b.H;
                RemoveBuilding(b);
                AddBuilding(nt, bx, by, nt.BuildDays + (bw * bh - 1) * 2, bw, bh);
            }
        }
    }

    /// <summary>Monthly: zoned buildings that keep failing their requirements are abandoned, and recover when fixed.</summary>
    void RunDecline()
    {
        int abandoned = 0, recovered = 0;
        int firstX = -1, firstY = -1;
        foreach (var b in Buildings.ToArray())
        {
            if (b == null || TypeOf(b).Placeable || b.ConstructionLeft > 0) continue;
            var v = EvaluateLot(Idx(b.X, b.Y), null, null);
            if (b.Abandoned)
            {
                if (v.AllMet)
                {
                    if (++b.GoodMonths >= MonthsToRecover)
                    {
                        b.Abandoned = false;
                        b.UnmetMonths = 0;
                        b.GoodMonths = 0;
                        b.ConstructionLeft = 5;
                        recovered++;
                    }
                }
                else
                {
                    b.GoodMonths = 0;
                    if (++b.UnmetMonths >= MonthsToCrumble) RemoveBuilding(b);
                }
            }
            else if (v.AllMet) b.UnmetMonths = 0;
            else if (++b.UnmetMonths >= MonthsToAbandon)
            {
                b.Abandoned = true;
                b.GoodMonths = 0;
                ReleaseAssignments(b);
                if (abandoned++ == 0) { firstX = b.X; firstY = b.Y; }
            }
        }
        if (abandoned > 0)
            Notices.Add(new SimNotice(abandoned == 1 ? "A building was abandoned. Query it to see why."
                : $"{abandoned} buildings were abandoned. Query one to see why.", "warning", firstX, firstY));
        if (recovered > 0)
            Notices.Add(new SimNotice(recovered == 1 ? "An abandoned building is being fixed up." : $"{recovered} abandoned buildings are being fixed up.", "good"));
    }
}
