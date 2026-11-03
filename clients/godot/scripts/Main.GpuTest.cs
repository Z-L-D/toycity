using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Gate;
using ToyCity.ClientModel.Graphics;

namespace ToyCity.Godot;

/// <summary>
/// The GPU test (Beta 4, Part 5), --gpu-test DIR: founds the hill town, lets it grow a little, then holds three camera
/// views under each graphics preset, under High at each lighting level, and under High at night, measuring every
/// frame, the GPU's and the CPU's time and the draw calls. It writes a Markdown and a JSON report into DIR and shows
/// the summary on screen. --gpu-hold SECONDS sets how long each view is held (4 by default); --gpu-quit closes the
/// game when it's done instead of showing the summary; --shots DIR saves a picture of the summary, then closes. It
/// founds a city, so the launchers (tools/gpu-test) run it against a server of its own with an empty save folder.
/// </summary>
public partial class Main
{
    string? _gpuDir;
    double _gpuHold = 4;
    bool _gpuQuit, _gpuDone;
    IEnumerator<GateWait>? _gpuSteps;
    double _gpuWaited;
    readonly List<GpuSample> _gpuSamples = new();
    Label? _gpuProgress;
    // The view being measured now.
    List<double>? _gpuFrames;
    double _gpuGpuMs, _gpuCpuMs;
    long _gpuTris, _gpuCalls;

    static readonly string[] LightingNames = { "sun only", "contact shadows", "bounce light", "full" };

    /// <summary>The setups measured: each preset as it is, then High with each other lighting level, then High at night.</summary>
    static List<(string Name, GraphicsSettings Settings)> GpuSetups()
    {
        int noon = Daylight.Pin("Noon");
        var setups = GraphicsSettings.Presets.Select(p => (p.Name, p.Settings with { TimeOfDay = noon })).ToList();
        foreach (var q in Enum.GetValues<LightQuality>())
            if (q != GraphicsSettings.High.Lighting)
                setups.Add(($"High, {LightingNames[(int)q]}", GraphicsSettings.High with { Lighting = q, TimeOfDay = noon }));
        setups.Add(("High at night", GraphicsSettings.High with { TimeOfDay = Daylight.Pin("Night") }));
        return setups;
    }

