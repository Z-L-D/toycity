using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Tools;

public enum ToolKind { Query, Bulldoze, Path, Zone, Dezone, Utility, Building, Terraform, Disaster }

/// <summary>What the player has picked: the tool and its options, as in the web client's toolbar.</summary>
public sealed class ToolState
{
    public ToolKind Kind = ToolKind.Query;
    public ZoneKind Zone = ZoneKind.ZoneResidential;
    public Density Density = Density.Low;
    public bool BlockZoning;              // zone whole blocks with a click instead of painting areas
    public Leftover Leftover = Leftover.Leave;
    public string ProfileId = "road";
    /// <summary>The surface to build paths in, by id; "" for the profile's default (Beta 3.2).</summary>
    public string Surface = "";
    /// <summary>Paths go under a hill in a tunnel where they'd run 8 m or more deep, rather than cutting through (Beta 3.2).</summary>
    public bool Tunnel;
    public bool Curve;                    // paths: set two ends, then bend
    public bool Diagonal;                 // paths: a straight line at any angle, drawn smooth
    public (int X0, int Y0, int X1, int Y1)? CurveEnds;
    public UtilityKind Utility = UtilityKind.UtilityPowerLine;
    public string BuildingTypeId = "police-station";
    public TerraformMode Terraform = TerraformMode.TerraformRaise;
    public DisasterKind Disaster = DisasterKind.DisasterFire;
    public bool Underground;              // bulldoze clears water pipes only
}

/// <summary>Where the cursor is and what it's doing: the cell, where in the cell, the drag's start, and the held keys.</summary>
public readonly record struct Pointer(int X, int Y, float Fx = 0.5f, float Fy = 0.5f, (int X, int Y)? DragStart = null, bool Free = false, bool VerticalFirst = false);

/// <summary>A tint for a previewed cell.</summary>
public enum Tint { Ok, Bad, Zone, Leftover, Road, Power, Water, Clear, Earth, Building, Stop }

/// <summary>
/// What the tool would do if the player clicked or let go now: the cells it covers and how to tint them, whether it
/// can be done, a cost line for the tag under the cursor, and the actions that do it.
/// </summary>
public sealed record Preview(List<(int X, int Y, Tint Tint)> Cells, bool Ok, string Text, List<PAction> Actions, string Label)
{
    /// <summary>Roadside stops: the kerb they'd stand on, 0 north to 3 west; -1 otherwise.</summary>
    public int Side { get; init; } = -1;
    /// <summary>Transit stops: how far around they serve, in cells; 0 otherwise.</summary>
    public int Catchment { get; init; }
}

/// <summary>
/// Every map tool's preview and action, shared by the Godot client and its tests. Placement snaps by default and
/// goes exactly where the cursor is while the free-placement key is held:
/// <list type="bullet">
/// <item>Paths and utility lines snap to a straight run along the drag's longer side; held free, they follow the
/// cursor as an L (vertical leg first with Shift), as the web client draws them.</item>
/// <item>Buildings snap to the nearest spot within two cells that faces a road; held free, they sit centred on the cursor.</item>
/// </list>
/// The checks mirror the server's so the preview is right nearly always; the server has the last word.
/// </summary>
public static class Planner
{
    /// <summary>A bridge costs this many times its path, as World.BridgeCostTimes.</summary>
    public const int BridgeCostTimes = 5;
    public const int ZoneCostPerCell = 5, PowerLinePerCell = 5, WaterPipePerCell = 3, TerraformPerCell = 25;
    /// <summary>As World: a road's cut and fill per step a cell, a pad's, and the most a pad levels.</summary>
    public const int GradeCostPerStep = 10, PadCostPerStep = 5, MaxPadRise = 3;
    public static readonly string[] DensityNames = { "Low", "Medium", "High" };
    public static readonly string[] ZoneNames = { "", "residential", "commercial", "industrial" };
    public static readonly string[] DisasterNames = { "Fire", "Tornado", "Earthquake", "Clockwork Colossus" };
    public static readonly string[] TerraformNames = { "Raise", "Lower", "Level", "Smooth", "Even slope" };
    public static readonly string[] SideNames = { "north", "east", "south", "west" };
    static readonly int[] SideDx = { 0, 1, 0, -1 }, SideDy = { -1, 0, 1, 0 };

