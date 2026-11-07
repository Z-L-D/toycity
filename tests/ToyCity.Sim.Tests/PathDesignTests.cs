using ToyCity.Paths;
using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>RC part 4: the path designer. Path types laid out from strips, saved with the city, built like any other.</summary>
public class PathDesignTests(ITestOutputHelper output)
{
    static World City(ulong seed = 42)
    {
        var w = World.Create(Fixture.Content, seed);
        w.Funds = 5_000_000;
        return w;
    }

    static List<PathStrip> S(params (string T, double W)[] s) => s.Select(x => new PathStrip(x.T, x.W)).ToList();

    static readonly List<PathStrip> Boulevard = S(("walk", 2.5), ("planter", 1.5), ("bike", 1.8), ("car", 3.5), ("median", 2), ("car", 3.5), ("bike", 1.8), ("planter", 1.5), ("walk", 2.5));

    /// <summary>A straight run along a row of open land, from x0 to x1.</summary>
    static int OpenRow(World w, int x0, int x1)
    {
        for (int y = 20; y < w.HeightCells - 20; y++)
        {
            bool open = true;
            for (int x = x0; x <= x1 && open; x++) { int i = y * w.Width + x; open = w.Path[i] == 0 && w.BuildingAt[i] == 0 && !w.IsWater(i); }
            if (open) return y;
        }
        throw new InvalidOperationException("no open row");
    }

    [Fact]
    public void ADesignLaidOutLikeABuiltInTypeComesOutCloseToIt()
    {
        // The rules are fitted to the built-in types: each one's strips make about its own numbers.
        var off = new List<string>();
        foreach (var p in Fixture.Content.Profiles.Where(p => !p.Rail && p.Strips != null))
        {
            var strips = p.Strips!.Select(s => (s.Type, s.WidthM)).ToList();
            Assert.Null(PathRules.Check(strips, p.OneWay));
            var d = PathRules.Derive(strips, p.OneWay);
            output.WriteLine($"{p.Id,-22} speed {d.SpeedKmh,3}/{p.SpeedKmh,-3} capacity {d.CapacityPerHour,5}/{p.CapacityPerHour,-5} cost {d.CostPerCell,3}/{p.CostPerCell,-3} {d.Kind}");
            Assert.Equal(p.Cars, d.Cars);
            Assert.Equal(p.Pedestrians, d.Pedestrians);
            Assert.Equal(p.Frontage, d.Frontage);
            Assert.Equal(p.OneWay, d.OneWay);
            Assert.Equal(p.Tram ?? "", d.Tram);
            if (d.SpeedKmh < p.SpeedKmh * 0.6 || d.SpeedKmh > p.SpeedKmh * 1.4) off.Add($"{p.Id} speed");
            if (d.CapacityPerHour < p.CapacityPerHour * 0.5 || d.CapacityPerHour > p.CapacityPerHour * 1.6) off.Add($"{p.Id} capacity");
            if (d.CostPerCell < p.CostPerCell * 0.4 || d.CostPerCell > p.CostPerCell * 2.5) off.Add($"{p.Id} cost");
        }
        Assert.Empty(off);
    }

