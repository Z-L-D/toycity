using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Graphics;
using ToyCity.ClientModel.Tools;

namespace ToyCity.Godot;

/// <summary>
/// Light and shadow (Beta 3.2, Part 6). The sun follows the day clock, or the hour the graphics options pin it to,
/// along its path for the month (<see cref="Daylight"/>): warm and low at golden hour, gone at night with the moon
/// in its place. The sky, the shade and the haze follow it and the weather. At night the windows, the street lights
/// and the buses light up through the project's `night` global. Contact shadows and bounce light come by the
/// lighting option, and while a cell is queried the view blurs fore and aft for the miniature look.
/// </summary>
public partial class Main
{
    global::Godot.Environment? _env;
    ProceduralSkyMaterial? _sky;
    readonly GameClock _dayClock = new();
    CameraAttributesPractical? _lens;
    /// <summary>The light last applied, for the gate and the panels.</summary>
    public Light LightNow { get; private set; }
    Light _skyFor;
    byte[]? _snowHeightsFor;
    int _snowFrames;
    float _snowMedianY;

    global::Godot.Environment MakeEnvironment()
    {
        _sky = new ProceduralSkyMaterial { SunAngleMax = 20, SunCurve = 0.1f, EnergyMultiplier = 1 };
        _env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, ProcessMode = Sky.ProcessModeEnum.Incremental },
            AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.75f, 0.78f, 0.85f),
            AmbientLightEnergy = 0.55f,
            ReflectedLightSource = global::Godot.Environment.ReflectionSource.Sky,
            FogEnabled = true,
            FogSkyAffect = 0.1f,
            FogAerialPerspective = 0.2f,
            GlowIntensity = 0.6f,
            GlowBloom = 0.05f,
            GlowHdrThreshold = 1.1f,
        };
        _lens = new CameraAttributesPractical { DofBlurAmount = 0.12f };
        return _env;
    }

    static Color C(Rgb c) => new(c.R, c.G, c.B);

    /// <summary>The light for this frame, from the clock (or the pinned hour) and the weather.</summary>
    void UpdateLight()
    {
        var c = _client!.City;
        if (_env == null || _sun == null) return;
        var l = Daylight.Now(c.Stats, _dayClock.Minute(c.Stats, DateTime.UtcNow), _graphics.TimeOfDay);
        LightNow = l;
        var (dx, dy, dz) = l.Direction;
        var dir = new Vector3(dx, dy, dz).Normalized();
        if (_sun.Basis.Z.Dot(-dir) < 0.99999f) _sun.Basis = Basis.LookingAt(dir, Math.Abs(dir.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
        _sun.LightColor = C(l.Sun);
        _sun.LightEnergy = l.SunEnergy;
        _sun.ShadowOpacity = Mathf.Lerp(1f, 0.55f, l.Overcast);
        _env.AmbientLightColor = C(l.Ambient);
        _env.AmbientLightEnergy = l.AmbientEnergy;
        // The sky redraws its reflections when it changes, so only on a visible change.
        if (Differs(l, _skyFor))
        {
            _skyFor = l;
            _sky!.SkyTopColor = C(l.Sky);
            _sky.SkyHorizonColor = C(l.Horizon);
            _sky.GroundHorizonColor = C(l.Horizon);
            _sky.GroundBottomColor = C(l.Horizon.Times(0.45f));
            _sky.SunAngleMax = l.Night > 0.5f ? 4 : 20;
            RenderingServer.GlobalShaderParameterSet("sky_colour", C(Rgb.Mix(l.Sky, l.Horizon, 0.5f)));
            RenderingServer.GlobalShaderParameterSet("sun_colour", C(l.Sun.Times(Math.Min(1, l.SunEnergy))));
        }
        _env.FogLightColor = C(l.Horizon);
        // The classic camera looks from far off along a fixed line, so its haze starts past what it looks at; the
        // free camera's thickens with distance.
        if (_camera!.IsFree)
        {
            _env.FogMode = global::Godot.Environment.FogModeEnum.Exponential;
            _env.FogDensity = l.Haze;
        }
        else
        {
            _env.FogMode = global::Godot.Environment.FogModeEnum.Depth;
            float begin = _camera.View.Distance + _camera.View.Size * 0.3f;
            _env.FogDepthBegin = begin;
            _env.FogDepthEnd = begin + 160;
            _env.FogDensity = Math.Clamp(l.Haze * 90, 0, 0.9f);
        }
        _env.GlowEnabled = l.Night > 0.2f && _graphics.Lighting != LightQuality.Simple;
        RenderingServer.GlobalShaderParameterSet("night", l.Night);
        // Lying snow (Beta 4, part 4): the city's depth and its median height, for the ground and the roofs.
        if (!ReferenceEquals(_snowHeightsFor, c.Heights) || ++_snowFrames > 600)
        {
            (_snowHeightsFor, _snowFrames) = (c.Heights, 0);
            _snowMedianY = (c.WaterLevel - 0.35f + SnowCover.MedianMetres(c) / 2) * ClassicView.HeightStep;
        }
        RenderingServer.GlobalShaderParameterSet("snow_mm", (float)SnowCover.GroundMm(c));
        RenderingServer.GlobalShaderParameterSet("snow_median_y", _snowMedianY);
        // The trees' leaves turn with the calendar (Beta 4.1).
        if (_monthPin > 0) RenderingServer.GlobalShaderParameterSet("month", _monthPin);
        else if (c.Stats.Month > 0) RenderingServer.GlobalShaderParameterSet("month", c.Stats.Month + (Math.Max(1, c.Stats.Day) - 1) / 30f);
        _buses?.SetNight(l.Night);
        UpdateLens();
    }

    static bool Differs(Light a, Light b) =>
        Math.Abs(a.Sky.R - b.Sky.R) + Math.Abs(a.Sky.G - b.Sky.G) + Math.Abs(a.Sky.B - b.Sky.B)
        + Math.Abs(a.Horizon.R - b.Horizon.R) + Math.Abs(a.Horizon.G - b.Horizon.G) + Math.Abs(a.Horizon.B - b.Horizon.B) > 0.03f
        || Math.Abs(a.Night - b.Night) > 0.1f;

    /// <summary>Tilt-shift (6.5): with a cell queried, what's nearer and farther than it blurs.</summary>
    void UpdateLens()
    {
        var sel = _panels?.Model.Selected;
        Camera3D cam = _camera!.IsFree ? _free! : _camera;
        if (cam.Attributes != _lens) cam.Attributes = _lens;
        bool on = _graphics.TiltShift && sel is { } && _tools!.State.Kind == ToolKind.Query;
        _lens!.DofBlurFarEnabled = _lens.DofBlurNearEnabled = on;
        if (!on || sel is not { } s) return;
        var at = new Vector3(s.X + 0.5f, _view!.Surface(s.X + 0.5f, s.Y + 0.5f), s.Y + 0.5f);
        float d = (at - cam.GlobalPosition).Dot(-cam.GlobalBasis.Z);
        float span = _camera.IsFree ? Math.Max(4, _free!.Distance * 0.35f) : _camera.View.Size * 0.18f;
        _lens.DofBlurFarDistance = d + span;
        _lens.DofBlurFarTransition = span * 1.5f;
        _lens.DofBlurNearDistance = Math.Max(0.1f, d - span);
        _lens.DofBlurNearTransition = span;
    }

    /// <summary>The lighting option (6.3): contact shadows, bounce light, and light through the whole scene.</summary>
    void ApplyLighting()
    {
        if (_env == null) return;
        var q = _graphics.Lighting;
        _env.SsaoEnabled = q >= LightQuality.Occlusion;
        _env.SsaoRadius = 1.2f;
        _env.SsaoIntensity = 1.6f;
        _env.SsilEnabled = q >= LightQuality.Bounce;
        _env.SdfgiEnabled = q == LightQuality.Full;
        // Softer, wider shadows on the better settings (6.2).
        _sun!.LightAngularDistance = _graphics.Shadows == ShadowQuality.High ? 0.6f : 0;
        _sun.DirectionalShadowBlendSplits = _graphics.Shadows == ShadowQuality.High;
    }
}