    public static string Money(long n) => (n < 0 ? "-$" : "$") + Math.Abs(n).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The preview for the pointer now, or null when the tool shows nothing here.</summary>
    public static Preview? Plan(CityMirror c, ToolState t, Pointer p)
    {
        if (!c.Loaded) return null;
        switch (t.Kind)
        {
            case ToolKind.Building:
                return c.TypeById.TryGetValue(t.BuildingTypeId, out var bt) ? bt.Roadside ? Roadside(c, bt, p) : Building(c, bt, p) : null;
            case ToolKind.Path when t.CurveEnds is { } e:
                return Curve(c, t, e, p);
            case ToolKind.Zone when t.BlockZoning && p.DragStart == null:
                return Block(c, t, p);
            case ToolKind.Disaster:
                if (!c.InBounds(p.X, p.Y)) return null;
                return new Preview(new() { (p.X, p.Y, Tint.Bad) }, true, $"{DisasterNames[(int)t.Disaster]} here",
                    new() { new PAction { Disaster = new TriggerDisaster { Kind = t.Disaster, X = p.X, Y = p.Y } } }, $"{DisasterNames[(int)t.Disaster]} at {p.X},{p.Y}");
            case ToolKind.Path when p.DragStart is { } s && t.Diagonal:
                return Diagonal(c, t, s, p);
            case ToolKind.Path when p.DragStart is { } s:
                var (bx, by) = BorderSnap(c, p.X, p.Y);
                return Line(c, t, s, p with { X = bx, Y = by, Free = p.Free || (bx, by) != (p.X, p.Y) });
            case ToolKind.Utility when p.DragStart is { } s:
                return Line(c, t, s, p);
            case ToolKind.Zone or ToolKind.Dezone or ToolKind.Bulldoze or ToolKind.Terraform when p.DragStart is { } s:
                return Area(c, t, s, p);
            default:
                return null;
        }
    }

    /// <summary>
    /// A road drawn to the map's edge near a neighbouring city's road snaps onto it, so the two meet cell for cell:
    /// within two cells of the edge and three along it. Otherwise the cell as it is.
    /// </summary>
    public static (int X, int Y) BorderSnap(CityMirror c, int x, int y)
    {
        (int X, int Y, int D)? best = null;
        foreach (var b in c.Borders)
        {
            bool along = b.Side is 0 or 2;
            int outward = b.Side switch { 0 => y, 2 => c.Height - 1 - y, 1 => c.Width - 1 - x, _ => x };
            if (outward > 2) continue;
            int pos = along ? x : y;
            for (int j = 0; j + 1 < b.Paths.Count; j += 2)
            {
                int k = b.Paths[j], d = Math.Abs(k - pos);
                if (d > 3 || best is { } bb && bb.D <= d) continue;
                best = along ? (k, b.Side == 0 ? 0 : c.Height - 1, d) : (b.Side == 1 ? c.Width - 1 : 0, k, d);
            }
        }
        return best is { } s ? (s.X, s.Y) : (x, y);
    }

    /// <summary>The end of a straight run: snapped to the drag's longer side unless placement is free.</summary>
    public static (int X, int Y) SnapEnd((int X, int Y) start, int x, int y, bool free) =>
        free ? (x, y) : Math.Abs(x - start.X) >= Math.Abs(y - start.Y) ? (x, start.Y) : (start.X, y);

    /// <summary>Cells of an L-shaped run from (x0,y0) to (x1,y1), horizontal leg first, as the server lays it.</summary>
    public static List<(int X, int Y)> LCells(int x0, int y0, int x1, int y1)
    {
        var cells = new List<(int, int)> { (x0, y0) };
        int sx = Math.Sign(x1 - x0), sy = Math.Sign(y1 - y0), x = x0, y = y0;
        while (x != x1) { x += sx; cells.Add((x, y)); }
        while (y != y1) { y += sy; cells.Add((x, y)); }
        return cells;
    }