    [Fact]
    public void TheRulesFollowFromTheStrips()
    {
        PathSummary D(bool oneWay, params (string, double)[] s) => PathRules.Derive(s.ToList(), oneWay);
        // A stroad: three lanes or more with nothing to slow it is 60; trees or a bike lane bring it to 50.
        Assert.Equal(60, D(false, ("walk", 2), ("car", 3.5), ("car", 3.5), ("car", 3.5), ("walk", 2)).SpeedKmh);
        Assert.Equal(50, D(false, ("walk", 2), ("planter", 1.5), ("car", 3.5), ("car", 3.5), ("car", 3.5), ("walk", 2)).SpeedKmh);
        // Narrow lanes or trams in the traffic, 30.
        Assert.Equal(30, D(false, ("walk", 2), ("car", 3), ("car", 3), ("walk", 2)).SpeedKmh);
        Assert.Equal(30, D(false, ("walk", 2), ("car-tram", 4), ("car-tram", 4), ("walk", 2)).SpeedKmh);
        // Shoulders and no sidewalk: a highway, no frontage, faster with more lanes, and twice the capacity a lane.
        var hw = D(false, ("shoulder", 2), ("car", 3.7), ("car", 3.7), ("car", 3.7), ("car", 3.7), ("shoulder", 2));
        Assert.Equal(("highway", 100, false, 4800), (hw.Kind, hw.SpeedKmh, hw.Frontage, hw.CapacityPerHour));
        // One way carries more; a turn lane more again.
        int two = D(false, ("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2)).CapacityPerHour;
        int one = D(true, ("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2)).CapacityPerHour;
        int turn = D(false, ("walk", 2), ("car", 3.5), ("turn", 3.5), ("car", 3.5), ("walk", 2)).CapacityPerHour;
        Assert.True(one > two && turn > two, $"{two} {one} {turn}");
        // Paths: people by the metre of sidewalk, bikes by the lane.
        var path = D(false, ("walk", 3));
        Assert.Equal((false, true, 1200, 5), (path.Cars, path.Pedestrians, path.CapacityPerHour, path.SpeedKmh));
        Assert.Equal("bike path", D(false, ("bike", 2), ("bike", 2)).Kind);
        // Trees make it quieter, and cost more.
        var bare = D(false, ("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2));
        var trees = D(false, ("walk", 2), ("planter", 1.5), ("car", 3.5), ("car", 3.5), ("planter", 1.5), ("walk", 2));
        Assert.True(trees.NoiseDb < bare.NoiseDb && trees.CostPerCell > bare.CostPerCell);
        // A boulevard with a median and bike lanes.
        var b = PathRules.Derive(Boulevard.Select(s => (s.Type, s.WidthM)).ToList(), false);
        Assert.Equal(("boulevard", 40, true, true), (b.Kind, b.SpeedKmh, b.Bikes, b.Pedestrians));
    }

    [Fact]
    public void WhatCantBeBuiltIsSaidWhy()
    {
        string? C(bool oneWay, params (string, double)[] s) => PathRules.Check(s.ToList(), oneWay);
        Assert.Contains("at least one", C(false));
        Assert.Contains("isn't a kind", C(false, ("moat", 3)));
        Assert.Contains("1.5 to 6 m", C(false, ("walk", 0.5)));
        Assert.Contains("2 to 40 m", C(false, ("planter", 1)));
        Assert.Contains("turn lane", C(false, ("walk", 2), ("turn", 3.5), ("walk", 2)));
        Assert.Contains("parking", C(false, ("walk", 2), ("parking", 2.4)));
        Assert.Contains("one way", C(true, ("walk", 3)));
        Assert.Contains("shared lane", C(false, ("shared", 6), ("car", 3.5)));
        Assert.Contains("at most 16", C(false, Enumerable.Repeat(("walk", 2.0), 17).ToArray()));
        Assert.Null(C(false, ("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2)));
    }

    [Fact]
    public void ADesignIsBuiltLikeAnyOtherPathType()
    {
        var w = City();
        int before = w.Content.Profiles.Count, version = w.ProfilesVersion;
        w.Enqueue(new DesignPathAction("", "Tree-lined boulevard", Boulevard));
        w.Step();
        Assert.Equal(before + 1, w.Content.Profiles.Count);
        Assert.True(w.ProfilesVersion > version);
        var p = w.Content.Profiles[^1];
        Assert.Equal(("design-1", "Tree-lined boulevard", true, 40), (p.Id, p.Name, p.Custom, p.SpeedKmh));
        Assert.Contains(w.Notices, n => n.Text.Contains("Designed the Tree-lined boulevard"));
        // It's this city's alone: the shared content and other cities don't have it.
        Assert.Equal(-1, Fixture.Content.ProfileIndex("design-1"));
        Assert.Equal(-1, City(7).Content.ProfileIndex("design-1"));

        int y = OpenRow(w, 30, 50);
        long funds = w.Funds;
        w.Enqueue(new PlacePathAction(30, y, 50, y, "design-1"));
        w.Step();
        int at = w.Content.ProfileIndex("design-1") + 1, cells = w.Path.Count(c => c == at);
        Assert.Equal(21, cells);
        Assert.True(funds - w.Funds >= 21L * p.CostPerCell, $"spent {funds - w.Funds}");
        // It runs like a path: the city keeps stepping with it.
        w.Run(30);
    }

    [Fact]
    public void RedesigningRebuildsWhatsBuiltAndChargesTheDifference()
    {
        var w = City();
        w.Enqueue(new DesignPathAction("", "Plain", S(("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2))));
        w.Step();
        int y = OpenRow(w, 30, 49);
        w.Enqueue(new PlacePathAction(30, y, 49, y, "design-1"));
        w.Step();
        int cost = w.Content.Profiles[^1].CostPerCell;
        long funds = w.Funds;
        w.Enqueue(new DesignPathAction("design-1", "", Boulevard));
        w.Step();
        var p = w.Content.Profiles[^1];
        Assert.Equal(("design-1", "Plain", 40), (p.Id, p.Name, p.SpeedKmh));
        Assert.Equal(20L * (p.CostPerCell - cost), funds - w.Funds);
        Assert.Contains(w.Notices, n => n.Text.Contains("rebuilt its 20 cells"));
        // Built-in types can't be redesigned, or deleted.
        w.Enqueue(new DesignPathAction("road", "Mine now", Boulevard));
        w.Enqueue(new DeletePathDesignAction("road"));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("can't be redesigned"));
        Assert.Contains(w.Notices, n => n.Text.Contains("can't be deleted"));
        // Nor can a design that won't build.
        w.Enqueue(new DesignPathAction("design-1", "", S(("turn", 3.5))));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("turn lane"));
        Assert.Equal(40, w.Content.Profiles[^1].SpeedKmh);
    }

    [Fact]
    public void ADesignInUseCantBeDeletedAndTheOnesAfterMoveUp()
    {
        var w = City();
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            w.Enqueue(new DesignPathAction("", name, S(("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2))));
            w.Step();
        }
        int y = OpenRow(w, 30, 40);
        w.Enqueue(new PlacePathAction(30, y, 40, y, "design-3"));
        w.Step();
        w.Enqueue(new DeletePathDesignAction("design-3"));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("is built on 11 cells"));
        // Deleting the first moves the third up the list, and its cells with it.
        w.Enqueue(new DeletePathDesignAction("design-1"));
        w.Step();
        Assert.Equal(-1, w.Content.ProfileIndex("design-1"));
        int at = w.Content.ProfileIndex("design-3") + 1;
        Assert.Equal(w.Content.BuiltInProfiles + 2, at);
        Assert.Equal(11, w.Path.Count(c => c == at));
        Assert.Equal("Three", w.Content.Profiles[at - 1].Name);
        // A new design takes the first free id.
        w.Enqueue(new DesignPathAction("", "", S(("walk", 3))));
        w.Step();
        Assert.Equal(("design-1", "Design 1"), (w.Content.Profiles[^1].Id, w.Content.Profiles[^1].Name));
    }

    [Fact]
    public void DesignsAreSavedWithTheCityAndReplay()
    {
        var w = City();
        w.Enqueue(new DesignPathAction("", "Tree-lined boulevard", Boulevard));
        w.Enqueue(new DesignPathAction("", "Lane", S(("shared", 5))));
        w.Step();
        int y = OpenRow(w, 30, 50);
        w.Enqueue(new PlacePathAction(30, y, 50, y, "design-2"));
        w.Run(20);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), back.StateHash());
        Assert.Equal(new[] { "design-1", "design-2" }, back.PathDesigns.Select(p => p.Id));
        Assert.Equal(Boulevard, back.PathDesigns.First().Strips);
        Assert.Equal(w.Content.Profiles[^1].SpeedKmh, back.Content.Profiles[^1].SpeedKmh);
        // Both carry on the same.
        w.Run(30);
        back.Run(30);
        Assert.Equal(w.StateHash(), back.StateHash());
        // And the log replays to the same city (from the same start, its funds topped up).
        var replay = World.Replay(City(), w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
    }

    [Fact]
    public void ACityAbsorbingAnotherTakesItsDesigns()
    {
        var a = City(1);
        var b = City(2);
        a.Enqueue(new DesignPathAction("", "Ours", S(("walk", 2), ("car", 3.5), ("car", 3.5), ("walk", 2))));
        a.Step();
        b.Enqueue(new DesignPathAction("", "Theirs", Boulevard));
        b.Step();
        int y = OpenRow(b, 80, 100);
        b.Enqueue(new PlacePathAction(80, y, 100, y, "design-1"));
        b.Step();
        var m = a.Merged(b, 64, 0, 64, 128);
        // Their design-1 is taken by ours, so theirs comes under a new id, and its cells point at it.
        Assert.Equal(new[] { ("design-1", "Ours"), ("design-2", "Theirs") }, m.PathDesigns.Select(p => (p.Id, p.Name)));
        int at = m.Content.ProfileIndex("design-2") + 1;
        Assert.Equal(21, Enumerable.Range(80, 21).Count(x => m.Path[y * m.Width + x] == at));
    }

    [Fact]
    public void ACityKeepsAtMostSoManyDesigns()
    {
        var w = City();
        for (int k = 0; k <= World.MaxPathDesigns; k++) w.Enqueue(new DesignPathAction("", "", S(("walk", 2))));
        w.Step();
        Assert.Equal(World.MaxPathDesigns, w.PathDesigns.Count());
        Assert.Contains(w.Notices, n => n.Text.Contains($"at most {World.MaxPathDesigns}"));
    }
}