    void GpuTest(double delta)
    {
        if (_gpuSteps == null)
        {
            _gpuSteps = GpuSteps().GetEnumerator();
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            var layer = new CanvasLayer { Layer = 50 };
            AddChild(layer);
            _gpuProgress = new Label { Position = new Vector2(ToolController.ColumnWidth + 12, 132) };
            _gpuProgress.AddThemeColorOverride("font_outline_color", Colors.Black);
            _gpuProgress.AddThemeConstantOverride("outline_size", 5);
            _gpuProgress.AddThemeFontSizeOverride("font_size", 20);
            layer.AddChild(_gpuProgress);
        }
        if (_gpuFrames != null)
        {
            var rid = GetViewport().GetViewportRid();
            _gpuFrames.Add(delta * 1000);
            _gpuGpuMs += RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
            _gpuCpuMs += RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid) + RenderingServer.GetFrameSetupTimeCpu();
            _gpuTris += (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
            _gpuCalls += (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        }
        try
        {
            var wait = _gpuSteps.Current;
            if (wait != null && !wait.Done())
            {
                if ((_gpuWaited += delta) > wait.Seconds) throw new GateFailed($"waited {wait.Seconds:F0} s for {wait.What}");
                return;
            }
            _gpuWaited = 0;
            if (!_gpuSteps.MoveNext()) GpuEnd(null);
        }
        catch (Exception e) { GpuEnd(e); }
    }

    IEnumerable<GateWait> GpuSteps()
    {
        var c = _client!.City;
        void Say(string text) { _gpuProgress!.Text = $"GPU test: {text}"; GD.Print($"GPU {text}"); }
        Say("founding the hill town…");
        yield return new GateWait("the first city", () => c.Loaded, 60);
        _client.Connection.Send(new Protocol.V0.ClientMessage { NewCity = new Protocol.V0.NewCityRequest { Name = "Hill Town", Seed = HillSeed, Size = 128, StarterTown = true } });
        yield return new GateWait("the hill town", () => c.Loaded && c.Stats.CityName == "Hill Town" && c.Buildings.Count > 0, 180);
        // A town with people in it, so the night has lit windows and the streets have traffic.
        Say("letting the town grow for a minute…");
        _client.Connection.Send(new Protocol.V0.ClientMessage { Pace = new Protocol.V0.SetPace { DaysPerClockDay = 720 } });
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 3 } });
        double grown = 0;
        yield return new GateWait("the town grown", () => c.Stats.Population >= 300 || (grown += GetProcessDeltaTime()) > 60, 90);
        _client.Connection.Send(new Protocol.V0.ClientMessage { Speed = new Protocol.V0.SetSpeed { Speed = 0 } });
        yield return new GateWait("the town drawn", () => _map!.Drawn && _centred, 120);

        var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
        float tx = built.Average(b => b.X + b.Width / 2f), ty = built.Average(b => b.Y + b.Height / 2f);
        var f = _free!;
        f.Snap = false;
        var views = new (string Name, Action Look)[]
        {
            ("classic, the town", () => { if (_camera!.IsFree) _camera.ToggleFree(); _camera.CentreOn(tx, ty); _camera.View.Zoom = 3; }),
            ("free, the town from the hills", () => Free(tx, ty, 60, 20, 30)),
            ("free, a street close up", () => Free(tx, ty, 18, 30, 200)),
        };
        void Free(float x, float y, float distance, float pitch, float yaw)
        {
            if (!_camera!.IsFree) _camera.ToggleFree();
            f.Target = new Vector3(x, _view!.Surface(x, y), y);
            (f.Distance, f.Pitch, f.Yaw) = (distance, pitch, yaw);
        }

        var setups = GpuSetups();
        for (int s = 0; s < setups.Count; s++)
        {
            var (name, settings) = setups[s];
            _graphics = settings;
            ApplyGraphics();
            foreach (var (view, look) in views)
            {
                look();
                Say($"{name} ({s + 1} of {setups.Count}), {view}");
                // Settle: shaders compiled and the detail swapped in before measuring.
                double settle = 0;
                yield return new GateWait("the view settled", () => (settle += GetProcessDeltaTime()) > Math.Min(1.5, _gpuHold), 30);
                (_gpuFrames, _gpuGpuMs, _gpuCpuMs, _gpuTris, _gpuCalls) = (new List<double>(), 0, 0, 0, 0);
                double held = 0;
                yield return new GateWait(view, () => (held += GetProcessDeltaTime()) > _gpuHold && _gpuFrames.Count >= 3, _gpuHold + 120);
                int n = Math.Max(1, _gpuFrames.Count);
                _gpuSamples.Add(new GpuSample(name, view, _gpuFrames, _gpuGpuMs / n, _gpuCpuMs / n, _gpuTris / n, _gpuCalls / n));
                _gpuFrames = null;
            }
        }
        if (_camera!.IsFree) _camera.ToggleFree();
        _graphics = setups[2].Settings;
        ApplyGraphics();
    }

    GpuMachine Machine()
    {
        var size = GetViewport().GetVisibleRect().Size;
        return new GpuMachine(RenderingServer.GetVideoAdapterName(), RenderingServer.GetVideoAdapterVendor(),
            $"{RenderingServer.GetCurrentRenderingDriverName()} {RenderingServer.GetVideoAdapterApiVersion()}",
            RenderingServer.GetCurrentRenderingMethod(), $"{OS.GetName()} {OS.GetVersion()}", OS.GetProcessorName(), OS.GetProcessorCount(),
            (string)Engine.GetVersionInfo()["string"], (int)size.X, (int)size.Y);
    }

    void GpuEnd(Exception? failure)
    {
        _gpuSteps = null;
        _gpuDone = true;
        if (failure != null)
        {
            GD.PrintErr($"GPU FAILED: {failure.Message}");
            if (_gpuQuit) { GetTree().Quit(1); return; }
        }
        var when = DateTime.UtcNow;
        var machine = Machine();
        string dir = _gpuDir!, stamp = when.ToString("yyyyMMdd-HHmmss");
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string md = $"{dir}/gpu-test-{stamp}.md", json = $"{dir}/gpu-test-{stamp}.json";
        var markdown = GpuReport.Markdown(machine, _gpuSamples, when);
        using (var file = global::Godot.FileAccess.Open(md, global::Godot.FileAccess.ModeFlags.Write)) file.StoreString(markdown);
        using (var file = global::Godot.FileAccess.Open(json, global::Godot.FileAccess.ModeFlags.Write)) file.StoreString(GpuReport.Json(machine, _gpuSamples, when));
        var rows = _gpuSamples.Select(GpuReport.Summarise).ToList();
        var setups = GpuReport.BySetup(rows);
        foreach (var s in setups) GD.Print($"GPU {s.Setup}: {s.Fps:F1} fps, 1% low {s.Low1Fps:F1}, GPU {s.GpuMs:F2} ms, {s.Verdict}");
        GD.Print($"GPU {(failure == null ? "OK" : "PARTIAL")}, wrote {md} and {json}");
        if (_gpuQuit && _shots == null) { GetTree().Quit(failure == null ? 0 : 1); return; }
        _gpuProgress!.Text = "";
        ShowGpuSummary(machine, setups, md, failure);
        // With --shots, a picture of the summary for the release notes, then close.
        if (_shots != null)
            GetTree().CreateTimer(1).Timeout += () =>
            {
                DirAccess.MakeDirRecursiveAbsolute(_shots);
                GetViewport().GetTexture().GetImage().SavePng($"{_shots}/gpu-test-summary.png");
                GetTree().Quit(failure == null ? 0 : 1);
            };
    }

    /// <summary>The summary over the game when the test is done, with the report's path and a button to close.</summary>
    void ShowGpuSummary(GpuMachine m, List<GpuReport.SetupRow> setups, string path, Exception? failure)
    {
        var layer = new CanvasLayer { Layer = 60 };
        AddChild(layer);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, UiTheme.Line, 10, 20, 16));
        (panel.GrowHorizontal, panel.GrowVertical) = (Control.GrowDirection.Both, Control.GrowDirection.Both);
        layer.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);
        var title = new Label { Text = failure == null ? "GPU test done" : $"GPU test stopped early: {failure.Message}" };
        title.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(title);
        box.AddChild(new Label { Text = $"{m.Adapter}, {m.Renderer} renderer, {m.Width} × {m.Height}" });
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 24);
        foreach (var h in new[] { "Setup", "fps", "1% low", "GPU ms", "" }) grid.AddChild(new Label { Text = h, Modulate = new Color(1, 1, 1, 0.7f) });
        foreach (var s in setups)
        {
            grid.AddChild(new Label { Text = s.Setup });
            grid.AddChild(new Label { Text = $"{s.Fps:F0}", HorizontalAlignment = HorizontalAlignment.Right });
            grid.AddChild(new Label { Text = $"{s.Low1Fps:F0}", HorizontalAlignment = HorizontalAlignment.Right });
            grid.AddChild(new Label { Text = s.GpuMs > 0 ? $"{s.GpuMs:F1}" : "n/a", HorizontalAlignment = HorizontalAlignment.Right });
            grid.AddChild(new Label { Text = s.Verdict });
        }
        box.AddChild(grid);
        box.AddChild(new Label { Text = $"The full report is in {path}" });
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var open = new Button { Text = "Open the folder" };
        open.Pressed += () => OS.ShellShowInFileManager(path);
        var close = new Button { Text = "Close the game" };
        close.Pressed += () => GetTree().Quit();
        row.AddChild(open);
        row.AddChild(close);
        box.AddChild(row);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
    }
}
