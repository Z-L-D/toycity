namespace ToyCity.Sim;

/// <summary>Map layers a front end can ask for. Numbers match the protocol's OverlayKind.</summary>
public enum OverlayKind
{
    None = 0, LandValue = 1, DesirabilityR = 2, DesirabilityC = 3, DesirabilityI = 4, GroundPollution = 5,
    WaterPollution = 6, Power = 7, Water = 8, Traffic = 9, Noise = 10, Police = 11, Fire = 12, Health = 13,
    Education = 14, Parks = 15, Commute = 16, Elevation = 17, Litter = 18, Blight = 19,
}

public sealed record WhyReport(int X, int Y, string Subject, string Summary, List<Requirement> Requirements, List<Factor> Factors);

public sealed record OverlayResult(OverlayKind Kind, byte[] Values, string LegendLow, string LegendHigh);

public sealed partial class World
{
    /// <summary>Answers "why is this lot the way it is?" using the same checks as growth and decline.</summary>
    public WhyReport Why(int x, int y)
    {
        var reqs = new List<Requirement>();
        var factors = new List<Factor>();
        if (!InBounds(x, y)) return new WhyReport(x, y, "Off the map", "", reqs, factors);
        int i = Idx(x, y);
        if (IsWater(i)) return new WhyReport(x, y, "Water", "Nothing can be built on water yet.", reqs, factors);

        if (ProfileAt(i) is { } p)
        {
            int level = Traffic[i];
            string load = level < 64 ? "light" : level < 128 ? "busy" : level < 192 ? "congested" : "gridlocked";
            return new WhyReport(x, y, p.Name, $"{Trips[i]} commuter trips a day: {load} ({level * 100 / 128}% of capacity).", reqs, factors);
        }

        var b = BuildingAtCell(i);
        if (b != null && TypeOf(b).Placeable) return WhyCivic(b, x, y);

        if (ZoneCode[i] == 0 && b == null)
        {
            LandValueOf(i, factors);
            return new WhyReport(x, y, "Empty land", "Nothing grows on unzoned land. Zone it residential, commercial or industrial.", reqs, factors);
        }

        var v = EvaluateLot(i, reqs, factors);
        if (b != null && b.ConstructionLeft == 0) reqs.Add(ConditionRequirement(b));
        if (b == null)
        {
            var zone = ZoneOf(ZoneCode[i]);
            var density = DensityOf(ZoneCode[i]);
            string subject = $"Empty {ZoneName(zone)} lot ({density.ToString().ToLowerInvariant()} density)";
            string summary = v.AllMet
                ? $"Ready to grow: a {WealthTag(zone, v.Wealth)} building should appear soon."
                : "Can't grow yet: " + string.Join("; ", reqs.Where(r => !r.Met).Select(r => r.Label.ToLowerInvariant())) + ".";
            return new WhyReport(x, y, subject, summary, reqs, factors);
        }

        var t = TypeOf(b);
        string what = $"{t.Name} ({WealthTag(t.Zone, t.Wealth)}, {t.Density.ToString().ToLowerInvariant()} density)";
        string verdict;
        if (b.BurningDays > 0) verdict = $"On fire for {b.BurningDays} days. It burns down after {BurnDaysToDestroy} unless firefighters put it out; fire coverage here is {Coverage[(int)Service.Fire][i] * 100 / 255}%.";
        else if (b.ConstructionLeft > 0) verdict = $"Under construction: {b.ConstructionLeft} days left.";
        else if (b.Abandoned) verdict = v.AllMet
            ? $"Abandoned, but the problems are fixed. People move back in {MonthsToRecover - b.GoodMonths} months if it stays that way."
            : "Abandoned. Fix the unmet needs below and people will move back.";
        else if (v.AllMet) verdict = b.Condition < RunDownBelow ? "Doing fine, but run down: see Condition below." : "Doing fine.";
        else verdict = $"At risk: unmet needs for {b.UnmetMonths} of {MonthsToAbandon} months before it's abandoned.";
        return new WhyReport(x, y, what, verdict, reqs, factors);
    }

