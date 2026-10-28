namespace ToyCity.Sim;

/// <summary>
/// A standing problem in the city, located so a front end can jump to it. <c>Topic</c> names the encyclopedia
/// article that explains the rule behind it.
/// </summary>
public sealed record Alert(string Key, string Severity, string Text, int X, int Y, int Count, string Topic);

public sealed partial class World
{
    /// <summary>
    /// Every standing problem, worst first. A query: it reads the state and changes nothing, so a host may call it
    /// as often as it likes.
    /// </summary>
    public List<Alert> Alerts()
    {
        var list = new List<Alert>();
        void Add(string key, string sev, string text, int i, int count, string topic) =>
            list.Add(new Alert(key, sev, text, i >= 0 ? i % Width : -1, i >= 0 ? i / Width : -1, count, topic));

        bool anyZoned = false;
        int firstZoned = -1;
        for (int i = 0; i < N && !anyZoned; i++)
            if (ZoneCode[i] != 0) { anyZoned = true; firstZoned = i; }

        foreach (var d in Disasters)
        {
            int at = InBounds(d.CellX, d.CellY) ? Idx(d.CellX, d.CellY) : -1;
            int burning = Buildings.Count(b => b is { BurningDays: > 0 });
            string text = d.Kind switch
            {
                DisasterKind.Fire => $"Fire: {burning} building{(burning == 1 ? " is" : "s are")} burning. Fire stations nearby put fires out faster and stop them spreading.",
                DisasterKind.Tornado => $"A tornado is on the ground: {d.Destroyed} buildings destroyed so far. It tears down power lines as it goes.",
                DisasterKind.Earthquake => $"Earthquake aftermath: {d.Destroyed} buildings destroyed and {d.Damaged} damaged.",
                _ => $"The Clockwork Colossus is loose, {d.DaysLeft} days of spring left: {d.Destroyed} buildings flattened so far.",
            };
            Add($"disaster-{d.Id}", "danger", text, at, 1, "disasters");
        }

        if (Funds < 0)
            Add("treasury", "danger", $"The treasury is ${-Funds:N0} in the red. Raise taxes, cut funding or take a loan.", -1, 1, "budget");

        if (anyZoned && PowerCapacity == 0)
            Add("no-power", "danger", "There is no power plant. Nothing will grow until you build one and connect it.", firstZoned, 1, "power");
        else if (PowerDemand > PowerCapacity)
        {
            int at = Array.IndexOf(Powered, (byte)2);
            Add("brownout", "danger", $"Brownout: buildings need {PowerDemand:N0} kW and the plants make {PowerCapacity:N0}. Build another plant.", at, 1, "power");
        }

        if (WaterDemand > WaterCapacity && WaterCapacity > 0)
        {
            int at = Array.IndexOf(Watered, (byte)2);
            Add("water-short", "warning", $"Water shortage: {WaterDemand:N0} wanted, {WaterCapacity:N0} pumped. Add a pump.", at, 1, "water");
        }

        if (GarbageProduced > GarbageCapacity)
        {
            var dump = Buildings.FirstOrDefault(b => b != null && TypeOf(b).GarbageCap > 0);
            int at = dump != null ? Idx(dump.X, dump.Y) : firstZoned;
            Add("garbage", "warning", $"Uncollected garbage: {GarbageProduced - GarbageCapacity:N0} units a day pile up and lower desirability by {GarbagePenalty}.", at, 1, "garbage");
        }

        // Buildings: civic problems and zoned buildings at risk or abandoned.
        int risk = 0, riskAt = -1, riskWorst = 0, abandoned = 0, abandonedAt = -1, runDown = 0, runDownAt = -1, tagged = 0, taggedAt = -1;
        foreach (var b in Buildings)
        {
            if (b == null || b.ConstructionLeft > 0) continue;
            var t = TypeOf(b);
            int at = Idx(b.X, b.Y);
            if (t.Placeable)
            {
                if (t.PowerKw > 0 && !b.Powered && PowerCapacity > 0)
                    Add("civic-unpowered", "warning", $"{t.Name} has no power and does nothing.", at, 1, t.Id);
                if (t.Category == Category.Water && !WaterPipeTouches(b))
                    Add("pump-pipe", "warning", $"{t.Name} isn't connected to a pipe.", at, 1, "water");
                if (t.Category == Category.Garbage && !HasFrontage(b))
                    Add("landfill-road", "warning", $"{t.Name} has no road, so no trucks come.", at, 1, "garbage");
                continue;
            }
            if (b.Graffiti >= 2) { tagged++; if (taggedAt < 0) taggedAt = at; }
            if (!b.Abandoned && b.Condition < RunDownBelow) { runDown++; if (runDownAt < 0) runDownAt = at; }
            if (b.Abandoned) { abandoned++; if (abandonedAt < 0) abandonedAt = at; }
            else if (b.UnmetMonths > 0)
            {
                risk++;
                if (b.UnmetMonths > riskWorst) { riskWorst = b.UnmetMonths; riskAt = at; }
            }
        }
        if (risk > 0)
            Add("at-risk", "warning", risk == 1 ? "A building is struggling and may be abandoned." : $"{risk} buildings are struggling and may be abandoned.", riskAt, risk, "decline");
        if (abandoned > 0)
            Add("abandoned", "info", abandoned == 1 ? "A building is abandoned." : $"{abandoned} buildings are abandoned.", abandonedAt, abandoned, "decline");

        if (runDown > 0)
            Add("run-down", "info", runDown == 1 ? "A building is run down and drags its block's value down." : $"{runDown} buildings are run down and drag their blocks' value down.", runDownAt, runDown, "wear");
        if (tagged >= 3)
            Add("graffiti", "info", $"{tagged} buildings are covered in graffiti. Police coverage or a graffiti removal ordinance clean it up.", taggedAt, tagged, "wear");

        // Zoned land no lot can use.
        int stranded = 0, strandedAt = -1;
        for (int i = 0; i < N; i++)
        {
            if (ZoneCode[i] == 0 || BuildingAt[i] != 0 || IsWater(i)) continue;
            if (LotPlacements(i, 1).Count > 0) continue;
            stranded++;
            if (strandedAt < 0) strandedAt = i;
        }
        if (stranded > 0)
            Add("stranded", "info", $"{stranded} zoned cells are too far from a road for any lot to use.", strandedAt, stranded, "lots");

        // Demand with nowhere to go.
        var room = GrowableLots();
        for (int z = 0; z < 3; z++)
        {
            int best = Math.Max(Demand[z * 3], Math.Max(Demand[z * 3 + 1], Demand[z * 3 + 2]));
            if (best < 25 || room[z] > 0 || PowerCapacity == 0) continue;
            var zone = (Zone)(z + 1);
            var (fx, fy) = FirstZoned(zone);
            list.Add(new Alert($"room-{"rci"[z]}", "info", $"There's {ZoneName(zone)} demand but no room for it. Zone more {ZoneName(zone)} land near roads.", fx, fy, 1, "zones"));
        }

        // Worst congestion.
        int jam = -1, jamLevel = 160;
        for (int i = 0; i < N; i++)
            if (Path[i] != 0 && Traffic[i] > jamLevel) { jamLevel = Traffic[i]; jam = i; }
        if (jam >= 0)
            Add("traffic", "warning", $"Traffic jam on the {ProfileAt(jam)!.Name.ToLowerInvariant()}: {Trips[jam]:N0} trips a day, {jamLevel * 100 / 128}% of capacity.", jam, 1, "traffic");

        static int Rank(string s) => s switch { "danger" => 0, "warning" => 1, _ => 2 };
        return list.Select((a, n) => (a, n)).OrderBy(p => Rank(p.a.Severity)).ThenBy(p => p.n).Select(p => p.a).ToList();
    }
}
