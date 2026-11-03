using ToyCity.ClientModel.Tools;
using ToyCity.Protocol;
using ToyCity.Protocol.V0;
using ToyCity.Sim;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Tests;

/// <summary>River brush and dams, as a client sees them: the tools' actions, the sites it finds, and the reservoir it's sent.</summary>
public class DamClientTests
{
    [Fact]
    public void TheDamToolBuildsWhatTheServerTakesAndTheClientSeesItsReservoir()
    {
        var w = World.Create(ContentDb.LoadDefault(), 42, 128, "Weir");
        w.Funds = 1_000_000;
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        var tracker = new DeltaTracker();
        tracker.Reset(w);
        var sites = DamSites.Find(c, 3);
        Assert.NotEmpty(sites);
        // Most of the sites the client finds, the server would build.
        int taken = sites.Take(10).Count(s => w.ValidateDam(new PlaceDamAction(s.X0, s.Y0, s.X1, s.Y1, 3), out _, out _, out _, out _, out _, out _) == null);
        var why = sites.Take(10).Select(s => $"{s}: {w.ValidateDam(new PlaceDamAction(s.X0, s.Y0, s.X1, s.Y1, 3), out _, out _, out _, out var pool, out int bx, out int by) ?? "ok"} ({pool.Count}) at {bx},{by} h{w.Height[w.Idx(bx, by)]} r{w.River[w.Idx(bx, by)]} river {w.IsRiver(w.Idx(bx, by))}");
        Assert.True(taken >= 5, $"{taken} of {Math.Min(10, sites.Count)} sites taken:\n{string.Join("\n", why)}");
        var site = sites.First(s => w.ValidateDam(new PlaceDamAction(s.X0, s.Y0, s.X1, s.Y1, 3), out _, out _, out _, out _, out _, out _) == null);

        // The tool's drag makes the action the server takes.
        var t = new ToolState { Kind = ToolKind.Dam, DamRise = 3 };
        var plan = Planner.Plan(c, t, new Pointer(site.X1, site.Y1, DragStart: (site.X0, site.Y0)))!;
        Assert.True(plan.Ok, plan.Text);
        Assert.NotNull(Mapping.ToSim(plan.Actions.Single()));
        w.Enqueue(Mapping.ToSim(plan.Actions.Single())!);
        w.Step();
        Assert.Contains(w.Dam, d => d != 0);

        // The client sees the dam and the reservoir, and the water there at its level whatever the flow.
        c.ApplyDelta(tracker.Next(w, 0, Array.Empty<SimNotice>()));
        Assert.Equal(w.Dam, c.Dams);
        Assert.Equal(w.River, c.Rivers);
        Assert.Equal(w.DamHeld, c.DamHeld);
        int pool = Enumerable.Range(0, c.Width * c.Height).First(i => c.DamHeld[i] == 2);
        Assert.Equal(World.WaterDm(c.Rivers[pool], c.WaterSize[pool], 100) / 20f, c.WaterSurface(pool));
    }

    [Fact]
    public void TheRiverBrushSendsALongStrokeAsDigsTheServerTakes()
    {
        var w = World.Create(ContentDb.LoadDefault(), 42, 128, "Weir");
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        var t = new ToolState { Kind = ToolKind.River, RiverWidth = 2, RiverDepth = 3 };
        var plan = Planner.Plan(c, t, new Pointer(120, 100, DragStart: (2, 4)))!;
        Assert.True(plan.Actions.Count >= 2);
        foreach (var a in plan.Actions)
        {
            var d = Assert.IsType<DigRiverAction>(Mapping.ToSim(a));
            Assert.InRange(World.LineCells(d.X0, d.Y0, d.X1, d.Y1).Count, 1, World.DigLongest);
            Assert.Equal((2, 3), (d.Width, d.Depth));
        }
        // End to end: each dig starts where the last ended.
        for (int k = 1; k < plan.Actions.Count; k++)
            Assert.Equal((plan.Actions[k - 1].DigRiver.X1, plan.Actions[k - 1].DigRiver.Y1), (plan.Actions[k].DigRiver.X0, plan.Actions[k].DigRiver.Y0));
        Assert.Equal((120, 100), (plan.Actions[^1].DigRiver.X1, plan.Actions[^1].DigRiver.Y1));
    }
}
