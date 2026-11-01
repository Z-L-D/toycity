using System.Text;
using Godot;
using ToyCity.ClientModel.Gate;

namespace ToyCity.Godot;

/// <summary>
/// The Beta 3.1 gate's mountain run (--mountains FILE): founds a 256-cell city with a starter town on a seed with high
/// mountains and rivers, then looks at it from the classic camera and the free camera (the peaks from afar, a
/// mountainside low down, the river valley, the town from above the hills, a street close up), measuring each
/// view's frame times and taking its picture. The lot is written to FILE as JSON and printed as a table.
/// </summary>
public partial class Main
{
    /// <summary>The seed: of the first forty, the one with the most ground over 240 m.</summary>
    public const ulong MountainSeed = 40;

    string? _mountainFile;
    IEnumerator<GateWait>? _mountainSteps;
    double _mountainWaited, _mountainClock;
    string? _mountainView;
    readonly Dictionary<string, List<double>> _mountainTimes = new();
    readonly List<string> _mountainOrder = new();
    readonly Dictionary<string, (long Tris, long Calls)> _mountainCost = new();
    readonly List<(string Check, bool Ok, string Seen)> _mountainChecks = new();
    int _mountainShot;

    bool Scripted => _bench != null || _gateFile != null || _togetherFile != null || _mountainFile != null;

    void Mountains(double delta)
    {
        _mountainSteps ??= MountainSteps().GetEnumerator();
        _mountainClock += delta;
        if (_mountainView != null) MountainTimes(_mountainView).Add(delta * 1000);
        try
        {
            var wait = _mountainSteps.Current;
            if (wait != null && !wait.Done())
            {
                if ((_mountainWaited += delta) > wait.Seconds) throw new GateFailed($"waited {wait.Seconds:F0} s for {wait.What}");
                return;
            }
            _mountainWaited = 0;
            if (!_mountainSteps.MoveNext()) MountainEnd(null);
        }
        catch (Exception e) { MountainEnd(e); }
    }

    IEnumerable<GateWait> MountainSteps()
    {
        var c = _client!.City;
        yield return new GateWait("the first city", () => c.Loaded, 60);
        _client.Connection.Send(new Protocol.V0.ClientMessage { NewCity = new Protocol.V0.NewCityRequest { Name = "Mountain City", Seed = MountainSeed, Size = 256, StarterTown = true } });
        yield return new GateWait("the mountain city", () => c.Loaded && c.Width == 256 && c.Stats.CityName == "Mountain City" && c.Buildings.Count > 0, 180);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        yield return new GateWait("the mountain city drawn", () => _map!.Drawn && _centred && _camera!.MapWidth == 256, 120);

        // What there is to look at.
        int peak = 0, rivers = 0;
        for (int i = 0; i < c.Width * c.Height; i++)
        {
            if (c.Heights[i] > c.Heights[peak]) peak = i;
            if (c.IsRiver(i)) rivers++;
        }
        var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
        float tx = built.Average(b => b.X + b.Width / 2f), ty = built.Average(b => b.Y + b.Height / 2f);
        int river = Enumerable.Range(0, c.Width * c.Height).Where(c.IsRiver)
            .OrderBy(i => Math.Abs(i % c.Width - tx) + Math.Abs(i / c.Width - ty)).FirstOrDefault(-1);
        int low = Enumerable.Range(0, c.Width * c.Height).Where(i => !c.IsWater(i)).Min(i => c.Heights[i]);
        int relief = (c.Heights[peak] - low) * 2;
        MountainCheck("mountains", relief >= 300, $"{relief} m from the lowest dry ground to the peak at {peak % c.Width},{peak / c.Width}");
        MountainCheck("rivers", rivers >= 200, $"{rivers:N0} river cells; the nearest the town at {(river < 0 ? "none" : $"{river % c.Width},{river / c.Width}")}");
        int streets = Enumerable.Range(0, c.Width * c.Height).Count(i => c.Paths[i] != 0);
        MountainCheck("starter town", built.Count >= 3 && streets >= 100, $"{built.Count} buildings and {streets} road cells around {tx:F0},{ty:F0}");
        int corners = c.Corners.Length;
        MountainCheck("ground from corners", corners == (c.Width + 1) * (c.Height + 1), $"{corners:N0} corners");

        var v = _camera!.View;
        _camera.CentreOn(c.Width / 2f, c.Height / 2f);
        v.Zoom = 0;
        yield return MountainMeasure("classic, the whole map");
        _camera.CentreOn(tx, ty);
        v.Zoom = 3;
        yield return MountainMeasure("classic, the town");

        _camera.ToggleFree();
        var f = _free!;
        f.Snap = false;
        void Look(float x, float y, float distance, float pitch, float yaw)
        {
            f.Target = new Vector3(x, _view!.Surface(x, y), y);
            (f.Distance, f.Pitch, f.Yaw) = (distance, pitch, yaw);
        }
        float px = peak % c.Width + 0.5f, py = peak / c.Width + 0.5f;
        Look(px, py, 170, 28, 30);
        yield return MountainMeasure("free, the peaks from afar");
        Look(px, py, 55, 12, 210);
        yield return MountainMeasure("free, a mountainside low down");
        if (river >= 0)
        {
            Look(river % c.Width + 0.5f, river / c.Width + 0.5f, 45, 22, 120);
            yield return MountainMeasure("free, the river valley");
        }
        Look(tx, ty, 95, 22, 300);
        yield return MountainMeasure("free, the town from above the hills");
        Look(tx, ty, 16, 38, 45);
        yield return MountainMeasure("free, a street close up");
        _camera.ToggleFree();
    }