    /// <summary>The cells of a curve from (x0,y0) bent toward (cx,cy) to (x1,y1): the same integer steps as World.CurveCells.</summary>
    public static List<(int X, int Y)> CurveCells(int x0, int y0, int cx, int cy, int x1, int y1)
    {
        var cells = new List<(int X, int Y)>();
        var seen = new HashSet<(int, int)>();
        void Add(int x, int y) { if (seen.Add((x, y))) cells.Add((x, y)); }
        long n = 4L * (Math.Abs(cx - x0) + Math.Abs(cy - y0) + Math.Abs(x1 - cx) + Math.Abs(y1 - cy)) + 1;
        long nn = n * n;
        const long Off = 1 << 20;
        static long Round(long num, long nn) => (2 * num + nn) / (2 * nn) - Off;
        int lx = x0, ly = y0;
        Add(x0, y0);
        for (long k = 1; k <= n; k++)
        {
            long a = (n - k) * (n - k), b = 2 * (n - k) * k, cc = k * k;
            int x = (int)Round(a * (x0 + Off) + b * (cx + Off) + cc * (x1 + Off), nn);
            int y = (int)Round(a * (y0 + Off) + b * (cy + Off) + cc * (y1 + Off), nn);
            while (lx != x || ly != y)
            {
                if (lx != x) lx += Math.Sign(x - lx);
                else ly += Math.Sign(y - ly);
                Add(lx, ly);
            }
        }
        return cells;
    }

    static string ProfileName(CityMirror c, string id) => c.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? id;

    static Preview Line(CityMirror c, ToolState t, (int X, int Y) s, Pointer p)
    {
        var end = SnapEnd(s, p.X, p.Y, p.Free);
        var legs = p.Free && p.VerticalFirst
            ? new List<(int, int, int, int)> { (s.X, s.Y, s.X, end.Y), (s.X, end.Y, end.X, end.Y) }
            : new List<(int, int, int, int)> { (s.X, s.Y, end.X, end.Y) };
        var cells = new List<(int X, int Y)>();
        foreach (var (x0, y0, x1, y1) in legs)
            foreach (var cell in LCells(x0, y0, x1, y1)) if (!cells.Contains(cell)) cells.Add(cell);
        bool path = t.Kind == ToolKind.Path;
        var prof = c.Profiles.FirstOrDefault(q => q.Id == t.ProfileId);
        int pi = prof == null ? 0 : c.Profiles.ToList().IndexOf(prof) + 1;
        long cost = 0;
        bool ok = true;
        var tinted = new List<(int, int, Tint)>();
        foreach (var (x, y) in cells)
        {
            bool good = true;
            if (!c.InBounds(x, y) || c.IsWater(c.Idx(x, y)) && !c.IsRiver(c.Idx(x, y))) good = false;
            else
            {
                int i = c.Idx(x, y);
                if (path) { if (c.BuildingAt[i] != 0) good = false; }
                else if (t.Utility == UtilityKind.UtilityPowerLine) { if (c.BuildingAt[i] == 0 && c.PowerLines[i] == 0) cost += PowerLinePerCell; }
                else if (c.WaterPipes[i] == 0) cost += WaterPipePerCell;
            }
            ok &= good;
            tinted.Add((x, y, !good ? Tint.Bad : path ? Tint.Road : t.Utility == UtilityKind.UtilityPowerLine ? Tint.Power : Tint.Water));
        }
        string? steep = null;
        long grading = 0;
        var depth = new PathPlan();
        if (path)
            foreach (var (x0, y0, x1, y1) in legs)
            {
                var plan = PlanPath(c, t, LCells(x0, y0, x1, y1));
                cost += plan.Cost;
                grading += plan.Grading;
                steep ??= plan.Problem;
                depth = depth with { DeepestCutDm = Math.Max(depth.DeepestCutDm, plan.DeepestCutDm), TunnelCells = depth.TunnelCells + plan.TunnelCells };
            }
        ok &= steep == null;
        var actions = legs.Select(l => path
            ? new PAction { Path = new PlacePath { X0 = l.Item1, Y0 = l.Item2, X1 = l.Item3, Y1 = l.Item4, ProfileId = t.ProfileId, Surface = t.Surface, Tunnel = t.Tunnel } }
            : new PAction { Utility = new PlaceUtility { X0 = l.Item1, Y0 = l.Item2, X1 = l.Item3, Y1 = l.Item4, Kind = t.Utility } }).ToList();
        string what = path ? ProfileName(c, t.ProfileId) : t.Utility == UtilityKind.UtilityPowerLine ? "Power line" : "Water pipe";
        return new Preview(tinted, ok, steep ?? $"{what}{SurfaceName(c, t)}, {cells.Count} cells: {Money(cost)}{Grading(grading)}{(path ? Depth(depth, t.Tunnel) : "")}", actions, $"{what} from {s.X},{s.Y} to {end.X},{end.Y}");
    }

