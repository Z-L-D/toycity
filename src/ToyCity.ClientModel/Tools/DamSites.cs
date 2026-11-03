namespace ToyCity.ClientModel.Tools;

/// <summary>
/// Places a dam could go, worked out from what a client sees (river brush and dams): a row or column across a river,
/// dry and unbuilt banks three cells out either side, with about how many cells its reservoir would cover at a rise.
/// The server has the last word (World.ValidateDam); this is for the gate runs and the shot scripts to find a site.
/// </summary>
public static class DamSites
{
    public readonly record struct Site(int X0, int Y0, int X1, int Y1, int River, int Pool);

    public static List<Site> Find(CityMirror c, int rise, int most = 4000)
    {
        int W = c.Width, H = c.Height;
        var sites = new List<Site>();
        bool Free(int i) => !c.IsWater(i) && c.BuildingAt[i] == 0 && c.Paths[i] == 0;
        foreach (bool across in new[] { true, false })
            for (int a = 10; a < (across ? H : W) - 10; a += 2)
            {
                int len = across ? W : H;
                int I(int k) => across ? a * W + k : k * W + a;
                for (int s = 6; s < len - 14; s++)
                {
                    if (!Free(I(s)) || !c.IsRiver(I(s + 1))) continue;
                    int e = s + 1;
                    while (e < len - 1 && c.IsRiver(I(e))) e++;
                    if (e - s > 6 || !Free(I(e))) continue;
                    bool ok = true;
                    for (int k = s - 3; k <= e + 3; k++) if (!c.IsRiver(I(k)) && !Free(I(k))) ok = false;
                    if (!ok) continue;
                    var line = new HashSet<int>();
                    for (int k = s - 3; k <= e + 3; k++) line.Add(I(k));
                    int top = 0;
                    for (int k = s + 1; k < e; k++) top = Math.Max(top, c.Rivers[I(k)]);
                    int level;
                    int Side(int j) => across ? Math.Sign(j / W - a) : Math.Sign(j % W - a);
                    int hiPlus = -1, hiMinus = -1;
                    var near = new List<int>();
                    foreach (int i in line)
                        foreach (int j in new[] { i - W, i + W, i - 1, i + 1 })
                        {
                            if (j < 0 || j >= W * H || line.Contains(j)) continue;
                            near.Add(j);
                            if (!c.IsRiver(j)) continue;
                            if (Side(j) > 0) hiPlus = Math.Max(hiPlus, c.Rivers[j]); else if (Side(j) < 0) hiMinus = Math.Max(hiMinus, c.Rivers[j]);
                        }
                    if (hiPlus < 0 || hiMinus < 0) continue;
                    level = Math.Max(top, Math.Max(hiPlus, hiMinus)) + rise;
                    HashSet<int>? Fill(int up)
                    {
                        // The river below: the reservoir may not reach round to it.
                        var below = new HashSet<int>();
                        var bq = new Queue<int>();
                        foreach (int j in near)
                            if (Side(j) == -up && c.IsRiver(j) && below.Add(j)) bq.Enqueue(j);
                        while (bq.Count > 0)
                        {
                            int i = bq.Dequeue();
                            foreach (int j in new[] { i - W, i + W, i - 1, i + 1 })
                            {
                                if (j < 0 || j >= W * H || Math.Abs(j % W - i % W) > 1 || line.Contains(j) || !c.IsRiver(j) || c.Rivers[j] >= level || !below.Add(j)) continue;
                                bq.Enqueue(j);
                            }
                        }
                        var seen = new HashSet<int>();
                        var queue = new Queue<int>();
                        foreach (int j in near)
                            if (Side(j) == up && c.Heights[j] < level && seen.Add(j)) queue.Enqueue(j);
                        bool bad = false;
                        while (queue.Count > 0 && !bad)
                        {
                            int i = queue.Dequeue(), x = i % W, y = i / W;
                            if (((x == 0 || y == 0 || x == W - 1 || y == H - 1) && !c.IsRiver(i)) || below.Contains(i) || seen.Count > most || c.BuildingAt[i] != 0 || c.Paths[i] != 0
                                || (c.Heights[i] < c.WaterLevel && !c.IsRiver(i)))
                            { bad = true; break; }
                            foreach (int j in new[] { i - W, i + W, i - 1, i + 1 })
                            {
                                if (j < 0 || j >= W * H || Math.Abs(j % W - x) > 1 || line.Contains(j) || (c.Dams.Length > j && c.Dams[j] != 0) || c.Heights[j] >= level || !seen.Add(j)) continue;
                                queue.Enqueue(j);
                            }
                        }
                        return bad ? null : seen;
                    }
                    int first = hiPlus >= hiMinus ? 1 : -1;
                    var pool = Fill(first) ?? (hiPlus == hiMinus ? Fill(-first) : null);
                    if (pool == null) continue;
                    var (x0, y0, x1, y1) = across ? (s - 3, a, e + 3, a) : (a, s - 3, a, e + 3);
                    sites.Add(new Site(x0, y0, x1, y1, top, pool.Count));
                }
            }
        return sites;
    }

    /// <summary>A river cell to dig a new channel from, with flat dry land for <paramref name="length"/> cells east of it.</summary>
    public static (int X, int Y)? DigStart(CityMirror c, int length = 14)
    {
        int W = c.Width, H = c.Height;
        for (int y = 20; y < H - 20; y++)
            for (int x = 20; x < W - length - 4; x++)
            {
                int i = y * W + x;
                if (!c.IsRiver(i) || (c.Dams.Length > i && c.Dams[i] != 0)) continue;
                bool ok = true;
                for (int k = 1; k <= length && ok; k++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int j = (y + dy) * W + x + k;
                        if (c.IsWater(j) || c.BuildingAt[j] != 0 || c.Paths[j] != 0 || c.Heights[j] > c.Rivers[i] + 3) ok = false;
                    }
                if (ok) return (x, y);
            }
        return null;
    }
}
