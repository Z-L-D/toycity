using Google.Protobuf;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol;
using ToyCity.Protocol.V0;
using ToyCity.Sim;

namespace ToyCity.ClientModel.Tests;

/// <summary>The map tools' previews: they agree with what the server will accept, and placement snaps unless held free.</summary>
public class PlannerTests
{
    static (World W, CityMirror C) Town(ulong seed = 501)
    {
        var w = World.Create(ContentDb.LoadDefault(), seed, 128, "Toolton");
        Assert.True(StarterTown.Apply(w));
        w.Run(60);  // transit unlocks
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        return (w, c);
    }

    [Fact]
    public void LinesAndCurvesAreLaidCellForCellAsTheServerLaysThem()
    {
        var r = new Random(3);
        for (int k = 0; k < 300; k++)
        {
            int x0 = r.Next(40), y0 = r.Next(40), cx = r.Next(40), cy = r.Next(40), x1 = r.Next(40), y1 = r.Next(40);
            Assert.Equal(World.CurveCells(x0, y0, cx, cy, x1, y1), Planner.CurveCells(x0, y0, cx, cy, x1, y1));
            Assert.Equal(World.LCells(x0, y0, x1, y1), Planner.LCells(x0, y0, x1, y1));
            Assert.Equal(World.LineCells(x0, y0, x1, y1), Planner.LineCells(x0, y0, x1, y1));
        }
    }

    [Fact]
    public void ADiagonalIsPreviewedBuiltAndDrawnAsOneSmoothRoad()
    {
        var (w, c) = Town();
        var t = new ToolState { Kind = ToolKind.Path, ProfileId = "road", Diagonal = true };
        // Find open, gentle ground for a 10 x 6 diagonal.
        var plan = Enumerable.Range(0, 100).SelectMany(y => Enumerable.Range(0, 100).Select(x => (x, y)))
            .Select(p => Planner.Plan(c, t, new Pointer(p.x + 10, p.y + 6, DragStart: (p.x, p.y)))!)
            .First(p => p.Ok && p.Cells.All(q => c.Paths[c.Idx(q.X, q.Y)] == 0));
        var path = plan.Actions.Single().Path;
        Assert.True(path.Diagonal);
        Assert.Equal(17, plan.Cells.Count);
        var tracker = new DeltaTracker();
        tracker.Reset(w);
        w.Enqueue(Mapping.Action(new ActionRecord { Tick = w.Tick, Action = plan.Actions[0] })!);
        w.Step();
        Assert.DoesNotContain(w.Notices, n => n.Severity == "error");
        c.ApplyDelta(tracker.Next(w, 0, Array.Empty<SimNotice>()));

        var s = Assert.Single(c.Strips);
        Assert.Equal(17, s.Cells.Count);
        // Its ends stay road cells, to meet the streets; the cells between are drawn by the strip.
        Assert.False(RoadShape.UnderStrip(c, s.Cells[0]));
        Assert.False(RoadShape.UnderStrip(c, s.Cells[^1]));
        Assert.All(s.Cells.Skip(1).SkipLast(1), i => Assert.True(RoadShape.UnderStrip(c, i)));
        // The line runs from the centre of one end cell to the other's, straight.
        var pts = RoadShape.Points(s);
        Assert.Equal((path.X0 + 0.5f, path.Y0 + 0.5f), pts[0]);
        Assert.Equal((path.X1 + 0.5f, path.Y1 + 0.5f), pts[^1]);
        Assert.All(pts, p => Assert.Equal(0, (p.X - pts[0].X) * 6 - (p.Y - pts[0].Y) * 10, 2));
        // A full state brings the same strips.
        var again = new CityMirror();
        again.ApplyFull(Mapping.FullState(w, 0));
        Assert.Equal(c.StripCells, again.StripCells);
    }

    [Fact]
    public void ARoadDrawnToTheBorderSnapsOntoTheNeighboursRoad()
    {
        var (_, c) = Town();
        c.Borders = new[] { new ToyCity.Protocol.V0.NeighbourEdge { Side = 1, Name = "Eastfield", Paths = { 40, 1, 90, 1 } } };
        Assert.Equal((127, 40), Planner.BorderSnap(c, 126, 42));   // near the edge, two cells off along it
        Assert.Equal((125, 44), Planner.BorderSnap(c, 125, 44));   // four cells along: too far
        Assert.Equal((100, 40), Planner.BorderSnap(c, 100, 40));   // nowhere near the edge
        Assert.Equal((0, 40), Planner.BorderSnap(c, 0, 40));       // no neighbour to the west
    }

    [Fact]
    public void ARunSnapsStraightUnlessHeldFree()
    {
        Assert.Equal((9, 2), Planner.SnapEnd((2, 2), 9, 4, free: false));
        Assert.Equal((2, 10), Planner.SnapEnd((2, 2), 4, 10, free: false));
        Assert.Equal((9, 4), Planner.SnapEnd((2, 2), 9, 4, free: true));

        var (_, c) = Town();
        var t = new ToolState { Kind = ToolKind.Path, ProfileId = "road" };
        var snapped = Planner.Plan(c, t, new Pointer(9, 4, DragStart: (2, 2)))!;
        Assert.All(snapped.Cells, cell => Assert.Equal(2, cell.Y));
        var free = Planner.Plan(c, t, new Pointer(9, 4, DragStart: (2, 2), Free: true, VerticalFirst: true))!;
        Assert.Equal(2, free.Actions.Count);  // vertical leg, then horizontal
        Assert.Equal((2, 2, 2, 4), (free.Actions[0].Path.X0, free.Actions[0].Path.Y0, free.Actions[0].Path.X1, free.Actions[0].Path.Y1));
        Assert.Contains(free.Cells, cell => (cell.X, cell.Y) == (9, 4));
    }

