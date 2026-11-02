using ToyCity.ClientModel.Gate;
using ToyCity.Protocol;
using ToyCity.Sim;
using Xunit;
using Xunit.Abstractions;
using P = ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 3.2, underground: what the underground view lays out, on the hill gate's town.</summary>
public class UndergroundClientTests(ITestOutputHelper output)
{
    [Fact]
    public void TheHillTownsPipesTunnelAndANewSewagePlantAreLaidOut()
    {
        var w = World.Create(ContentDb.LoadDefault(), HillTests.HillSeed, 128, "Hill Town");
        Assert.True(StarterTown.Apply(w));
        w.Step();
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        foreach (var s in HillSpots.Find(c, w.Funds))
        {
            output.WriteLine($"{s.What}: {s.Path}");
            w.Enqueue(Mapping.ToSim(new P.Action { Path = s.Path })!);
        }
        w.Step();
        c.ApplyFull(Mapping.FullState(w, 0));

        // The gate's way of finding a site for a plant beside the pipes: the first that the server takes.
        var plant = c.TypeById["sewage-plant"];
        Assert.True(plant.SewageCap > 0);
        float tx = (float)c.Buildings.Values.Average(b => b.X), ty = (float)c.Buildings.Values.Average(b => b.Y);
        int tried = 0;
        foreach (var (x, y) in Underground.PlantSites(c, plant.Width, plant.Height, tx, ty).Take(8))
        {
            tried++;
            w.Enqueue(Mapping.ToSim(new P.Action { Building = new P.PlaceBuilding { X = x, Y = y, TypeId = plant.Id } })!);
            w.Step();
            if (w.BuildingAt[w.Idx(x, y)] != 0) { output.WriteLine($"plant at {x},{y}"); break; }
        }
        output.WriteLine($"placed after {tried} sites");
        // Two months on, the town has grown and its sewage goes to the plant.
        w.Run(60);
        c.ApplyFull(Mapping.FullState(w, 0));
        Assert.True(c.Stats.SewageProduced > 0);
        Assert.True(c.Stats.SewageCapacity > 0, Underground.Legend(c.Stats));
        Assert.Equal(0, c.Stats.SewageUntreated);
        Assert.Contains("all treated", Underground.Legend(c.Stats));

        // Every pipe cell is in a run, and the tunnel too; the pump and the plant are picked out.
        var pipes = Underground.PipeRuns(c);
        var covered = new HashSet<int>();
        foreach (var r in pipes)
            for (int y = r.Y0; y <= r.Y1; y++)
                for (int x = r.X0; x <= r.X1; x++) covered.Add(c.Idx(x, y));
        Assert.Equal(Enumerable.Range(0, c.Width * c.Height).Where(i => c.WaterPipes[i] != 0).ToHashSet(), covered);
        Assert.NotEmpty(Underground.TunnelRuns(c));
        Assert.All(Underground.TunnelRuns(c), r => Assert.True(r.X0 == r.X1 || r.Y0 == r.Y1));
        var works = Underground.Works(c).ToList();
        Assert.Contains(works, k => k.Plant);
        Assert.Contains(works, k => !k.Plant);
    }
}