    static Preview Curve(CityMirror c, ToolState t, (int X0, int Y0, int X1, int Y1) e, Pointer p)
    {
        var cells = CurveCells(e.X0, e.Y0, p.X, p.Y, e.X1, e.Y1);
        var prof = c.Profiles.FirstOrDefault(q => q.Id == t.ProfileId);
        int pi = prof == null ? 0 : c.Profiles.ToList().IndexOf(prof) + 1;
        long cost = 0;
        bool ok = true;
        var tinted = new List<(int, int, Tint)>();
        foreach (var (x, y) in cells)
        {
            bool good = c.InBounds(x, y) && (!c.IsWater(c.Idx(x, y)) || c.IsRiver(c.Idx(x, y))) && c.BuildingAt[c.Idx(x, y)] == 0;
            ok &= good;
            tinted.Add((x, y, good ? Tint.Road : Tint.Bad));
        }
        var plan = PlanPath(c, t, cells);
        cost += plan.Cost;
        ok &= plan.Problem == null;
        var action = new PAction { Path = new PlacePath { X0 = e.X0, Y0 = e.Y0, X1 = e.X1, Y1 = e.Y1, ProfileId = t.ProfileId, Curved = true, Cx = p.X, Cy = p.Y, Surface = t.Surface, Tunnel = t.Tunnel } };
        return new Preview(tinted, ok, plan.Problem ?? $"Curve{SurfaceName(c, t)}, {cells.Count} cells: {Money(cost)}{Grading(plan.Grading)}{Depth(plan, t.Tunnel)}", new() { action }, $"Curved {ProfileName(c, t.ProfileId)} from {e.X0},{e.Y0} to {e.X1},{e.Y1}");
    }

    /// <summary>The cells of a straight line at any angle: the same integer steps as World.LineCells.</summary>
    public static List<(int X, int Y)> LineCells(int x0, int y0, int x1, int y1)
    {
        var cells = new List<(int X, int Y)> { (x0, y0) };
        long n = 4L * (Math.Abs(x1 - x0) + Math.Abs(y1 - y0)) + 1;
        const long Off = 1 << 20;
        static long Round(long num, long n) => (2 * num + n) / (2 * n) - Off;
        int lx = x0, ly = y0;
        for (long k = 1; k <= n; k++)
        {
            int x = (int)Round((n - k) * (x0 + Off) + k * (x1 + Off), n);
            int y = (int)Round((n - k) * (y0 + Off) + k * (y1 + Off), n);
            while (lx != x || ly != y)
            {
                if (lx != x) lx += Math.Sign(x - lx);
                else ly += Math.Sign(y - ly);
                cells.Add((lx, ly));
            }
        }
        return cells;
    }

    /// <summary>What a path run costs (its cells, bridges and grading), and why it can't be built, as the server works it out.</summary>
    public readonly record struct PathPlan(long Cost, long Grading, string? Problem, int GradePct, int DeepestCutDm = 0, int TunnelCells = 0);

    /// <summary>
    /// What the cost line adds about depth (Beta 3.2): a deep cut names how deep and that a tunnel is on offer; a
    /// tunnel says how many cells run under the hill.
    /// </summary>
    public static string Depth(PathPlan p, bool tunnel) =>
        p.TunnelCells > 0 ? $", {p.TunnelCells} cells in tunnel"
        : !tunnel && p.DeepestCutDm >= ToyCity.Sim.PathGrade.TunnelMinDm ? $", cuts {p.DeepestCutDm / 10} m deep (or tunnel under it)"
        : "";

