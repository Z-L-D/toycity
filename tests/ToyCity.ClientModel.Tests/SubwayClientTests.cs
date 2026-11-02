using ToyCity.ClientModel.Gate;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol;
using ToyCity.Protocol.V0;
using ToyCity.Sim;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 3.2, subways: the tool's preview, the tunnel reaching the client, and the stations' preview.</summary>
public class SubwayClientTests
{
    [Fact]
    public void ATunnelUnderTheTownIsPricedLaidAndShown()
    {
        var w = World.Create(ContentDb.LoadDefault(), 501, 128, "Toolton");
        Assert.True(StarterTown.Apply(w));
        w.Run(60);
        w.Funds = 1_000_000;
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        Assert.Equal(c.Width * c.Height, c.Subways.Length);
        Assert.All(c.Subways, b => Assert.Equal(0, b));

        // Straight across the town, under whatever's there: every cell is priced the same.
        int cy = (int)c.Buildings.Values.Average(b => b.Y);
        int x0 = c.Buildings.Values.Min(b => b.X), x1 = c.Buildings.Values.Max(b => b.X);
        var t = new ToolState { Kind = ToolKind.Utility, Utility = ToyCity.Protocol.V0.UtilityKind.UtilitySubway };
        var plan = Planner.Plan(c, t, new Pointer(x1, cy, DragStart: (x0, cy)))!;
        Assert.True(plan.Ok, plan.Text);
        Assert.Contains("Subway tunnel", plan.Text);
        Assert.Contains(Planner.Money((x1 - x0 + 1) * (long)Planner.SubwayPerCell), plan.Text);

        var tracker = new DeltaTracker();
        tracker.Reset(w);
        foreach (var a in plan.Actions) w.Enqueue(Mapping.Action(new ActionRecord { Tick = w.Tick, Action = a })!);
        w.Step();
        Assert.DoesNotContain(w.Notices, n => n.Severity == "error");
        Assert.Equal(x1 - x0 + 1, w.Subway.Count(b => b != 0));
        c.ApplyDelta(tracker.Next(w, 0, Array.Empty<SimNotice>()));
        var run = Assert.Single(Underground.SubwayRuns(c));
        Assert.Equal((x0, cy, x1, cy), (run.X0, run.Y0, run.X1, run.Y1));
        var again = new CityMirror();
        again.ApplyFull(Mapping.FullState(w, 0));
        Assert.Equal(c.Subways, again.Subways);

        // A station's preview says whether it'd be served: over the tunnel or beside it, yes; well off it, no.
        var station = c.TypeById["subway-station"];
        Assert.Equal("subway", station.Transit);
        Assert.True(Planner.SubwayUnder(c, x0, cy - 1, station.Width, station.Height));
        Assert.True(Planner.SubwayUnder(c, x0, cy + 1, station.Width, station.Height));
        Assert.False(Planner.SubwayUnder(c, x0, cy + 4, station.Width, station.Height));
        Assert.Contains("subways red", Underground.Legend(c.Stats));
    }

    [Fact]
    public void TheHillGatesSubwayAndStationsAreBuilt()
    {
        // As the Godot hill run does it: the hill works, then a tunnel under the town and a station near each end.
        var w = World.Create(ContentDb.LoadDefault(), HillTests.HillSeed, 128, "Hill Town");
        Assert.True(StarterTown.Apply(w));
        w.Step();
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        foreach (var s in HillSpots.Find(c, w.Funds)) w.Enqueue(Mapping.ToSim(new ToyCity.Protocol.V0.Action { Path = s.Path })!);
        w.Step();
        c.ApplyFull(Mapping.FullState(w, 0));
        var built = c.Buildings.Values.Where(b => !c.IsRoadside(b)).ToList();
        float tx = built.Average(b => b.X + b.Width / 2f), ty = built.Average(b => b.Y + b.Height / 2f);
        int sy = (int)ty, sx0 = Math.Max(2, (int)tx - 12), sx1 = Math.Min(c.Width - 3, (int)tx + 12);
        w.Enqueue(Mapping.ToSim(new ToyCity.Protocol.V0.Action { Utility = new PlaceUtility { X0 = sx0, Y0 = sy, X1 = sx1, Y1 = sy, Kind = ToyCity.Protocol.V0.UtilityKind.UtilitySubway } })!);
        w.Step();
        c.ApplyFull(Mapping.FullState(w, 0));
        Assert.Equal(sx1 - sx0 + 1, c.Subways.Count(b => b != 0));
        var st = c.TypeById["subway-station"];
        int placed = 0;
        foreach (var endX in new[] { sx0 + 1, sx1 - 1 })
            foreach (var (px, py) in Underground.StationSites(c, st.Width, st.Height, endX, sy).Take(8))
            {
                w.Enqueue(Mapping.ToSim(new ToyCity.Protocol.V0.Action { Building = new PlaceBuilding { X = px, Y = py, TypeId = st.Id } })!);
                w.Step();
                c.ApplyFull(Mapping.FullState(w, 0));
                if (c.BuildingAt[c.Idx(px, py)] != 0) { placed++; break; }
            }
        Assert.Equal(2, placed);
        Assert.Single(Underground.SubwayRuns(c));
    }
}
