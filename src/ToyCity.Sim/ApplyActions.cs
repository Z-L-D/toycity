namespace ToyCity.Sim;

public sealed partial class World
{
    public const int PowerLineCost = 5;
    public const int WaterPipeCost = 3;
    public const int MaxDebt = 500_000;

    void Apply(SimAction action)
    {
        switch (action)
        {
            case PlacePathAction a: ApplyPath(a); break;
            case PaintZoneAction a: ApplyZone(a); break;
            case ZoneBlockAction a: ApplyZoneBlock(a); break;
            case PlaceBuildingAction a: ApplyPlaceBuilding(a); break;
            case BulldozeAction a: ApplyBulldoze(a); break;
            case TerraformAction a: ApplyTerraform(a); break;
            case SetTaxAction a:
                if (a.Zone != Zone.None) TaxTenths[((int)a.Zone - 1) * 3 + (int)a.Wealth] = Math.Clamp(a.PercentTenths, 0, 200);
                break;
            case SetFundingAction a:
                if (a.BuildingId >= 0 && a.BuildingId < Buildings.Count && Buildings[a.BuildingId] is { } fb && TypeOf(fb).Placeable)
                    fb.Funding = Math.Clamp(a.Percent, 0, 200);
                break;
            case TakeLoanAction a:
                if (a.Amount <= 0) break;
                if (Debt + a.Amount > MaxDebt) { Reject($"The bank won't lend more than ${MaxDebt:N0} in total."); break; }
                Debt += a.Amount;
                Funds += a.Amount;
                OneOffs += a.Amount;
                Notices.Add(new SimNotice($"Took a loan of ${a.Amount:N0}.", "info"));
                break;
            case PlaceUtilityAction a: ApplyUtility(a); break;
            case SetOrdinanceAction a: ApplySetOrdinance(a); break;
            case RespondPromptAction a: ApplyRespondPrompt(a); break;
            case TriggerDisasterAction a: StartDisaster(a.Kind, a.X, a.Y); break;
            case AttachSideGameAction a: ApplyAttach(a); break;
            case SideGameAction a: ApplySideGameAction(a); break;
            case SideGameMissedAction a: ApplyMissed(a); break;
            case SetSeatAction a: ApplySetSeat(a); break;
            case SetAwayLevelAction a: ApplySetAwayLevel(a); break;
            case SeatAwayAction a: ApplySeatAway(a); break;
            case NeighbourEdgeAction a: ApplyNeighbourEdge(a); break;
            case CityDealAction a: ApplyCityDeal(a); break;
            case WeatherAction a: ApplyWeather(a); break;
            case FloodAction a: ApplyFlood(a); break;
            case RenameOwnerAction a: RenameOwner(a.From, a.To); break;
            case LeaseLotAction a: ApplyLeaseLot(a); break;
            case SetLeaseTermsAction a: ApplySetLeaseTerms(a); break;
            case EndLeaseAction a: ApplyEndLease(a); break;
            case OfferLeaseAction a: ApplyOfferLease(a); break;
            case AnswerOfferAction a: ApplyAnswerOffer(a); break;
            case DelinkAction a: ApplyDelink(a); break;
            case OpenTenderAction a: ApplyOpenTender(a); break;
            case ForkAction a: ApplyFork(a); break;
            case SetConsentAction a: ApplySetConsent(a); break;
            case SetCityRuleAction a: ApplySetCityRule(a); break;
            case SetDeputyAction a: ApplySetDeputy(a); break;
            case SetCouncilAction a: ApplySetCouncil(a); break;
            case VoteAction a: ApplyVote(a); break;
            case SetHostRulesAction a: ApplySetHostRules(a); break;
            case OfferPactAction a: ApplyOfferPact(a); break;
            case AcceptPactAction a: ApplyAcceptPact(a); break;
            case EndPactAction a: ApplyEndPact(a); break;
            case UpgradeBuildingAction a: ApplyUpgrade(a); break;
            case SetDisasterSettingAction a:
                DisasterSetting = a.Setting;
                Notices.Add(new SimNotice(a.Setting switch { DisasterSetting.Off => "Disasters off: only the ones you start will happen.", DisasterSetting.Rare => "Disasters set to rare.", _ => "Disasters set to normal." }, "info"));
                break;
        }
    }

