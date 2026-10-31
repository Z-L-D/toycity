using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// The computer playing the city seat while its owner is away. It runs monthly after the budget and applies what it
/// does directly rather than through the action log: it is deterministic, so a replay of the log runs it again and
/// gets the same city. Everything it does goes in the "while you were away" notes with its reason.
/// </summary>
public sealed partial class World
{
    long _autoSpent;

    void RunAutomation()
    {
        var seat = MayorSeat;
        if (seat.Level is not (OperatorLevel.Caretaker or OperatorLevel.Autopilot)) return;
        _autoSpent = 0;
        TakeLoanIfRed();
        if (seat.Level == OperatorLevel.Caretaker)
        {
            CaretakerTaxes();
            ReplaceLostCivic();
            foreach (var p in Prompts.Where(p => p.Status == PromptStatus.Open && p.Kind == EventKind.Deal).ToList())
            {
                ApplyRespondPrompt(new RespondPromptAction(p.Id, false));
                AwayNoteAdd($"Turned down a deal ({Content.Events[p.Event].Id.Replace("deal-", "")}): caretaker doesn't take deals.", p.X, p.Y);
            }
            return;
        }
        AutopilotTaxes(seat.Style);
        foreach (var lease in Leases.Where(l => l.Terminable).ToList())
        {
            var b = Buildings[lease.BuildingId]!;
            EndLease(lease, $"The city ended {GameName(lease.GameId)}'s lease on {{0}}.");
            AwayNoteAdd($"Ended {GameName(lease.GameId)}'s lease on {LotName(b)}: it stayed in bad standing through its cure period.", b.X, b.Y);
        }
        ReplaceLostCivic();
        AutopilotUtilities(seat.Style);
        AutopilotPetitions();
        AutopilotDeals(seat.Style);
        AutopilotZoning(seat.Style);
    }

    /// <summary>What the computer may still spend this month: the owner's cap, and never more than the treasury holds.</summary>
    bool Affordable(long cost) => cost <= Funds && _autoSpent + cost <= MayorSeat.SpendCap;

    /// <summary>Applies an action as the computer and counts what it cost against the month's cap.</summary>
    bool Do(SimAction a)
    {
        long before = Funds;
        int notices = Notices.Count;
        Apply(a);
        _autoSpent += Math.Max(0, before - Funds);
        // A rejection leaves a warning; the computer's attempts shouldn't read as the player's mistakes.
        bool ok = !Notices.Skip(notices).Any(n => n.Severity == "warning");
        if (!ok) Notices.RemoveRange(notices, Notices.Count - notices);
        return ok;
    }

    void TakeLoanIfRed()
    {
        if (Funds >= 0) return;
        long amount = (-Funds + 5_000 + 4_999) / 5_000 * 5_000;
        if (Debt + amount > MaxDebt) amount = MaxDebt - Debt;
        if (amount <= 0) return;
        Apply(new TakeLoanAction(amount));
        AwayNoteAdd($"Took a ${amount:N0} loan: the treasury was ${-(Funds - amount):N0} in the red.");
    }

    void SetAllTaxes(Func<int, int> target, string why)
    {
        var before = (int[])TaxTenths.Clone();
        for (int k = 0; k < 9; k++) TaxTenths[k] = Math.Clamp(target(k), 0, 200);
        if (!before.SequenceEqual(TaxTenths))
            AwayNoteAdd($"Moved taxes to an average of {TaxTenths.Average() / 10.0:0.0}% (was {before.Average() / 10.0:0.0}%): {why}.");
    }

    /// <summary>Caretaker keeps taxes within a point of where the owner left them, leaning on whichever way the budget needs.</summary>
    void CaretakerTaxes()
    {
        var baseline = MayorSeat.Away!.TaxTenths;
        if (LastIncome < LastExpenses)
            SetAllTaxes(k => Math.Min(baseline[k] + 10, Math.Max(TaxTenths[k], TaxTenths[k] + 5)), "the city spent more than it took in");
        else if (LastIncome > LastExpenses * 11 / 10 && Approval < 45)
            SetAllTaxes(k => Math.Max(baseline[k] - 10, TaxTenths[k] - 5), "the budget had room and people were unhappy");
    }

    static (int Min, int Max) TaxBand(AutopilotStyle s) => s switch
    {
        AutopilotStyle.Growth => (70, 100),
        AutopilotStyle.Green => (80, 110),
        AutopilotStyle.Profit => (100, 130),
        _ => (50, 150),
    };