    WhyReport WhyCivic(Building b, int x, int y)
    {
        var t = TypeOf(b);
        var reqs = new List<Requirement>();
        var factors = new List<Factor>();
        if (t.PowerKw > 0)
            reqs.Add(new Requirement("power", "Power", b.Powered, b.Powered ? "Powered." : "No power: this building does nothing until it is connected to a working plant."));
        if (b.Jobs > 0)
            reqs.Add(new Requirement("workers", "Workers", b.Workers > 0, $"{b.Workers} of {b.Jobs} jobs filled."));
        string summary = t.Category switch
        {
            Category.Power => $"Makes {t.PowerOutKw * FundingFactor(b.Funding) / 100:N0} kW. City grid: {PowerDemand:N0} of {PowerCapacity:N0} kW used.",
            Category.Water => b.Powered ? $"Pumps {PumpOutput(b):N0} units a day (water pollution here cuts output). City: {WaterDemand:N0} of {WaterCapacity:N0} used."
                : "Not pumping: a pump needs power.",
            Category.Garbage => HasFrontage(b) ? $"Takes {t.GarbageCap * FundingFactor(b.Funding) / 100:N0} units a day. City makes {GarbageProduced:N0}, can handle {GarbageCapacity:N0}."
                : "Not collecting: garbage trucks need a road next to the landfill.",
            _ => b.Powered || t.PowerKw == 0 ? $"Covers a radius of {EffectiveRadius(b)} cells at {b.Funding}% funding." : "Not working: no power.",
        };
        if (t.PowerKw > 0 && !b.Powered && t.Category != Category.Power) reqs.Add(new Requirement("grid", "Grid", false, "Run a power line from a plant, or place it next to powered buildings or zones."));
        if (b.ConstructionLeft == 0) reqs.Add(ConditionRequirement(b));
        if (b.BurningDays > 0) summary = $"On fire for {b.BurningDays} days! " + summary;
        if (t.Category == Category.Water) reqs.Add(new Requirement("pipe", "Pipe connection", WaterPipeTouches(b), WaterPipeTouches(b) ? "Connected to a pipe." : "Lay a water pipe touching the pump."));
        return new WhyReport(x, y, t.Name, summary, reqs, factors);
    }

    /// <summary>Condition, where it is heading and why, as a line for the why-panel.</summary>
    Requirement ConditionRequirement(Building b)
    {
        var parts = new List<Factor>();
        int target = UpkeepTarget(b, parts);
        string why = b.Abandoned
            ? "Nobody looks after an abandoned building: it loses 5 a month."
            : $"Owners keep it at {target}: " + string.Join(", ", parts.Select(p => $"{p.Label.ToLowerInvariant()} {(p.Value > 0 ? "+" : "")}{p.Value}")) + ".";
        string tags = b.Graffiti > 0 ? $" Graffiti: {b.Graffiti} of 3." : "";
        return new Requirement("condition", "Condition", b.Condition >= RunDownBelow, $"{b.Condition} of 100 ({LookOf(b).ToString().ToLowerInvariant()}). {why}{tags}");
    }

    bool WaterPipeTouches(Building b)
    {
        for (int y = b.Y - 1; y <= b.Y + b.H; y++)
        for (int x = b.X - 1; x <= b.X + b.W; x++)
            if (InBounds(x, y) && WaterPipe[Idx(x, y)] != 0) return true;
        return false;
    }