    [Fact]
    public void ABuildingSnapsToFaceARoadUnlessHeldFree()
    {
        var (_, c) = Town();
        var type = c.TypeById["police-station"];
        int snappedToRoad = 0, tried = 0;
        for (int y = 2; y < c.Height - 6; y++)
            for (int x = 2; x < c.Width - 6; x++)
            {
                var p = new Pointer(x, y);
                var at = Planner.Footprint(c, type, p);
                Assert.True(Math.Abs(at.X - (x - (type.Width - 1) / 2)) <= 2 && Math.Abs(at.Y - (y - (type.Height - 1) / 2)) <= 2);
                Assert.Equal((x - (type.Width - 1) / 2, y - (type.Height - 1) / 2), Planner.Footprint(c, type, p with { Free = true }));
                // Near a road, the snapped spot is clear and faces it.
                var plan = Planner.Plan(c, new ToolState { Kind = ToolKind.Building, BuildingTypeId = type.Id }, p)!;
                if (plan.Ok && !plan.Text.Contains("no road")) snappedToRoad++;
                tried++;
            }
        Assert.True(snappedToRoad > 0 && snappedToRoad < tried);
    }

    [Fact]
    public void WhereTheServerWouldPlaceABuildingThePreviewSaysSo()
    {
        var (w, c) = Town();
        foreach (var id in new[] { "police-station", "clinic", "small-park" })
        {
            var t = new ToolState { Kind = ToolKind.Building, BuildingTypeId = id };
            int agree = 0;
            for (int y = 0; y < c.Height; y += 2)
                for (int x = 0; x < c.Width; x += 2)
                {
                    var plan = Planner.Plan(c, t, new Pointer(x, y, Free: true))!;
                    var a = (PlaceBuildingAction)Mapping.ToSim(plan.Actions[0])!;
                    bool server = w.ValidatePlacement(a, out _) == null;
                    // The server also checks money, slope and unlocks; it never accepts what the preview refuses.
                    Assert.False(server && !plan.Ok, $"{id} at {x},{y}: the server would place it but the preview says no");
                    if (server == plan.Ok) agree++;
                }
            Assert.True(agree > 0);
        }
    }

    [Fact]
    public void RoadsideStopsGoOnTheKerbTheServerWouldUse()
    {
        var (w, c) = Town();
        var type = c.TypeById["bus-stop"];
        var t = new ToolState { Kind = ToolKind.Building, BuildingTypeId = type.Id };
        int placed = 0;
        for (int y = 1; y < c.Height - 1; y++)
            for (int x = 1; x < c.Width - 1; x++)
            {
                if (c.Paths[c.Idx(x, y)] == 0 && c.Paths[c.Idx(x, y - 1)] == 0 && c.Paths[c.Idx(x - 1, y)] == 0) continue;
                foreach (var (fx, fy) in new[] { (0.1f, 0.5f), (0.9f, 0.5f), (0.5f, 0.1f), (0.5f, 0.9f) })
                {
                    var plan = Planner.Plan(c, t, new Pointer(x, y, fx, fy))!;
                    if (!plan.Ok) continue;
                    var a = (PlaceBuildingAction)Mapping.ToSim(plan.Actions[0])!;
                    Assert.Null(w.ValidatePlacement(a, out _));
                    Assert.Equal(plan.Side, a.Side);
                    placed++;
                }
            }
        Assert.True(placed > 20, $"{placed}");
    }

    [Fact]
    public void ARoadPreviewCostsWhatTheServerCharges()
    {
        var (w, c) = Town();
        var r = new Random(9);
        int compared = 0;
        for (int k = 0; k < 400 && compared < 25; k++)
        {
            int x0 = r.Next(10, 118), y0 = r.Next(10, 118);
            var plan = Planner.Plan(c, new ToolState { Kind = ToolKind.Path, ProfileId = "road" }, new Pointer(x0 + r.Next(-8, 9), y0 + r.Next(-8, 9), DragStart: (x0, y0)))!;
            var a = (PlacePathAction)Mapping.ToSim(plan.Actions[0])!;
            bool server = w.ValidatePath(a, out long cost, out _, out _) == null;
            Assert.False(server && !plan.Ok, "the server would build a road the preview refuses");
            if (!server || !plan.Ok) continue;
            Assert.EndsWith(Planner.Money(cost), plan.Text);
            compared++;
        }
        Assert.True(compared >= 10);
    }

    [Fact]
    public void ZoningAWholeBlockPreviewsItsLots()
    {
        var (w, c) = Town();
        var t = new ToolState { Kind = ToolKind.Zone, BlockZoning = true, Zone = ZoneKind.ZoneCommercial, Density = Density.Medium };
        for (int i = 0; i < c.N; i++)
        {
            int x = i % c.Width, y = i / c.Width;
            var plan = Planner.Plan(c, t, new Pointer(x, y));
            if (plan == null || !plan.Ok) continue;
            Assert.Equal(w.BlockAt(x, y)!.Count, plan.Cells.Count);
            if (!plan.Cells.Any(cell => cell.Tint == Tint.Zone)) continue;  // no road with frontage reaches this one
            Assert.Equal(ZoneKind.ZoneCommercial, plan.Actions[0].Block.Zone);
            return;
        }
        Assert.Fail("no block in the starter town");
    }
}
