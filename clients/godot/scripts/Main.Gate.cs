using System.Text;
using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Gate;

namespace ToyCity.Godot;

/// <summary>
/// The Beta 1 gate run (--gate FILE): founds the benchmark city from the New city dialog, measures the classic view
/// at every zoom level and the free camera near and far, then plays the Alpha 3 loop through the tools and panels
/// (<see cref="GateRun"/>), watching from the classic view, the company's view and a bus's ride-along camera. Every
/// frame's time is filed under what was on screen, with memory, and the lot is written to FILE as JSON and printed
/// as a table against the frame budget.
/// </summary>
public partial class Main
{
    string? _gateFile;
    GateRun? _gate;
    IEnumerator<GateWait>? _gateSteps;
    double _gateWaited, _gateClock;
    string _gatePhase = "";
    readonly Dictionary<string, List<double>> _gateTimes = new();
    readonly List<string> _gateOrder = new();
    readonly Dictionary<string, (long Tris, long Calls, double ProcessMB, double VideoMB)> _gateCost = new();
    double _gatePeakMB;
    int _gateShot;
    string? _measuring;  // a camera measurement under way: its frames go under this name, not the loop's phase

    void GateStart()
    {
        _gate = new GateRun(_client!, _panels!.Model, _tools!.Use);
        _gateSteps = GateSteps().GetEnumerator();
        if (!_gateSteps.MoveNext()) GateEnd(null);
    }

    void Gate(double delta)
    {
        if (_gateSteps == null) return;
        _gateClock += delta;
        Record(delta);
        var wait = _gateSteps.Current;
        try
        {
            if (!wait.Done())
            {
                if ((_gateWaited += delta) > wait.Seconds) throw new GateFailed($"waited {wait.Seconds:F0} s for {wait.What}");
                return;
            }
            _gateWaited = 0;
            if (!_gateSteps.MoveNext()) GateEnd(null);
        }
        catch (Exception e) { GateEnd(e); }
    }

    /// <summary>The run: the city, the cameras, then the loop with the camera following along.</summary>
    IEnumerable<GateWait> GateSteps()
    {
        var c = _client!.City;
        foreach (var w in _gate!.Found()) yield return w;
        yield return new GateWait("the benchmark city drawn", () => _map!.Drawn && _centred && _camera!.MapWidth == c.Width, 120);
        var v = _camera!.View;
        _camera.CentreOn(c.Width / 2f, c.Height / 2f);
        for (int z = 0; z < ClassicView.ZoomSizes.Length; z++)
        {
            v.Zoom = z;
            yield return Measure($"classic, zoom {z}", 5);
        }
        _camera.ToggleFree();
        foreach (float d in new[] { 160f, 60f, 18f })
        {
            _free!.Distance = d;
            yield return Measure($"free, {d:0} cells off", 5);
        }
        _camera.ToggleFree();

        // The loop, watched as a player would: the street being built on, the company's map, the line's buses.
        string seen = "";
        foreach (var w in _gate.Play())
        {
            if (_gate.Phase != seen)
            {
                seen = _gate.Phase;
                Watch(seen);
            }
            yield return w;
        }
        // The away report, left open, from the classic view.
        if (_camera.IsFree) _camera.ToggleFree();
        int frames = 0;
        yield return new GateWait("the report drawn", () => ++frames > 5, 30);
        Shot("report");
    }

    /// <summary>Where the camera goes for each part of the loop.</summary>
    void Watch(string phase)
    {
        var (fx, fy) = _gate!.Focus;
        switch (phase)
        {
            case "building stops":
                _camera!.CentreOn(fx, fy);
                _camera.View.Zoom = 3;
                break;
            case "stasis":
                Shot("company");
                _camera!.View.Zoom = 2;
                _panels!.Model.Click("routes");
                break;
            case "caretaker":
                Shot("stasis");
                break;
            case "autopilot":
                Shot("caretaker");
                // Ride along with one of the line's buses.
                _camera!.ToggleFree();
                _free!.Target = new Vector3(fx, 0, fy);
                _free.Distance = 30;
                for (int k = 0; k < 6 && !_free.RideName.EndsWith(" bus"); k++) _free.NextRide();
                break;
            case "taking back":
                Shot("autopilot ride-along");
                break;
        }
    }

    /// <summary>Measures the camera as it stands: 20 frames to settle, then the given seconds.</summary>
    GateWait Measure(string name, double seconds)
    {
        int settle = 20;
        double clock = 0;
        _measuring = null;
        return new GateWait(name, () =>
        {
            if (settle-- > 0) return false;
            if (_measuring == null) { _measuring = name; return false; }
            clock += GetProcessDeltaTime();
            if (clock < seconds && Times(name).Count < 600) return false;
            Cost(name);
            Shot(name);
            _measuring = null;
            return true;
        }, seconds + 60);
    }