    /// <summary>Raw values at a cell, for the inspector.</summary>
    public List<(string Key, string Value)> Inspect(int x, int y)
    {
        var f = new List<(string, string)>();
        if (!InBounds(x, y)) return f;
        int i = Idx(x, y);
        f.Add(("Cell", $"{x}, {y}"));
        f.Add(("Elevation", $"{Height[i]} (water level {WaterLevel})"));
        if (ZoneCode[i] != 0) f.Add(("Zone", $"{ZoneName(ZoneOf(ZoneCode[i]))}, {DensityOf(ZoneCode[i]).ToString().ToLowerInvariant()} density"));
        if (ProfileAt(i) is { } p) f.Add(("Path", $"{p.Name} ({p.SpeedKmh} km/h)"));
        f.Add(("Land value", LandValue[i].ToString()));
        f.Add(("Desirability R / C / I", $"{DesR[i]} / {DesC[i]} / {DesI[i]}"));
        f.Add(("Pollution (ground / water)", $"{Pollution[i]} / {WaterPollution[i]}"));
        f.Add(("Noise", Noise[i].ToString()));
        f.Add(("Litter / blight", $"{Litter[i]} / {Blight[i]}"));
        f.Add(("Power", Powered[i] switch { 1 => "yes", 2 => "brownout", _ => "no" }));
        f.Add(("Water", Watered[i] switch { 1 => "yes", 2 => "shortage", _ => "no" }));
        if (Path[i] != 0) f.Add(("Traffic", $"{Trips[i]} trips/day, level {Traffic[i]}"));
        f.Add(("Coverage P / F / H / E / Parks", string.Join(" / ", Coverage.Select(c => c[i] * 100 / 255 + "%"))));
        if (BuildingAtCell(i) is { } b)
        {
            var t = TypeOf(b);
            f.Add(("Building", $"#{b.Id} {t.Name}"));
            if (b.Residents > 0) f.Add(("Residents", $"{b.Residents}, {b.Employed} employed, {b.CommuteMinutes} min commute"));
            f.Add(("Lot", $"{b.W} × {b.H}"));
            if (b.Jobs > 0) f.Add(("Jobs", $"{b.Workers} of {b.Jobs} filled"));
            if (t.Placeable) f.Add(("Funding", $"{b.Funding}%"));
            f.Add(("Condition", $"{b.Condition} ({LookOf(b)}), graffiti {b.Graffiti}{(b.BurningDays > 0 ? $", burning {b.BurningDays} days" : "")}"));
        }
        return f;
    }

    public OverlayResult Overlay(OverlayKind kind)
    {
        var v = new byte[N];
        string low = "low", high = "high";
        static byte Des(short d) => (byte)Math.Clamp(d + 100, 0, 255);
        switch (kind)
        {
            case OverlayKind.LandValue: Array.Copy(LandValue, v, N); low = "$"; high = "$$$"; break;
            case OverlayKind.DesirabilityR: for (int i = 0; i < N; i++) v[i] = Des(DesR[i]); break;
            case OverlayKind.DesirabilityC: for (int i = 0; i < N; i++) v[i] = Des(DesC[i]); break;
            case OverlayKind.DesirabilityI: for (int i = 0; i < N; i++) v[i] = Des(DesI[i]); break;
            case OverlayKind.GroundPollution: Array.Copy(Pollution, v, N); low = "clean"; high = "filthy"; break;
            case OverlayKind.WaterPollution: Array.Copy(WaterPollution, v, N); low = "clean"; high = "filthy"; break;
            case OverlayKind.Power: for (int i = 0; i < N; i++) v[i] = Powered[i] switch { 1 => 255, 2 => 120, _ => 0 }; low = "none"; high = "powered"; break;
            case OverlayKind.Water: for (int i = 0; i < N; i++) v[i] = Watered[i] switch { 1 => 255, 2 => 120, _ => 0 }; low = "none"; high = "watered"; break;
            case OverlayKind.Traffic: Array.Copy(Traffic, v, N); low = "free"; high = "gridlock"; break;
            case OverlayKind.Noise: Array.Copy(Noise, v, N); low = "quiet"; high = "loud"; break;
            case OverlayKind.Police: Array.Copy(Coverage[(int)Service.Police], v, N); break;
            case OverlayKind.Fire: Array.Copy(Coverage[(int)Service.Fire], v, N); break;
            case OverlayKind.Health: Array.Copy(Coverage[(int)Service.Health], v, N); break;
            case OverlayKind.Education: Array.Copy(Coverage[(int)Service.Education], v, N); break;
            case OverlayKind.Parks: Array.Copy(Coverage[(int)Service.Parks], v, N); break;
            case OverlayKind.Commute:
                foreach (var b in Buildings)
                {
                    if (b == null || b.Residents == 0 || !b.Operating) continue;
                    v[Idx(b.X, b.Y)] = (byte)Math.Min(255, b.Employed == 0 ? 255 : b.CommuteMinutes * 4);
                }
                low = "short"; high = "long or none"; break;
            case OverlayKind.Litter: Array.Copy(Litter, v, N); low = "tidy"; high = "strewn"; break;
            case OverlayKind.Blight: Array.Copy(Blight, v, N); low = "kept up"; high = "run down"; break;
            case OverlayKind.Elevation: for (int i = 0; i < N; i++) v[i] = (byte)(Height[i] * 255 / MaxHeight); break;
        }
        return new OverlayResult(kind, v, low, high);
    }
}