    void Reject(string text, int x = -1, int y = -1) => Notices.Add(new SimNotice(text, "warning", x, y, _actor));

    /// <summary>Cells of an L-shaped run from (x0,y0) to (x1,y1), horizontal leg first.</summary>
    public static List<(int X, int Y)> LCells(int x0, int y0, int x1, int y1)
    {
        var cells = new List<(int, int)>();
        int sx = Math.Sign(x1 - x0), sy = Math.Sign(y1 - y0);
        int x = x0, y = y0;
        cells.Add((x, y));
        while (x != x1) { x += sx; cells.Add((x, y)); }
        while (y != y1) { y += sy; cells.Add((x, y)); }
        return cells;
    }

    bool Spend(long cost, int x = -1, int y = -1)
    {
        if (cost > Funds)
        {
            Reject($"Not enough money: that costs ${cost:N0} and the city has ${Funds:N0}.", x, y);
            return false;
        }
        Funds -= cost;
        OneOffs -= cost;
        return true;
    }

    /// <summary>A path over a river is a bridge, and costs this many times the path.</summary>
    public const int BridgeCostTimes = 5;

    /// <summary>Checks a path run without changing anything. Returns null when valid, else the reason.</summary>
    public string? ValidatePath(PlacePathAction a, out long cost, out int badX, out int badY) => ValidatePath(a, out cost, out badX, out badY, out _);

    string? ValidatePath(PlacePathAction a, out long cost, out int badX, out int badY, out PathWorks? works)
    {
        cost = 0; badX = -1; badY = -1; works = null;
        int profile = Content.ProfileIndex(a.ProfileId);
        if (profile < 0) return $"Unknown path type '{a.ProfileId}'.";
        int surface = Content.SurfaceCode(profile, a.Surface);
        if (surface < 0) return $"A {Content.Profiles[profile].Name.ToLowerInvariant()} can't be built in '{a.Surface}'.";
        var cells = PathCellsOf(a);
        if (cells.Count > MaxPathRun) return $"That run is too long: at most {MaxPathRun} cells at a time.";
        int costPct = surface > 0 ? Content.Surfaces[surface - 1].CostPct : 100;
        foreach (var (x, y) in cells)
        {
            badX = x; badY = y;
            if (!InBounds(x, y)) return "That runs off the map.";
            int i = Idx(x, y);
            if (IsWater(i) && !IsRiver(i)) return "Paths can't cross the sea or a lake; a river they can bridge.";
            if (BuildingAt[i] != 0) return $"A building is in the way at {x},{y}. Bulldoze it first.";
        }
        // The ground is graded to the steepest the path's type and surface allow, between the paths it meets.
        works = PlanPath(cells, profile, surface, a.Tunnel, out int bad, out int need);
        if (works == null)
        {
            (badX, badY) = cells[bad];
            return $"Too steep at {badX},{badY}: the paths it joins would need a {need}% climb, and a {Content.Profiles[profile].Name.ToLowerInvariant()}" +
                   $"{(surface > 0 ? " in " + Content.Surfaces[surface - 1].Name.ToLowerInvariant() : "")} climbs at most {Content.GradePct(profile, surface)}%.{SteeperSuggestion(profile, need)}";
        }
        // The path itself: a bridge costs five times the road on the ground, a tunnel eight.
        for (int k = 0; k < cells.Count; k++)
        {
            int i = Idx(cells[k].X, cells[k].Y);
            if (Path[i] == profile + 1 && PathSurface[i] == surface) continue;
            long each = (long)Content.Profiles[profile].CostPerCell * costPct / 100;
            cost += each * (works.Tunnel[k] ? PathGrade.TunnelCostTimes : IsRiver(i) ? BridgeCostTimes : 1);
        }
        cost += works.Cost;
        badX = badY = -1;
        return null;
    }