    List<double> Times(string name)
    {
        if (!_gateTimes.TryGetValue(name, out var list))
        {
            _gateTimes[name] = list = new List<double>();
            _gateOrder.Add(name);
        }
        return list;
    }

    void Record(double delta)
    {
        if (_gate == null) return;
        string name = _measuring ?? (_gate.Phase is "starting" or "founding" ? "" : $"loop: {_gate.Phase}");
        if (name != _gatePhase && _gatePhase.StartsWith("loop:")) Cost(_gatePhase);
        _gatePhase = name;
        if (name == "") return;
        Times(name).Add(delta * 1000);
        if (Engine.GetProcessFrames() % 30 == 0) _gatePeakMB = Math.Max(_gatePeakMB, System.Environment.WorkingSet / 1048576.0);
    }

    void Cost(string name)
    {
        double process = System.Environment.WorkingSet / 1048576.0;
        _gatePeakMB = Math.Max(_gatePeakMB, process);
        _gateCost[name] = ((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
            (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            process, Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0);
    }

    void Shot(string name)
    {
        if (_shots == null || DisplayServer.GetName() == "headless") return;
        DirAccess.MakeDirRecursiveAbsolute(_shots);
        string file = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        GetViewport().GetTexture().GetImage().SavePng($"{_shots}/gate-{_gateShot++:00}-{file}.png");
    }

    /// <summary>Writes the report and quits: 0 when every check passed.</summary>
    void GateEnd(Exception? failure)
    {
        _gateSteps = null;
        if (_gatePhase.StartsWith("loop:")) Cost(_gatePhase);
        var c = _client!.City;
        var json = new StringBuilder();
        var table = new StringBuilder("| What was on screen | Frames | Avg ms | p95 ms | Worst ms | fps | Triangles | Draw calls | Process MB |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        json.Append($"{{\"passed\":{(failure == null ? "true" : "false")},\"failure\":{Quote(failure?.Message ?? "")},\"city\":\"{c.Width}x{c.Height}\",\"buildings\":{c.Buildings.Count},");
        json.Append($"\"renderer\":{Quote($"{RenderingServer.GetVideoAdapterName()} / {RenderingServer.GetCurrentRenderingDriverName()}")},\"seconds\":{_gateClock:F0},\"peakProcessMB\":{_gatePeakMB:F0},\"phases\":[");
        bool first = true;
        foreach (var name in _gateOrder)
        {
            var t = _gateTimes[name].OrderBy(x => x).ToList();
            if (t.Count == 0) continue;
            double avg = t.Average(), p95 = t[(int)(t.Count * 0.95)], worst = t[^1];
            var cost = _gateCost.GetValueOrDefault(name);
            json.Append(first ? "" : ",").Append($"{{\"name\":{Quote(name)},\"frames\":{t.Count},\"avgMs\":{avg:F2},\"p95Ms\":{p95:F2},\"worstMs\":{worst:F2},\"fps\":{1000 / avg:F1},"
                + $"\"triangles\":{cost.Tris},\"drawCalls\":{cost.Calls},\"processMB\":{cost.ProcessMB:F0},\"videoMB\":{cost.VideoMB:F0}}}");
            table.Append($"| {name} | {t.Count} | {avg:F1} | {p95:F1} | {worst:F1} | {1000 / avg:F1} | {cost.Tris:N0} | {cost.Calls:N0} | {cost.ProcessMB:F0} |\n");
            first = false;
        }
        json.Append("],\"checks\":[");
        json.Append(string.Join(",", _gate!.Checks.Select(k => $"{{\"check\":{Quote(k.Check)},\"ok\":{(k.Ok ? "true" : "false")},\"seen\":{Quote(k.Seen)}}}")));
        json.Append("]}");
        using (var f = global::Godot.FileAccess.Open(_gateFile, global::Godot.FileAccess.ModeFlags.Write)) f.StoreString(json.ToString());
        foreach (var (check, ok, seen) in _gate.Checks) GD.Print($"GATE {(ok ? "ok" : "FAILED")}  {check}: {seen}");
        foreach (var line in table.ToString().TrimEnd().Split('\n')) GD.Print($"GATE {line}");
        GD.Print($"GATE peak process memory {_gatePeakMB:F0} MB over {_gateClock:F0} s");
        if (failure != null) GD.PrintErr($"GATE FAILED: {failure.Message}");
        else GD.Print($"GATE OK, wrote {_gateFile}");
        GetTree().Quit(failure == null ? 0 : 1);
    }

    static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
