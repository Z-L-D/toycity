using System.Text;
using ToyCity.Protocol.V0;
using static ToyCity.ClientModel.Panels.Bb;

namespace ToyCity.ClientModel.Panels;

// The panel's text, tab by tab, as the web client lays it out.
public sealed partial class PanelModel
{
    static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    static readonly string[] Zones = { "", "Residential", "Commercial", "Industrial" };
    static readonly string[][] WealthTags = { new[] { "R$", "R$$", "R$$$" }, new[] { "C$", "C$$", "C$$$" }, new[] { "Dirty", "Manuf.", "High-tech" } };
    public static readonly string[] Levels = { "Manual", "Stasis", "Caretaker", "Autopilot" };
    static readonly string[] LevelHelp =
    {
        "You play it.",
        "The city holds still: no growth, decay, money or new builds. People keep moving and side games keep running.",
        "Keeps things running: pays the bills (with a loan if it must), rebuilds what is lost like for like, nudges taxes a point either way, turns down deals. Never zones, builds roads or expands.",
        "Plays the city like a player would, in a style: zones blocks, lays new streets, builds plants and services, answers petitions and deals. It never demolishes anything and stays under the monthly spending cap.",
    };
    public static readonly string[] Styles = { "Growth", "Green", "Profit", "Chaos" };
    static readonly string[] StyleHelp =
    {
        "Taxes 7–10%, the cheapest power per kW, takes deals that bring money or jobs.",
        "Taxes 8–11%, clean power only, parks in leftover land, no polluting deals, industry kept away from homes.",
        "Taxes 10–13%, takes any deal that pays.",
        "Taxes anywhere from 5% to 15%, zones on a whim, flips a coin on deals.",
    };

    public static readonly (int Kind, string Name, string Scheme)[] OverlayKinds =
    {
        (1, "Land value", "good"), (2, "Desirability R", "diverging"), (3, "Desirability C", "diverging"), (4, "Desirability I", "diverging"),
        (5, "Pollution", "bad"), (6, "Water pollution", "bad"), (7, "Power", "binary"), (8, "Water", "binary"), (9, "Traffic", "bad"),
        (10, "Noise", "bad"), (11, "Police", "good"), (12, "Fire", "good"), (13, "Health", "good"), (14, "Education", "good"),
        (15, "Parks", "good"), (16, "Commute", "bad"), (17, "Elevation", "good"), (18, "Litter", "bad"), (19, "Run-down", "bad"),
        (20, "Transit riders", "good"),
    };

    /// <summary>The panel as BBCode: the open dialog, or else the open tab.</summary>
    public string Render() => Company.Active && Dialog == null ? Company.Render() : Dialog switch
    {
        "seat" => RenderSeat(),
        "consent" => RenderConsent(),
        "council" => RenderCouncil(),
        "dispatch" => Emergency.Render() + Footer($"[b]{Link("close", "Close")}[/b]"),
        "report" => RenderReport(),
        "terms" => RenderTerms(),
        "offer" => RenderOffer(),
        "confirm" => RenderConfirm(),
        "new" => RenderNew(),
        "save" => RenderSave(),
        "load" => RenderLoad(),
        _ => Tab switch
        {
            "why" => RenderWhy(),
            "inspect" => RenderInspect(),
            "demand" => RenderDemand(),
            "alerts" => RenderAlerts(),
            "forecast" => RenderForecast(),
            "people" => RenderPeople(),
            "laws" => RenderLaws(),
            "budget" => RenderBudget(),
            "layers" => RenderLayers(),
            "guide" => RenderGuide(),
            "buses" => Company.RenderCityTab(),
            _ => "",
        },
    };

    static string Topic(string? topic, string text = "Learn more") => string.IsNullOrEmpty(topic) ? "" : Link($"topic:{topic}", text);

    /// <summary>A "Go there" link for something on the map, flashing its area when it has one.</summary>
    static string Where(int x, int y, Rect? area, string text = "Go there →") =>
        x < 0 ? "" : Link(area is { X0: >= 0 } a ? $"jump:{x}:{y}:{a.X0}:{a.Y0}:{a.X1}:{a.Y1}" : $"jump:{x}:{y}", text);

    /// <summary>A "Place a clinic" link when a building would help.</summary>
    string FixLink(string fixType, int x, int y, Rect? area)
    {
        if (string.IsNullOrEmpty(fixType) || !_city.TypeById.TryGetValue(fixType, out var t)) return "";
        string at = area is { X0: >= 0 } a ? $":{a.X0}:{a.Y0}:{a.X1}:{a.Y1}" : "";
        return Link($"fix:{t.Id}:{x}:{y}{at}", $"Place {Esc(t.Name)} →");
    }

    static string Severity(string s) => s switch { "danger" => Neg, "warning" => Warn, "good" => Pos, _ => "#d8dee8" };

    // ---------------------------------------------------------------------------------------------------------
    // Why and Inspect

    string RenderWhy()
    {
        var w = Why;
        if (w == null || Selected is not { } sel || w.X != sel.X || w.Y != sel.Y)
            return H3("Why") + Dim("Pick a cell with the query tool (Q) to see why it is as it is.");
        var sb = new StringBuilder(H3(Esc(w.Subject)));
        foreach (var g in w.Gauges)
        {
            double pct = g.Max > 0 ? g.Value * 100.0 / g.Max : 0;
            string colour = g.Fun ? Blue : pct > 100 ? Neg : pct >= 85 ? Warn : Pos;
            string note = g.Fun ? "City statistic, this month"
                : pct > 100 ? $"Over capacity by {Num(g.Value - g.Max)}: build another nearby"
                : pct >= 85 ? "Nearly full: plan another one" : $"Room for {Num(g.Max - g.Value)} more";
            sb.Append($"{(g.Fun ? "✨ " : "")}{Esc(g.Label)}\n[b]{Num(g.Value)}[/b] / {Num(g.Max)} {Dim(Esc(g.Unit))}\n{Bar(pct / 100, colour, 24)}\n{Small(Dim(note))}\n\n");
        }
        sb.Append(Dim($"Cell {w.X}, {w.Y}")).Append('\n').Append(Esc(w.Summary)).Append('\n');
        if (w.Requirements.Count > 0)
        {
            sb.Append(H4("Needs"));
            foreach (var r in w.Requirements)
                sb.Append($"{Col(r.Met ? Pos : Neg, r.Met ? "✓" : "✗")} {Esc(r.Label)} {Topic(r.Topic, "?")}\n    {Small(Dim(Esc(r.Detail)))}\n");
        }
        if (w.Factors.Count > 0)
        {
            sb.Append(H4(w.Requirements.Any(r => r.Key == "desirability") ? "Desirability factors" : "Land value factors"));
            int max = Math.Max(20, w.Factors.Max(f => Math.Abs(f.Value)));
            sb.Append(Table(w.Factors.Select(f => (Esc(f.Label), $"{SignedBar(f.Value, max)} {Signed(f.Value)}"))));
        }
        sb.Append(Upgrades());
        if (Building is { } b && _city.TypeById.TryGetValue(b.TypeId, out var t) && t.Placeable)
        {
            sb.Append(Funding(b));
            sb.Append(LeaseText(b));
        }
        return sb.ToString();
    }

    /// <summary>The building on the selected cell, from the last inspect reply.</summary>
    BuildingState? Building => Inspect is { } r && Selected is { } s && r.X == s.X && r.Y == s.Y ? r.Building : null;