    void AutopilotTaxes(AutopilotStyle style)
    {
        var (min, max) = TaxBand(style);
        if (style == AutopilotStyle.Chaos)
        {
            SetAllTaxes(k => min + (int)(Hash((int)Tick, 0x7a, k, 0) % (ulong)(max - min + 1)), "chaos picks taxes out of a hat");
            return;
        }
        // The strongest demand in each zone, averaged: wealth tiers nobody wants shouldn't read as a slump.
        int avgDemand = (int)new[] { Zone.Residential, Zone.Commercial, Zone.Industrial }.Average(z => Enumerable.Range(0, 3).Max(k => DemandFor(z, (Level)k)));
        if (LastIncome < LastExpenses)
            SetAllTaxes(k => Math.Clamp(TaxTenths[k] + 5, min, max), $"the city ran a deficit ({StyleName(style)} keeps taxes between {min / 10}% and {max / 10}%)");
        else if (avgDemand < 10)
            SetAllTaxes(k => Math.Clamp(TaxTenths[k] - 5, min, max), "demand was weak, so lower taxes to draw people in");
        else
            SetAllTaxes(k => Math.Clamp(TaxTenths[k], min, max), $"{StyleName(style)} keeps taxes between {min / 10}% and {max / 10}%");
    }

    /// <summary>Like for like: a civic building that was there at hand-off and is gone (a fire, a tornado) is rebuilt.</summary>
    void ReplaceLostCivic()
    {
        var away = MayorSeat.Away!;
        for (int k = 0; k < away.Civic.Count; k++)
        {
            var (id, type, x, y) = away.Civic[k];
            if (id < Buildings.Count && Buildings[id] is { } b && b.Type == type) continue;
            var t = Content.Types[type];
            if (!Affordable(t.Cost)) continue;
            var site = FindSite(t, x, y, 6, (sx, sy) => true, exact: true);
            if (site is not var (px, py)) continue;
            // On sloping ground the pad's earthworks cost on top of the building.
            if (!Affordable(t.Cost + PadCost(px, py, t.W, t.H))) continue;
            if (!Do(new PlaceBuildingAction(px, py, t.Id))) continue;
            away.Civic[k] = (Buildings.Count - 1, type, px, py);
            AwayNoteAdd($"Rebuilt the {t.Name} that was lost, like for like.", px, py);
        }
    }

    /// <summary>Where most of the city is: the middle of its zoned buildings, or the map's middle before there are any.</summary>
    (int X, int Y) CityCentre()
    {
        long sx = 0, sy = 0, n = 0;
        foreach (var b in Buildings)
            if (b != null && !TypeOf(b).Placeable) { sx += b.X; sy += b.Y; n++; }
        return n == 0 ? (Width / 2, HeightCells / 2) : ((int)(sx / n), (int)(sy / n));
    }

    bool Borders(int x, int y, int w, int h, Func<int, bool> test)
    {
        for (int xx = x - 1; xx <= x + w; xx++)
        for (int yy = y - 1; yy <= y + h; yy++)
        {
            bool edge = xx == x - 1 || xx == x + w || yy == y - 1 || yy == y + h;
            if (!edge || (xx == x - 1 || xx == x + w) && (yy == y - 1 || yy == y + h)) continue; // edges, not corners
            if (InBounds(xx, yy) && test(Idx(xx, yy))) return true;
        }
        return false;
    }

    bool NearResidential(int x, int y, int w, int h, int dist)
    {
        for (int yy = Math.Max(0, y - dist); yy < Math.Min(HeightCells, y + h + dist); yy++)
        for (int xx = Math.Max(0, x - dist); xx < Math.Min(Width, x + w + dist); xx++)
            if (ZoneOf(ZoneCode[Idx(xx, yy)]) == Zone.Residential) return true;
        return false;
    }

