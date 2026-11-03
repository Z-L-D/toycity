using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Rivers that carry the month's rain: their flow, depth and floods.</summary>
public class RiverFlowTests(ITestOutputHelper output)
{
    [Fact]
    public void TheRiversRiseAndFallWithTheMonthsRain()
    {
        using var r = Region.Create(Fixture.Content, "Wetlands", 7, 3, 3, 64);
        var flows = new List<int>();
        var cache = new Dictionary<long, List<Region.Front>>();
        for (int k = 0; k < r.Tiles.Length; k++)
            for (long t = 30; t < 4 * 360; t += 3)
                flows.Add(r.FlowAt(k, t, cache));
        var rains = new List<(double Month, double Season)>();
        for (int k = 0; k < r.Tiles.Length; k++)
            for (long t = 30; t < 4 * 360; t += 3)
                rains.Add(r.RainAt(k, t, cache));
        output.WriteLine($"rain: month mean {rains.Average(x => x.Month):0.0}, season mean {rains.Average(x => x.Season):0.0}, dry months {rains.Count(x => x.Month == 0) / (double)rains.Count:P0}, dry seasons {rains.Count(x => x.Season == 0) / (double)rains.Count:P0}");
        flows.Sort();
        int median = flows[flows.Count / 2];
        double flood = flows.Count(f => f >= 200) / (double)flows.Count, low = flows.Count(f => f < 50) / (double)flows.Count;
        output.WriteLine($"median {median}%, 10th {flows[flows.Count / 10]}%, 90th {flows[flows.Count * 9 / 10]}%, max {flows[^1]}%; in flood {flood:P1}, low {low:P1}");
        Assert.InRange(median, 60, 140);
        Assert.InRange(flood, 0.005, 0.15);
        Assert.True(low > 0.01, "a dry month should leave the rivers low now and then");
    }
}