    string RenderInspect()
    {
        var r = Inspect;
        if (r == null || Selected is not { } sel || r.X != sel.X || r.Y != sel.Y)
            return H3("Inspect") + Dim("Pick a cell with the query tool (Q) to see everything about it.");
        var sb = new StringBuilder(H3($"Cell {r.X}, {r.Y}"));
        sb.Append(Upgrades());
        sb.Append(Table(r.Fields.Select(f => (Dim(Esc(f.Key)), Esc(f.Value)))));
        if (Building is { } b && _city.TypeById.TryGetValue(b.TypeId, out var t) && t.Placeable) sb.Append(Funding(b));
        sb.Append(Residents(r));
        return sb.ToString();
    }

    string Funding(BuildingState b)
    {
        int p = b.FundingPercent;
        string step(int by, string label) => p + by is >= 0 and <= 200 ? Link($"fund:{b.Id}:{p + by}", label) : Dim(label);
        return H4("Funding") + $"Budget  {step(-50, "−50")} {step(-10, "−10")}  [b]{p}%[/b]  {step(10, "+10")} {step(50, "+50")}\n"
            + Small(Dim("Funding scales running cost, output and how many people it can serve. Coverage radius grows with the square root of funding.")) + "\n";
    }

    /// <summary>The upgrades on offer for the inspected building; the ones not yet on offer say what they need.</summary>
    string Upgrades()
    {
        var ups = Inspect?.Upgrades;
        if (Building is not { } b || ups == null || ups.Count == 0) return "";
        var sb = new StringBuilder(H4("Upgrade"));
        if (ups.Count > 1) sb.Append(Small(Dim("Pick one: the others are gone once it's built."))).Append('\n');
        foreach (var u in ups)
        {
            _city.TypeById.TryGetValue(u.ToTypeId, out var t);
            string label = $"{Esc(u.Name)}: {Money(u.Cost)}";
            sb.Append(u.Available && _city.Stats.Funds >= u.Cost ? $"[b]{Link($"upgrade:{b.Id}:{u.ToTypeId}", label)}[/b]" : Dim(label)).Append('\n');
            sb.Append(Small(Esc(u.Text) + (t?.Cost > 0 ? Dim($" ({Money(t.Cost)} to build new)") : ""))).Append('\n');
            if (t != null)
                sb.Append(Small(Dim($"{(t.Capacity > 0 ? $"{Num(t.Capacity)} riders a day, " : "")}upkeep {Money(t.Maintenance)}/mo{(t.LeaseRent > 0 ? $", lease {Money(t.LeaseRent)}/mo" : "")}"))).Append('\n');
            if (!u.Available) sb.Append(Small(Col(Warn, Esc(u.WhyNot)))).Append('\n');
        }
        return sb.ToString();
    }

    static readonly Dictionary<string, string> TierNames = new()
    {
        ["full"] = "On the map: followed every day",
        ["light"] = "Kept on file: checked monthly",
        ["household"] = "Counted with the household",
    };