    /// <summary>" Try a ..." naming a path type and surface that climbs <paramref name="pct"/>, or "" when none does.</summary>
    string SteeperSuggestion(int profile, int pct)
    {
        var p = Content.Profiles[profile];
        // The same type in a surface that holds the grade first, then a type that carries the same traffic.
        foreach (var sid in p.Surfaces)
        {
            int s = Content.SurfaceIndex(sid) + 1;
            if (Content.GradePct(profile, s) >= pct) return $" Try it in {Content.Surfaces[s - 1].Name.ToLowerInvariant()}.";
        }
        for (int k = 0; k < Content.Profiles.Count; k++)
        {
            var q = Content.Profiles[k];
            if (q.Rail != p.Rail || (p.Cars && !q.Cars)) continue;
            foreach (var sid in q.Surfaces.DefaultIfEmpty(""))
            {
                int s = sid == "" ? 0 : Content.SurfaceIndex(sid) + 1;
                if (Content.GradePct(k, s) >= pct) return $" Try a {q.Name.ToLowerInvariant()}{(s > 0 && sid != q.Surfaces[0] ? " in " + Content.Surfaces[s - 1].Name.ToLowerInvariant() : "")}.";
            }
        }
        return "";
    }

    /// <summary>The time to cross a path cell at free flow, by its type and surface.</summary>
    public int PathCellTime(int i)
    {
        if (Path[i] == 0) return 0;
        int t = Content.Profiles[Path[i] - 1].CellTime;
        return PathSurface[i] > 0 ? Math.Max(1, t * 100 / Content.Surfaces[PathSurface[i] - 1].SpeedPct) : t;
    }

    void ApplyPath(PlacePathAction a)
    {
        var err = ValidatePath(a, out long cost, out int bx, out int by, out var works);
        if (err != null) { Reject(err, bx, by); return; }
        if (!Spend(cost, a.X0, a.Y0)) return;
        int profile = Content.ProfileIndex(a.ProfileId);
        ushort code = (ushort)(profile + 1);
        byte surface = (byte)Content.SurfaceCode(profile, a.Surface);
        ForgetBusRoutes();
        var cells = PathCellsOf(a);
        ApplyPathWorks(works!);
        for (int k = 0; k < cells.Count; k++)
        {
            var (x, y) = cells[k];
            int i = Idx(x, y);
            // A bridge or tunnel keeps the road's own level; a path on the ground follows the ground.
            if (Path[i] == 0) PathDeck[i] = (ushort)(works!.Tunnel[k] || IsRiver(i) ? Math.Clamp(works.Target[k], 1, MaxCornerDm) : 0);
            Path[i] = code;
            PathSurface[i] = surface;
            ZoneCode[i] = 0;
            TravelCost[i] = PathCellTime(i);
        }
        CheckStrips();
        AddStrip(a, code, cells);
    }

    void ApplyZone(PaintZoneAction a)
    {
        byte code = MakeZoneCode(a.Zone, a.Density);
        long cost = 0;
        var r = a.Area;
        for (int y = Math.Max(0, r.MinY); y <= Math.Min(HeightCells - 1, r.MaxY); y++)
        for (int x = Math.Max(0, r.MinX); x <= Math.Min(Width - 1, r.MaxX); x++)
        {
            int i = Idx(x, y);
            if (IsWater(i) || Path[i] != 0) continue;
            if (BuildingAtCell(i) is { } b && TypeOf(b).Placeable) continue;
            if (ZoneCode[i] != code && code != 0) cost += Content.ZoneCostPerCell;
        }
        if (!Spend(cost, r.X0, r.Y0)) return;
        for (int y = Math.Max(0, r.MinY); y <= Math.Min(HeightCells - 1, r.MaxY); y++)
        for (int x = Math.Max(0, r.MinX); x <= Math.Min(Width - 1, r.MaxX); x++)
        {
            int i = Idx(x, y);
            if (IsWater(i) || Path[i] != 0) continue;
            var b = BuildingAtCell(i);
            if (b != null && TypeOf(b).Placeable) continue;
            // A different zone kind (or de-zoning) clears the old building; a density change keeps it until it redevelops.
            if (b != null && (code == 0 || TypeOf(b).Zone != a.Zone)) RemoveBuilding(b);
            ZoneCode[i] = code;
        }
    }

    /// <summary>Checks a civic building placement. Returns null when valid.</summary>
    public string? ValidatePlacement(PlaceBuildingAction a, out BuildingType? type) => CheckPlacement(a, out type, out _);

