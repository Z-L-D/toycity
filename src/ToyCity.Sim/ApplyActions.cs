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
            case SetSeatAction a: ApplySetSeat(a); break;
            case LeaseLotAction a: ApplyLeaseLot(a); break;
            case SetLeaseTermsAction a: ApplySetLeaseTerms(a); break;
            case EndLeaseAction a: ApplyEndLease(a); break;
            case SetConsentAction a: ApplySetConsent(a); break;
            case UpgradeBuildingAction a: ApplyUpgrade(a); break;
            case SetDisasterSettingAction a:
                DisasterSetting = a.Setting;
                Notices.Add(new SimNotice(a.Setting switch { DisasterSetting.Off => "Disasters off: only the ones you start will happen.", DisasterSetting.Rare => "Disasters set to rare.", _ => "Disasters set to normal." }, "info"));
                break;
        }
    }

    void Reject(string text, int x = -1, int y = -1) => Notices.Add(new SimNotice(text, "warning", x, y));

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

    /// <summary>Checks a path run without changing anything. Returns null when valid, else the reason.</summary>
    public string? ValidatePath(PlacePathAction a, out long cost, out int badX, out int badY)
    {
        cost = 0; badX = -1; badY = -1;
        int profile = Content.ProfileIndex(a.ProfileId);
        if (profile < 0) return $"Unknown path type '{a.ProfileId}'.";
        var cells = PathCellsOf(a);
        if (cells.Count > MaxPathRun) return $"That run is too long: at most {MaxPathRun} cells at a time.";
        int prevH = -1;
        foreach (var (x, y) in cells)
        {
            badX = x; badY = y;
            if (!InBounds(x, y)) return "That runs off the map.";
            int i = Idx(x, y);
            if (IsWater(i)) return "Paths can't cross water yet (bridges come later).";
            if (BuildingAt[i] != 0) return $"A building is in the way at {x},{y}. Bulldoze it first.";
            if (prevH >= 0 && Math.Abs(Height[i] - prevH) > 1) return $"Too steep at {x},{y}. Level the ground first.";
            prevH = Height[i];
            if (Path[i] != profile + 1) cost += Content.Profiles[profile].CostPerCell;
        }
        badX = badY = -1;
        return null;
    }

    void ApplyPath(PlacePathAction a)
    {
        var err = ValidatePath(a, out long cost, out int bx, out int by);
        if (err != null) { Reject(err, bx, by); return; }
        if (!Spend(cost, a.X0, a.Y0)) return;
        ushort code = (ushort)(Content.ProfileIndex(a.ProfileId) + 1);
        ForgetBusRoutes();
        foreach (var (x, y) in PathCellsOf(a))
        {
            int i = Idx(x, y);
            Path[i] = code;
            ZoneCode[i] = 0;
            TravelCost[i] = Content.Profiles[code - 1].CellTime;
        }
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
        if (maxH - minH > 1) return "The ground is too uneven. Level it first.";
        return null;
    }

    void ApplyPlaceBuilding(PlaceBuildingAction a)
    {
        var err = CheckPlacement(a, out var type, out var at);
        if (err != null) { Reject(err, a.X, a.Y); return; }
        if (!Spend(type!.Cost, a.X, a.Y)) return;
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
            PowerLine[i] = 0;
            TravelCost[i] = 0;
            Traffic[i] = 0;
        }
    }

    void ApplyTerraform(TerraformAction a)
    {
        var r = a.Area;
        int x0 = Math.Max(0, r.MinX), x1 = Math.Min(Width - 1, r.MaxX), y0 = Math.Max(0, r.MinY), y1 = Math.Min(HeightCells - 1, r.MaxY);
        if (!InBounds(r.X0, r.Y0)) return;
        int target = Height[Idx(r.X0, r.Y0)];
        var cells = new List<(int I, int H)>();
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            if (BuildingAt[i] != 0 || Path[i] != 0) continue;
            int h = a.Mode switch
            {
                TerraformMode.Raise => Height[i] + 1,
                TerraformMode.Lower => Height[i] - 1,
                _ => target,
            };
            h = Math.Clamp(h, 0, MaxHeight);
            if (h != Height[i]) cells.Add((i, h));
        }
        if (cells.Count == 0) return;
        if (!Spend((long)cells.Count * Content.TerraformCostPerCell, r.X0, r.Y0)) return;
        foreach (var (i, h) in cells)
        {
            Height[i] = (byte)h;
            if (IsWater(i)) { ZoneCode[i] = 0; PowerLine[i] = 0; }
        }
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
            if (IsWater(i)) { Reject("Lines and pipes can't cross water yet.", x, y); return; }
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