    /// <summary>
    /// A path run of the tool's profile and surface along these cells: what it costs and whether it can be graded,
    /// with the same <c>PathGrade</c> the server uses (Beta 3.2). Cells off the map or under water count for nothing.
    /// </summary>
    public static PathPlan PlanPath(CityMirror c, ToolState t, List<(int X, int Y)> cells)
    {
        var prof = c.Profiles.FirstOrDefault(q => q.Id == t.ProfileId);
        if (prof == null) return new PathPlan(0, 0, $"Unknown path type '{t.ProfileId}'", 0);
        int pi = c.Profiles.ToList().IndexOf(prof) + 1;
        int surface = c.SurfaceCode(prof, t.Surface);
        if (surface < 0) return new PathPlan(0, 0, $"A {prof.Name.ToLowerInvariant()} can't be built in that surface", 0);
        int costPct = surface > 0 ? c.Surfaces[surface - 1].CostPct : 100;
        int pct = c.GradePct(prof, surface);
        long each = prof.CostPerCell * costPct / 100;
        if (cells.Any(q => !c.InBounds(q.X, q.Y)))
        {
            long rough = cells.Where(q => c.InBounds(q.X, q.Y) && c.Paths[c.Idx(q.X, q.Y)] != pi).Sum(q => each * (c.IsRiver(c.Idx(q.X, q.Y)) ? BridgeCostTimes : 1));
            return new PathPlan(rough, 0, null, pct);
        }
        var idx = cells.Select(q => c.Idx(q.X, q.Y)).ToList();
        // A bridge's deck clears its river by at least a step; a path already there keeps its level, as the server grades it.
        int Level(int i) => c.PathDecks.Length > i && c.PathDecks[i] > 0 ? c.PathDecks[i] : ToyCity.Sim.PathGrade.CellDmOf(c.Corners, c.Width, i);
        var h = idx.Select(i => c.Paths[i] != 0 ? Level(i) : c.IsRiver(i) ? (c.Rivers[i] + 1) * 20 : ToyCity.Sim.PathGrade.CellDmOf(c.Corners, c.Width, i)).ToList();
        var fix = idx.Select(i => c.Paths[i] != 0).ToList();
        var deck = idx.Select(i => c.IsRiver(i) ? (c.Rivers[i] + 1) * 20 : int.MinValue).ToList();
        int floor = c.WaterLevel * 20;
        var g = ToyCity.Sim.PathGrade.Grade(h, fix, floor, ToyCity.Sim.PathGrade.MaxDeltaDm(pct), deck);
        if (g == null)
        {
            var (need, _) = ToyCity.Sim.PathGrade.Needed(h, fix);
            return new PathPlan(0, 0, $"Too steep: the paths it joins need a {need}% climb; this climbs at most {pct}%", pct);
        }
        var under = t.Tunnel ? ToyCity.Sim.PathGrade.TunnelCells(h, g, fix) : new bool[g.Length];
        long cost = 0;
        var openIdx = new List<int>();
        var openG = new List<int>();
        for (int k = 0; k < idx.Count; k++)
        {
            int i = idx[k];
            if (!under[k]) { openIdx.Add(i); openG.Add(g[k]); }
            if (c.Paths[i] == pi && (c.PathSurfaces.Length > i ? c.PathSurfaces[i] : 0) == surface) continue;
            cost += each * (under[k] ? ToyCity.Sim.PathGrade.TunnelCostTimes : c.IsRiver(i) ? BridgeCostTimes : 1);
        }
        bool Locked(int i) => c.BuildingAt[i] != 0 || c.IsWater(i) || c.Paths[i] != 0;
        var moved = ToyCity.Sim.PathGrade.Shape(c.Corners, c.Width, c.Height, openIdx, openG, Locked, floor);
        long grading = ToyCity.Sim.PathGrade.Cost(c.Corners, c.Width, openIdx, moved);
        return new PathPlan(cost + grading, grading, null, pct, ToyCity.Sim.PathGrade.DeepestCut(h, g, fix, under), under.Count(u => u));
    }

