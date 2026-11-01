using ToyCity.ClientModel.Gate;
using ToyCity.Protocol;
using ToyCity.Sim;
using Xunit;
using Xunit.Abstractions;
using P = ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 3.2's gate works on the hills, found by the client's planner and built by the server.</summary>
public class HillTests(ITestOutputHelper output)
{
    /// <summary>The Beta 3.2 gate's city: a starter town among hills, with rock near the surface.</summary>
    public const ulong HillSeed = 501;

    [Fact]
    public void TheGateFindsACutATunnelAndStairsAndTheServerBuildsWhatThePlannerPriced()
    {
        var w = World.Create(ContentDb.LoadDefault(), HillSeed, 128, "Hill Town");
        Assert.True(StarterTown.Apply(w));
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        var spots = HillSpots.Find(c, w.Funds);
        foreach (var s in spots) output.WriteLine($"{s.What}: {s.Path.X0},{s.Path.Y0} to {s.Path.X1},{s.Path.Y1}, ${s.Plan.Cost}, cut {s.Plan.DeepestCutDm} dm, tunnel {s.Plan.TunnelCells}");
        Assert.Equal(3, spots.Count);
        foreach (var s in spots)
        {
            var a = (PlacePathAction)Mapping.ToSim(new P.Action { Path = s.Path })!;
            Assert.Null(w.ValidatePath(a, out long cost, out _, out _));
            // The planner's price is the server's, rock and all.
            Assert.Equal(cost, s.Plan.Cost);
            w.Enqueue(a);
        }
        var tracker = new DeltaTracker();
        tracker.Reset(w);
        w.Step();
        c.ApplyDelta(tracker.Next(w, 0, Array.Empty<SimNotice>()));
        Assert.Equal(w.Corner, c.Corners);
        Assert.Equal(w.Bedrock, c.Bedrock);
        var tunnel = spots.Single(s => s.What.Contains("tunnel")).Path;
        Assert.Contains(Planner_Cells(tunnel), i => c.IsTunnel(i));
        var stairs = spots.Single(s => s.What.Contains("stairs")).Path;
        Assert.Contains(Planner_Cells(stairs), i => RoadLook.IsStairs(c, i, tunnel.X0 == tunnel.X1 ? 0b0101 : 0b1010) || RoadLook.IsStairs(c, i, 0b1111));
        // The cut went into the rock: bare rock beside it.
        var cut = spots.Single(s => s.What.Contains("cut")).Path;
        int bare = 0, cw = w.Width + 1;
        foreach (int i in Planner_Cells(cut))
            for (int d = -3; d <= 3; d++)
            {
                int x = i % w.Width + (cut.Y0 == cut.Y1 ? 0 : d), y = i / w.Width + (cut.Y0 == cut.Y1 ? d : 0);
                if (w.InBounds(x, y) && w.SoilDm(y * cw + x) == 0 && w.Made[w.Idx(x, y)] != 0) bare++;
            }
        output.WriteLine($"{bare} bare rock corners beside the cut");
        Assert.True(bare > 0, "the cut didn't reach the rock");

        IEnumerable<int> Planner_Cells(P.PlacePath p) => Tools.Planner.LineCells(p.X0, p.Y0, p.X1, p.Y1).Select(q => w.Idx(q.X, q.Y));
    }
}
