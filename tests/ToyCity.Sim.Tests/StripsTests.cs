using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

/// <summary>Part 9: diagonals and arcs remembered as drawn, and the Smooth brush.</summary>
public class StripsTests
{
    static int Road => Fixture.Content.ProfileIndex("road") + 1;

    [Fact]
    public void ADiagonalIsLaidEdgeToEdgeAlongTheLineDrawn()
    {
        var r = new Random(9);
        for (int k = 0; k < 300; k++)
        {
            int x0 = r.Next(40), y0 = r.Next(40), x1 = r.Next(40), y1 = r.Next(40);
            var cells = World.LineCells(x0, y0, x1, y1);
            Assert.Equal((x0, y0), cells[0]);
            Assert.Equal((x1, y1), cells[^1]);
            Assert.Equal(Math.Abs(x1 - x0) + Math.Abs(y1 - y0) + 1, cells.Count);
            for (int j = 1; j < cells.Count; j++)
                Assert.Equal(1, Math.Abs(cells[j].X - cells[j - 1].X) + Math.Abs(cells[j].Y - cells[j - 1].Y));
            // Every cell stays within a cell of the true line.
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (len > 0)
                Assert.All(cells, c => Assert.True(Math.Abs((c.X - x0) * (y1 - y0) - (c.Y - y0) * (x1 - x0)) / len <= 1.0));
        }
    }

    [Fact]
    public void DiagonalsAndArcsAreRememberedSavedAndReplayed()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x, y, x + 12, y + 7, "road", Diagonal: true));
        w.Enqueue(new PlacePathAction(x, y + 20, x + 12, y + 12, "road", Curved: true, CX: x + 12, CY: y + 20));
        w.Enqueue(new PlacePathAction(x + 20, y, x + 20, y + 10, "road", Diagonal: true));  // straight down: nothing to remember
        w.Enqueue(new PlacePathAction(x + 22, y, x + 28, y + 6, "road"));                   // an L: nothing to remember
        w.Step();
        Assert.Equal(2, w.Strips.Count);
        var d = w.Strips[0];
        Assert.False(d.Curved);
        Assert.Equal((x, y, x + 12, y + 7), (d.X0, d.Y0, d.X1, d.Y1));
        Assert.Equal(World.LineCells(x, y, x + 12, y + 7).Select(c => w.Idx(c.X, c.Y)), d.Cells);
        Assert.All(d.Cells, i => Assert.Equal(Road, w.Path[i]));
        Assert.True(w.Strips[1].Curved);

        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), back.StateHash());
        Assert.Equal(2, back.Strips.Count);
        var replayed = World.Replay(World.Create(Fixture.Content, w.Seed, 128, w.Name), w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replayed.StateHash());
        // Over the wire too.
        var full = Mapping.FullState(w, 0);
        Assert.Equal(2, full.Strips.Count);
        Assert.Equal(d.Cells, full.Strips[0].Cells);
    }

    [Fact]
    public void AStripBrokenOrLaidOverIsForgottenAndItsCellsStayRoad()
    {
        var (w, x, y) = Fixture.FlatSite();
        w.Enqueue(new PlacePathAction(x, y, x + 10, y + 10, "road", Diagonal: true));
        w.Enqueue(new PlacePathAction(x, y + 15, x + 10, y + 25, "road", Diagonal: true));
        w.Step();
        Assert.Equal(2, w.Strips.Count);
        var tracker = new DeltaTracker();
        tracker.Reset(w);

        // Bulldozing a cell in the middle breaks the first; the rest of its cells stay road.
        var mid = w.Strips[0].Cells[10];
        w.Enqueue(new BulldozeAction(new CellRect(mid % w.Width, mid / w.Width, mid % w.Width, mid / w.Width)));
        w.Step();
        Assert.Single(w.Strips);
        Assert.Equal(0, w.Path[mid]);
        Assert.Equal(Road, w.Path[w.Idx(x, y)]);
        var delta = tracker.Next(w, 0, Array.Empty<SimNotice>());
        Assert.True(delta.StripsChanged);
        Assert.Single(delta.Strips);
        Assert.False(tracker.Next(w, 0, Array.Empty<SimNotice>()).StripsChanged);

        // Laying another kind of path over the second replaces it with a strip of that kind.
        w.Enqueue(new PlacePathAction(x, y + 15, x + 10, y + 25, "street", Diagonal: true));
        w.Step();
        Assert.Single(w.Strips);
        Assert.Equal(Fixture.Content.ProfileIndex("street") + 1, w.Strips[0].Code);
        // The same line laid again isn't remembered twice.
        w.Enqueue(new PlacePathAction(x, y + 15, x + 10, y + 25, "street", Diagonal: true));
        w.Step();
        Assert.Single(w.Strips);
    }

    [Fact]
    public void TheSmoothBrushWearsStepsDownIntoSlopes()
    {
        var (w, x, y) = Fixture.FlatSite();
        int flat = w.Height[w.Idx(x + 5, y + 5)];
        // A tower of earth three high in the middle of the flat.
        for (int k = 0; k < 3; k++) w.Enqueue(new TerraformAction(new CellRect(x + 5, y + 5, x + 5, y + 5), TerraformMode.Raise));
        w.Enqueue(new PlacePathAction(x + 2, y + 2, x + 8, y + 2, "road"));
        w.Step();
        Assert.Equal(flat + 3, w.Height[w.Idx(x + 5, y + 5)]);
        long funds = w.Funds;
        var before = (byte[])w.Height.Clone();
        w.Enqueue(new TerraformAction(new CellRect(x + 1, y + 1, x + 9, y + 9), TerraformMode.Smooth));
        w.Step();
        // The tower is worn down and nothing around it is raised past it; the road kept its ground.
        Assert.True(w.Height[w.Idx(x + 5, y + 5)] < flat + 3);
        Assert.True(w.Funds < funds);
        for (int xx = x + 2; xx <= x + 8; xx++) Assert.Equal(before[w.Idx(xx, y + 2)], w.Height[w.Idx(xx, y + 2)]);
        for (int yy = y + 1; yy <= y + 9; yy++)
            for (int xx = x + 1; xx <= x + 9; xx++)
                Assert.InRange(w.Height[w.Idx(xx, yy)], flat, flat + 2);
        // Smoothing flat ground does nothing and costs nothing.
        funds = w.Funds;
        w.Enqueue(new TerraformAction(new CellRect(x + 15, y + 15, x + 25, y + 25), TerraformMode.Smooth));
        w.Step();
        Assert.Equal(funds, w.Funds);
    }
}