    /// <summary>A building's pad: the steps of earth it moves, or null when the lot rises more than a pad can level.</summary>
    public static int? PadSteps(CityMirror c, int x0, int y0, int w, int h)
    {
        int lo = int.MaxValue, hi = int.MinValue, sum = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                if (!c.InBounds(x, y)) return 0;
                int v = c.Heights[c.Idx(x, y)];
                lo = Math.Min(lo, v); hi = Math.Max(hi, v); sum += v;
            }
        if (hi - lo > MaxPadRise) return null;
        int n = w * h, level = Math.Max(c.WaterLevel, (2 * sum + n) / (2 * n)), steps = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) steps += Math.Abs(c.Heights[c.Idx(x, y)] - level);
        return steps;
    }

    /// <summary>The cost line's grading part: " (grading $X)", or nothing on ground that needs none.</summary>
    static string Grading(long dollars) => dollars > 0 ? $" (grading {Money(dollars)})" : "";

    /// <summary>
    /// The surfaces a path type may be built in, for a picker: the id to put in <see cref="ToolState.Surface"/> ("" for
    /// the default, which comes first) and a label with the grade it climbs (Beta 3.2).
    /// </summary>
    public static List<(string Id, string Label)> SurfaceChoices(CityMirror c, string profileId)
    {
        var prof = c.Profiles.FirstOrDefault(q => q.Id == profileId);
        var list = new List<(string, string)>();
        if (prof == null) return list;
        for (int k = 0; k < prof.Surfaces.Count; k++)
        {
            var s = c.Surfaces.FirstOrDefault(q => q.Id == prof.Surfaces[k]);
            if (s == null) continue;
            string grade = prof.Stairs ? $"ramp {s.MaxGradePct}%, stairs to {prof.MaxGradePct}%" : $"climbs {Math.Min(prof.MaxGradePct, s.MaxGradePct)}%";
            list.Add((k == 0 ? "" : s.Id, $"{s.Name} ({grade})"));
        }
        return list;
    }

    /// <summary>" in brick" when the tool builds in a surface other than the profile's default, else "".</summary>
    static string SurfaceName(CityMirror c, ToolState t)
    {
        if (string.IsNullOrEmpty(t.Surface)) return "";
        var s = c.Surfaces.FirstOrDefault(q => q.Id == t.Surface);
        return s == null ? "" : $" in {s.Name.ToLowerInvariant()}";
    }

    static Preview Diagonal(CityMirror c, ToolState t, (int X, int Y) s, Pointer p)
    {
        var (ex, ey) = BorderSnap(c, p.X, p.Y);
        var cells = LineCells(s.X, s.Y, ex, ey);
        var prof = c.Profiles.FirstOrDefault(q => q.Id == t.ProfileId);
        int pi = prof == null ? 0 : c.Profiles.ToList().IndexOf(prof) + 1;
        long cost = 0;
        bool ok = true;
        var tinted = new List<(int, int, Tint)>();
        foreach (var (x, y) in cells)
        {
            bool good = c.InBounds(x, y) && (!c.IsWater(c.Idx(x, y)) || c.IsRiver(c.Idx(x, y))) && c.BuildingAt[c.Idx(x, y)] == 0;
            ok &= good;
            tinted.Add((x, y, good ? Tint.Road : Tint.Bad));
        }
        var plan = PlanPath(c, t, cells);
        cost += plan.Cost;
        ok &= plan.Problem == null;
        var action = new PAction { Path = new PlacePath { X0 = s.X, Y0 = s.Y, X1 = ex, Y1 = ey, ProfileId = t.ProfileId, Diagonal = true, Surface = t.Surface, Tunnel = t.Tunnel } };
        return new Preview(tinted, ok, plan.Problem ?? $"Diagonal{SurfaceName(c, t)}, {cells.Count} cells: {Money(cost)}{Grading(plan.Grading)}{Depth(plan, t.Tunnel)}", new() { action }, $"Diagonal {ProfileName(c, t.ProfileId)} from {s.X},{s.Y} to {ex},{ey}");
    }

    static Rect RectOf((int X, int Y) a, (int X, int Y) b) => new() { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y };

    static Preview Area(CityMirror c, ToolState t, (int X, int Y) s, Pointer p)
    {
        int x0 = Math.Min(s.X, p.X), x1 = Math.Max(s.X, p.X), y0 = Math.Min(s.Y, p.Y), y1 = Math.Max(s.Y, p.Y);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var tint = t.Kind switch { ToolKind.Zone => Tint.Zone, ToolKind.Bulldoze => Tint.Clear, ToolKind.Terraform => Tint.Earth, _ => Tint.Leftover };
        var cells = new List<(int, int, Tint)>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (c.InBounds(x, y)) cells.Add((x, y, tint));
        var area = RectOf(s, (p.X, p.Y));
        string size = $"{w}×{h}";
        return t.Kind switch
        {
            ToolKind.Zone => new Preview(cells, true, $"{size}: about {Money((long)w * h * ZoneCostPerCell)}",
                new() { new PAction { Zone = new PaintZone { Area = area, Zone = t.Zone, Density = t.Density } } },
                $"Zone {size} {ZoneNames[(int)t.Zone]} ({DensityNames[(int)t.Density].ToLowerInvariant()})"),
            ToolKind.Dezone => new Preview(cells, true, size, new() { new PAction { Zone = new PaintZone { Area = area, Zone = ZoneKind.ZoneNone } } }, $"De-zone {size}"),
            ToolKind.Bulldoze => new Preview(cells, true, t.Underground ? $"{size}: pipes only" : size,
                new() { new PAction { Bulldoze = new Bulldoze { Area = area, Underground = t.Underground } } }, $"Bulldoze {size} at {s.X},{s.Y}"),
            _ => new Preview(cells, true, $"{size}: up to {Money((long)w * h * TerraformPerCell)}",
                new() { new PAction { Terraform = new Terraform { Area = area, Mode = t.Terraform } } }, $"{TerraformNames[(int)t.Terraform]} {size}"),
        };
    }

    static Preview Block(CityMirror c, ToolState t, Pointer p)
    {
        var block = c.BlockAt(p.X, p.Y);
        if (block == null)
            return new Preview(new(), false, c.InBounds(p.X, p.Y) ? "Not a block: no roads close off this land" : "", new(), "");
        var depth = c.FrontageDepth(block);
        int reach = c.LotDepth[(int)t.Density];
        var cells = new List<(int, int, Tint)>();
        int lots = 0;
        foreach (int i in block)
        {
            int d = depth[i];
            bool lot = d > 0 && d <= reach;
            if (lot) lots++;
            cells.Add((i % c.Width, i / c.Width, lot ? Tint.Zone : Tint.Leftover));
        }
        int rest = block.Count - lots;
        long park = c.TypeById.TryGetValue("pocket-park", out var pp) ? pp.Cost : 0;
        long cost = (t.Leftover == Leftover.Zone ? block.Count : lots) * (long)ZoneCostPerCell + (t.Leftover == Leftover.Parks ? rest * park : 0);
        string left = rest == 0 ? "" : $", {rest} leftover ({t.Leftover switch { Leftover.Parks => "parks", Leftover.Zone => "zoned anyway", _ => "left empty" }})";
        return new Preview(cells, true, $"Block: {lots} cells in reach of a road{left}: about {Money(cost)}",
            new() { new PAction { Block = new ZoneBlock { X = p.X, Y = p.Y, Zone = t.Zone, Density = t.Density, Leftover = t.Leftover } } },
            $"Zone the block at {p.X},{p.Y} {ZoneNames[(int)t.Zone]} ({DensityNames[(int)t.Density].ToLowerInvariant()})");
    }

    /// <summary>Whether a footprint is all dry, empty land.</summary>
    static bool Clear(CityMirror c, int fx, int fy, int w, int h)
    {
        for (int y = fy; y < fy + h; y++)
            for (int x = fx; x < fx + w; x++)
            {
                if (!c.InBounds(x, y)) return false;
                int i = c.Idx(x, y);
                if (c.IsWater(i) || c.BuildingAt[i] != 0 || c.Paths[i] != 0) return false;
            }
        return true;
    }

    /// <summary>Whether a footprint has a path of the given kind along one of its sides.</summary>
    static bool Touches(CityMirror c, int fx, int fy, int w, int h, Func<PathProfileInfo, bool> carries)
    {
        for (int y = fy - 1; y <= fy + h; y++)
            for (int x = fx - 1; x <= fx + w; x++)
            {
                bool edge = (x == fx - 1 || x == fx + w) != (y == fy - 1 || y == fy + h);
                if (edge && c.InBounds(x, y) && c.ProfileAt(c.Idx(x, y)) is { } prof && carries(prof)) return true;
            }
        return false;
    }

    static Func<PathProfileInfo, bool> Carries(BuildingTypeInfo t) => t.Transit switch
    {
        "bus" => q => q.Cars,
        "tram" => q => !string.IsNullOrEmpty(q.Tram),
        "rail" => q => q.Rail,
        _ => q => q.Frontage,
    };

    /// <summary>
    /// Where a building's footprint goes for the cursor: centred on it when free, else the nearest clear spot
    /// within two cells that faces a road (or the path its vehicles use), falling back to the cursor.
    /// </summary>
    public static (int X, int Y) Footprint(CityMirror c, BuildingTypeInfo t, Pointer p)
    {
        int w = Math.Max(1, t.Width), h = Math.Max(1, t.Height);
        var at = (X: p.X - (w - 1) / 2, Y: p.Y - (h - 1) / 2);
        if (p.Free) return at;
        var carries = Carries(t);
        (int X, int Y)? best = null;
        int bestD = int.MaxValue;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int d = dx * dx + dy * dy;
                if (d >= bestD || !Clear(c, at.X + dx, at.Y + dy, w, h) || !Touches(c, at.X + dx, at.Y + dy, w, h, carries)) continue;
                (best, bestD) = ((at.X + dx, at.Y + dy), d);
            }
        return best ?? at;
    }

    static Preview Building(CityMirror c, BuildingTypeInfo t, Pointer p)
    {
        int w = Math.Max(1, t.Width), h = Math.Max(1, t.Height);
        var (fx, fy) = Footprint(c, t, p);
        var pad = PadSteps(c, fx, fy, w, h);
        bool ok = Clear(c, fx, fy, w, h) && pad != null;
        var cells = new List<(int, int, Tint)>();
        for (int y = fy; y < fy + h; y++)
            for (int x = fx; x < fx + w; x++)
                if (c.InBounds(x, y)) cells.Add((x, y, ok ? Tint.Building : Tint.Bad));
        string text = pad == null ? $"{t.Name}: the ground is too uneven for a pad"
            : pad > 0 ? $"{t.Name}: {Money(t.Cost + pad.Value * (long)PadCostPerStep)} (pad {Money(pad.Value * (long)PadCostPerStep)})"
            : $"{t.Name}: {Money(t.Cost)}";
        if (!string.IsNullOrEmpty(t.Transit))
            text += Touches(c, fx, fy, w, h, Carries(t)) ? $" · serves {t.Catchment} cells around" : $" · must touch {(t.Transit == "bus" ? "a road" : t.Transit == "tram" ? "a tram line" : "a rail line")}";
        else if (ok && !Touches(c, fx, fy, w, h, q => q.Frontage)) text += " · no road beside it";
        return new Preview(cells, ok, text, new() { new PAction { Building = new PlaceBuilding { X = fx, Y = fy, TypeId = t.Id } } }, $"{t.Name} at {fx},{fy}")
            { Catchment = string.IsNullOrEmpty(t.Transit) ? 0 : t.Catchment };
    }

    /// <summary>
    /// Where a roadside stop goes for the cursor: on the kerb of the road under it nearest the cursor, or on the kerb
    /// of a road beside the land under it, facing that land. Mirrors World.CheckKerb and the web client.
    /// </summary>
    public static (int X, int Y, int Side, string Why) KerbSpot(CityMirror c, BuildingTypeInfo t, Pointer p)
    {
        var carries = Carries(t);
        bool Carries2(int x, int y) => c.InBounds(x, y) && c.ProfileAt(c.Idx(x, y)) is { } q && (string.IsNullOrEmpty(t.Transit) || carries(q));
        bool Kerb(int x, int y, int k) { int nx = x + SideDx[k], ny = y + SideDy[k]; return !c.InBounds(nx, ny) || c.Paths[c.Idx(nx, ny)] == 0; }
        float[] edge = { p.Fy, 1 - p.Fx, 1 - p.Fy, p.Fx };
        var order = new[] { 0, 1, 2, 3 }.OrderBy(k => edge[k]).ToArray();
        string what = t.Transit switch { "bus" => "a road buses can use", "tram" => "a tram line", "rail" => "a rail line", _ => "a road" };
        int x = p.X, y = p.Y, side = -1;
        if (Carries2(x, y)) side = order.Where(k => Kerb(x, y, k)).DefaultIfEmpty(-1).First();
        else if (c.InBounds(x, y) && c.Paths[c.Idx(x, y)] == 0)
        {
            int k = order.Where(k => Carries2(x + SideDx[k], y + SideDy[k])).DefaultIfEmpty(-1).First();
            if (k >= 0) { x += SideDx[k]; y += SideDy[k]; side = (k + 2) % 4; }
        }
        if (side < 0) return (x, y, 0, Carries2(x, y) ? "no kerb here: roads join on every side" : $"must go beside {what}");
        if (c.StopsAt(x, y).Any(b => b.Side == side)) return (x, y, side, "a stop is already on that side");
        return (x, y, side, "");
    }

    static Preview Roadside(CityMirror c, BuildingTypeInfo t, Pointer p)
    {
        var k = KerbSpot(c, t, p);
        bool ok = k.Why == "";
        var cells = c.InBounds(k.X, k.Y) ? new List<(int, int, Tint)> { (k.X, k.Y, ok ? Tint.Stop : Tint.Bad) } : new();
        string text = $"{t.Name}: {Money(t.Cost)} · " + (ok ? $"{SideNames[k.Side]} side of the road, serves {t.Catchment} cells around" : k.Why);
        var actions = ok ? new List<PAction> { new() { Building = new PlaceBuilding { X = k.X, Y = k.Y, TypeId = t.Id, Side = k.Side + 1 } } } : new();
        return new Preview(cells, ok, text, actions, $"{t.Name} on the {SideNames[k.Side]} side at {k.X},{k.Y}") { Side = k.Side, Catchment = t.Catchment };
    }
}
