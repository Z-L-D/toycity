using System.Text.Json;
using ToyCity.ClientModel.Graphics;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 4, Part 5: the GPU test's report.</summary>
public class GpuReportTests
{
    static readonly GpuMachine Machine = new("Test GPU", "Vendor", "vulkan 1.3", "forward_plus", "Windows 11", "Test CPU", 16, "4.7.2-stable", 1920, 1080);

    [Fact]
    public void TheOnePercentLowsAreTheSlowestFrames()
    {
        // 99 frames at 10 ms and one at 50 ms: 100 fps less a little on average, the lows the one slow frame.
        var frames = Enumerable.Repeat(10.0, 99).Append(50.0).ToList();
        var r = GpuReport.Summarise(new GpuSample("High", "classic", frames, 6, 3, 1000, 50));
        Assert.Equal(100, r.Frames);
        Assert.Equal(10.4, r.AvgMs, 3);
        Assert.Equal(1000 / 10.4, r.Fps, 3);
        Assert.Equal(20, r.Low1Fps, 3);
        Assert.Equal("holds 60 fps", GpuReport.Verdict(96, 45));
        Assert.Equal("holds 30 fps", GpuReport.Verdict(96, 20 + 2));
        Assert.Equal("under 30 fps", GpuReport.Verdict(29, 25));
        Assert.Equal("under 30 fps", GpuReport.Verdict(40, 12));
    }

    [Fact]
    public void TheReportSummarisesEachSetupOverItsViews()
    {
        var samples = new List<GpuSample>
        {
            new("Medium", "classic", Enumerable.Repeat(10.0, 200).ToList(), 5, 2, 100_000, 300),
            new("Medium", "street", Enumerable.Repeat(20.0, 200).ToList(), 12, 3, 200_000, 500),
            new("Ultra", "classic", Enumerable.Repeat(40.0, 100).ToList(), 0, 4, 400_000, 900),
        };
        var setups = GpuReport.BySetup(samples.Select(GpuReport.Summarise).ToList());
        Assert.Equal(new[] { "Medium", "Ultra" }, setups.Select(s => s.Setup));
        Assert.Equal(1000 / 15.0, setups[0].Fps, 3);
        Assert.Equal(50, setups[0].Low1Fps, 3);  // the street view's lows
        Assert.Equal("holds 60 fps", setups[0].Verdict);
        Assert.Equal("under 30 fps", setups[1].Verdict);

        var md = GpuReport.Markdown(Machine, samples, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        Assert.Contains("Test GPU (Vendor), vulkan 1.3, forward_plus renderer", md);
        Assert.Contains("| Medium | 66.7 | 50.0 | 8.50 | holds 60 fps |", md);
        Assert.Contains("| Ultra | classic | 100 | 25.0 | 25.0 | 40.00 | n/a | 4.00 | 900 | 400,000 |", md);

        using var json = JsonDocument.Parse(GpuReport.Json(Machine, samples, DateTime.UtcNow));
        Assert.Equal(2, json.RootElement.GetProperty("setups").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("views").GetArrayLength());
        Assert.Equal("Test GPU", json.RootElement.GetProperty("machine").GetProperty("adapter").GetString());
    }
}
