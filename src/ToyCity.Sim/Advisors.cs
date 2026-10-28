namespace ToyCity.Sim;

/// <summary>One advisor's view. Mood is "happy", "worried" or "alarmed"; messages come worst first.</summary>
public sealed record AdvisorReport(string Role, string Name, string Title, string Mood, List<Alert> Messages);

public sealed partial class World
{
    static readonly (string Role, string Name, string Title)[] AdvisorCast =
    {
        ("finance", "Penny Farthing", "Finance"),
        ("utilities", "Watt Ampleby", "Utilities"),
        ("planning", "Zoe Ning", "City planning"),
        ("safety", "Chief Hal Ladder", "Safety and health"),
        ("transport", "Rhoda Way", "Transport and environment"),
    };

    /// <summary>
    /// The advisors' panel: each advisor reads the same numbers as the alerts and demand panel and says what they
    /// would do, in the template voice. A query: nothing changes.
    /// </summary>
    public List<AdvisorReport> Advisors()
    {
        var list = new List<AdvisorReport>();
        int n = 0;
        foreach (var (role, name, title) in AdvisorCast)
        {
            var msgs = new List<Alert>();
            void Tell(string key, string sev, Dictionary<string, string> facts, int x = -1, int y = -1, string topic = "")
                => msgs.Add(new Alert(key, sev, Say($"advisor.{role}.{key}", Tick / DaysPerMonth, n * 31 + msgs.Count, facts), x, y, 1, topic));
            switch (role)
            {
                case "finance":
                {
                    long net = LastIncome - LastExpenses;
                    if (Funds < 0) Tell("red", "danger", new() { ["amount"] = (-Funds).ToString("N0") }, topic: "budget");
                    else if (net < 0 && Tick > DaysPerMonth)
                        Tell("losing", "warning", new() { ["amount"] = (-net).ToString("N0"), ["months"] = Math.Max(1, Funds / -net).ToString("N0") }, topic: "budget");
                    if (StatOf("taxR") > 100) Tell("taxes-high", "info", new() { ["percent"] = (StatOf("taxR") / 10.0).ToString("0.0") }, topic: "taxes");
                    if (msgs.Count == 0) Tell("ok", "good", new() { ["amount"] = net.ToString("N0") }, topic: "budget");
                    break;
                }
                case "utilities":
                {
                    int pct = PowerCapacity > 0 ? (int)(PowerDemand * 100 / PowerCapacity) : 0;
                    var alerts = Alerts();
                    var noPower = alerts.FirstOrDefault(a => a.Key == "no-power");
                    var brown = alerts.FirstOrDefault(a => a.Key == "brownout");
                    if (noPower != null) Tell("no-power", "danger", new(), noPower.X, noPower.Y, "power");
                    else if (brown != null) Tell("brownout", "danger", new() { ["used"] = PowerDemand.ToString("N0"), ["capacity"] = PowerCapacity.ToString("N0") }, brown.X, brown.Y, "power");
                    else if (pct > 75) Tell("power-tight", "warning", new() { ["percent"] = pct.ToString() }, topic: "power");
                    if (WaterCapacity > 0 && WaterDemand > WaterCapacity)
                        Tell("water", "warning", new() { ["used"] = WaterDemand.ToString("N0"), ["capacity"] = WaterCapacity.ToString("N0") }, topic: "water");
                    if (GarbageProduced > GarbageCapacity)
                        Tell("garbage", "warning", new() { ["amount"] = (GarbageProduced - GarbageCapacity).ToString("N0") }, topic: "garbage");
                    if (msgs.Count == 0) Tell("ok", "good", new() { ["percent"] = pct.ToString() }, topic: "power");
                    break;
                }
                case "planning":
                {
                    var room = GrowableLots();
                    for (int z = 0; z < 3; z++)
                    {
                        int best = Math.Max(Demand[z * 3], Math.Max(Demand[z * 3 + 1], Demand[z * 3 + 2]));
                        if (best < 20 || room[z] > 0 || PowerCapacity == 0) continue;
                        var (fx, fy) = FirstZoned((Zone)(z + 1));
                        Tell("no-room", "warning", new() { ["zone"] = ZoneName((Zone)(z + 1)) }, fx, fy, "zones");
                    }
                    var stranded = Alerts().FirstOrDefault(a => a.Key == "stranded");
                    if (stranded != null) Tell("stranded", "info", new() { ["count"] = stranded.Count.ToString("N0") }, stranded.X, stranded.Y, "lots");
                    if (EdgeCapacity == 0 && Population >= 1000) Tell("connect", "info", new(), topic: "neighbors");
                    if (msgs.Count == 0)
                    {
                        int top = 0;
                        for (int k = 1; k < 9; k++) if (Demand[k] > Demand[top]) top = k;
                        Tell("ok", "good", new() { ["zone"] = ZoneName((Zone)(top / 3 + 1)) }, topic: "demand");
                    }
                    break;
                }
                case "safety":
                {
                    int Share(Service s)
                    {
                        long covered = 0, all = 0;
                        foreach (var b in Buildings)
                        {
                            if (b == null || !b.Operating || b.Residents == 0) continue;
                            all += b.Residents;
                            if (Coverage[(int)s][Idx(b.X, b.Y)] > 40) covered += b.Residents;
                        }
                        return all > 0 ? (int)(covered * 100 / all) : 100;
                    }
                    if (Population >= 500)
                    {
                        int p = Share(Service.Police), h = Share(Service.Health), e = Share(Service.Education);
                        if (p < 50) Tell("police", "warning", new() { ["percent"] = p.ToString() }, topic: "police-station");
                        if (h < 50) Tell("health", "info", new() { ["percent"] = h.ToString() }, topic: "clinic");
                        if (e < 50 && Population >= 1000) Tell("schools", "info", new() { ["percent"] = e.ToString() }, topic: "elementary-school");
                    }
                    if (Approval < 40) Tell("approval", "warning", new() { ["percent"] = Approval.ToString() }, topic: "approval");
                    if (msgs.Count == 0) Tell("ok", "good", new() { ["percent"] = Approval.ToString() }, topic: "approval");
                    break;
                }
                default:
                {
                    int jam = -1, level = 0;
                    for (int i = 0; i < N; i++) if (Path[i] != 0 && Traffic[i] > level) { level = Traffic[i]; jam = i; }
                    if (jam >= 0 && level > 128)
                        Tell("jam", "warning", new() { ["street"] = Content.Voice.Street(jam % Width, jam / Width), ["percent"] = (level * 100 / 128).ToString() }, jam % Width, jam / Width, "traffic");
                    long pol = 0, people = 0;
                    foreach (var b in Buildings)
                        if (b != null && b.Operating && b.Residents > 0) { pol += (long)Pollution[Idx(b.X, b.Y)] * b.Residents; people += b.Residents; }
                    int avg = people > 0 ? (int)(pol / people) : 0;
                    if (avg > 25) Tell("pollution", "warning", new() { ["percent"] = avg.ToString() }, topic: "pollution");
                    if (msgs.Count == 0) Tell("ok", "good", new() { ["percent"] = (level * 100 / 128).ToString() }, topic: "traffic");
                    break;
                }
            }
            string mood = msgs.Any(m => m.Severity == "danger") ? "alarmed" : msgs.Any(m => m.Severity == "warning") ? "worried" : "happy";
            list.Add(new AdvisorReport(role, name, title, mood, msgs));
            n++;
        }
        return list;
    }
}