    /// <summary>Checks a placement, and says where it would go: roadside buildings snap to a kerb.</summary>
    string? CheckPlacement(PlaceBuildingAction a, out BuildingType? type, out (int X, int Y, int Side) at)
    {
        at = (a.X, a.Y, 0);
        int ti = Content.TypeIndex(a.TypeId);
        type = ti >= 0 ? Content.Types[ti] : null;
        if (type == null || !type.Placeable) return $"'{a.TypeId}' isn't something the mayor can place.";
        if (!TypeUnlocked[ti]) return $"{type.Name} isn't available yet. {type.UnlockText}";
        if (type.Unique && Buildings.Any(b => b != null && b.Type == ti)) return $"The city already has a {type.Name}.";
        if (type.Roadside) return CheckKerb(type, a.X, a.Y, a.Side, out at);
        int minH = int.MaxValue, maxH = int.MinValue;
        for (int y = a.Y; y < a.Y + type.H; y++)
        for (int x = a.X; x < a.X + type.W; x++)
        {
            if (!InBounds(x, y)) return "That doesn't fit on the map.";
            int i = Idx(x, y);
            if (IsWater(i)) return "Can't build on water.";
            if (BuildingAt[i] != 0) return "Something is already built there.";
            if (Path[i] != 0) return "A path is in the way.";
            minH = Math.Min(minH, Height[i]);
            maxH = Math.Max(maxH, Height[i]);
        }
        if (maxH - minH > MaxPadRise) return $"The ground is too uneven: it rises {maxH - minH} steps across the lot, and a pad can level at most {MaxPadRise}. Level it first.";
        return null;
    }

    /// <summary>What a civic building's pad would cost to level here: nothing on flat ground.</summary>
    public long PadCost(int x, int y, int w, int h) => Pad(x, y, w, h) is { } pad ? EarthworksCost(pad, PadCostPerStep) : 0;

    void ApplyPlaceBuilding(PlaceBuildingAction a)
    {
        var err = CheckPlacement(a, out var type, out var at);
        if (err != null) { Reject(err, a.X, a.Y); return; }
        var pad = type!.Roadside ? new() : Pad(a.X, a.Y, type.W, type.H)!;
        if (!Spend(type.Cost + EarthworksCost(pad, PadCostPerStep), a.X, a.Y)) return;
        ApplyEarthworks(pad);
        if (type.Roadside)
        {
            AddBuilding(type, at.X, at.Y, constructionDays: 0).Side = at.Side;
            Notices.Add(new SimNotice($"Built {type.Name} on {StreetNear(at.X, at.Y)}.", "info", at.X, at.Y));
            return;
        }
        for (int y = a.Y; y < a.Y + type.H; y++)
        for (int x = a.X; x < a.X + type.W; x++)
        {
            int i = Idx(x, y);
            ZoneCode[i] = 0;
            PowerLine[i] = 0;
        }
        AddBuilding(type, a.X, a.Y, constructionDays: 0);
        Notices.Add(new SimNotice($"Built {type.Name}.", "info", a.X, a.Y));
    }

    /// <summary>Sets a building's totals: zoned templates give values per cell, civic types per building.</summary>
    internal void InitTotals(Building b)
    {
        var t = TypeOf(b);
        int k = t.Placeable ? 1 : b.W * b.H;
        b.Residents = t.Residents * k;
        b.Jobs = t.Jobs * k;
        b.PowerKw = t.PowerKw * k;
        b.WaterUse = t.WaterUse * k;
        b.Garbage = t.Garbage * k;
    }

    internal Building AddBuilding(BuildingType type, int x, int y, int constructionDays, int w = 0, int h = 0)
    {
        if (w == 0) { w = type.W; h = type.H; }
        var b = new Building { Id = Buildings.Count, Type = type.Index, X = x, Y = y, W = w, H = h, ConstructionLeft = constructionDays };
        InitTotals(b);
        if (b.Residents > 0) { b.Since = new int[b.Residents]; Array.Fill(b.Since, MonthIndex); }
        b.Access = type.Roadside ? Idx(x, y) : FindAccess(x, y, w, h);
        Buildings.Add(b);
        if (type.Roadside) return b;
        for (int yy = y; yy < y + h; yy++)
        for (int xx = x; xx < x + w; xx++)
            BuildingAt[Idx(xx, yy)] = b.Id + 1;
        FlattenLot(x, y, w, h);
        return b;
    }

