using Godot;
using ToyCity.ClientModel.Graphics;

namespace ToyCity.Godot;

/// <summary>
/// The graphics options: loaded from user://graphics.cfg, or on the first run picked for the graphics adapter
/// (Medium on integrated graphics, High otherwise); --graphics PRESET overrides them for a run without saving,
/// for measuring each preset with --bench or --gate.
/// </summary>
public partial class Main
{
    const string GraphicsFile = "user://graphics.cfg";
    GraphicsSettings _graphics = GraphicsSettings.High;
    string? _graphicsPreset;  // --graphics
    GraphicsPanel? _graphicsPanel;
    BusLinesView? _buses;

    void LoadGraphics()
    {
        var suggested = GraphicsSettings.Suggest(RenderingServer.GetVideoAdapterName());
        if (_graphicsPreset != null)
            _graphics = GraphicsSettings.Preset(_graphicsPreset) ?? throw new ArgumentException($"--graphics: no preset \"{_graphicsPreset}\" (Low, Medium, High or Ultra)");
        else if (global::Godot.FileAccess.FileExists(GraphicsFile))
            _graphics = GraphicsSettings.Load(global::Godot.FileAccess.GetFileAsString(GraphicsFile), suggested);
        else _graphics = suggested;
        _graphicsPanel = new GraphicsPanel(_graphics);
        _graphicsPanel.Changed += g =>
        {
            _graphics = g;
            ApplyGraphics();
            if (_graphicsPreset != null) return;
            using var f = global::Godot.FileAccess.Open(GraphicsFile, global::Godot.FileAccess.ModeFlags.Write);
            f?.StoreString(g.Save());
        };
        AddChild(_graphicsPanel);
    }

    void ApplyGraphics()
    {
        var g = _graphics;
        var vp = GetViewport();
        vp.Scaling3DScale = g.RenderScale / 100f;
        // FSR is sharper than plain stretching where the renderer has it (Forward+).
        vp.Scaling3DMode = RenderingServer.GetCurrentRenderingMethod() == "forward_plus" && g.RenderScale < 100
            ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        vp.Msaa3D = g.AntiAliasing switch { AntiAliasing.Msaa2 => Viewport.Msaa.Msaa2X, AntiAliasing.Msaa4 => Viewport.Msaa.Msaa4X, _ => Viewport.Msaa.Disabled };
        vp.ScreenSpaceAA = g.AntiAliasing == AntiAliasing.Fxaa ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
        _sun!.ShadowEnabled = g.Shadows != ShadowQuality.Off;
        RenderingServer.DirectionalShadowAtlasSetSize(g.Shadows == ShadowQuality.High ? 4096 : 2048, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(g.Shadows == ShadowQuality.High ? RenderingServer.ShadowQuality.SoftLow : RenderingServer.ShadowQuality.Hard);
        // The measuring runs always draw as fast as they can.
        bool measuring = _bench != null || _gateFile != null || _togetherFile != null;
        DisplayServer.WindowSetVsyncMode(g.VSync && !measuring ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = measuring ? 0 : g.MaxFps;
        foreach (Node3D life in new Node3D[] { _agents!, _buses! })
        {
            life.ProcessMode = g.CityLife ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            if (!g.CityLife) life.Visible = false;
        }
        if (g.CityLife) _agents!.Visible = true;
        var (start, budget) = g.Shapes;
        _view!.SetShapes(g.FullTerrain, start, budget);
    }

    /// <summary>The buildings' models for the camera as it is now, from the settings.</summary>
    void UpdateDetail()
    {
        var g = _graphics;
        if (_camera!.IsFree)
        {
            float d = _free!.Riding >= 0 ? 4 : _free.Distance;
            var detail = g.FreeDetail(d);
            var rest = g.FreeDetail(Math.Max(d, 24));
            _view!.SetDetail(detail, rest, _free.Target.X, _free.Target.Z, g.SmoothRadius);
            _sun!.DirectionalShadowMaxDistance = Math.Min(300, d * 3 + 40);
        }
        else
        {
            var detail = g.ClassicDetail(_camera.View.Zoom);
            _view!.SetDetail(detail, detail, _camera.View.Target.X, _camera.View.Target.Y, 0);
            // Shadows only as far as the view reaches, so close zoom levels don't cast the whole city.
            _sun!.DirectionalShadowMaxDistance = _camera.View.Distance + _camera.View.Size * 1.5f;
        }
    }
}
