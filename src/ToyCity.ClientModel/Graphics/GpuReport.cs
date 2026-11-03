using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ToyCity.ClientModel.Graphics;

/// <summary>One view held under one setup during the GPU test: every frame's time, and the GPU's and the CPU's share.</summary>
public sealed record GpuSample(string Setup, string View, IReadOnlyList<double> FrameMs, double GpuMs, double CpuMs, long Triangles, long DrawCalls);

/// <summary>The machine the GPU test ran on.</summary>
public sealed record GpuMachine(string Adapter, string Vendor, string Api, string Renderer, string Os, string Cpu, int Cores, string Godot, int Width, int Height);

/// <summary>
/// The GPU test's report (Beta 4, Part 5): for each graphics setup and camera view, the average frame time and frame
/// rate, the 1% lows (the frame rate over the slowest 1% of frames), the time the GPU and the CPU spent drawing each
/// frame, and the draw calls and triangles. Written as Markdown to read and as JSON to compare runs.
/// </summary>
public static class GpuReport
{
    public sealed record Row(string Setup, string View, int Frames, double AvgMs, double Fps, double Low1Fps, double GpuMs, double CpuMs, long Triangles, long DrawCalls);

    public sealed record SetupRow(string Setup, double Fps, double Low1Fps, double GpuMs, string Verdict);

    public static Row Summarise(GpuSample s)
    {
        var t = s.FrameMs.Where(x => x > 0).OrderByDescending(x => x).ToList();
        if (t.Count == 0) return new Row(s.Setup, s.View, 0, 0, 0, 0, s.GpuMs, s.CpuMs, s.Triangles, s.DrawCalls);
        double avg = t.Average();
        // The 1% lows: the frame rate over the slowest 1% of frames (at least one).
        double slow = t.Take(Math.Max(1, t.Count / 100)).Average();
        return new Row(s.Setup, s.View, t.Count, avg, 1000 / avg, 1000 / slow, s.GpuMs, s.CpuMs, s.Triangles, s.DrawCalls);
    }

    /// <summary>What a setup holds: 60 fps with lows above 45, 30 fps with lows above 22, or neither.</summary>
    public static string Verdict(double fps, double low1) =>
        fps >= 60 && low1 >= 45 ? "holds 60 fps" : fps >= 30 && low1 >= 22 ? "holds 30 fps" : "under 30 fps";

    /// <summary>Each setup over all its views: the average frame rate, the worst view's 1% lows and its verdict.</summary>
    public static List<SetupRow> BySetup(IReadOnlyList<Row> rows) =>
        rows.GroupBy(r => r.Setup).Select(g =>
        {
            double fps = 1000 / g.Average(r => r.AvgMs), low = g.Min(r => r.Low1Fps);
            return new SetupRow(g.Key, fps, low, g.Average(r => r.GpuMs), Verdict(fps, low));
        }).ToList();

    static string F(double v, string format = "0.0") => v.ToString(format, CultureInfo.InvariantCulture);
    static string Gpu(double ms) => ms > 0 ? F(ms, "0.00") : "n/a";

    public static string Markdown(GpuMachine m, IReadOnlyList<GpuSample> samples, DateTime when)
    {
        var rows = samples.Select(Summarise).ToList();
        var md = new StringBuilder();
        md.Append("# ToyCity GPU test\n\n");
        md.Append($"{when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC\n\n");
        md.Append($"- **Graphics:** {m.Adapter} ({m.Vendor}), {m.Api}, {m.Renderer} renderer\n");
        md.Append($"- **Processor:** {m.Cpu}, {m.Cores} threads\n");
        md.Append($"- **System:** {m.Os}, Godot {m.Godot}, window {m.Width} × {m.Height}\n\n");
        md.Append("## Each setup\n\n");
        md.Append("Over every view: the average frame rate, the worst view's 1% lows, and the GPU's time a frame.\n\n");
        md.Append("| Setup | fps | 1% low fps | GPU ms | |\n| --- | --- | --- | --- | --- |\n");
        foreach (var s in BySetup(rows)) md.Append($"| {s.Setup} | {F(s.Fps)} | {F(s.Low1Fps)} | {Gpu(s.GpuMs)} | {s.Verdict} |\n");
        md.Append("\n## Each view\n\n");
        md.Append("| Setup | View | Frames | fps | 1% low fps | Frame ms | GPU ms | CPU ms | Draw calls | Triangles |\n");
        md.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var r in rows)
            md.Append($"| {r.Setup} | {r.View} | {r.Frames} | {F(r.Fps)} | {F(r.Low1Fps)} | {F(r.AvgMs, "0.00")} | {Gpu(r.GpuMs)} | {F(r.CpuMs, "0.00")} | {r.DrawCalls.ToString("N0", CultureInfo.InvariantCulture)} | {r.Triangles.ToString("N0", CultureInfo.InvariantCulture)} |\n");
        md.Append("\nGPU ms is the time the graphics card spent on a frame, as the driver reports it (n/a where it doesn't). ");
        md.Append("CPU ms is the time the processor spent preparing the frame for it. ");
        md.Append("Contact shadows, bounce light and full lighting need the Forward+ renderer; on the compatibility renderer they cost nothing because they aren't drawn.\n");
        return md.ToString();
    }

    public static string Json(GpuMachine m, IReadOnlyList<GpuSample> samples, DateTime when)
    {
        var rows = samples.Select(Summarise).ToList();
        var doc = new
        {
            when = when.ToString("o", CultureInfo.InvariantCulture),
            machine = m,
            setups = BySetup(rows).Select(s => new { s.Setup, fps = Math.Round(s.Fps, 1), low1Fps = Math.Round(s.Low1Fps, 1), gpuMs = Math.Round(s.GpuMs, 2), s.Verdict }),
            views = rows.Select(r => new
            {
                r.Setup, r.View, r.Frames, avgMs = Math.Round(r.AvgMs, 2), fps = Math.Round(r.Fps, 1), low1Fps = Math.Round(r.Low1Fps, 1),
                gpuMs = Math.Round(r.GpuMs, 2), cpuMs = Math.Round(r.CpuMs, 2), r.DrawCalls, r.Triangles,
            }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