    /// <summary>The people who live in the inspected building, one row each, with their commute and how often they're late.</summary>
    string Residents(InspectResult r)
    {
        if (r.Residents.Count == 0) return "";
        int total = r.Building?.Residents is > 0 and var n ? n : r.Residents.Count;
        var sb = new StringBuilder(H4($"Residents ({total})"));
        foreach (var c in r.Residents)
        {
            string work = !string.IsNullOrEmpty(c.Job)
                ? $"{(c.ByTransit ? "🚌" : "🚗")} {Link($"jump:{c.JobX}:{c.JobY}", Esc(c.Job))}, {c.CommuteMinutes} min"
                : c.Role == "job seeker" ? "looking for work" : Esc(c.Role);
            string late = c.Tier == "full" && c.DaysTracked > 0 ? $" · late {c.DaysLate} of {c.DaysTracked} days, about {c.AvgMinutes} min door to door" : "";
            string follow = c.Tier == "full" ? " " + Link($"follow:{c.Id}:{Arg(c.Name)}:{Arg(c.Job)}", Followed == c.Id ? "[lb]Unfollow]" : "[lb]Follow]") : "";
            sb.Append($"• [b]{Esc(c.Name)}[/b], {c.Age} · {work}{late}{follow}\n   {Small(Dim(TierNames.GetValueOrDefault(c.Tier, Esc(c.Tier))))}\n");
        }
        if (total > r.Residents.Count) sb.Append(Dim($"…and {total - r.Residents.Count} more.")).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Leases on side-game lots

    static string Fare(int cents) => cents == 0 ? "free" : $"${cents / 100.0:0.00}";

    public static string TermsText(LeaseTerms t) => t.Mode == LeaseMode.LeaseBusiness
        ? $"Business lease: {Money(t.RentPerMonth)} a month and {t.TaxTenths / 10.0:0.#}% of fares"
        : $"Public service: the city pays {Money(t.SubsidyPerMonth)} a month, fares capped at {Fare(t.FareCapCents)}";

    /// <summary>The lease part of a building's panel: who leases it and on what terms, or who could.</summary>
    string LeaseText(BuildingState b)
    {
        var lease = Civic?.Leases.FirstOrDefault(l => l.BuildingId == b.Id);
        var takers = Civic?.SideGames.Where(g => g.LotKinds.Contains(b.TypeId)).ToList() ?? new();
        if (lease == null && takers.Count == 0) return "";
        var sb = new StringBuilder(H4("Lease"));
        if (lease == null)
        {
            sb.Append(Small(Dim("Not leased. Unleased stops keep the city's own bus service."))).Append('\n');
            foreach (var g in takers) sb.Append(Link($"lease-to:{g.Id}:{b.Id}", $"Lease to {Esc(g.Name)}…")).Append("  ");
            return sb.Append('\n').ToString();
        }
        bool evict = !lease.Terminable && !lease.BadStanding && lease.EvictMonths == 0 && Allowed(Mechanic.MechHostileEvictions);
        sb.Append($"Leased to [b]{Esc(lease.GameName)}[/b]. {Esc(TermsText(lease.Terms))}, {lease.Terms.NoticeMonths} months' notice.\n");
        if (lease.PendingTerms != null) sb.Append(Small($"From {Plural(lease.PendingMonths, "month")} on: {Esc(TermsText(lease.PendingTerms))}.")).Append('\n');
        sb.Append(Small($"{lease.Jobs} jobs here · {Money(lease.LastMonthFares)} in fares last month{(lease.Arrears > 0 ? " · " + Col(Neg, $"{Money(lease.Arrears)} owed") : "")}")).Append('\n');
        sb.Append($"{Col(lease.BadStanding ? Neg : Pos, lease.BadStanding ? "✗" : "✓")} Standing: {Small(lease.BadStanding ? Esc(string.Join(" ", lease.Targets)) : "Good: paid up and well regarded.")}\n");
        sb.Append(Small($"Rating in the district: [b]{Col(lease.Rating < 0 ? Neg : Pos, lease.Rating.ToString())}[/b] {Dim("(−1000 to 1000)")}")).Append('\n');
        if (lease.EvictMonths > 0) sb.Append(Small(Col(Neg, $"Evicted: it leaves in {Plural(lease.EvictMonths, "month")}, with compensation."))).Append('\n');
        sb.Append(Link($"lease-change:{lease.Id}", "Change terms…")).Append("   ");
        if (evict) sb.Append(Link($"lease-end:{lease.Id}:evict", "Evict…"));
        else if (lease.Terminable) sb.Append(Link($"lease-end:{lease.Id}", "End lease"));
        else sb.Append(Dim("End lease")).Append(Small(Dim($"  (only a lease in bad standing past its cure period can be ended by the city{(Allowed(Mechanic.MechHostileEvictions) ? "" : ", unless every player allows hostile evictions")})")));
        return sb.Append('\n').Append(DisputeText(lease)).ToString();
    }

    /// <summary>The dispute ladder: each rung with the rule behind it, the offer on the table, and the last few decided.</summary>
    string DisputeText(LeaseState lease)
    {
        var sb = new StringBuilder(H4("Dispute ladder"));
        foreach (var r in lease.Ladder)
            sb.Append($"{Col(r.Open ? Pos : Neg, r.Open ? "✓" : "✗")} [b]{Esc(r.Step)}[/b]  {Small(Dim(Esc(r.Rule)))}\n");
        string who(OfferState o) => (o.FromOperator ? lease.GameName : "The city") + (o.By != "" ? $" ({o.By})" : "");
        if (OpenOffer(lease) is { } open)
        {
            sb.Append($"\n[b]On the table:[/b] {Esc(who(open))} offers {Esc(open.Text)} {Small(Dim($"({Plural(open.DaysLeft, "day")} left)"))}\n");
            if (open.By == Me()) sb.Append(Link($"offer-answer:{open.Id}:0", "Withdraw")).Append('\n');
            else sb.Append($"[b]{Link($"offer-answer:{open.Id}:1", "Accept")}[/b]   {Link($"offer-answer:{open.Id}:0", "Decline")}   {Link($"offer-make:{lease.Id}:{(int)open.Kind}", "Counter…")}\n");
        }
        else
        {
            sb.Append(Link($"offer-make:{lease.Id}:0", "Offer terms…")).Append("   ");
            sb.Append(Link($"offer-make:{lease.Id}:1", SpeaksForOperator(lease) ? "Ask for a buyout…" : "Offer a buyout…")).Append("   ");
            sb.Append(lease.MoveTo.Count > 0 ? Link($"offer-make:{lease.Id}:2", "Propose a move…") : Dim("Propose a move")).Append('\n');
        }
        if (Civic?.Seats.FirstOrDefault(s => s.Id == lease.Game)?.Owner is { Length: > 0 } op)
        {
            if (op == Me()) sb.Append(Link($"delink:{Uri.EscapeDataString(op)}", "Leave with a copy…")).Append('\n');
            else if (Mayor?.Owner == Me() && Civic.DelinkOptions.FirstOrDefault(o => o.Player == op) is { Open: true })
                sb.Append(Link($"delink:{Uri.EscapeDataString(op)}", $"De-link {Esc(op)}…")).Append('\n');
        }
        foreach (var o in lease.Offers.Where(o => o.Status != "open").Take(4))
            sb.Append(Small(Dim($"{Esc(who(o))}: {Esc(o.Text)} — [b]{o.Status}[/b]{(o.Note != "" ? $". {Esc(o.Note)}" : "")}"))).Append('\n');
        return sb.ToString();
    }

    string RenderOffer()
    {
        if (_offer is not { } o || Civic?.Leases.FirstOrDefault(l => l.Id == o.LeaseId) is not { } lease) return "";
        bool buy = o.Kind == OfferKind.OfferBuyout;
        var sb = new StringBuilder(H3(buy ? $"Buy out {Esc(lease.GameName)}" : $"Move {Esc(lease.GameName)}"));
        string step(string by, string label) => Link($"offer:amount:{by}", label);
        if (buy) sb.Append(Small(Dim("The lease ends the day the other side accepts, and the city pays the amount."))).Append('\n');
        else
        {
            sb.Append(Small(Dim("The lease moves with its terms and jobs. The city can offer to pay the moving costs."))).Append("\n\n");
            foreach (var t in lease.MoveTo)
                sb.Append(t.BuildingId == o.To ? $"◉ [b]{Esc(t.Name)}[/b]" : $"○ {Link($"offer:to:{t.BuildingId}", Esc(t.Name))}").Append('\n');
        }
        sb.Append($"\n{(buy ? "Amount" : "Moving costs the city pays")}  {step("-1000", "−1000")} {step("-100", "−100")}  [b]{Money(o.Amount)}[/b]  {step("100", "+100")} {step("1000", "+1000")}\n");
        return sb.Append(Footer(Link("close", "Cancel"), $"[b]{Link("offer:ok", buy ? "Offer" : "Propose")}[/b]")).ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Demand

    string RenderDemand()
    {
        var sb = new StringBuilder(H3("Demand"));
        sb.Append(Dim("Demand is what the city wants more of. Each zone lists its sources, then the one thing holding it back.")).Append('\n');
        if (Demand == null) return sb.Append(Dim("Loading…")).ToString();
        foreach (var z in Demand.Zones)
        {
            int zi = (int)z.Zone - 1;
            if (zi is < 0 or > 2) continue;
            string colour = new[] { Bb.R, Bb.C, Bb.I }[zi];
            sb.Append($"\n[font_size=18][b]{Col(colour, Zones[(int)z.Zone])}[/b][/font_size]   ");
            for (int k = 0; k < 3; k++)
            {
                int f = k < z.Final.Count ? z.Final[k] : 0, raw = k < z.Raw.Count ? z.Raw[k] : f;
                sb.Append($"{Dim(WealthTags[zi][k])} [b]{Col(f > 0 ? Pos : Neg, Signed(f))}[/b]{(f < raw ? Dim($" of {Signed(raw)}") : "")}   ");
            }
            sb.Append('\n');
            sb.Append(Col(z.BindingKey == "growing" ? Pos : Warn, Esc(z.Binding))).Append(' ').Append(Where(z.X, z.Y, null, "Show me →")).Append(' ').Append(Topic(z.Topic)).Append('\n');
            sb.Append(H4("Where demand comes from"));
            foreach (var d in z.Sources)
            {
                int v = d.Points.Count > 0 ? d.Points[0] : 0;
                sb.Append($"{Esc(d.Label)}  {SignedBar(v, 100)} {Signed(v)}\n   {Small(Dim(Esc(d.Detail)))}\n");
            }
            sb.Append(H4("By wealth"));
            var rows = new List<string> { "[cell] [/cell]" };
            rows.AddRange(WealthTags[zi].Select(t => $"[cell]{Dim(t)}  [/cell]"));
            foreach (var adj in z.Adjustments)
            {
                rows.Add($"[cell]{Esc(adj.Label)}  [/cell]");
                for (int k = 0; k < 3; k++) rows.Add($"[cell]{Signed(k < adj.Points.Count ? adj.Points[k] : adj.Points.FirstOrDefault())}  [/cell]");
            }
            sb.Append($"[table=4]{string.Concat(rows)}[/table]\n");
            sb.Append(H4("Limits"));
            foreach (var l in z.Limits)
            {
                int v = l.Points.FirstOrDefault();
                string value = l.Key == "power" ? v >= 100 ? "no limit" : $"caps at {v}" : $"{Num(v)} cells";
                sb.Append($"{Esc(l.Label)}: {value}\n   {Small(Dim(Esc(l.Detail)))}\n");
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Alerts

    /// <summary>How many alerts, and the worst one's severity, for the top bar's badge.</summary>
    public (int Count, string Worst) AlertBadge =>
        (Alerts.Count, Alerts.Any(a => a.Severity == "danger") ? "danger" : Alerts.Any(a => a.Severity == "warning") ? "warning" : Alerts.Count > 0 ? "info" : "none");

    string RenderAlerts()
    {
        var sb = new StringBuilder(H3("Alerts"));
        if (Alerts.Count == 0) return sb.Append(Dim("Nothing needs your attention right now.")).ToString();
        sb.Append(Dim("Click an alert to go to its source.")).Append('\n');
        foreach (var a in Alerts)
        {
            string text = Col(Severity(a.Severity), "● ") + Esc(a.Text);
            sb.Append('\n').Append(a.X >= 0 ? Link(AreaMeta(a.X, a.Y, a.Area), text) : text).Append('\n');
            var foot = new[] { Where(a.X, a.Y, a.Area), FixLink(a.FixType, a.X, a.Y, a.Area), Topic(a.Topic, "Why?") }.Where(s => s != "");
            sb.Append("   ").Append(Small(string.Join("   ", foot))).Append('\n');
        }
        return sb.ToString();
    }

    static string AreaMeta(int x, int y, Rect? area) => area is { X0: >= 0 } a ? $"jump:{x}:{y}:{a.X0}:{a.Y0}:{a.X1}:{a.Y1}" : $"jump:{x}:{y}";

    // ---------------------------------------------------------------------------------------------------------
    // Forecast and what-if planning

    string RenderForecast()
    {
        var sb = new StringBuilder(H3("Forecast"));
        sb.Append(Dim("Runs a copy of the city ahead. The simulation is deterministic, so this is exactly what happens if you change nothing. ")).Append(Topic("forecast")).Append('\n');
        sb.Append("Months ahead: ");
        foreach (int m in new[] { 3, 6, 12, 24, 60 })
        {
            string label = m < 12 ? $"{m} months" : $"{m / 12} year{(m > 12 ? "s" : "")}";
            sb.Append(m == _forecastMonths ? $"[b]{label}[/b]" : Link($"months:{m}", label)).Append("  ");
        }
        sb.Append('\n').Append(_forecastBusy ? Dim("Running…") : $"[b]{Link("forecast", "Run forecast")}[/b]").Append('\n');
        sb.Append(H4("What if…"));
        sb.Append(Link("plan", Planning ? "Planning: on (click to stop)" : "Plan changes"));
        if (_plan.Count > 0)
            sb.Append("   ").Append(_forecastBusy ? Dim("Forecast plan") : Link("plan-run", "Forecast plan")).Append("   ").Append(Link("plan-build", "Build it")).Append("   ").Append(Link("plan-clear", "Discard"));
        sb.Append('\n');
        if (_plan.Count > 0) for (int k = 0; k < _plan.Count; k++) sb.Append($"{k + 1}. {Esc(_plan[k].Label)}\n");
        else sb.Append(Dim("Turn on planning, then use the tools as usual. Nothing is built until you say so.")).Append('\n');
        if (Forecast is { Points.Count: > 0 } f)
        {
            var pts = f.Points;
            string label(ForecastPoint p) => $"{Months[Math.Clamp(p.Month - 1, 0, 11)]} {p.Year}";
            sb.Append(H4($"{(f.WithPlan ? "With your plan" : "If nothing changes")}, {f.Months} months ahead"));
            foreach (var s in f.Summary) sb.Append("• ").Append(Esc(s)).Append('\n');
            void chart(string title, IEnumerable<double> values, Func<double, string> fmt, string colour)
            {
                var v = values.ToList();
                sb.Append($"\n{title}  {fmt(v[0])} → [b]{fmt(v[^1])}[/b]\n{Spark(v, colour)}\n");
            }
            chart("Population", pts.Select(p => (double)p.Population), v => Num((long)v), Bb.R);
            chart("Treasury", pts.Select(p => (double)p.Funds), v => Money((long)v), Gold);
            chart("Jobs", pts.Select(p => (double)p.Jobs), v => Num((long)v), Bb.C);
            sb.Append(Small(Dim($"{label(pts[0])} to {label(pts[^1])}"))).Append('\n');
            sb.Append(H4("What happens"));
            if (f.Events.Count == 0) sb.Append(Dim("Nothing notable.")).Append('\n');
            foreach (var e in f.Events)
            {
                string text = $"{Dim($"{Months[Math.Clamp(e.Month - 1, 0, 11)]} {e.Year}")} {Col(Severity(e.Severity), "●")} {Esc(e.Text)}";
                sb.Append(e.X >= 0 ? Link($"jump:{e.X}:{e.Y}", text) : text).Append('\n');
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // People: approval, petitions and deals, advisors, news

    string RenderPeople()
    {
        var sb = new StringBuilder(H3("People"));
        var c = Civic;
        if (c == null) return sb.Append(Dim("Loading…")).ToString();
        sb.Append($"Mayor approval [b]{c.Approval}%[/b]  {Topic("approval")}\n{Bar(c.Approval / 100.0, c.Approval >= 40 ? Pos : Neg, 24)}\n");
        if (Advisors != null && Advisors.ApprovalFactors.Count > 0)
            sb.Append(Small(Table(Advisors.ApprovalFactors.Select(f => (Dim(Esc(f.Label)), PosNeg(f.Value, Signed(f.Value)))))));
        var open = c.Prompts.Where(p => p.Status == PromptStatus.PromptOpen).ToList();
        sb.Append(H4($"Waiting for you ({open.Count})"));
        if (open.Count == 0) sb.Append(Dim("No petitions or offers right now.")).Append('\n');
        foreach (var p in open)
        {
            bool deal = p.Kind == "deal";
            sb.Append('\n').Append(Small(Dim($"{(deal ? "Deal" : $"Petition from {Esc(p.Petitioner)}")} · {Plural(p.MonthsLeft, "month")} left"))).Append('\n');
            sb.Append(Col(deal ? Blue : Gold, "● ")).Append(Esc(p.Text)).Append('\n');
            if (!string.IsNullOrEmpty(p.Goal)) sb.Append(Small($"Wants: {Esc(p.Goal)}")).Append('\n');
            if (!string.IsNullOrEmpty(p.Progress)) sb.Append(Small(Dim($"Now: {Esc(p.Progress)}. It clears the day this is met."))).Append('\n');
            var acts = new List<string>();
            if (deal) { acts.Add($"[b]{Link($"respond:{p.Id}:1", "Accept")}[/b]"); acts.Add(Link($"respond:{p.Id}:0", "Decline")); }
            else acts.Add(Link($"respond:{p.Id}:0", "Dismiss"));
            acts.Add(Where(p.X, p.Y, p.Area));
            acts.Add(FixLink(p.FixType, p.X, p.Y, p.Area));
            sb.Append("   ").Append(string.Join("   ", acts.Where(s => s != ""))).Append('\n');
        }
        sb.Append(H4("Advisors"));
        if (Advisors == null) sb.Append(Dim("Asking the advisors…")).Append('\n');
        else foreach (var a in Advisors.Advisors)
        {
            string face = a.Mood switch { "worried" => "😟", "alarmed" => "😱", _ => "🙂" };
            sb.Append($"\n{face} [b]{Esc(a.Name)}[/b] {Small(Dim(Esc(a.Title)))}\n");
            foreach (var m in a.Messages)
            {
                var links = new[] { Where(m.X, m.Y, m.Area, "Go →"), FixLink(m.FixType, m.X, m.Y, m.Area), Topic(m.Topic, "Why?") }.Where(s => s != "");
                sb.Append($"   {Col(Severity(m.Severity), "●")} {Esc(m.Text)}  {Small(string.Join("  ", links))}\n");
            }
        }
        var done = c.Prompts.Where(p => p.Status != PromptStatus.PromptOpen).TakeLast(6).Reverse().ToList();
        if (done.Count > 0)
        {
            sb.Append(H4("Recently settled"));
            foreach (var p in done)
            {
                string text = Col(p.Status is PromptStatus.PromptFulfilled or PromptStatus.PromptAccepted ? Pos : Warn, "● ") + Esc(p.ResultText);
                sb.Append(p.X >= 0 ? Link($"jump:{p.X}:{p.Y}", text) : text).Append('\n');
            }
        }
        sb.Append(H4("News"));
        foreach (var h in Headlines.Take(12))
        {
            string text = Col(Severity(h.Severity), "● ") + Esc(h.Text);
            sb.Append(h.X >= 0 ? Link($"jump:{h.X}:{h.Y}", text) : text).Append('\n');
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Laws

    string RenderLaws()
    {
        string[] status = { "Locked", "Available", "Offered", "Enacted" };
        var sb = new StringBuilder(H3("Laws"));
        sb.Append(Dim("Ordinances change how the city runs. Each preview is the ordinance's own rule graph run against your city right now. ")).Append(Topic("ordinances")).Append('\n');
        var enacted = Ordinances.Where(o => o.Status == OrdinanceStatus.OrdinanceEnacted).ToList();
        long net = enacted.Sum(o => o.MonthlyNet);
        sb.Append($"{enacted.Count} enacted, {(net >= 0 ? "+" : "-")}${Num(Math.Abs(net))} a month.\n");
        if (Ordinances.Count == 0) sb.Append(Dim("Loading…")).Append('\n');
        foreach (var group in Ordinances.GroupBy(o => o.Category))
        {
            sb.Append(H4(Esc(group.Key)));
            foreach (var o in group)
            {
                bool open = _openLaw == o.Id;
                int st = (int)o.Status;
                string left = o.MonthsLeft > 0 ? $" · {Plural(o.MonthsLeft, "month")} {(o.Status == OrdinanceStatus.OrdinanceOffered ? "to decide" : "left")}" : "";
                string money = o.MonthlyNet == 0 ? "free" : $"{(o.MonthlyNet > 0 ? "+" : "-")}${Num(Math.Abs(o.MonthlyNet))}/mo";
                string chip = Col(st switch { 3 => Pos, 2 => Gold, 1 => "#d8dee8", _ => Muted }, $"{status[st]}{left}");
                sb.Append($"{(open ? "▾" : "▸")} [b]{Link($"law:{o.Id}", Esc(o.Name))}[/b]  {Small(chip)}  {Small(PosNeg(o.MonthlyNet, money))}\n");
                if (!open)
                {
                    sb.Append("   ").Append(Small(Dim(Esc(o.Effects.Count > 0 ? string.Join(", ", o.Effects) : o.Description)))).Append('\n');
                    continue;
                }
                sb.Append(Esc(o.Description)).Append('\n').Append(Small(Dim(Esc(o.Availability)))).Append('\n');
                if (o.Effects.Count > 0)
                {
                    sb.Append(H4("Effects now"));
                    foreach (var e in o.Effects) sb.Append("• ").Append(Esc(e)).Append('\n');
                }
                sb.Append(H4("How it works out in your city"));
                sb.Append(Table(o.Steps.Select(s => (Dim(Esc(s.Key)), Esc(s.Value)))));
                var acts = new List<string>();
                if (o.Status == OrdinanceStatus.OrdinanceEnacted) acts.Add(Link($"enact:{o.Id}:0", "Repeal"));
                else if (o.Status != OrdinanceStatus.OrdinanceLocked) acts.Add($"[b]{Link($"enact:{o.Id}:1", "Enact")}[/b]");
                if (o.Status != OrdinanceStatus.OrdinanceLocked) acts.Add(Link($"compare:{o.Id}", "Forecast with and without"));
                acts.Add(Topic(o.Id, "Guide"));
                sb.Append(string.Join("   ", acts)).Append('\n');
                if (_compare is { } c && c.Id == o.Id)
                {
                    if (c.Base is not { Points.Count: > 0 } bf || c.Plan is not { Points.Count: > 0 } pf) sb.Append(Dim("Forecasting a year both ways…")).Append('\n');
                    else
                    {
                        var b = bf.Points[^1];
                        var p = pf.Points[^1];
                        var cells = new StringBuilder("[cell]In a year  [/cell][cell]" + Dim("Without") + "  [/cell][cell]" + Dim("With") + "  [/cell][cell]" + Dim("Change") + "[/cell]");
                        void row(string label, long x, long y, Func<long, string> fmt) =>
                            cells.Append($"[cell]{label}  [/cell][cell]{fmt(x)}  [/cell][cell]{fmt(y)}  [/cell][cell]{PosNeg(y - x, (y - x >= 0 ? "+" : "") + fmt(y - x))}[/cell]");
                        row("Population", b.Population, p.Population, Num);
                        row("Jobs", b.Jobs, p.Jobs, Num);
                        row("Treasury", b.Funds, p.Funds, Money);
                        row("Power use %", b.PowerPercent, p.PowerPercent, Num);
                        row("Abandoned", b.Abandoned, p.Abandoned, Num);
                        sb.Append($"[table=4]{cells}[/table]\n");
                    }
                }
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Budget

    static readonly string[] TaxTags = { "Residential $", "Residential $$", "Residential $$$", "Commercial $", "Commercial $$", "Commercial $$$", "Industry: dirty", "Industry: manufacturing", "Industry: high-tech" };

    string RenderBudget()
    {
        var s = _city.Stats;
        var sb = new StringBuilder(H3($"{Esc(s.CityName)} budget"));
        if (s.TaxTenths.Count < 9) return sb.Append(Dim("Loading…")).ToString();
        sb.Append(Dim("Last month")).Append('\n');
        var rows = s.Budget.Select(l => (Esc(l.Label), PosNeg(l.Amount, Money(l.Amount)))).ToList();
        if (rows.Count == 0) rows.Add(("No month has closed yet.", ""));
        long net = s.LastMonthIncome - s.LastMonthExpenses;
        rows.Add(("[b]Net[/b]", $"[b]{PosNeg(net, Money(net))}[/b]"));
        rows.Add(("Treasury", Money(s.Funds)));
        rows.Add(("Debt", Money(s.Debt)));
        sb.Append(Table(rows));
        sb.Append(H4("Taxes"));
        var cells = new StringBuilder();
        for (int k = 0; k < 9; k++)
        {
            int t = s.TaxTenths[k];
            string step(int by, string label) => t + by is >= 0 and <= 200 ? Link($"tax:{k}:{t + by}", label) : Dim(label);
            cells.Append($"[cell]{TaxTags[k]}  [/cell][cell]{step(-5, "−")}  [b]{t / 10.0:0.0}%[/b]  {step(5, "+")}[/cell]");
        }
        sb.Append($"[table=2]{cells}[/table]\n");
        sb.Append(Small(Dim("Every point above 9% lowers that group's demand; every point below raises it."))).Append('\n');
        sb.Append(H4("Utilities"));
        string util(long used, long cap) => Col(used > cap ? Neg : "#d8dee8", $"{Num(used)} / {Num(cap)}");
        sb.Append(Table(new[]
        {
            ("Power (kW)", util(s.PowerDemandKw, s.PowerCapacityKw)),
            ("Water", util(s.WaterDemand, s.WaterCapacity)),
            ("Garbage", util(s.GarbageProduced, s.GarbageCapacity)),
            ("Jobs filled", $"{Num(s.Workers)} / {Num(s.Jobs)}"),
        }));
        sb.Append(H4("Loans"));
        sb.Append(Link("loan:10000", "Borrow $10,000")).Append("   ").Append(Link("loan:50000", "Borrow $50,000")).Append('\n');
        sb.Append(Small(Dim("Loans are repaid over about 10 years with 6% yearly interest."))).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Layers

    string RenderLayers()
    {
        var sb = new StringBuilder(H3("Map layers"));
        string choice(int kind, string name) => kind == ActiveOverlay ? $"[b]{Col(Gold, name)}[/b]" : Link($"overlay:{kind}", name);
        sb.Append(choice(0, "None"));
        foreach (var (kind, name, _) in OverlayKinds) sb.Append("  ·  ").Append(choice(kind, name));
        sb.Append('\n');
        if (Overlay is { } o && (int)o.Kind == ActiveOverlay && ActiveOverlay != 0)
        {
            var scheme = OverlayKinds.First(k => k.Kind == ActiveOverlay).Scheme;
            string ramp = scheme switch
            {
                "bad" => Col("#b47a1e", "███") + Col("#ff8a1e", "███") + Col("#ff141e", "███"),
                "diverging" => Col("#dc3228", "███") + Col("#ffffff", "███") + Col("#28ff46", "███"),
                "binary" => Col("#c82828", "███") + Col("#f0aa1e", "███") + Col("#3cc85a", "███"),
                _ => Col("#2878c8", "███") + Col("#28c0a0", "███") + Col("#28ff50", "███"),
            };
            sb.Append($"\n{Esc(o.LegendLow is { Length: > 0 } lo ? lo : "low")} {ramp} {Esc(o.LegendHigh is { Length: > 0 } hi ? hi : "high")}\n");
        }
        sb.Append(H4("Commuters"));
        sb.Append(Link("agents", ShowAgents ? "Showing commuters (M)" : "Show commuters (M)")).Append('\n');
        if (Roster is { } r)
            sb.Append(Small(Dim($"{Num(r.Full)} people followed daily ({Num(r.Trips.Count)} drawn), {Num(r.Light)} checked monthly, of {Num(r.Citizens)}. This machine's limits: {Num(r.FullTier)} and {Num(r.LightTier)} ({Esc(r.Machine)}). Yellow drive, blue ride transit; the map runs the morning and evening rush on a loop."))).Append('\n');
        sb.Append(H4("Bus company"));
        sb.Append(Link("routes", ShowRoutes ? "Showing bus routes" : "Show bus routes")).Append('\n');
        sb.Append(H4("Underground"));
        sb.Append(Link("under", ShowUnderground ? "Showing pipes (U)" : "Show water pipes (U)")).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Guide

    string RenderGuide()
    {
        var byId = Articles.ToDictionary(a => a.Id);
        if (_article != "" && byId.TryGetValue(_article, out var a))
        {
            var sb = new StringBuilder(Link("guide-back", "← All articles")).Append('\n');
            sb.Append(H3(Esc(a.Title))).Append(Small(Dim(Esc(a.Category)))).Append('\n');
            foreach (var p in a.Paragraphs) sb.Append('\n').Append(Esc(p)).Append('\n');
            if (a.Facts.Count > 0) sb.Append(H4("In your city now")).Append(Table(a.Facts.Select(f => (Dim(Esc(f.Key)), Esc(f.Value)))));
            if (a.SeeAlso.Count > 0)
                sb.Append(H4("See also")).Append(string.Join("  ·  ", a.SeeAlso.Select(s => Topic(s, Esc(byId.TryGetValue(s, out var x) ? x.Title : s))))).Append('\n');
            return sb.ToString();
        }
        var g = new StringBuilder(H3("Guide"));
        g.Append(Dim("Every rule in the game, written from the live city.")).Append('\n');
        if (Articles.Count == 0) return g.Append(Dim("Loading…")).ToString();
        string q = Input.Trim().ToLowerInvariant();
        var hits = Articles.Where(x => q == "" || x.Title.ToLowerInvariant().Contains(q) || x.Paragraphs.Any(p => p.ToLowerInvariant().Contains(q)));
        foreach (var grp in hits.GroupBy(x => x.Category))
            g.Append(H4(Esc(grp.Key))).Append(string.Join("  ·  ", grp.Select(x => Topic(x.Id, Esc(x.Title))))).Append('\n');
        return g.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Who runs the city, house rules and the report

    /// <summary>What the seat button says: who runs the city now.</summary>
    public string SeatLabel => Mayor is { } m
        ? $"{new[] { "🧑", "⏸", "🛠", "🤖" }[(int)m.Level]} {(m.Level == SeatLevel.SeatManual ? "Mayor" : Levels[(int)m.Level])}{(m.Level == SeatLevel.SeatAutopilot ? $" ({Styles[(int)m.Style].ToLowerInvariant()})" : "")}"
        : "Mayor";

    /// <summary>The banner while the computer has the city, or null while the player does.</summary>
    public string? AwayText => Mayor is { } m && m.Level != SeatLevel.SeatManual
        ? m.Level == SeatLevel.SeatStasis ? "The city is in stasis."
        : m.Level == SeatLevel.SeatCaretaker ? "The computer is minding the city (caretaker)."
        : $"The computer is playing the city (autopilot, {Styles[(int)m.Style].ToLowerInvariant()})."
        : null;

    static string Footer(params string[] links) => "\n" + string.Join("     ", links.Where(l => l != "")) + "\n";

    string RenderSeat()
    {
        var sb = new StringBuilder(H3("Who runs the city"));
        sb.Append(Small(Dim("Hand the city to the computer when you step away. Take it back any time and you get a report of what happened."))).Append('\n');
        for (int i = 0; i < Levels.Length; i++)
        {
            bool on = (int)_seatDraft.Level == i;
            sb.Append($"\n{(on ? "◉" : "○")} [b]{(on ? Levels[i] : Link($"level:{i}", Levels[i]))}[/b]\n   {Small(Dim(LevelHelp[i]))}\n");
        }
        if (_seatDraft.Level == SeatLevel.SeatAutopilot)
        {
            sb.Append(H4("Autopilot style"));
            for (int i = 0; i < Styles.Length; i++)
                sb.Append((int)_seatDraft.Style == i ? $"[b]{Col(Gold, Styles[i])}[/b]" : Link($"style:{i}", Styles[i])).Append("  ");
            sb.Append('\n').Append(Small(Dim(StyleHelp[(int)_seatDraft.Style]))).Append('\n');
        }
        if (_seatDraft.Level is SeatLevel.SeatCaretaker or SeatLevel.SeatAutopilot)
            sb.Append(H4("Most the computer may spend in a month")).Append($"{Link("cap:-5000", "−5,000")} {Link("cap:-1000", "−1,000")}  [b]{Money(_seatDraft.Cap)}[/b]  {Link("cap:1000", "+1,000")} {Link("cap:5000", "+5,000")}\n");
        if (OnServer() && Mayor is { } mayor && mayor.Owner == Me())
        {
            sb.Append(H4("While you're away from this server"));
            for (int i = 1; i < Levels.Length; i++)
                sb.Append((int)mayor.AwayLevel == i ? $"[b]{Col(Gold, Levels[i])}[/b]" : Link($"away:{i}", Levels[i])).Append("  ");
            sb.Append('\n').Append(Small(Dim("When your last window closes, the computer plays the city this way after a short wait, and hands it back when you return."))).Append('\n');
        }
        var others = Civic?.Seats.Where(s => s.Id != "mayor").ToList() ?? new();
        if (others.Count > 0)
        {
            sb.Append(H4("Side games"));
            foreach (var s in others)
                sb.Append($"[b]{Esc(s.Name)}[/b] {Small(Dim($"{(string.IsNullOrEmpty(s.Owner) ? "nobody runs it" : $"run by {Esc(s.Owner)}")} · {Levels[(int)s.Level].ToLowerInvariant()}"))}  "
                    + (s.Owner == Me() && s.Level == SeatLevel.SeatManual ? Link($"leave:{s.Id}", "Hand to the computer") : Link($"claim:{s.Id}", "Run it yourself")) + "\n");
        }
        sb.Append(PartingText());
        return sb.Append(Footer(Link("dialog:consent", "House rules…"), Link("dialog:council", "Deputies and council…"), Link("close", "Cancel"), $"[b]{Link("seat-apply", "Apply")}[/b]")).ToString();
    }

    /// <summary>Parting ways: the mayor may cut players loose on the rules' grounds; anyone else may leave with a copy.</summary>
    string PartingText()
    {
        if (Civic == null) return "";
        bool mayor = Mayor?.Owner == Me();
        var mine = Civic.DelinkOptions.FirstOrDefault(o => o.Player == Me());
        if (!mayor && mine == null && Civic.Delinks.Count == 0) return "";
        var sb = new StringBuilder(H4("Parting ways"));
        if (mayor)
            foreach (var o in Civic.DelinkOptions)
                sb.Append($"[b]{Esc(o.Player)}[/b]  {(o.Open ? Link($"delink:{Uri.EscapeDataString(o.Player)}", "De-link…") : Dim("De-link"))}\n   {Small(Dim(Esc(o.Rule)))}\n");
        else if (mine != null)
            sb.Append(Link($"delink:{Uri.EscapeDataString(Me())}", "Leave with a copy…")).Append("  ")
                .Append(Small(Dim("You take a copy of the city as it is now, with your seats and lots, and become its mayor. This city carries on without you."))).Append('\n');
        foreach (var d in Civic.Delinks.Take(4))
            sb.Append(Small(Dim($"Day {d.Day}: {Esc(d.Player)} {(d.By == d.Player ? "left" : $"was de-linked by {Esc(d.By)}")}"
                + (d.LeasesEnded > 0 ? $", {Plural(d.LeasesEnded, "lease")} ended" : "") + (d.WrittenOff > 0 ? $", {Money(d.WrittenOff)} written off" : "")
                + (d.Paid > 0 ? $", {Money(d.Paid)} paid" : "") + "." + (d.CooldownDays > 0 ? $" They can share a city with {Esc(d.Mayor)} again in {Plural(d.CooldownDays, "day")}." : "")))).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// House rules: every seat owner against every mechanic. A mechanic is on only while everyone who owns a seat
    /// allows it, so the table names who is blocking each one. The player can only change their own column.
    /// </summary>
    string RenderConsent()
    {
        var sb = new StringBuilder(H3("House rules"));
        sb.Append(Small(Dim("Each player switches these on or off for themselves. One that reaches everyone is on only while every player who owns a seat allows it. One between two players needs only those two to allow it, or a pact between them. The host and the city can forbid any of them outright."))).Append('\n');
        var consent = Civic?.Consent;
        if (consent == null) return sb.Append(Footer($"[b]{Link("close", "Done")}[/b]")).ToString();
        string me = Me();
        bool mayor = Mayor?.Owner == me;
        var owners = consent.Owners.ToList();
        var cols = owners.Contains(me) ? owners : owners.Append(me).ToList();
        var cells = new StringBuilder("[cell] [/cell]");
        foreach (var o in cols) cells.Append($"[cell][b]{Esc(o == me ? "You" : o)}[/b]  [/cell]");
        if (mayor) cells.Append("[cell][b]City forbids[/b]  [/cell]");
        cells.Append("[cell][b]In this city[/b][/cell]");
        foreach (var r in consent.Rows)
        {
            cells.Append($"[cell][b]{Esc(r.Name)}[/b]\n{Small(Dim(Esc(r.Note)))}  [/cell]");
            foreach (var o in cols)
            {
                int i = owners.IndexOf(o);
                bool on = i >= 0 && i < r.ByOwner.Count ? r.ByOwner[i] : r.Mechanic == Mechanic.MechAbsurdDisasters;
                cells.Append(o == me ? $"[cell]{Link($"consent:{(int)r.Mechanic}:{(on ? 0 : 1)}", on ? "☑" : "☐")}  [/cell]" : $"[cell]{(on ? "✓" : "✗")}  [/cell]");
            }
            if (mayor) cells.Append($"[cell]{Link($"rule:{(int)r.Mechanic}:{(r.CityForbids ? 0 : 1)}", r.CityForbids ? "☑" : "☐")}  [/cell]");
            string who = string.Join(", ", r.Blockers.Select(b => b == me ? "you" : b));
            string result = r.HostForbids ? Col(Neg, "Forbidden") + Small(Dim(" by the host"))
                : r.CityForbids ? Col(Neg, "Forbidden") + Small(Dim(" by the city"))
                : r.Pairwise ? "Between two" + Small(Dim(who.Length > 0 ? $" (not with {Esc(who)})" : " (everyone allows it)"))
                : r.Effective ? Col(Pos, "On") : Col(Neg, "Off") + (who.Length > 0 ? Small(Dim($" ({Esc(who)})")) : "");
            cells.Append($"[cell]{result}[/cell]");
        }
        sb.Append($"[table={cols.Count + (mayor ? 3 : 2)}]{cells}[/table]\n");

        sb.Append(H4("Pacts"));
        string MechName(Mechanic m) => consent.Rows.FirstOrDefault(r => r.Mechanic == m)?.Name.ToLowerInvariant() ?? "";
        string Pl(int step, Mechanic m, string with, string text) => Link($"pact:{step}:{(int)m}:{Uri.EscapeDataString(with)}", text);
        bool any = false;
        foreach (var p in consent.Pacts.Where(p => p.A == me || p.B == me))
        {
            any = true;
            string other = p.A == me ? p.B : p.A;
            if (!p.Offer) sb.Append($"Pact with [b]{Esc(other)}[/b]: {MechName(p.Mechanic)}  {Pl(2, p.Mechanic, other, "End it")}\n");
            else if (p.B == me) sb.Append($"[b]{Esc(other)}[/b] offers a pact: {MechName(p.Mechanic)}  {Pl(1, p.Mechanic, other, "Accept")}  {Pl(2, p.Mechanic, other, "Turn down")}\n");
            else sb.Append($"Offered [b]{Esc(other)}[/b]: {MechName(p.Mechanic)}  {Pl(2, p.Mechanic, other, "Withdraw")}\n");
        }
        if (!any) sb.Append(Small(Dim("None yet. A pact lets something between two players happen between just you two, whatever either of you allows in general."))).Append('\n');
        var offerable = consent.Rows.Where(r => r.Pairwise && !r.HostForbids && !r.CityForbids).ToList();
        foreach (var o in owners.Where(o => o != me))
            sb.Append($"Offer [b]{Esc(o)}[/b] a pact on: {string.Join("  ", offerable.Select(r => Pl(0, r.Mechanic, o, Esc(r.Name.ToLowerInvariant()))))}\n");
        return sb.Append(Footer($"[b]{Link("close", "Done")}[/b]")).ToString();
    }

    static readonly string[] Portfolios = { "Planning", "Budget", "Services", "Transit" };

    /// <summary>
    /// Deputies and the council. The mayor names deputies, each with the portfolios they may act in, and can seat a
    /// council that votes on taxes, laws and loans. Members vote here; ties go the way the mayor voted.
    /// </summary>
    string RenderCouncil()
    {
        var sb = new StringBuilder(H3("Deputies and council"));
        sb.Append(Small(Dim("Deputies act for the mayor in their portfolios: planning (roads, zones, utilities), the budget (taxes, funding, loans), services (civic buildings) and transit (stops, depots and leases)."))).Append('\n');
        var c = Civic;
        if (c == null) return sb.Append(Footer($"[b]{Link("close", "Done")}[/b]")).ToString();
        string me = Me(), mayorName = Mayor?.Owner ?? "";
        bool mayor = mayorName == me || (mayorName == "" && !OnServer());
        var people = Players.Concat(c.Deputies.Select(d => d.Player)).Distinct().Where(n => n != mayorName).ToList();
        if (people.Count == 0) sb.Append(Small(Dim("Nobody else is here to make a deputy."))).Append('\n');
        else
        {
            var cells = new StringBuilder("[cell] [/cell]");
            foreach (var p in Portfolios) cells.Append($"[cell][b]{p}[/b]  [/cell]");
            foreach (var n in people)
            {
                uint bits = c.Deputies.FirstOrDefault(d => d.Player == n)?.Portfolios ?? 0;
                cells.Append($"[cell]{Esc(n == me ? "You" : n)}  [/cell]");
                for (int i = 0; i < Portfolios.Length; i++)
                {
                    bool on = (bits & (1u << i)) != 0;
                    string box = on ? "☑" : "☐";
                    cells.Append($"[cell]{(mayor ? Link($"deputy:{bits ^ (1u << i)}:{Uri.EscapeDataString(n)}", box) : box)}  [/cell]");
                }
            }
            sb.Append($"[table={Portfolios.Length + 1}]{cells}[/table]\n");
        }
        sb.Append(H4("Council"));
        string toggle = c.CouncilOn ? "☑ The council sits" : "☐ The council sits";
        sb.Append(mayor ? Link($"council:{(c.CouncilOn ? 0 : 1)}", toggle) : toggle).Append('\n');
        sb.Append(Small(Dim(c.CouncilOn ? $"Members: {Esc(string.Join(", ", c.Council))}. Tax changes, laws and loans go to a vote for 7 days; ties go to the mayor." : "Off: the mayor decides alone."))).Append('\n');
        foreach (var p in c.Proposals)
        {
            string tally = $"{p.Yes.Count} for, {p.No.Count} against";
            sb.Append($"\n[b]{Esc(p.Text)}[/b] {Small(Dim($"by {Esc(p.Proposer)} · {(p.Status == "open" ? $"{p.DaysLeft} days left · {tally}" : $"{p.Status} ({tally})")}"))}");
            if (p.Status == "open" && c.Council.Contains(me))
            {
                string mine = p.Yes.Contains(me) ? "yes" : p.No.Contains(me) ? "no" : "";
                sb.Append("  ").Append(mine == "yes" ? $"[b]{Col(Gold, "Yes")}[/b]" : Link($"vote:{p.Id}:1", "Yes"))
                    .Append("  ").Append(mine == "no" ? $"[b]{Col(Gold, "No")}[/b]" : Link($"vote:{p.Id}:0", "No"));
            }
            sb.Append('\n');
        }
        return sb.Append(Footer($"[b]{Link("close", "Done")}[/b]")).ToString();
    }

    string RenderReport()
    {
        var r = Mayor?.Report;
        var sb = new StringBuilder(H3("While you were away"));
        if (r == null) return sb.Append(Footer($"[b]{Link("close", "Back to it")}[/b]")).ToString();
        foreach (var c in r.Changes) sb.Append("• ").Append(Esc(c)).Append('\n');
        if (r.Notes.Count > 0)
        {
            sb.Append(H4($"What the computer did ({r.Notes.Count})"));
            foreach (var n in r.Notes.TakeLast(40)) sb.Append(n.X >= 0 ? Link($"jump:{n.X}:{n.Y}", Esc(n.Text) + " →") : Esc(n.Text)).Append('\n');
        }
        if (r.News.Count > 0)
        {
            sb.Append(H4("In the news"));
            foreach (var t in r.News.TakeLast(12)) sb.Append(Dim("● ")).Append(Esc(t)).Append('\n');
        }
        return sb.Append(Footer($"[b]{Link("close", "Back to it")}[/b]")).ToString();
    }

    string RenderTerms()
    {
        if (_terms is not { } tm) return "";
        var t = tm.Terms;
        var sb = new StringBuilder(H3(Esc(tm.Title)));
        if (tm.Note != "") sb.Append(Small(Dim(Esc(tm.Note)))).Append('\n');
        bool pub = t.Mode == LeaseMode.LeasePublicService;
        sb.Append($"\n{(pub ? "○" : "◉")} [b]{(pub ? Link("terms:mode:0", "Business lease") : "Business lease")}[/b] {Small(Dim("the operator pays rent and a fare tax and keeps the profit"))}\n");
        sb.Append($"{(pub ? "◉" : "○")} [b]{(pub ? "Public service" : Link("terms:mode:1", "Public service"))}[/b] {Small(Dim("the city pays a subsidy and caps fares"))}\n\n");
        string step(string field, string by, string label) => Link($"terms:{field}:{by}", label);
        if (!pub)
        {
            sb.Append($"Rent a month  {step("rent", "-100", "−100")} {step("rent", "-10", "−10")}  [b]{Money(t.RentPerMonth)}[/b]  {step("rent", "10", "+10")} {step("rent", "100", "+100")}\n");
            sb.Append($"Fare tax  {step("tax", "-5", "−0.5")}  [b]{t.TaxTenths / 10.0:0.0}%[/b]  {step("tax", "5", "+0.5")}\n");
        }
        else
        {
            sb.Append($"Subsidy a month  {step("subsidy", "-100", "−100")} {step("subsidy", "-10", "−10")}  [b]{Money(t.SubsidyPerMonth)}[/b]  {step("subsidy", "10", "+10")} {step("subsidy", "100", "+100")}\n");
            sb.Append($"Fare cap  {step("farecap", "-25", "−0.25")}  [b]{Fare(t.FareCapCents)}[/b]  {step("farecap", "25", "+0.25")}\n");
        }
        sb.Append($"Notice for future changes  {step("notice", "-1", "−")}  [b]{Plural(t.NoticeMonths, "month")}[/b]  {step("notice", "1", "+")}\n");
        return sb.Append(Footer(Link("close", "Cancel"), $"[b]{Link("terms:ok", Esc(tm.Ok))}[/b]")).ToString();
    }

    string RenderConfirm() =>
        _confirm is { } c ? H3("Are you sure?") + Esc(c.Text) + "\n" + Footer(Link("close", "Cancel"), $"[b]{Link("yes", "Yes")}[/b]") : "";

    string RenderNew()
    {
        var sb = new StringBuilder(H3("New city"));
        sb.Append(Dim("Name it in the box above.")).Append('\n').Append(H4("Map size"));
        foreach (var (size, label) in new[] { (64, "Small (64 × 64, 1 km)"), (128, "Medium (128 × 128, 2 km)"), (256, "Large (256 × 256, 4 km)") })
            sb.Append(_newSize == size ? $"◉ [b]{label}[/b]" : $"○ {Link($"size:{size}", label)}").Append('\n');
        sb.Append('\n').Append(Link("starter", $"{(_newStarter ? "☑" : "☐")} Start with a small working town")).Append('\n');
        sb.Append(Link("bench", $"{(_newBench ? "☑" : "☐")} Benchmark city: 256 × 256, every block built")).Append('\n');
        return sb.Append(Footer(Link("close", "Cancel"), $"[b]{Link("new-ok", "Found city")}[/b]")).ToString();
    }

    string RenderSave()
    {
        var sb = new StringBuilder(H3("Save city"));
        sb.Append(Dim("Name the save in the box above: letters, digits, spaces, _ and -.")).Append('\n');
        if (Saves.Count > 0) sb.Append(H4("Existing saves")).Append(Dim(Esc(string.Join(", ", Saves)))).Append('\n');
        return sb.Append(Footer(Link("close", "Cancel"), $"[b]{Link("save-ok", "Save")}[/b]")).ToString();
    }

    string RenderLoad()
    {
        var sb = new StringBuilder(H3("Load city"));
        if (Saves.Count == 0) sb.Append(Dim("No saves yet.")).Append('\n');
        foreach (var n in Saves) sb.Append("• ").Append(Link($"load:{Arg(n)}", Esc(n))).Append('\n');
        return sb.Append(Footer(Link("close", "Cancel"))).ToString();
    }
}
