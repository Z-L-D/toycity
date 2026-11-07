using ToyCity.ClientModel.Tools;
using ToyCity.Protocol;
using ToyCity.Sim;

namespace ToyCity.ClientModel.Tests;

/// <summary>RC part 4: the path designer's model, designs reaching the client, in the build menu and drawn from their strips.</summary>
public class PathDesignerTests
{
    static readonly ContentDb Content = ContentDb.LoadDefault();

    static (World W, CityMirror C, DeltaTracker T) City()
    {
        var w = World.Create(Content, 42);
        w.Funds = 5_000_000;
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        var t = new DeltaTracker();
        t.Reset(w);
        return (w, c, t);
    }

    static void Step(World w, CityMirror c, DeltaTracker t)
    {
        w.Step();
        c.ApplyDelta(t.Next(w, 0, Array.Empty<SimNotice>()));
    }

    [Fact]
    public void LayingOutAPathShowsWhatTheCityWillMakeOfIt()
    {
        var d = new PathDesigner();
        // It starts as a plain street, two lanes between sidewalks.
        Assert.Equal(new[] { "walk", "car", "car", "walk" }, d.Strips.Select(s => s.Type));
        Assert.Null(d.Problem);
        Assert.Equal(("road", 50, "cars, bikes and people"), (d.Stats!.Kind, d.Stats.SpeedKmh, d.Stats.Users));
        // Trees and a median make a boulevard at 40.
        d.Select(0);
        Assert.True(d.Add("planter"));
        Assert.Equal(1, d.Selected);
        d.Select(2);
        Assert.True(d.Add("median"));
        Assert.Equal(new[] { "walk", "planter", "car", "median", "car", "walk" }, d.Strips.Select(s => s.Type));
        Assert.Equal(("boulevard", 40), (d.Stats!.Kind, d.Stats.SpeedKmh));
        // Widening keeps to the strip's widths, a quarter metre at a time.
        int v = d.Version;
        Assert.True(d.Widen(PathDesigner.Step));
        Assert.Equal(2.25, d.Strips[3].WidthM);
        Assert.True(d.Version > v);
        for (int k = 0; k < 40; k++) d.Widen(PathDesigner.Step);
        Assert.Equal(6, d.Strips[3].WidthM);
        Assert.False(d.Widen(PathDesigner.Step));
        // Moving and taking out.
        Assert.True(d.Move(1));
        Assert.Equal(("median", 4), (d.Strips[4].Type, d.Selected));
        Assert.False(d.Move(2));
        Assert.True(d.Remove());
        Assert.DoesNotContain(d.Strips, s => s.Type == "median");
        // Something it can't be says why, and has no numbers.
        d.New();
        d.Select(1); d.Remove(); d.Select(1); d.Remove();
        d.Select(0);
        d.Add("turn");
        Assert.Contains("turn lane", d.Problem);
        Assert.Null(d.Stats);
        // One way needs drive lanes; with them it carries more.
        d.New();
        int two = d.Stats!.CapacityPerHour;
        d.SetOneWay(true);
        Assert.True(d.Stats!.CapacityPerHour > two && d.Stats.OneWay);
        // At most 16 strips.
        d.New();
        while (d.Add("walk")) { }
        Assert.Equal(PathDesigner.MaxStrips, d.Strips.Count);
    }

    [Fact]
    public void ADesignStartsFromABuiltInTypeAndSavesAsAnAction()
    {
        var (_, c, _) = City();
        var d = new PathDesigner();
        var boulevard = c.Profiles.Single(p => p.Id == "boulevard-2");
        Assert.True(PathDesigner.CanStartFrom(boulevard));
        Assert.False(PathDesigner.CanStartFrom(c.Profiles.Single(p => p.Rail)));
        d.StartFrom(boulevard);
        Assert.Equal(("", $"My {boulevard.Name.ToLowerInvariant()}"), (d.Id, d.Name));
        Assert.Equal(boulevard.Strips.Select(s => s.Type), d.Strips.Select(s => s.Type));
        // Built the same, it comes out about the same.
        Assert.Equal(boulevard.SpeedKmh, d.Stats!.SpeedKmh);
        Assert.Equal(boulevard.CapacityPerHour, d.Stats.CapacityPerHour);
        var a = d.Save();
        Assert.Equal(Protocol.V0.Action.KindOneofCase.DesignPath, a.KindCase);
        Assert.Equal(boulevard.Strips.Count, a.DesignPath.Strips.Count);
        Assert.Equal("", a.DesignPath.Id);
        Assert.Equal("x", PathDesigner.Delete("x").DeletePathDesign.Id);
    }

