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
        // The ground's materials up close (RC part 6): open meadow on gentle wild ground near the town, and the
        // steepest crag on the map.
        {
            (int X, int Y) meadow = ((int)tx, (int)ty), crag = ((int)tx, (int)ty);
            float best = float.MaxValue, steep = -1;
            for (int y = 2; y < c.Height - 2; y++)
                for (int x = 2; x < c.Width - 2; x++)
                {
                    int i = c.Idx(x, y);
                    if (c.IsWater(i) || c.Paths[i] != 0 || c.BuildingAt[i] != 0 || c.Zones[i] != 0) continue;
                    float sl = ClientModel.TerrainShape.Slope(c, x, y);
                    float near = MathF.Abs(x - tx) + MathF.Abs(y - ty);
                    if (sl > 0.3f && sl < 1.2f && near < best) { best = near; meadow = (x, y); }
                    if (sl > steep) { steep = sl; crag = (x, y); }
                }
            Look(meadow.X + 0.5f, meadow.Y + 0.5f, 2.5f, 50, 150);
            yield return MountainMeasure("free, meadow up close");
            Look(crag.X + 0.5f, crag.Y + 0.5f, 14, 30, 30);
            yield return MountainMeasure("free, a crag up close");
        }
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

        // Snow (Beta 4, part 4): a blizzard over the town until the snow lies on the ground and the roofs.
        _client.Connection.Send(new Protocol.V0.ClientMessage { PinWeather = new Protocol.V0.PinWeather { Kind = Protocol.V0.WeatherKind.WeatherSnow, Strength = 3 } });
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 3 } });
        yield return new GateWait("snow lying", () => ClientModel.SnowCover.GroundMm(c) >= 100, 300);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        float median = ClientModel.SnowCover.MedianMetres(c);
        int dry = 0, white = 0;
        for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
            {
                if (c.IsWater(c.Idx(x, y))) continue;
                dry++;
                if (ClientModel.SnowCover.Whiteness(ClientModel.SnowCover.AtCell(c, x, y, median)) >= 0.5f) white++;
            }
        MountainCheck("snow on the ground", white * 2 >= dry,
            $"{ClientModel.SnowCover.GroundMm(c)} mm on level ground at the median height ({median:0} m above the water); {white:N0} of {dry:N0} dry cells white; {c.Stats.Roads?.Text}");
        _camera.CentreOn(tx, ty);
        v.Zoom = 3;
        yield return MountainMeasure("classic, snow");
        _camera.ToggleFree();
        Look(tx, ty, 34, 18, 210);
        yield return MountainMeasure("free, snow");
        _camera.ToggleFree();
        _client.Connection.Send(new Protocol.V0.ClientMessage { PinWeather = new Protocol.V0.PinWeather { Kind = Protocol.V0.WeatherKind.WeatherClear } });

        // Water quality (Beta 4, part 3): the sewage plant lets out raw sewage, and the water quality layer shows the
        // foul water below its outfall. Then back to secondary treatment.
        _client.Connection.Act(new Protocol.V0.Action { Treatment = new Protocol.V0.SetTreatment { Level = 0 } });
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 3 } });
        yield return new GateWait("foul water below the outfall", () => c.Stats.WaterQuality is { Treatment: 0, Worst: > 60 }, 300);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        _panels.Model.Click("overlay:21");
        yield return new GateWait("the water quality layer", () => _panels.Model.Overlay is { } o && (int)o.Kind == 21, 30);
        var layer = _panels.Model.Overlay!.Values.ToByteArray();
        int worst = Enumerable.Range(0, layer.Length).Where(i => c.IsWater(i)).OrderByDescending(i => layer[i]).ThenBy(i => i).First();
        var wq = c.Stats.WaterQuality!;
        MountainCheck("raw sewage fouls the water", wq.Worst > 60 && layer[worst] > 60 && wq.UpkeepPct == 25,
            $"{wq.Text} Worst {wq.Worst} at {worst % c.Width},{worst / c.Width}; plants' upkeep {wq.UpkeepPct}%");
        _camera.CentreOn(worst % c.Width + 0.5f, worst / c.Width + 0.5f);
        v.Zoom = 3;
        yield return MountainMeasure("classic, water quality");
        _panels.Model.Click("overlay:0");
        _camera.ToggleFree();
        // From over the water, steeply, so the murk below the outfall shows past the trees on the bank (Beta 4.1, part 4).
        Look(worst % c.Width + 0.5f, worst / c.Width + 0.5f, 20, 45, 20);
        yield return MountainMeasure("free, the foul river");
        _camera.ToggleFree();
        _client.Connection.Act(new Protocol.V0.Action { Treatment = new Protocol.V0.SetTreatment { Level = 2 } });

        // Direct dispatch (Beta 4, part 1): stations by the town's streets, leased to the service, which the player
        // runs. Every unit is picked with the box and sent across town by a right-click; they drive there by road.
        var em = _panels.Model.Emergency;
        em.Toast += (t, _) => GD.Print($"HILLS toast: {t}");
        _client.Message += m => { if (m.Error != null) GD.Print($"HILLS refused: {m.Error.Message}"); if (m.Delta != null) foreach (var n in m.Delta.Notices) GD.Print($"HILLS notice: {n.Text}"); };
        var placedStations = new List<int>();
        foreach (var kind in new[] { "fire-station", "police-station", "clinic" })
        {
            var t = c.TypeById[kind];
            foreach (var (px, py) in StationSpots(c, t.Width, t.Height, tx, ty, placedStations).Take(10))
            {
                _client.Connection.Act(new Protocol.V0.Action { Building = new Protocol.V0.PlaceBuilding { X = px, Y = py, TypeId = kind } });
                int at = c.Idx(px, py);
                int frames = 0;
                yield return new GateWait($"a {kind} placed", () => c.BuildingAt[at] != 0 || ++frames > 120, 30);
                if (c.BuildingAt[at] != 0) { placedStations.Add(at); break; }
            }
        }
        if (!em.Mine) _panels.Model.Click("em-claim");
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 2 } });
        yield return new GateWait("the service run by the player, with stations to lease", () => em.Mine && em.View!.OpenStations.Count >= placedStations.Count, 120);
        // Focus: the computer picks who goes to the town's own calls and settles them, so no scene comes up over the map.
        _panels.Model.Click("em-style:2");
        foreach (var o in em.View!.OpenStations) _client.Connection.Act(new Protocol.V0.Action { Lease = new Protocol.V0.LeaseLot { Game = "emergency", BuildingId = o.BuildingId } });
        yield return new GateWait("units at the stations", () => em.View!.Units.Count(u => u.State == "ready") >= 3, 120);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        em.SetActive(true);
        var ready = em.View!.Units.Where(u => u.State == "ready").ToList();
        MountainCheck("units at their stations", ready.All(u => u.Cell == u.Home && u.Home >= 0),
            string.Join(", ", ready.Select(u => $"{u.Name} at {u.Home % c.Width},{u.Home / c.Width}")));
        em.Map.Box(ready.Select(u => u.Id), false);
        // The street farthest across town from the first station, among the town's own (the hill works may not join
        // up with them).
        int home0 = ready[0].Home, w = c.Width;
        int bx0 = built.Min(b => b.X) - 1, by0 = built.Min(b => b.Y) - 1, bx1 = built.Max(b => b.X + b.Width), by1 = built.Max(b => b.Y + b.Height);
        int target = Enumerable.Range(0, c.Paths.Length).Where(i => c.ProfileAt(i) is { Frontage: true, Cars: true } && !c.IsTunnel(i)
                && i % w >= bx0 && i % w <= bx1 && i / w >= by0 && i / w <= by1)
            .OrderByDescending(i => Math.Abs(i % w - home0 % w) + Math.Abs(i / w - home0 / w)).ThenBy(i => i).First();
        em.Map.RightClick(target % w, target / w, null, false);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 1 } });
        yield return new GateWait("the units driving", () => em.View!.Units.Any(u => u.Order == "posted" && u.Leg.Count > 1 && u.LegStart >= 0), 60);
        var driving = em.View!.Units.Where(u => u.Order == "posted" && u.Leg.Count > 1).ToList();
        MountainCheck("units drive by road", driving.Count > 0 && driving.All(u => u.Leg.All(cell => c.Paths[cell] != 0)),
            $"{driving.Count} of {ready.Count} on the road: " + string.Join(", ", driving.Select(u => $"{u.Name} {u.Leg.Count} cells in {u.LegMinutes} min")));
        _camera.CentreOn((home0 % w + target % w) / 2f + 0.5f, (home0 / w + target / w) / 2f + 0.5f);
        v.Zoom = 3;
        yield return MountainMeasure("classic, units driving out");
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 3 } });
        yield return new GateWait("the units posted across town", () => em.View!.Units.Where(u => u.Order == "posted").All(u => u.Leg.Count == 0), 120);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        // Auto-dispatch stays on, so a posted unit may have been sent on to one of the town's calls from there.
        var posted = em.View!.Units.Where(u => u.Order == "posted").ToList();
        int called = em.View!.Units.Count(u => ready.Any(r => r.Id == u.Id) && u.State is "on call" or "at scene");
        MountainCheck("units posted where they were sent", posted.Count > 0 && posted.Count + called == ready.Count && posted.All(u => Math.Abs(u.Cell % w - target % w) + Math.Abs(u.Cell / w - target / w) <= 4),
            $"{posted.Count} posted near {target % w},{target / w} and {called} sent on to calls: " + string.Join(", ", em.View!.Units.Where(u => ready.Any(r => r.Id == u.Id)).Select(u => $"{u.Name} {em.Map.Doing(u)}")));
        // The posted units from the free camera, waiting on the street where they were sent.
        _camera.ToggleFree();
        Look(target % w + 0.5f, target / w + 0.5f, 7, 32, 210);
        yield return MountainMeasure("free, units posted on a street");
        _camera.ToggleFree();
        em.Map.Return();
        em.SetActive(false);

        // The UI overhaul (Beta 4, part 2, variation A): the build rail's flyouts, a tile picked from one, and the
        // panel floating over the map with the L along the bottom left.
        var tools = _tools!;
        var pv = _panels!;
        int uiFrames = 0;
        yield return new GateWait("the mayor's view back", () => ++uiFrames > 10 && !em.Active, 30);
        _camera.CentreOn(tx, ty);
        v.Zoom = 3;
        var cats = tools.Menu;
        var everything = cats.SelectMany(m => m.Groups).SelectMany(g => g.Items).ToList();
        MountainCheck("build menu", cats.Count == 9 && c.Types.Where(t => t.Placeable).All(t => everything.Count(i => i.Type == t.Id) == 1) && c.Profiles.All(p => everything.Count(i => i.Profile == p.Id) == 1),
            $"{cats.Count} categories on the rail, {everything.Count} things to build: " + string.Join(", ", cats.Select(m => $"{m.Name} {m.Groups.Sum(g => g.Items.Count)}")));
        tools.OpenMenu("transport", 1);
        uiFrames = 0;
        yield return new GateWait("the transport flyout", () => ++uiFrames > 5, 10);
        int roads = cats.Single(m => m.Id == "transport").Groups[1].Items.Count;
        MountainCheck("flyout open", tools.FlyoutShown == "transport" && tools.TilesShown == roads && !tools.OptionsShown, $"Transport, Roads: {tools.TilesShown} tiles of {roads}");
        yield return MountainMeasure("classic, the roads flyout");
        bool picked = tools.PickTile(i => i.Profile == "road");
        uiFrames = 0;
        yield return new GateWait("the road picked", () => ++uiFrames > 5, 10);
        MountainCheck("a tile picks the tool", picked && tools.State.Kind == ClientModel.Tools.ToolKind.Path && tools.State.ProfileId == "road" && tools.FlyoutShown == null && tools.OptionsShown,
            $"tool {tools.State.Kind} {tools.State.ProfileId}; flyout {(tools.FlyoutShown == null ? "closed" : "open")}; options {(tools.OptionsShown ? "shown" : "hidden")}");
        tools.OpenMenu("civic", 0, pin: true);
        pv.Model.Open("budget");
        uiFrames = 0;
        yield return new GateWait("the civic flyout and the budget", () => ++uiFrames > 5, 10);
        var screen = GetViewport().GetVisibleRect().Size;
        var side = pv.SideRect;
        var bar = pv.BarRect;
        var up = pv.UprightRect;
        MountainCheck("panel floats over the map", side is { } sr && sr.End.X <= screen.X - 8 && sr.Position.Y >= 40 && sr.End.Y <= bar.Position.Y && bar.End.Y <= screen.Y - 8 && up.End.Y <= bar.Position.Y && up.Position.X < 20 && pv.ModesRect.End.X <= screen.X - 8,
            $"panel {side?.Position.X:0},{side?.Position.Y:0} to {side?.End.X:0},{side?.End.Y:0}; bar {bar.Position.Y:0} to {bar.End.Y:0}; upright {up.Position.X:0},{up.Position.Y:0}; screen {screen.X:0}×{screen.Y:0}");
        yield return MountainMeasure("classic, the civic flyout and the budget");
        tools.CloseMenu();
        pv.ClosePanel();
        tools.SetTool(ClientModel.Tools.ToolKind.Query);
    }

    /// <summary>Spots for a station by the streets, near a point and away from the stations already placed.</summary>
    static IEnumerable<(int X, int Y)> StationSpots(ClientModel.CityMirror c, int bw, int bh, float nx, float ny, List<int> placed)
    {
        var list = new List<(int X, int Y, float D)>();
        for (int y = 2; y < c.Height - bh - 2; y++)
            for (int x = 2; x < c.Width - bw - 2; x++)
            {
                bool ok = true, road = false;
                for (int yy = y; yy < y + bh && ok; yy++)
                    for (int xx = x; xx < x + bw && ok; xx++) { int i = c.Idx(xx, yy); ok = !c.IsWater(i) && c.BuildingAt[i] == 0 && c.Paths[i] == 0; }
                if (!ok) continue;
                for (int yy = y - 1; yy <= y + bh && !road; yy++)
                    for (int xx = x - 1; xx <= x + bw && !road; xx++)
                        if ((xx < x || xx >= x + bw || yy < y || yy >= y + bh) && (xx == x - 1 || xx == x + bw) != (yy == y - 1 || yy == y + bh) && c.ProfileAt(c.Idx(xx, yy)) is { Frontage: true }) road = true;
                if (!road) continue;
                float near = Math.Abs(x - nx) + Math.Abs(y - ny), apart = placed.Count == 0 ? 0 : placed.Min(p => Math.Abs(p % c.Width - x) + Math.Abs(p / c.Width - y));
                list.Add((x, y, near - Math.Min(apart, 20)));
            }
        return list.OrderBy(s => s.D).ThenBy(s => s.Y).ThenBy(s => s.X).Select(s => (s.X, s.Y));
    }
}
