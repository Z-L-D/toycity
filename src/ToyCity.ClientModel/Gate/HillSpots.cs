using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Gate;

/// <summary>
/// The Beta 3.2 gate's hill works: near the starter town, the best places to show what paths do on hills. A road
/// cut deep into the rock, a road that tunnels under a hill, and a footpath steep enough to be stairs. Found with
/// the client's own planner (the same grading the server does), so the gate, a test and a player see one plan.
/// </summary>
public static class HillSpots
{
    /// <summary>One of the works: what it shows, the path to lay, and what the planner says it will cost.</summary>
    public sealed record Spot(string What, PlacePath Path, Planner.PathPlan Plan)
    {
        public float Fx => (Path.X0 + Path.X1) / 2f + 0.5f;
        public float Fy => (Path.Y0 + Path.Y1) / 2f + 0.5f;
    }

    /// <summary>
    /// The three works, each as dear as at most a third of <paramref name="budget"/>, on open ground within
    /// <paramref name="reach"/> cells of the town, not crossing one another. Any that can't be found are left out.
    /// </summary>
    public static List<Spot> Find(CityMirror c, long budget, int reach = 40)
    {
        var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
        int tx = built.Count > 0 ? (int)built.Average(b => b.X + b.Width / 2f) : c.Width / 2;
        int ty = built.Count > 0 ? (int)built.Average(b => b.Y + b.Height / 2f) : c.Height / 2;
        var used = new HashSet<int>();
        var spots = new List<Spot>();
        bool Open(int i) => c.Paths[i] == 0 && c.BuildingAt[i] == 0 && !c.IsWater(i) && !used.Contains(i);
        int Natural(List<(int X, int Y)> cells)
        {
            int steepest = 0;
            for (int k = 1; k < cells.Count; k++)
                steepest = Math.Max(steepest, Math.Abs(Sim.PathGrade.CellDmOf(c.Corners, c.Width, c.Idx(cells[k].X, cells[k].Y))
                    - Sim.PathGrade.CellDmOf(c.Corners, c.Width, c.Idx(cells[k - 1].X, cells[k - 1].Y))));
            return steepest;
        }
        // The deepest cut's soil: a cut that goes into the rock shows it.
        int BareAtCut(List<(int X, int Y)> cells)
        {
            int thinnest = int.MaxValue;
            foreach (var (x, y) in cells)
            {
                int cw = c.Width + 1, k = y * cw + x;
                thinnest = Math.Min(thinnest, Math.Min(c.SoilDm(k), c.SoilDm(k + cw + 1)));
            }
            return thinnest;
        }
        void Best(string what, string profile, bool tunnel, int length, Func<List<(int X, int Y)>, Planner.PathPlan, int> score)
        {
            var t = new ToolState { Kind = ToolKind.Path, ProfileId = profile, Tunnel = tunnel };
            (int Score, List<(int X, int Y)> Cells, Planner.PathPlan Plan)? best = null;
            for (int y = Math.Max(1, ty - reach); y < Math.Min(c.Height - 1, ty + reach); y += 2)
            for (int x = Math.Max(1, tx - reach); x < Math.Min(c.Width - 1, tx + reach); x += 2)
            foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
            {
                int x1 = x + dx * (length - 1), y1 = y + dy * (length - 1);
                if (x1 >= c.Width - 1 || y1 >= c.Height - 1) continue;
                var cells = Planner.LineCells(x, y, x1, y1);
                // Open ground with a clear cell either side, so the works stand on their own.
                if (!cells.All(q => Open(c.Idx(q.X, q.Y)) && Open(c.Idx(q.X + dy, q.Y + dx)) && Open(c.Idx(q.X - dy, q.Y - dx)))) continue;
                var plan = Planner.PlanPath(c, t, cells);
                if (plan.Problem != null || plan.Cost > budget / 3) continue;
                int s = score(cells, plan);
                if (s > 0 && (best == null || s > best.Value.Score)) best = (s, cells, plan);
            }
            if (best is not { } b) return;
            foreach (var (x, y) in b.Cells)
                for (int d = -2; d <= 2; d++) { used.Add(c.Idx(Math.Clamp(x + d, 0, c.Width - 1), y)); used.Add(c.Idx(x, Math.Clamp(y + d, 0, c.Height - 1))); }
            var (x0, y0) = b.Cells[0];
            var (xe, ye) = b.Cells[^1];
            spots.Add(new Spot(what, new PlacePath { X0 = x0, Y0 = y0, X1 = xe, Y1 = ye, ProfileId = profile, Tunnel = tunnel }, b.Plan));
            budget -= b.Plan.Cost;
        }
        // A tunnel: the most cells under a hill.
        Best("a road tunnelling under a hill", "road", true, 18, (_, p) => p.TunnelCells >= 2 ? p.TunnelCells : 0);
        // A cut: deep, and into the rock (the less soil at its deepest the better), short of a tunnel.
        Best("a road cut into the rock", "road", false, 14, (cells, p) =>
            p.DeepestCutDm >= 30 && p.DeepestCutDm < Sim.PathGrade.TunnelMinDm + 40 ? p.DeepestCutDm * 4 - Math.Min(200, BareAtCut(cells)) : 0);
        // Stairs: a footpath up the steepest ground (more than a ramp holds) that it can climb without much digging.
        Best("a footpath up the hill as stairs", "walking-trail", false, 6, (cells, p) =>
            Natural(cells) * 100 / Sim.PathGrade.CellDm > 30 && p.DeepestCutDm <= 20 ? Natural(cells) : 0);
        return spots;
    }
}