    /// <summary>
    /// The nearest valid site for a civic building, searching square rings out from (cx, cy), that has road frontage
    /// and passes <paramref name="extra"/>. With <paramref name="exact"/> the exact spot is tried first even without a road.
    /// </summary>
    (int X, int Y)? FindSite(BuildingType t, int cx, int cy, int maxR, Func<int, int, bool> extra, bool exact = false)
    {
        if (exact && InBounds(cx, cy) && ValidatePlacement(new PlaceBuildingAction(cx, cy, t.Id), out _) == null) return (cx, cy);
        for (int r = 0; r <= maxR; r++)
        for (int y = cy - r; y <= cy + r; y++)
        for (int x = cx - r; x <= cx + r; x++)
        {
            if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != r || !InBounds(x, y)) continue;
            if (ValidatePlacement(new PlaceBuildingAction(x, y, t.Id), out _) != null) continue;
            if (!Borders(x, y, t.W, t.H, i => ProfileAt(i) is { Frontage: true })) continue;
            if (extra(x, y)) return (x, y);
        }
        return null;
    }

    IEnumerable<BuildingType> Unlocked(Category c) => Content.Types.Where(t => t.Placeable && t.Category == c && TypeUnlocked[t.Index]);

    /// <summary>Builds one of a category near the city's middle, if it can afford it and a site exists. Polluters keep away from homes.</summary>
    bool BuildFor(Category c, Func<BuildingType, double> score, string why, int cx, int cy, int maxR, Func<BuildingType, int, int, bool>? extra = null)
    {
        foreach (var t in Unlocked(c).OrderByDescending(score).ThenBy(t => t.Index))
        {
            if (!Affordable(t.Cost)) continue;
            int keepAway = t.Pollution > 0 ? 6 : 0;
            var site = FindSite(t, cx, cy, maxR, (x, y) => (keepAway == 0 || !NearResidential(x, y, t.W, t.H, keepAway)) && (extra?.Invoke(t, x, y) ?? true));
            if (site is not var (x, y)) continue;
            if (!Affordable(t.Cost + PadCost(x, y, t.W, t.H))) continue;
            if (!Do(new PlaceBuildingAction(x, y, t.Id))) continue;
            AwayNoteAdd($"Built {Article(t.Name)} {t.Name}: {why}.", x, y);
            return true;
        }
        return false;
    }

    static string Article(string name) => "AEIOUaeiou".Contains(name[0]) ? "an" : "a";

    void AutopilotUtilities(AutopilotStyle style)
    {
        var (cx, cy) = CityCentre();
        bool anyZoned = ZoneCode.Any(z => z != 0);
        double PlantScore(BuildingType t) => style switch
        {
            AutopilotStyle.Green => t.Pollution == 0 ? t.PowerOutKw : -t.Pollution,
            AutopilotStyle.Chaos => Hash((int)Tick, 0x70, t.Index, 0) % 1000,
            _ => (double)t.PowerOutKw / Math.Max(1, t.Cost),
        };
        if (anyZoned && (PowerCapacity == 0 || PowerDemand * 10 > PowerCapacity * 9))
            BuildFor(Category.Power, PlantScore, PowerCapacity == 0 ? "the city had no power" : $"power use was {PowerDemand * 100 / Math.Max(1, PowerCapacity)}% of what the plants make", cx, cy, 40);
        bool pipes = WaterPipe.Any(p => p != 0);
        if (pipes && WaterDemand * 10 > WaterCapacity * 9)
            BuildFor(Category.Water, t => t.WaterOut, "water use was close to what the pumps supply", cx, cy, 40,
                (t, x, y) => Borders(x, y, t.W, t.H, i => WaterPipe[i] != 0));
        if (GarbageProduced > GarbageCapacity)
            BuildFor(Category.Garbage, t => t.GarbageCap, "garbage was piling up", cx, cy, 40);
    }

    void AutopilotPetitions()
    {
        foreach (var p in Prompts.Where(p => p.Status == PromptStatus.Open && p.Kind == EventKind.Petition).ToList())
        {
            var (fix, _) = PromptHelp(p);
            if (fix.Length == 0) continue;
            var t = Content.Types[Content.TypeIndex(fix)];
            // One answer per petition: if one of these already stands nearby, wait for it to work.
            if (Buildings.Any(b => b != null && b.Type == t.Index && Math.Abs(b.X - p.X) <= 8 && Math.Abs(b.Y - p.Y) <= 8)) continue;
            BuildFor(t.Category, c => c.Index == t.Index ? 1 : 0, $"answering a petition from {p.Petitioner}", p.X, p.Y, 8,
                (c, x, y) => c.Index == t.Index);
        }
    }

    /// <summary>Weighs a deal by what accepting it does: money in or out, mood, and what it brings to town.</summary>
    bool WantsDeal(AutopilotStyle style, Prompt p)
    {
        var ev = Content.Events[p.Event];
        if (style == AutopilotStyle.Chaos) return Hash((int)Tick, 0x64, p.Id, 0) % 2 == 0;
        long money = 0, mood = 0, demand = 0;
        bool polluter = false;
        long Const(RuleNode n) => n.Consts.Length > 0 && n.InputIsConst.Length > 0 && n.InputIsConst[0] ? n.Consts[0] : 0;
        foreach (var n in ev.Yes?.Graph.Nodes ?? new List<RuleNode>())
        {
            int months = Math.Max(1, ev.Yes!.DurationMonths);
            switch (n.Op)
            {
                case "act.grant": money += Const(n); break;
                case "act.income": money += Const(n) * months; break;
                case "act.expense": money -= Const(n) * months; break;
                case "act.mood": mood += Const(n); break;
                case "act.modifier": demand += n.Key.ToString().StartsWith("Desirability") ? Const(n) * 2 : Const(n); break;
                case "act.unlock":
                    if (n.Params.TryGetValue("type", out var id) && Content.TypeIndex(id) is var ti and >= 0 && Content.Types[ti].Pollution > 0) polluter = true;
                    break;
            }
        }
        return style switch
        {
            AutopilotStyle.Green => !polluter && mood >= 0 && demand >= 0,
            AutopilotStyle.Profit => money > 0,
            _ => money + demand * 200 > 0 && mood > -5,
        };
    }

    void AutopilotDeals(AutopilotStyle style)
    {
        foreach (var p in Prompts.Where(p => p.Status == PromptStatus.Open && p.Kind == EventKind.Deal).ToList())
        {
            bool yes = WantsDeal(style, p);
            ApplyRespondPrompt(new RespondPromptAction(p.Id, yes));
            string name = Content.Events[p.Event].Id.Replace("deal-", "").Replace('-', ' ');
            AwayNoteAdd(yes ? $"Took the {name} deal: it suits a {StyleName(style)} city." : $"Turned down the {name} deal: it doesn't suit a {StyleName(style)} city.", p.X, p.Y);
        }
    }

    void AutopilotZoning(AutopilotStyle style)
    {
        var zones = new[] { Zone.Residential, Zone.Commercial, Zone.Industrial };
        var wanted = style == AutopilotStyle.Chaos
            ? new[] { zones[Hash((int)Tick, 0x5a, 0, 0) % 3] }
            : zones.Where(z => Enumerable.Range(0, 3).Max(w => DemandFor(z, (Level)w)) >= 20)
                   .OrderByDescending(z => Enumerable.Range(0, 3).Max(w => DemandFor(z, (Level)w))).ToArray();
        if (wanted.Length == 0) return;
        var blocks = EmptyBlocks();
        var (cx, cy) = CityCentre();
        var density = Population >= 3_000 ? Level.Medium : Level.Low;
        if (style == AutopilotStyle.Chaos) density = (Level)(Hash((int)Tick, 0x5b, 0, 0) % 3);
        var leftover = style == AutopilotStyle.Green ? Leftover.Parks : Leftover.Leave;
        bool expanded = false;
        foreach (var z in wanted)
        {
            // Homes and shops go near the middle; industry goes as far from homes as the empty blocks allow.
            var pick = blocks
                .OrderBy(b => z == Zone.Industrial ? ResidentialCellsNear(b, 6) : 0)
                .ThenBy(b => Dist2(b[0], cx, cy))
                .ThenBy(b => b[0])
                .FirstOrDefault();
            if (z == Zone.Industrial && pick != null && ResidentialCellsNear(pick, 6) > 0 && style == AutopilotStyle.Green) pick = null;
            if (pick == null)
            {
                if (expanded || !Expand(style)) continue;
                expanded = true;
                blocks = EmptyBlocks();
                pick = blocks.OrderBy(b => Dist2(b[0], cx, cy)).ThenBy(b => b[0]).FirstOrDefault();
                if (pick == null) continue;
            }
            long cost = pick.Count * (long)Content.ZoneCostPerCell;
            if (!Affordable(cost)) continue;
            int x = pick[0] % Width, y = pick[0] / Width;
            if (!Do(new ZoneBlockAction(x, y, z, density, leftover))) continue;
            blocks.Remove(pick);
            AwayNoteAdd(style == AutopilotStyle.Chaos
                ? $"Zoned a block {ZoneName(z)} on a whim."
                : $"Zoned a block {ZoneName(z)}: demand was {Enumerable.Range(0, 3).Max(w => DemandFor(z, (Level)w))}.", x, y);
        }
    }

    int Dist2(int cell, int cx, int cy) { int dx = cell % Width - cx, dy = cell / Width - cy; return dx * dx + dy * dy; }

    int ResidentialCellsNear(List<int> block, int dist)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
        foreach (int c in block) { minX = Math.Min(minX, c % Width); maxX = Math.Max(maxX, c % Width); minY = Math.Min(minY, c / Width); maxY = Math.Max(maxY, c / Width); }
        int n = 0;
        for (int y = Math.Max(0, minY - dist); y <= Math.Min(HeightCells - 1, maxY + dist); y++)
        for (int x = Math.Max(0, minX - dist); x <= Math.Min(Width - 1, maxX + dist); x++)
            if (ZoneOf(ZoneCode[Idx(x, y)]) == Zone.Residential) n++;
        return n;
    }

    /// <summary>Road-enclosed blocks with nothing zoned or built in them yet, each listed once.</summary>
    List<List<int>> EmptyBlocks()
    {
        var seen = new HashSet<int>();
        var list = new List<List<int>>();
        for (int i = 0; i < N; i++)
        {
            if (seen.Contains(i) || Path[i] != 0 || IsWater(i) || BuildingAt[i] != 0 || ZoneCode[i] != 0) continue;
            int x = i % Width, y = i / Width;
            if (!Borders(x, y, 1, 1, n => ProfileAt(n) is { Frontage: true })) continue;
            var block = BlockAt(x, y);
            if (block == null) { seen.Add(i); continue; }
            foreach (int c in block) seen.Add(c);
            if (block.Count >= 6 && block.All(c => ZoneCode[c] == 0 && BuildingAt[c] == 0)) list.Add(block);
        }
        return list;
    }

    const int RingSize = 11;

    /// <summary>
    /// Grows the city by one block: a square ring of street sharing a side with an existing road, split by a
    /// middle street, with a water pipe down the middle joined to the nearest pipe. Ground is levelled first.
    /// </summary>
    bool Expand(AutopilotStyle style)
    {
        var (cx, cy) = CityCentre();
        int s = RingSize - 1;
        (int X, int Y, int Dir, int Target)? best = null;
        int bestD = int.MaxValue;
        for (int y = 0; y + s < HeightCells; y++)
        for (int x = 0; x + s < Width; x++)
        for (int dir = 0; dir < 4; dir++)
        {
            int d = (x + s / 2 - cx) * (x + s / 2 - cx) + (y + s / 2 - cy) * (y + s / 2 - cy);
            if (d >= bestD) continue;
            if (RingFits(x, y, dir, out int target)) { best = (x, y, dir, target); bestD = d; }
        }
        if (best is not var (bx, by, bdir, height)) return false;

        // The shared side is already road; the other three sides and the middle street are new.
        var (sides, middle) = RingRuns(bx, by, bdir);
        long cost = 0;
        int levelled = 0;
        for (int yy = by; yy <= by + s; yy++)
        for (int xx = bx; xx <= bx + s; xx++)
            if (Path[Idx(xx, yy)] == 0 && Height[Idx(xx, yy)] != height) levelled++;
        cost += levelled * (long)Content.TerraformCostPerCell;
        long pathCost = (sides.Count + 1) * RingSize * (long)Content.Profiles[Content.ProfileIndex("street")].CostPerCell;
        cost += pathCost + RingSize * WaterPipeCost;
        if (!Affordable(cost + 2_000)) return false;

        if (levelled > 0) LevelSquare(bx, by, height);
        foreach (var r in sides.Append(middle))
            if (!Do(new PlacePathAction(r.X0, r.Y0, r.X1, r.Y1, "street"))) return false;
        ConnectPipe(middle.X0, middle.Y0);
        Do(new PlaceUtilityAction(middle.X0, middle.Y0, middle.X1, middle.Y1, UtilityKind.WaterPipe));
        RebuildAccess();
        RunNeighborConnections();
        AwayNoteAdd($"Built a new block of streets: there was demand and no empty land to zone ({StyleName(style)}).", bx + s / 2, by + s / 2);
        return true;
    }

    /// <summary>The run of cells on a ring's shared side: dir 0 top, 1 right, 2 bottom, 3 left.</summary>
    static (int X0, int Y0, int X1, int Y1) Side(int x, int y, int dir)
    {
        int s = RingSize - 1;
        return dir switch
        {
            0 => (x, y, x + s, y),
            1 => (x + s, y, x + s, y + s),
            2 => (x, y + s, x + s, y + s),
            _ => (x, y, x, y + s),
        };
    }

    (List<(int X0, int Y0, int X1, int Y1)> Sides, (int X0, int Y0, int X1, int Y1) Middle) RingRuns(int x, int y, int dir)
    {
        var sides = Enumerable.Range(0, 4).Where(d => d != dir).Select(d => Side(x, y, d)).ToList();
        int s = RingSize - 1, m = s / 2;
        // The middle street runs from the shared side to the opposite one, so the new streets join the old road.
        var middle = dir is 0 or 2 ? (x + m, dir == 0 ? y : y + s, x + m, dir == 0 ? y + s : y) : (dir == 3 ? x : x + s, y + m, dir == 3 ? x + s : x, y + m);
        return (sides, middle);
    }

    /// <summary>Levels the ring's square, all but its road cells, to one height.</summary>
    void LevelSquare(int x, int y, int height)
    {
        var works = new List<(int I, int H)>();
        for (int yy = y; yy < y + RingSize; yy++)
        for (int xx = x; xx < x + RingSize; xx++)
        {
            int i = Idx(xx, yy);
            if (Path[i] == 0 && Height[i] != height) works.Add((i, height));
        }
        SetLevels(works);
        long cost = works.Count * Content.TerraformCostPerCell;
        Funds -= cost;
        OneOffs -= cost;
        _autoSpent += cost;
        RecomputeWaterDistance();
    }

    /// <summary>
    /// A ring fits when one side is all existing frontage road and the rest of the square is empty land, low enough
    /// in relief to level to the road's height.
    /// </summary>
    bool RingFits(int x, int y, int dir, out int target)
    {
        target = 0;
        var (sx0, sy0, sx1, sy1) = Side(x, y, dir);
        int lo = int.MaxValue, hi = int.MinValue;
        for (int yy = sy0; yy <= sy1; yy++)
        for (int xx = sx0; xx <= sx1; xx++)
        {
            int i = Idx(xx, yy);
            if (ProfileAt(i) is not { Frontage: true }) return false;
            lo = Math.Min(lo, Height[i]); hi = Math.Max(hi, Height[i]);
        }
        if (hi - lo > 1) return false;
        int s = RingSize - 1;
        int mlo = int.MaxValue, mhi = int.MinValue;
        for (int yy = y; yy <= y + s; yy++)
        for (int xx = x; xx <= x + s; xx++)
        {
            bool shared = xx >= sx0 && xx <= sx1 && yy >= sy0 && yy <= sy1;
            if (shared) continue;
            int i = Idx(xx, yy);
            if (IsWater(i) || Path[i] != 0 || BuildingAt[i] != 0 || ZoneCode[i] != 0 || PowerLine[i] != 0) return false;
            mlo = Math.Min(mlo, Height[i]); mhi = Math.Max(mhi, Height[i]);
        }
        if (mhi - mlo > 3) return false;
        // Level to a height within one of every shared road cell.
        target = hi;
        return true;
    }

    /// <summary>Joins a new pipe to the nearest existing pipe within 30 cells, if there is one.</summary>
    void ConnectPipe(int x, int y)
    {
        int best = -1, bestD = 30 * 30 + 1;
        for (int yy = Math.Max(0, y - 30); yy <= Math.Min(HeightCells - 1, y + 30); yy++)
        for (int xx = Math.Max(0, x - 30); xx <= Math.Min(Width - 1, x + 30); xx++)
        {
            int i = Idx(xx, yy);
            if (WaterPipe[i] == 0) continue;
            int dx = xx - x, dy = yy - y, d = dx * dx + dy * dy;
            if (d > 2 && d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;
        int cost = (Math.Abs(best % Width - x) + Math.Abs(best / Width - y)) * WaterPipeCost;
        if (Affordable(cost)) Do(new PlaceUtilityAction(x, y, best % Width, best / Width, UtilityKind.WaterPipe));
    }
}
