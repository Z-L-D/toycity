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
    public bool Curve;                    // paths: set two ends, then bend
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
    public const int ZoneCostPerCell = 5, PowerLinePerCell = 5, WaterPipePerCell = 3, TerraformPerCell = 25;
    public static readonly string[] DensityNames = { "Low", "Medium", "High" };
    public static readonly string[] ZoneNames = { "", "residential", "commercial", "industrial" };
    public static readonly string[] DisasterNames = { "Fire", "Tornado", "Earthquake", "Clockwork Colossus" };
    public static readonly string[] TerraformNames = { "Raise", "Lower", "Level" };
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
            case ToolKind.Path or ToolKind.Utility when p.DragStart is { } s:
                return Line(c, t, s, p);
            case ToolKind.Zone or ToolKind.Dezone or ToolKind.Bulldoze or ToolKind.Terraform when p.DragStart is { } s:
                return Area(c, t, s, p);
            default:
                return null;
        }
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
            if (!c.InBounds(x, y) || c.IsWater(c.Idx(x, y))) good = false;
            else
            {
                int i = c.Idx(x, y);
                if (path) { if (c.BuildingAt[i] != 0) good = false; if (c.Paths[i] != pi) cost += prof?.CostPerCell ?? 0; }
                else if (t.Utility == UtilityKind.UtilityPowerLine) { if (c.BuildingAt[i] == 0 && c.PowerLines[i] == 0) cost += PowerLinePerCell; }
                else if (c.WaterPipes[i] == 0) cost += WaterPipePerCell;
            }
            ok &= good;
            tinted.Add((x, y, !good ? Tint.Bad : path ? Tint.Road : t.Utility == UtilityKind.UtilityPowerLine ? Tint.Power : Tint.Water));
        }
        var actions = legs.Select(l => path
            ? new PAction { Path = new PlacePath { X0 = l.Item1, Y0 = l.Item2, X1 = l.Item3, Y1 = l.Item4, ProfileId = t.ProfileId } }
            : new PAction { Utility = new PlaceUtility { X0 = l.Item1, Y0 = l.Item2, X1 = l.Item3, Y1 = l.Item4, Kind = t.Utility } }).ToList();
        string what = path ? ProfileName(c, t.ProfileId) : t.Utility == UtilityKind.UtilityPowerLine ? "Power line" : "Water pipe";
        return new Preview(tinted, ok, $"{what}, {cells.Count} cells: {Money(cost)}", actions, $"{what} from {s.X},{s.Y} to {end.X},{end.Y}");
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
            bool good = c.InBounds(x, y) && !c.IsWater(c.Idx(x, y)) && c.BuildingAt[c.Idx(x, y)] == 0;
            if (good && c.Paths[c.Idx(x, y)] != pi) cost += prof?.CostPerCell ?? 0;
            ok &= good;
            tinted.Add((x, y, good ? Tint.Road : Tint.Bad));
        }
        var action = new PAction { Path = new PlacePath { X0 = e.X0, Y0 = e.Y0, X1 = e.X1, Y1 = e.Y1, ProfileId = t.ProfileId, Curved = true, Cx = p.X, Cy = p.Y } };
        return new Preview(tinted, ok, $"Curve, {cells.Count} cells: {Money(cost)}", new() { action }, $"Curved {ProfileName(c, t.ProfileId)} from {e.X0},{e.Y0} to {e.X1},{e.Y1}");
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
        bool ok = Clear(c, fx, fy, w, h);
        var cells = new List<(int, int, Tint)>();
        for (int y = fy; y < fy + h; y++)
            for (int x = fx; x < fx + w; x++)
                if (c.InBounds(x, y)) cells.Add((x, y, ok ? Tint.Building : Tint.Bad));
        string text = $"{t.Name}: {Money(t.Cost)}";
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