    internal void RemoveBuilding(Building b)
    {
        if (!TypeOf(b).Roadside)
            for (int yy = b.Y; yy < b.Y + b.H; yy++)
        for (int xx = b.X; xx < b.X + b.W; xx++)
            BuildingAt[Idx(xx, yy)] = 0;
        ReleaseAssignments(b);
        Buildings[b.Id] = null;
        // Its workers lose their jobs now, not at the next monthly recount.
        if (b.Jobs > 0)
            foreach (var home in Buildings)
            {
                if (home == null || home.Assignments.Count == 0 || home.Assignments.All(a => a.SiteId != b.Id)) continue;
                home.Assignments.RemoveAll(a => a.SiteId == b.Id);
                home.Employed = home.Assignments.Sum(a => a.Count);
                home.CommuteMinutes = home.Employed > 0 ? (int)(home.Assignments.Sum(a => (long)a.Minutes * a.Count) / home.Employed) : 0;
            }
    }

    void ApplyBulldoze(BulldozeAction a)
    {
        var r = a.Area;
        long cost = 0;
        var doomed = new SortedSet<int>();
        int x0 = Math.Max(0, r.MinX), x1 = Math.Min(Width - 1, r.MaxX), y0 = Math.Max(0, r.MinY), y1 = Math.Min(HeightCells - 1, r.MaxY);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            if (a.Underground) { if (WaterPipe[i] != 0) cost += Content.BulldozeCostPerCell; continue; }
            if (BuildingAt[i] != 0) doomed.Add(BuildingAt[i] - 1);
            if (Path[i] != 0 || PowerLine[i] != 0) cost += Content.BulldozeCostPerCell;
        }
        // Roadside stops go with their road.
        if (!a.Underground)
            foreach (var s in Buildings)
                if (s != null && TypeOf(s).Roadside && s.X >= x0 && s.X <= x1 && s.Y >= y0 && s.Y <= y1) doomed.Add(s.Id);
        if (doomed.Select(id => LeaseOn(id)).FirstOrDefault(l => l != null && !l.Terminable) is { } leased)
        {
            var lb = Buildings[leased.BuildingId]!;
            Reject($"{Cap(LotName(lb))} is leased to {GameName(leased.GameId)}. The lease has to end first.", lb.X, lb.Y);
            return;
        }
        foreach (var id in doomed) cost += Content.BulldozeCostPerCell * Buildings[id]!.W * Buildings[id]!.H;
        if (!Spend(cost, r.X0, r.Y0)) return;
        ForgetBusRoutes();
        foreach (var id in doomed) RemoveBuilding(Buildings[id]!);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            if (a.Underground) { WaterPipe[i] = 0; continue; }
            Path[i] = 0;
            PathSurface[i] = 0;
            PathDeck[i] = 0;
            PowerLine[i] = 0;
            TravelCost[i] = 0;
            Traffic[i] = 0;
        }
    }

    /// <summary>How far, in cells, levelling reaches out to embank the ground around it.</summary>
    public const int EmbankReach = 12;
    /// <summary>How much an embankment rises or falls a cell: 3 m in 16, about 1 in 5.</summary>
    public const int EmbankDm = 30;

    void ApplyTerraform(TerraformAction a)
    {
        var r = a.Area;
        if (!InBounds(r.X0, r.Y0)) return;
        int x0 = Math.Max(0, r.MinX), x1 = Math.Min(Width - 1, r.MaxX), y0 = Math.Max(0, r.MinY), y1 = Math.Min(HeightCells - 1, r.MaxY);
        int cw = Width + 1;
        // The brush moves the corners of the cells it covers. A corner a building or a path stands on stays put, so
        // nothing built is lifted or sunk.
        bool Held(int cx, int cy)
        {
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
                if (InBounds(cx + dx, cy + dy) && (BuildingAt[Idx(cx + dx, cy + dy)] != 0 || Path[Idx(cx + dx, cy + dy)] != 0)) return true;
            return false;
        }
        // Level brings the area to the first cell's height, a whole step, so levelled land is never under the water.
        int target = Height[Idx(r.X0, r.Y0)] * StepDm;
        // Smooth reads the corners as they were before the brush, so the result doesn't depend on the order.
        int SmoothAt(int cx, int cy)
        {
            int sum = 0, n = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (cx + dx >= 0 && cy + dy >= 0 && cx + dx <= Width && cy + dy <= HeightCells) { sum += Corner[(cy + dy) * cw + cx + dx]; n++; }
            return (sum + n / 2) / n;
        }
        var moves = new List<(int C, int H)>();
        for (int cy = y0; cy <= y1 + 1; cy++)
        for (int cx = x0; cx <= x1 + 1; cx++)
        {
            if (Held(cx, cy)) continue;
            int c = cy * cw + cx, now = Corner[c];
            int h = a.Mode switch
            {
                TerraformMode.Raise => now + StepDm,
                TerraformMode.Lower => now - StepDm,
                TerraformMode.Smooth => SmoothAt(cx, cy),
                _ => target,
            };
            h = Math.Clamp(h, 0, MaxCornerDm);
            if (h != now) moves.Add((c, h));
        }
        // Levelled ground meets the land around it on an embankment, cut or fill sloping about 1 in 5, rather than a
        // cliff: each corner within reach comes to within EmbankDm a corner of the level, more the farther out it is.
        // Ground by the water is left as it is, so levelling never fills the sea.
        int ex0 = x0, ey0 = y0, ex1 = x1, ey1 = y1;
        if (a.Mode == TerraformMode.Level)
        {
            ex0 = Math.Max(0, x0 - EmbankReach); ey0 = Math.Max(0, y0 - EmbankReach);
            ex1 = Math.Min(Width - 1, x1 + EmbankReach); ey1 = Math.Min(HeightCells - 1, y1 + EmbankReach);
            bool ByWater(int cx, int cy)
            {
                for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                    if (InBounds(cx + dx, cy + dy) && IsWater(Idx(cx + dx, cy + dy))) return true;
                return false;
            }
            for (int cy = ey0; cy <= ey1 + 1; cy++)
            for (int cx = ex0; cx <= ex1 + 1; cx++)
            {
                int d = Math.Max(Math.Max(x0 - cx, cx - (x1 + 1)), Math.Max(y0 - cy, cy - (y1 + 1)));
                if (d <= 0 || Held(cx, cy) || ByWater(cx, cy)) continue;
                int c = cy * cw + cx, now = Corner[c];
                int h = Math.Clamp(now, target - d * EmbankDm, target + d * EmbankDm);
                if (h != now) moves.Add((c, h));
            }
        }
        if (moves.Count == 0) return;
        // Priced by the cells the brush reshaped; the embankment around a levelled area comes with it.
        var cells = new HashSet<int>();
        foreach (var (c, _) in moves)
        {
            int cx = c % cw, cy = c / cw;
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
                if (InBounds(cx + dx, cy + dy)) cells.Add(Idx(cx + dx, cy + dy));
        }
        long priced = cells.Count(i => i % Width >= x0 - 1 && i % Width <= x1 + 1 && i / Width >= y0 - 1 && i / Width <= y1 + 1);
        if (!Spend(priced * Content.TerraformCostPerCell, r.X0, r.Y0)) return;
        foreach (var (c, h) in moves) Corner[c] = (ushort)h;
        DeriveHeights(ex0 - 1, ey0 - 1, ex1 + 1, ey1 + 1);
        foreach (int i in cells)
            if (IsWater(i)) { ZoneCode[i] = 0; PowerLine[i] = 0; }
        RecomputeWaterDistance();
    }

    void ApplyUtility(PlaceUtilityAction a)
    {
        var cells = LCells(a.X0, a.Y0, a.X1, a.Y1);
        long cost = 0;
        var todo = new List<int>();
        foreach (var (x, y) in cells)
        {
            if (!InBounds(x, y)) { Reject("That runs off the map.", x, y); return; }
            int i = Idx(x, y);
            if (IsWater(i) && !IsRiver(i)) { Reject("Lines and pipes can't cross the sea or a lake; a river they can.", x, y); return; }
            if (a.Kind == UtilityKind.PowerLine)
            {
                if (BuildingAt[i] != 0 || PowerLine[i] != 0) continue; // buildings conduct by themselves
                cost += PowerLineCost;
            }
            else
            {
                if (WaterPipe[i] != 0) continue;
                cost += WaterPipeCost;
            }
            todo.Add(i);
        }
        if (!Spend(cost, a.X0, a.Y0)) return;
        foreach (var i in todo)
        {
            if (a.Kind == UtilityKind.PowerLine) PowerLine[i] = 1;
            else WaterPipe[i] = 1;
        }
    }
}
