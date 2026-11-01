using Godot;
using ToyCity.ClientModel.Gate;

namespace ToyCity.Godot;

/// <summary>
/// The Beta 3.2 gate's hill run (--hills FILE): founds a 128-cell city whose starter town sits on a hillside, checks
/// the town isn't on a flat platform and that the ground has its layers, then builds the hill works the client's
/// planner finds near it (a road tunnelling under a hill, a road cut into the rock, a footpath up the hill as
/// stairs) and looks at each, measuring frame times and taking pictures as the mountain run does.
/// </summary>
public partial class Main
{
    /// <summary>The seed: a town among hills with rock near the surface (the same as the client model's HillTests).</summary>
    public const ulong HillSeed = 5;

    IEnumerable<GateWait> HillSteps()
    {
        var c = _client!.City;
        yield return new GateWait("the first city", () => c.Loaded, 60);
        _client.Connection.Send(new Protocol.V0.ClientMessage { NewCity = new Protocol.V0.NewCityRequest { Name = "Hill Town", Seed = HillSeed, Size = 128, StarterTown = true } });
        yield return new GateWait("the hill town", () => c.Loaded && c.Stats.CityName == "Hill Town" && c.Buildings.Count > 0, 180);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        yield return new GateWait("the hill town drawn", () => _map!.Drawn && _centred, 120);

        // The town stands on a hillside: its streets climb.
        var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
        float tx = built.Average(b => b.X + b.Width / 2f), ty = built.Average(b => b.Y + b.Height / 2f);
        var (lo, hi) = HillSpots.StreetRise(c);
        MountainCheck("town on a hillside", hi - lo >= 60, $"streets from {lo / 10} m to {hi / 10} m, {(hi - lo) / 10.0:0.#} m apart");
        // The ground has its layers.
        var kinds = c.Rock.Where(k => k != 0).Distinct().Count();
        int bare = 0;
        for (int k = 0; k < c.Bedrock.Length; k++) if (c.SoilDm(k) == 0) bare++;
        MountainCheck("ground in layers", kinds >= 3 && bare > 0, $"{kinds} kinds of rock; {bare:N0} corners of bare rock");

        // The works.
        var spots = HillSpots.Find(c, c.Stats.Funds);
        MountainCheck("hill works found", spots.Count == 3, string.Join("; ", spots.Select(s => $"{s.What} at {s.Path.X0},{s.Path.Y0} (${s.Plan.Cost:N0})")));
        foreach (var s in spots) _client.Connection.Act(new Protocol.V0.Action { Path = s.Path });
        yield return new GateWait("the hill works built", () => spots.All(s => c.Paths[c.Idx(s.Path.X1, s.Path.Y1)] != 0), 60);
        yield return new GateWait("the hill works drawn", () => _map!.Drawn, 60);
        var tunnel = spots.Single(s => s.What.Contains("tunnel"));
        int under = ClientModel.Tools.Planner.LineCells(tunnel.Path.X0, tunnel.Path.Y0, tunnel.Path.X1, tunnel.Path.Y1).Count(q => c.IsTunnel(c.Idx(q.X, q.Y)));
        MountainCheck("tunnel", under >= 2, $"{under} cells under the hill");

        var v = _camera!.View;
        _camera.CentreOn(tx, ty);
        v.Zoom = 3;
        yield return MountainMeasure("classic, the town on its hillside");
        _camera.ToggleFree();
        var f = _free!;
        f.Snap = false;
        void Look(float x, float y, float distance, float pitch, float yaw)
        {
            f.Target = new Vector3(x, _view!.Surface(x, y), y);
            (f.Distance, f.Pitch, f.Yaw) = (distance, pitch, yaw);
        }
        Look(tx, ty, 60, 20, 30);
        yield return MountainMeasure("free, the town from the hills");
        Look(tx, ty, 18, 30, 200);
        yield return MountainMeasure("free, a street on the slope");
        foreach (var s in spots)
        {
            bool alongX = s.Path.Y0 == s.Path.Y1;
            // Looking along the road for the cut and the tunnel; across the hill for the stairs.
            // The tunnel from its open end, outside the hill, looking at its portal.
            float yaw = s.What.Contains("stairs") ? (alongX ? 0 : 90) : s.What.Contains("tunnel") ? (alongX ? 270 : 180) : (alongX ? 90 : 0);
            var cells = ClientModel.Tools.Planner.LineCells(s.Path.X0, s.Path.Y0, s.Path.X1, s.Path.Y1);
            var portal = cells.FirstOrDefault(q => c.IsTunnel(c.Idx(q.X, q.Y)), cells[0]);
            float ex = s.What.Contains("tunnel") ? portal.X + 0.5f : s.Fx, ey = s.What.Contains("tunnel") ? portal.Y + 0.5f : s.Fy;
            Look(ex, ey, s.What.Contains("stairs") ? 9 : 16, 24, yaw);
            yield return MountainMeasure($"free, {s.What}");
        }
        _camera.ToggleFree();
    }
}
