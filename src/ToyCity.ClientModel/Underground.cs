using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// What the underground view (Beta 3.2) draws, as a client lays it out: every pipe cell's water main and sewer, side
/// by side and at their depths, and the road tunnels at their decks. Pumps and sewage plants are picked out.
/// </summary>
public static class Underground
{
    /// <summary>Depths below the ground, in metres: the water main shallow, the sewer below it.</summary>
    public const float WaterDepthM = 2.5f, SewerDepthM = 5f;
    /// <summary>How far each main sits to one side of the cell's middle, in cells (a cell is 16 m).</summary>
    public const float Offset = 0.16f;

    /// <summary>One straight length of main: from one cell's middle to the next's, or a lone cell's stub.</summary>
    public readonly record struct Run(int X0, int Y0, int X1, int Y1);

    /// <summary>
    /// The pipes as straight runs: each longest row and column of pipe cells, and a lone cell as a run of itself.
    /// A cell where a row and a column meet is in both.
    /// </summary>
    public static List<Run> PipeRuns(CityMirror c) => Runs(c, i => c.WaterPipes.Length > i && c.WaterPipes[i] != 0);

    /// <summary>The road tunnels as straight runs, as <see cref="PipeRuns"/> does for pipes.</summary>
    public static List<Run> TunnelRuns(CityMirror c) => Runs(c, c.IsTunnel);

    static List<Run> Runs(CityMirror c, Func<int, bool> on)
    {
        var runs = new List<Run>();
        int w = c.Width, h = c.Height;
        bool On(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && on(y * w + x);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!On(x, y)) continue;
                // A row starts here: run east while it lasts.
                if (!On(x - 1, y) && On(x + 1, y))
                {
                    int e = x;
                    while (On(e + 1, y)) e++;
                    runs.Add(new Run(x, y, e, y));
                }
                if (!On(x, y - 1) && On(x, y + 1))
                {
                    int s = y;
                    while (On(x, s + 1)) s++;
                    runs.Add(new Run(x, y, x, s));
                }
                if (!On(x - 1, y) && !On(x + 1, y) && !On(x, y - 1) && !On(x, y + 1)) runs.Add(new Run(x, y, x, y));
            }
        return runs;
    }

    /// <summary>Pumps and sewage plants, the buildings the view picks out.</summary>
    public static IEnumerable<(BuildingState B, bool Plant)> Works(CityMirror c)
    {
        foreach (var b in c.Buildings.Values)
            if (c.TypeById.TryGetValue(b.TypeId, out var t) && (t.WaterOut > 0 || t.SewageCap > 0))
                yield return (b, t.SewageCap > 0);
    }

    /// <summary>The view's key and the city's sewage, for the panel.</summary>
    public static string Legend(CityStats s)
    {
        string sewage = s.SewageProduced == 0 ? "No sewage yet."
            : s.SewageUntreated > 0 ? $"Sewage {s.SewageProduced:N0} a day, {s.SewageCapacity:N0} treated by plants, {s.SewageUntreated:N0} left raw."
            : s.SewageCapacity == 0 ? $"Sewage {s.SewageProduced:N0} a day, on septic tanks."
            : $"Sewage {s.SewageProduced:N0} a day, all treated (plants take {s.SewageCapacity:N0}).";
        return $"Water mains blue, sewers brown beside them, road tunnels grey. {sewage}";
    }

    /// <summary>
    /// Where a sewage plant of the given size can go beside the pipes: empty dry land touching a pipe cell, nearest
    /// the given point first. For the gate and for tests.
    /// </summary>
    public static IEnumerable<(int X, int Y)> PlantSites(CityMirror c, int w, int h, float nearX, float nearY)
    {
        var sites = new List<(int X, int Y, float D)>();
        for (int y = 1; y + h < c.Height - 1; y++)
            for (int x = 1; x + w < c.Width - 1; x++)
            {
                bool free = true, pipe = false;
                for (int yy = y - 1; yy <= y + h && free; yy++)
                    for (int xx = x - 1; xx <= x + w && free; xx++)
                    {
                        int i = c.Idx(xx, yy);
                        bool inside = xx >= x && xx < x + w && yy >= y && yy < y + h;
                        if (inside && (c.Paths[i] != 0 || c.BuildingAt[i] != 0 || c.IsWater(i) || c.Zones[i] != 0)) free = false;
                        if (!inside && c.WaterPipes[i] != 0 && (xx == x - 1 || xx == x + w) != (yy == y - 1 || yy == y + h)) pipe = true;
                    }
                if (free && pipe) sites.Add((x, y, (x + w / 2f - nearX) * (x + w / 2f - nearX) + (y + h / 2f - nearY) * (y + h / 2f - nearY)));
            }
        return sites.OrderBy(s => s.D).Select(s => (s.X, s.Y));
    }
}
