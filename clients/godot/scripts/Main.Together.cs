using System.Text;
using Godot;
using ToyCity.ClientModel.Gate;

namespace ToyCity.Godot;

/// <summary>
/// The Beta 2 gate run (--together FILE, signed in to a dedicated server as its first account): this window is
/// Mayor May, and Tess, Dan and Sam play from the same process through their own client models
/// (<see cref="TogetherRun"/>). The camera and the mayor's dialogs follow the session, with a picture at each moment
/// worth one; the checks, frame times and memory go to FILE as JSON.
/// </summary>
public partial class Main
{
    string? _togetherFile;
    TogetherRun? _together;
    IEnumerator<GateWait>? _togetherSteps;
    double _togetherWaited, _togetherClock, _togetherPeakMB;
    readonly List<double> _togetherFrames = new();

    void TogetherStart()
    {
        _together = new TogetherRun(_client!.Connection.Uri, _user ?? "Mayor May", _client, _panels!.Model, _tools!.Use)
        {
            Moment = TogetherMoment,
        };
        _togetherSteps = _together.Play().GetEnumerator();
        if (!_togetherSteps.MoveNext()) TogetherEnd(null);
    }

    void Together(double delta)
    {
        if (_togetherSteps == null) return;
        _togetherClock += delta;
        _togetherFrames.Add(delta * 1000);
        if (Engine.GetProcessFrames() % 30 == 0) _togetherPeakMB = Math.Max(_togetherPeakMB, System.Environment.WorkingSet / 1048576.0);
        try
        {
            _together!.Poll(delta);
            var wait = _togetherSteps.Current;
            if (!wait.Done())
            {
                if ((_togetherWaited += delta) > wait.Seconds) throw new GateFailed($"waited {wait.Seconds:F0} s for {wait.What}");
                return;
            }
            _togetherWaited = 0;
            if (!_togetherSteps.MoveNext()) TogetherEnd(null);
        }
        catch (Exception e) { TogetherEnd(e); }
    }

    /// <summary>Sets the camera for a moment of the session and takes its picture once it has been drawn.</summary>
    GateWait TogetherMoment(string moment)
    {
        var (fx, fy) = _together!.Focus;
        var pm = _panels!.Model;
        if (_camera!.IsFree && moment != "free camera") _camera.ToggleFree();
        switch (moment)
        {
            case "four players":
                _camera.View.Zoom = 2;
                break;
            case "stops and stations":
                _camera.CentreOn(fx, fy + 4);
                _camera.View.Zoom = 3;
                break;
            case "the line":
                _camera.CentreOn(fx, fy + 4);
                _camera.View.Zoom = 2;
                break;
            case "free camera":
                pm.CloseDialog();
                _camera.ToggleFree();
                _free!.Target = new Vector3(fx, 0, fy + 4);
                _free.Distance = 28;
                break;
        }
        int frames = 0;
        return new GateWait($"the picture of {moment}", () =>
        {
            if (++frames < 20) return false;
            Shot(moment);
            if (moment == "free camera") _camera.ToggleFree();
            return true;
        }, 60);
    }

    /// <summary>Writes the report and quits: 0 when every check passed.</summary>
    void TogetherEnd(Exception? failure)
    {
        _togetherSteps = null;
        var t = _togetherFrames.OrderBy(x => x).ToList();
        double avg = t.Count > 0 ? t.Average() : 0, p95 = t.Count > 0 ? t[(int)(t.Count * 0.95)] : 0;
        var c = _client!.City;
        var json = new StringBuilder();
        json.Append($"{{\"passed\":{(failure == null ? "true" : "false")},\"failure\":{Quote(failure?.Message ?? "")},\"city\":\"{c.Width}x{c.Height}\",\"buildings\":{c.Buildings.Count},");
        json.Append($"\"renderer\":{Quote($"{RenderingServer.GetVideoAdapterName()} / {RenderingServer.GetCurrentRenderingDriverName()}")},\"graphics\":\"{_graphics.PresetName}\",");
        json.Append($"\"seconds\":{_togetherClock:F0},\"frames\":{t.Count},\"avgMs\":{avg:F2},\"p95Ms\":{p95:F2},\"peakProcessMB\":{_togetherPeakMB:F0},\"checks\":[");
        json.Append(string.Join(",", _together!.Checks.Select(k => $"{{\"check\":{Quote(k.Check)},\"ok\":{(k.Ok ? "true" : "false")},\"seen\":{Quote(k.Seen)}}}")));
        json.Append("]}");
        using (var f = global::Godot.FileAccess.Open(_togetherFile, global::Godot.FileAccess.ModeFlags.Write)) f.StoreString(json.ToString());
        foreach (var (check, ok, seen) in _together.Checks) GD.Print($"TOGETHER {(ok ? "ok" : "FAILED")}  {check}: {seen}");
        GD.Print($"TOGETHER {t.Count} frames over {_togetherClock:F0} s, avg {avg:F1} ms, p95 {p95:F1} ms, peak process memory {_togetherPeakMB:F0} MB");
        _together.Dispose();
        if (failure != null) GD.PrintErr($"TOGETHER FAILED in {_together.Phase}: {failure.Message}");
        else GD.Print($"TOGETHER OK, wrote {_togetherFile}");
        GetTree().Quit(failure == null ? 0 : 1);
    }
}
