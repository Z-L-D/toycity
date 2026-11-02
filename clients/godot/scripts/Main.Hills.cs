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

        // Underground (Beta 3.2): a sewage plant beside the town's pipes, then the view under the ground.
        var plantType = c.TypeById["sewage-plant"];
        int plantAt = -1;
        foreach (var (px, py) in ClientModel.Underground.PlantSites(c, plantType.Width, plantType.Height, tx, ty).Take(8))
        {
            _client.Connection.Act(new Protocol.V0.Action { Building = new Protocol.V0.PlaceBuilding { X = px, Y = py, TypeId = plantType.Id } });
            int at = c.Idx(px, py);
            int frames = 0;
            yield return new GateWait("the sewage plant placed", () => c.BuildingAt[at] != 0 || ++frames > 120, 30);
            if (c.BuildingAt[at] != 0) { plantAt = at; break; }
        }
        MountainCheck("sewage plant beside the pipes", plantAt >= 0, plantAt < 0 ? "no site took it" : $"at {plantAt % c.Width},{plantAt / c.Width}");
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 2 } });
        yield return new GateWait("the plant treating sewage", () => c.Stats.SewageCapacity > 0, 120);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        MountainCheck("sewers to the plant", c.Stats.SewageCapacity > 0 && c.Stats.SewageUntreated == 0, ClientModel.Underground.Legend(c.Stats));

        // Subways (Beta 3.2): a tunnel right under the town, and a station near each end.
        int sy = (int)ty, sx0 = Math.Max(2, (int)tx - 12), sx1 = Math.Min(c.Width - 3, (int)tx + 12);
        _client.Connection.Act(new Protocol.V0.Action { Utility = new Protocol.V0.PlaceUtility { X0 = sx0, Y0 = sy, X1 = sx1, Y1 = sy, Kind = Protocol.V0.UtilityKind.UtilitySubway } });
        yield return new GateWait("the subway laid", () => c.Subways[c.Idx(sx1, sy)] != 0, 30);
        var stationType = c.TypeById["subway-station"];
        var stationsAt = new List<int>();
        foreach (var endX in new[] { sx0 + 1, sx1 - 1 })
            foreach (var (px, py) in ClientModel.Underground.StationSites(c, stationType.Width, stationType.Height, endX, sy).Take(8))
            {
                _client.Connection.Act(new Protocol.V0.Action { Building = new Protocol.V0.PlaceBuilding { X = px, Y = py, TypeId = stationType.Id } });
                int at = c.Idx(px, py);
                int frames = 0;
                yield return new GateWait("a subway station placed", () => c.BuildingAt[at] != 0 || ++frames > 120, 30);
                if (c.BuildingAt[at] != 0) { stationsAt.Add(at); break; }
            }
        MountainCheck("subway under the town", c.Subways[c.Idx(sx0, sy)] != 0 && stationsAt.Count == 2,
            $"{sx1 - sx0 + 1} cells along row {sy}, stations at {string.Join(" and ", stationsAt.Select(i => $"{i % c.Width},{i / c.Width}"))}");

        _panels!.Model.ShowUnderground = true;
        int shown = 0;
        yield return new GateWait("the underground drawn", () => ++shown > 10 && _underground!.Drawn.Water > 0, 30);
        var d = _underground!.Drawn;
        MountainCheck("underground view", d.Water > 0 && d.Sewer == d.Water && d.Tunnel > 0 && d.Works >= 2 && _view!.Underground,
            $"{d.Water:N0} lengths of water main and {d.Sewer:N0} of sewer, {d.Tunnel} of road tunnel, {d.Works} pumps and plants; buildings hidden");
        var ds = _underground.DrawnSubway;
        MountainCheck("subway drawn", ds.Subway == sx1 - sx0 + 1 && ds.Stations == stationsAt.Count, $"{ds.Subway} lengths of subway tunnel, {ds.Stations} stations reaching down to it");
        _camera.CentreOn(tx, ty);
        v.Zoom = 3;
        yield return MountainMeasure("classic, underground");
        _camera.ToggleFree();
        var tunnelCells = ClientModel.Tools.Planner.LineCells(tunnel.Path.X0, tunnel.Path.Y0, tunnel.Path.X1, tunnel.Path.Y1).Where(q => c.IsTunnel(c.Idx(q.X, q.Y))).ToList();
        var mid = tunnelCells[tunnelCells.Count / 2];
        Look(mid.X + 0.5f, mid.Y + 0.5f, 22, 38, 30);
        yield return MountainMeasure("free, the tunnel and pipes underground");
        Look(tx, sy + 0.5f, 26, 22, 20);
        yield return MountainMeasure("free, the subway under the town");
        _camera.ToggleFree();
        _panels.Model.ShowUnderground = false;

        // Light (Beta 3.2, Part 6): the town grown a little, then at golden hour and at night.
        _client.Connection.Send(new Protocol.V0.ClientMessage { Pace = new Protocol.V0.SetPace { DaysPerClockDay = 720 } });
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 3 } });
        yield return new GateWait("the town grown", () => c.Stats.Population >= 150, 240);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        int lit = c.Buildings.Values.Count(b => ClientModel.BuildingLife.LitShare(b) > 0);
        foreach (var (pin, check) in new[] { ("Golden hour", "golden hour"), ("Night", "night") })
        {
            _graphics = _graphics with { TimeOfDay = ClientModel.Daylight.Pin(pin) };
            ApplyGraphics();
            int frames = 0;
            yield return new GateWait($"the light at {check}", () => ++frames > 5, 10);
            var l = LightNow;
            bool ok = pin == "Night" ? l.Night > 0.95f && lit > 0 : l.Sun.Warmth > 0.4f && l.Night < 0.1f && l.Elevation is > 0 and < 12;
            MountainCheck(check, ok, $"sun {l.Elevation:0}° up at {l.Azimuth:0}°, light ({l.Sun.R:0.00}, {l.Sun.G:0.00}, {l.Sun.B:0.00}) × {l.SunEnergy:0.00}, night {l.Night:0.00}"
                + (pin == "Night" ? $"; {lit} of {c.Buildings.Count} buildings with lit windows" : ""));
            _camera.CentreOn(tx, ty);
            v.Zoom = 3;
            yield return MountainMeasure($"classic, {check}");
            _camera.ToggleFree();
            Look(tx, ty, 34, 18, 210);
            yield return MountainMeasure($"free, {check}");
            _camera.ToggleFree();
        }
        _graphics = _graphics with { TimeOfDay = ClientModel.Daylight.Pin("Noon") };
        ApplyGraphics();
    }
}