    [Fact]
    public void DesignsReachTheClientAndItsBuildMenuAndAreDrawnFromTheirStrips()
    {
        var (w, c, t) = City();
        int version = c.ProfilesVersion, before = c.Profiles.Count;
        var menu = BuildMenu.For(c);
        var mine = menu.Single(m => m.Id == "transport").Groups.Single(g => g.Name == "Your designs");
        // Before there are any, only the designer.
        Assert.Equal("path-designer", Assert.Single(mine.Items).Opens);

        var d = new PathDesigner();
        d.Select(0);
        d.Add("planter");
        d.Add("bike");
        d.Select(5);
        d.Add("bike");
        d.Add("planter");
        d.Name = "Leafy street";
        w.Enqueue(Mapping.ToSim(d.Save())!);
        Step(w, c, t);
        Assert.True(c.ProfilesVersion > version);
        Assert.Equal(before + 1, c.Profiles.Count);
        var p = c.Profiles[^1];
        Assert.Equal(("design-1", "Leafy street", true), (p.Id, p.Name, p.Custom));
        Assert.Equal(d.Strips.Select(s => s.Type), p.Strips.Select(s => s.Type));
        Assert.Equal(d.Stats!.SpeedKmh, p.SpeedKmh);
        Assert.Equal(d.Stats.CapacityPerHour, p.CapacityPerHour);

        // In the menu under Your designs, and only there.
        menu = BuildMenu.For(c);
        var items = menu.SelectMany(m => m.Groups).SelectMany(g => g.Items).ToList();
        Assert.Single(items, i => i.Kind == ToolKind.Path && i.Profile == "design-1");
        mine = menu.Single(m => m.Id == "transport").Groups.Single(g => g.Name == "Your designs");
        Assert.Equal(new[] { "Leafy street", "Design a path…" }, mine.Items.Select(i => i.Name));
        // Picking the designer's tile doesn't change the tool.
        var tool = new ToolState { Kind = ToolKind.Bulldoze };
        Assert.True(new BuildMenu().Pick(mine.Items[1], tool));
        Assert.Equal(ToolKind.Bulldoze, tool.Kind);
        Assert.False(BuildMenu.IsCurrent(mine.Items[1], new ToolState { Kind = ToolKind.Query }));

        // Drawn from its strips: sidewalks, two lanes, a bike lane each side.
        var look = RoadLook.For(p, null);
        Assert.True(look.Sidewalks);
        Assert.Equal((2, 2), (look.Lanes, look.BikeLanes));
        Assert.Equal(1.8f, look.BikeM, 2);
        Assert.InRange(look.CarriagewayM, 10, 13);
        // A built-in road with bike lanes paints them too; one without doesn't.
        Assert.Equal(2, RoadLook.For(c.Profiles.Single(q => q.Id == "road-unprotected-bike"), null).BikeLanes);
        Assert.Equal(0, RoadLook.For(c.Profiles.Single(q => q.Id == "road"), null).BikeLanes);

        // Built, its cells are the design's; deleted once bulldozed, the list comes again without it.
        int y = Enumerable.Range(20, 80).First(yy => Enumerable.Range(30, 11).All(x => w.Path[yy * w.Width + x] == 0 && w.BuildingAt[yy * w.Width + x] == 0 && !w.IsWater(yy * w.Width + x)));
        w.Enqueue(new PlacePathAction(30, y, 40, y, "design-1"));
        Step(w, c, t);
        Assert.Equal("design-1", c.ProfileAt(c.Idx(35, y))!.Id);
        w.Enqueue(new BulldozeAction(new CellRect(30, y, 40, y)));
        w.Enqueue(new DeletePathDesignAction("design-1"));
        Step(w, c, t);
        Assert.Equal(before, c.Profiles.Count);
        Assert.DoesNotContain(c.Profiles, q => q.Custom);
    }
}