    GateWait MountainMeasure(string name)
    {
        int settle = 30;
        double clock = 0;
        _mountainView = null;
        return new GateWait(name, () =>
        {
            if (settle-- > 0) return false;
            if (_mountainView == null) { _mountainView = name; return false; }
            clock += GetProcessDeltaTime();
            if (clock < 5 && MountainTimes(name).Count < 600) return false;
            _mountainCost[name] = ((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
                (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
            if (_shots != null && DisplayServer.GetName() != "headless")
            {
                DirAccess.MakeDirRecursiveAbsolute(_shots);
                string file = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
                GetViewport().GetTexture().GetImage().SavePng($"{_shots}/mountain-{_mountainShot++:00}-{file}.png");
            }
            _mountainView = null;
            return true;
        }, 90);
    }

    List<double> MountainTimes(string name)
    {
        if (!_mountainTimes.TryGetValue(name, out var list))
        {
            _mountainTimes[name] = list = new List<double>();
            _mountainOrder.Add(name);
        }
        return list;
    }

    void MountainCheck(string check, bool ok, string seen)
    {
        _mountainChecks.Add((check, ok, seen));
        if (!ok) throw new GateFailed($"{check}: {seen}");
    }

    void MountainEnd(Exception? failure)
    {
        _mountainSteps = null;
        var json = new StringBuilder();
        var table = new StringBuilder("| View | Frames | Avg ms | p95 ms | Worst ms | fps | Triangles | Draw calls |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");
        json.Append($"{{\"passed\":{(failure == null ? "true" : "false")},\"failure\":{Quote(failure?.Message ?? "")},\"seed\":{MountainSeed},");
        json.Append($"\"renderer\":{Quote($"{RenderingServer.GetVideoAdapterName()} / {RenderingServer.GetCurrentRenderingDriverName()}")},\"graphics\":\"{_graphics.PresetName}\",\"seconds\":{_mountainClock:F0},\"views\":[");
        bool first = true;
        foreach (var name in _mountainOrder)
        {
            var t = _mountainTimes[name].OrderBy(x => x).ToList();
            if (t.Count == 0) continue;
            double avg = t.Average(), p95 = t[(int)(t.Count * 0.95)], worst = t[^1];
            var cost = _mountainCost.GetValueOrDefault(name);
            json.Append(first ? "" : ",").Append($"{{\"name\":{Quote(name)},\"frames\":{t.Count},\"avgMs\":{avg:F2},\"p95Ms\":{p95:F2},\"worstMs\":{worst:F2},\"fps\":{1000 / avg:F1},\"triangles\":{cost.Tris},\"drawCalls\":{cost.Calls}}}");
            table.Append($"| {name} | {t.Count} | {avg:F1} | {p95:F1} | {worst:F1} | {1000 / avg:F1} | {cost.Tris:N0} | {cost.Calls:N0} |\n");
            first = false;
        }
        json.Append("],\"checks\":[");
        json.Append(string.Join(",", _mountainChecks.Select(k => $"{{\"check\":{Quote(k.Check)},\"ok\":{(k.Ok ? "true" : "false")},\"seen\":{Quote(k.Seen)}}}")));
        json.Append("]}");
        using (var file = global::Godot.FileAccess.Open(_mountainFile, global::Godot.FileAccess.ModeFlags.Write)) file.StoreString(json.ToString());
        foreach (var (check, ok, seen) in _mountainChecks) GD.Print($"MOUNTAIN {(ok ? "ok" : "FAILED")}  {check}: {seen}");
        foreach (var line in table.ToString().TrimEnd().Split('\n')) GD.Print($"MOUNTAIN {line}");
        if (failure != null) GD.PrintErr($"MOUNTAIN FAILED: {failure.Message}");
        else GD.Print($"MOUNTAIN OK, wrote {_mountainFile}");
        GetTree().Quit(failure == null ? 0 : 1);
    }
}
