using System.Globalization;
using System.Text;

namespace ToyCity.ClientModel.Graphics;

/// <summary>How much of each building's voxel detail is drawn up close: a quarter, half or all of it.</summary>
public enum BuildingDetail { Low, Medium, High }

/// <summary>Sun shadows: none, a small shadow map with hard edges, or a large one with soft edges.</summary>
public enum ShadowQuality { Off, Low, High }

/// <summary>Smoothed (rounded) buildings around where the free camera looks: none, the nearest blocks, or a wide ring.</summary>
public enum Smoothing { Off, Near, Wide }

/// <summary>
/// How finely smoothed shapes are made: coarse voxels in few triangles, or the full voxels, curved, in many. Each
/// model is cut to this many triangles at most.
/// </summary>
public enum ShapeDetail { Low, Medium, High }

/// <summary>Edge smoothing for the 3D view: none, FXAA (cheap, a little soft), or 2× or 4× MSAA.</summary>
public enum AntiAliasing { Off, Fxaa, Msaa2, Msaa4 }

/// <summary>
/// The graphics options, each on its own and as presets from Low (integrated graphics) to Ultra. Changing any one
/// option makes the preset Custom. Saved as plain "key = value" lines; a missing or unknown line keeps the default,
/// so an old file still loads.
/// </summary>
public sealed record GraphicsSettings
{
    /// <summary>The 3D view drawn at this percentage of the window's resolution and scaled up (the panels stay sharp).</summary>
    public int RenderScale { get; init; } = 100;
    public ShadowQuality Shadows { get; init; } = ShadowQuality.High;
    public BuildingDetail Detail { get; init; } = BuildingDetail.High;
    public Smoothing Smoothing { get; init; } = Smoothing.Near;
    public ShapeDetail ShapeDetail { get; init; } = ShapeDetail.Medium;
    /// <summary>Ground far from the camera keeps every triangle; off, distant ground is drawn at half the detail.</summary>
    public bool FullTerrain { get; init; } = true;
    public AntiAliasing AntiAliasing { get; init; } = AntiAliasing.Msaa2;
    /// <summary>How far, in cells, the free camera draws buildings in detail; beyond it they use their far models.</summary>
    public int DetailDistance { get; init; } = 120;
    public bool VSync { get; init; } = true;
    /// <summary>A cap on frames a second; 0 for none.</summary>
    public int MaxFps { get; init; }
    public bool StreetLabels { get; init; } = true;
    /// <summary>Commuters moving about and the buses running their lines.</summary>
    public bool CityLife { get; init; } = true;

    public static readonly int[] RenderScales = { 50, 60, 75, 85, 100 };
    public static readonly int[] DetailDistances = { 40, 80, 120, 200 };
    public static readonly int[] FpsCaps = { 0, 30, 60, 120 };

    public static readonly GraphicsSettings Low = new()
    {
        RenderScale = 75, Shadows = ShadowQuality.Off, Detail = BuildingDetail.Low, Smoothing = Smoothing.Off,
        AntiAliasing = AntiAliasing.Off, DetailDistance = 40, ShapeDetail = ShapeDetail.Low, FullTerrain = false,
    };
    public static readonly GraphicsSettings Medium = new()
    {
        RenderScale = 100, Shadows = ShadowQuality.Low, Detail = BuildingDetail.Medium, Smoothing = Smoothing.Off,
        AntiAliasing = AntiAliasing.Fxaa, DetailDistance = 80, ShapeDetail = ShapeDetail.Low,
    };
    public static readonly GraphicsSettings High = new();
    public static readonly GraphicsSettings Ultra = new()
    {
        Smoothing = Smoothing.Wide, AntiAliasing = AntiAliasing.Msaa4, DetailDistance = 200, ShapeDetail = ShapeDetail.High,
    };

    public static readonly (string Name, GraphicsSettings Settings)[] Presets =
        { ("Low", Low), ("Medium", Medium), ("High", High), ("Ultra", Ultra) };

