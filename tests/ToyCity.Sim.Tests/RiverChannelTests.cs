using ToyCity.Protocol;
using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Rivers in shaped channels: a smoothed line with sloped banks, as the Flint Hills creek was drawn.</summary>
public class RiverChannelTests(ITestOutputHelper output)
{
    const int S = 128;

    static (int Cells, int Steps) RiverStats(World.Ground g)
    {
        var steps = World.StepsFromCorners(g.Corners, S, S);
        int cells = 0, wall = 0;
        for (int i = 0; i < S * S; i++)
        {
            if (g.Rivers[i] <= steps[i]) continue;
            cells++;
            // The steps a river cell's ground drops from its dry neighbours: a vertical wall where it's big.
            int x = i % S, y = i / S;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= S || ny >= S) continue;
                int j = ny * S + nx;
                if (g.Rivers[j] <= steps[j]) wall += Math.Max(0, steps[j] - steps[i] - 1);
            }
        }
        return (cells, wall);
    }

    [Fact]
    public void ChannelsMakeWiderRiversWithSlopedBanks()
    {
        foreach (ulong seed in new ulong[] { 3, 42, 77 })
        {
            var old = World.GenerateGround(seed, S, S, channels: 0);
            var shaped = World.GenerateGround(seed, S, S);
            var (oc, ow) = RiverStats(old);
            var (sc, sw) = RiverStats(shaped);
            output.WriteLine($"seed {seed}: {oc} river cells before, {sc} with channels; wall steps a river cell {(double)ow / Math.Max(1, oc):0.00} before, {(double)sw / Math.Max(1, sc):0.00} after");
            if (oc == 0) continue;
            Assert.True(sc > oc, "the channels should widen the rivers");
            Assert.True((double)sw / sc <= (double)ow / oc + 0.05, "the banks should be no steeper than the old cut, for each cell of river");
            // Every river cell still runs downhill to the sea or off the map, never climbing.
            var steps = World.StepsFromCorners(shaped.Corners, S, S);
            for (int i = 0; i < S * S; i++)
            {
                if (shaped.Rivers[i] <= steps[i]) continue;
                int d = shaped.Downstream[i];
                if (d >= 0) Assert.True(shaped.Rivers[d] <= shaped.Rivers[i], $"the river climbs from {i} to {d}");
            }
        }
    }

    [Fact]
    public void ARegionKeepsTheRiversItWasMadeWith()
    {
        using var r = Region.Create(Fixture.Content, "Channels", 9, 2, 2, 64);
        Assert.Equal(World.ChannelsVersion, r.Channels);
        using var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r));
        Assert.Equal(World.ChannelsVersion, back.Channels);
        Assert.Equal(r.Ground.Rivers, back.Ground.Rivers);
        // A region saved before channels keeps its old rivers.
        r.Channels = 0;
        using var older = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r));
        Assert.Equal(0, older.Channels);
        Assert.Equal(World.GenerateGround(9, 128, 128, channels: 0).Rivers, older.Ground.Rivers);
    }

    [Fact]
    public void MeandersMoveTheChannelOffTheGridsLines()
    {
        // The deepest ground across each river cell, as an offset from the cell's middle: with the old line it sits
        // on the smoothed drainage, with meanders it wanders to either side.
        foreach (ulong seed in new ulong[] { 42, 101 })
        {
            double Wander(int version)
            {
                var g = World.GenerateGround(seed, S, S, channels: version);
                double sum = 0; int n = 0;
                for (int i = 0; i < S * S; i++)
                {
                    int d = g.Downstream[i];
                    if (g.Rivers[i] == 0 || d < 0 || d - i is not (1 or -1)) continue;
                    // A river running east or west: compare the corners above and below its middle.
                    int x = i % S, y = i / S, top = y * (S + 1) + x, bottom = top + S + 1;
                    if (y == 0 || y == S - 1) continue;
                    sum += Math.Abs(g.Corners[top] - g.Corners[bottom]);
                    n++;
                }
                return n == 0 ? 0 : sum / n;
            }
            double before = Wander(1), after = Wander(2);
            output.WriteLine($"seed {seed}: the bed's tilt across a river cell {before:0.0} dm with banks, {after:0.0} dm with meanders");
            Assert.True(after > before, "the meanders should move the channel off the drainage line");
        }
    }
}