    public static GraphicsSettings? Preset(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Settings;

    /// <summary>The preset these settings match, or "Custom". The frame cap, v-sync, labels and city life aren't part of a preset.</summary>
    public string PresetName
    {
        get
        {
            foreach (var (name, p) in Presets)
                if (p with { VSync = VSync, MaxFps = MaxFps, StreetLabels = StreetLabels, CityLife = CityLife } == this) return name;
            return "Custom";
        }
    }

    /// <summary>A preset's options, keeping this player's frame cap, v-sync, labels and city life.</summary>
    public GraphicsSettings WithPreset(GraphicsSettings p) => p with { VSync = VSync, MaxFps = MaxFps, StreetLabels = StreetLabels, CityLife = CityLife };

    /// <summary>
    /// The preset to start with on a machine with this graphics adapter: Medium for integrated graphics (Intel's,
    /// and AMD's built into the processor), High otherwise. The player can change it straight away.
    /// </summary>
    public static GraphicsSettings Suggest(string adapter)
    {
        string a = adapter.ToLowerInvariant();
        bool integrated = a.Contains("intel") && !a.Contains("arc")
            || a.Contains("radeon(tm) graphics") || a.Contains("radeon graphics") || a.Contains("vega") && !a.Contains("rx vega")
            || a.Contains("llvmpipe") || a.Contains("swiftshader") || a.Contains("software");
        return integrated ? Medium : High;
    }

    /// <summary>
    /// Which building models a camera this far from what it looks at should draw: classic zoom levels 0-2 and far
    /// free-camera views use the far models; closer ones use the detail setting; and the free camera up close smooths
    /// the blocks around its focus, if smoothing is on.
    /// </summary>
    public ModelDetail ClassicDetail(int zoom) => zoom <= 2 ? ModelDetail.Far : Detail switch
    {
        BuildingDetail.Low => zoom >= 4 ? ModelDetail.Mid : ModelDetail.Far,
        BuildingDetail.Medium => ModelDetail.Mid,
        _ => ModelDetail.Near,
    };

    public ModelDetail FreeDetail(float distance)
    {
        if (distance > DetailDistance) return ModelDetail.Far;
        if (distance < 24 && Smoothing != Smoothing.Off) return ModelDetail.Smooth;
        return Detail switch { BuildingDetail.Low => ModelDetail.Far, BuildingDetail.Medium => ModelDetail.Mid, _ => ModelDetail.Near };
    }

    /// <summary>The voxel size smooth shapes start from (1 full, 2 half, 4 a quarter) and the most triangles a model may take.</summary>
    public (int Start, int Budget) Shapes => ShapeDetail switch
    {
        ShapeDetail.Low => (4, 2_000),
        ShapeDetail.Medium => (2, 6_000),
        _ => (1, 20_000),
    };

    /// <summary>How many map regions out from the focus get smoothed models: 0 just the focus's, 1 the ring around it.</summary>
    public int SmoothRadius => Smoothing == Smoothing.Wide ? 1 : 0;

    // ---------------------------------------------------------------------------------------------------------
    // The options panel: one row each, a label, a line of help and its choices

    public static readonly GraphicsOption[] Options =
    {
        new("3D resolution", "Draws the city at a lower resolution and scales it up; the panels stay sharp. The biggest saving on a slow GPU.",
            RenderScales.Select(r => $"{r}%").ToArray(), s => Near(RenderScales, s.RenderScale), (s, i) => s with { RenderScale = RenderScales[i] }),
        new("Shadows", "The sun's shadows. They draw the city a second time, so off is much faster.",
            new[] { "Off", "Low", "High" }, s => (int)s.Shadows, (s, i) => s with { Shadows = (ShadowQuality)i }),
        new("Building detail", "How much of each building's voxel detail shows when you're close: a quarter, half or all of it.",
            new[] { "Low", "Medium", "High" }, s => (int)s.Detail, (s, i) => s with { Detail = (BuildingDetail)i }),
        new("Smoothed buildings", "Smooth shapes for the buildings around what the free camera looks at, up close: domes, leaves and roofs curve, walls keep their edges.",
            new[] { "Off", "Nearest blocks", "Wide" }, s => (int)s.Smoothing, (s, i) => s with { Smoothing = (Smoothing)i }),
        new("Smooth shape detail", "How finely smooth shapes are made. Higher is rounder and takes more triangles; each shape is made once and kept on disk.",
            new[] { "Low", "Medium", "High" }, s => (int)s.ShapeDetail, (s, i) => s with { ShapeDetail = (ShapeDetail)i }),
        new("Distant terrain", "How finely the ground far from the camera is drawn. Coarse uses a quarter of the triangles there; nearby ground is always full.",
            new[] { "Coarse", "Full" }, s => s.FullTerrain ? 1 : 0, (s, i) => s with { FullTerrain = i == 1 }),
        new("Anti-aliasing", "Smooths jagged edges. FXAA is cheap and a little soft; MSAA is sharper and costs more.",
            new[] { "Off", "FXAA", "MSAA 2×", "MSAA 4×" }, s => (int)s.AntiAliasing, (s, i) => s with { AntiAliasing = (AntiAliasing)i }),
        new("Detail distance", "How far the free camera draws buildings in detail before switching to their far models.",
            DetailDistances.Select(d => $"{d} cells").ToArray(), s => Near(DetailDistances, s.DetailDistance), (s, i) => s with { DetailDistance = DetailDistances[i] }),
        new("V-sync", "Waits for the screen's refresh: no tearing, and no faster than the screen.",
            new[] { "Off", "On" }, s => s.VSync ? 1 : 0, (s, i) => s with { VSync = i == 1 }),
        new("Frame cap", "Stops drawing faster than this, to keep a laptop cool and quiet.",
            FpsCaps.Select(f => f == 0 ? "None" : $"{f} fps").ToArray(), s => Near(FpsCaps, s.MaxFps), (s, i) => s with { MaxFps = FpsCaps[i] }),
        new("Street names", "Names on the streets when you're zoomed in.",
            new[] { "Off", "On" }, s => s.StreetLabels ? 1 : 0, (s, i) => s with { StreetLabels = i == 1 }),
        new("City life", "Commuters moving about and buses running their lines.",
            new[] { "Off", "On" }, s => s.CityLife ? 1 : 0, (s, i) => s with { CityLife = i == 1 }),
    };

    /// <summary>The index of the listed value nearest to v.</summary>
    static int Near(int[] values, int v) => Array.IndexOf(values, values.OrderBy(x => Math.Abs(x - v)).First());

    // ---------------------------------------------------------------------------------------------------------
    // Saving

    public string Save()
    {
        var sb = new StringBuilder("# ToyCity graphics options\n");
        void Line(string key, object value) => sb.Append(key).Append(" = ").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        Line("render_scale", RenderScale);
        Line("shadows", Shadows);
        Line("detail", Detail);
        Line("smoothing", Smoothing);
        Line("shape_detail", ShapeDetail);
        Line("terrain_detail", FullTerrain);
        Line("anti_aliasing", AntiAliasing);
        Line("detail_distance", DetailDistance);
        Line("vsync", VSync);
        Line("max_fps", MaxFps);
        Line("street_labels", StreetLabels);
        Line("city_life", CityLife);
        return sb.ToString();
    }

    public static GraphicsSettings Load(string text, GraphicsSettings defaults)
    {
        var s = defaults;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq < 0) continue;
            string key = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
            int I(int fallback) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
            bool B(bool fallback) => bool.TryParse(v, out var b) ? b : fallback;
            T E<T>(T fallback) where T : struct, Enum => Enum.TryParse<T>(v, true, out var e) && Enum.IsDefined(e) ? e : fallback;
            s = key switch
            {
                "render_scale" => s with { RenderScale = Math.Clamp(I(s.RenderScale), 25, 100) },
                "shadows" => s with { Shadows = E(s.Shadows) },
                "detail" => s with { Detail = E(s.Detail) },
                "smoothing" => s with { Smoothing = E(s.Smoothing) },
                "shape_detail" => s with { ShapeDetail = E(s.ShapeDetail) },
                "terrain_detail" => s with { FullTerrain = B(s.FullTerrain) },
                "anti_aliasing" => s with { AntiAliasing = E(s.AntiAliasing) },
                "detail_distance" => s with { DetailDistance = Math.Clamp(I(s.DetailDistance), 20, 400) },
                "vsync" => s with { VSync = B(s.VSync) },
                "max_fps" => s with { MaxFps = Math.Clamp(I(s.MaxFps), 0, 1000) },
                "street_labels" => s with { StreetLabels = B(s.StreetLabels) },
                "city_life" => s with { CityLife = B(s.CityLife) },
                _ => s,
            };
        }
        return s;
    }
}

/// <summary>One row of the graphics options panel.</summary>
public sealed record GraphicsOption(string Label, string Help, string[] Choices, Func<GraphicsSettings, int> Get, Func<GraphicsSettings, int, GraphicsSettings> Set);

/// <summary>Which of a building's models to draw.</summary>
public enum ModelDetail { Far, Mid, Near, Smooth }
