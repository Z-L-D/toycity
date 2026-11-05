using System.Text.Json;
using Google.Protobuf;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>The realistic asset set (RC part 2): a model for every type, variants, details and facing the road.</summary>
public class RealSetTests
{
    readonly Xunit.Abstractions.ITestOutputHelper _out;
    public RealSetTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    static JsonElement Content()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "content", "buildings.json"))) dir = dir.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "content", "buildings.json"))).RootElement;
    }

    /// <summary>Every type the game builds, with each lot size it can grow on.</summary>
    static IEnumerable<(string Id, int W, int D)> Lots()
    {
        var c = Content();
        var sizes = c.GetProperty("lotSizes");
        foreach (var t in c.GetProperty("zoned").EnumerateArray())
        {
            string id = t.GetProperty("id").GetString()!;
            foreach (var s in sizes.GetProperty(t.GetProperty("density").GetString()!).EnumerateArray())
                yield return (id, s[0].GetInt32(), s[1].GetInt32());
        }
        foreach (var t in c.GetProperty("civic").EnumerateArray())
            yield return (t.GetProperty("id").GetString()!, t.GetProperty("w").GetInt32(), t.GetProperty("h").GetInt32());
    }

    [Fact]
    public void EveryTypeButTheRoadsideStopsHasAModelOnEveryLotSize()
    {
        int made = 0;
        foreach (var (id, w, d) in Lots())
        {
            if (RealSet.Roadside.Contains(id)) { Assert.False(RealSet.Has(id), id); continue; }
            Assert.True(RealSet.Has(id), $"no model for {id}");
            for (int v = 0; v < RealSet.Variants; v++)
            {
                var r = RealSet.Build(id, w, d, v);
                Assert.NotNull(r);
                var m = r!.Model;
                // The model covers its lot exactly at its detail, in at most 256 voxels a side.
                Assert.Contains(r.Detail, AssetDoc.Details);
                Assert.Equal((w * VoxModel.PerCell * r.Detail, d * VoxModel.PerCell * r.Detail), (m.SizeX, m.SizeY));
                Assert.True(m.SizeZ <= 256, $"{id} {w}x{d} is {m.SizeZ} tall");
                Assert.True(m.Count > m.SizeX * m.SizeY, $"{id} {w}x{d} is little more than its ground");
                // It stands on the whole lot: the ground layer is covered.
                Assert.True(Enumerable.Range(0, m.SizeX).All(x => m[x, 0, 0] != 0 && m[x, m.SizeY - 1, 0] != 0), $"{id} {w}x{d} leaves bare ground");
                made++;
            }
        }
        _out.WriteLine($"{made} models");
        Assert.True(made > 150);
    }

    [Fact]
    public void DetailIsTheFinestThatFitsTheLotAndTheHeight()
    {
        Assert.Equal(8, RealSet.DetailFor(1, 1, 10));
        Assert.Equal(4, RealSet.DetailFor(1, 1, 10, 4));
        Assert.Equal(4, RealSet.DetailFor(2, 2, 20));
        Assert.Equal(2, RealSet.Build("r-m-l", 2, 2, 0)!.Detail);
        Assert.Equal(4, RealSet.Build("c-l-l", 1, 1, 0)!.Detail);
        Assert.Equal(2, RealSet.DetailFor(3, 3, 20));
        Assert.Equal(2, RealSet.DetailFor(4, 4, 20));
        // A tower too tall for its lot's detail is drawn coarser.
        Assert.Equal(2, RealSet.DetailFor(2, 2, 50));
        Assert.Equal(1, RealSet.DetailFor(2, 2, 100));
        // Houses are drawn at 6.25 cm voxels, like the editor's.
        Assert.Equal(8, RealSet.Build("r-l-m", 1, 1, 0)!.Detail);
    }

    [Fact]
    public void VariantsDifferAndAreSpreadOverBuildings()
    {
        foreach (string id in new[] { "r-l-l", "r-l-m", "r-l-h", "r-m-m", "c-l-l", "c-m-m", "i-l-m" })
        {
            var a = RealSet.Build(id, 1, 2, 0)!.Model.Voxels;
            var b = RealSet.Build(id, 1, 2, 1)!.Model.Voxels;
            var c = RealSet.Build(id, 1, 2, 2)!.Model.Voxels;
            Assert.False(a.SequenceEqual(b) && b.SequenceEqual(c), $"{id}'s variants are all the same");
        }
        // The same building always gets the same variant, and a street of them gets all three.
        Assert.Equal(RealSet.VariantOf(1234), RealSet.VariantOf(1234));
        var counts = Enumerable.Range(1, 300).GroupBy(RealSet.VariantOf).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(RealSet.Variants, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, 60, 140));
        // And making a model twice makes the same one.
        Assert.Equal(RealSet.Build("r-l-h", 1, 1, 2)!.Model.Voxels, RealSet.Build("r-l-h", 1, 1, 2)!.Model.Voxels);
    }

    [Fact]
    public void HousesAndPitchedBuildingsGetAnAutoRoofOverTheirWalls()
    {
        foreach (var (id, w, d) in new[] { ("r-l-l", 1, 1), ("r-l-m", 1, 1), ("r-l-h", 1, 1), ("r-l-h", 2, 2), ("r-m-h", 2, 2), ("c-l-h", 1, 1), ("i-m-m", 2, 2) })
            for (int v = 0; v < RealSet.Variants; v++)
            {
                var r = RealSet.Build(id, w, d, v)!;
                Assert.NotNull(r.Roof);
                var found = AutoRoof.Footprint(r.Model, r.Detail);
                Assert.True(found != null, $"{id} {w}x{d} v{v}: the roof finds no walls");
                var roof = AutoRoof.Build(r.Model, r.Detail, r.Roof!, 1f / r.Detail / VoxModel.PerCell);
                Assert.True(roof.Indices.Count > 0, $"{id} {w}x{d} v{v}: an empty roof");
                // The roof sits on the walls, not on a fence or a tree: at least a storey up, under 12 m.
                double eaves = found!.Value.Eaves * 0.5 / r.Detail;
                Assert.InRange(eaves, 2.5, 12);
            }
    }

    [Fact]
    public void TheHouseMeshesWithinBudget()
    {
        var r = RealSet.Build("r-l-h", 1, 1, 0)!;
        var near = VoxMesher.Mesh(r.Model, 1f);
        var far = VoxMesher.Mesh(r.Model.Downsample(4 * r.Detail), 1f);
        _out.WriteLine($"house near {near.Indices.Count / 6} quads, far {far.Indices.Count / 6}");
        Assert.InRange(near.Indices.Count / 6, 1000, 200_000);
        Assert.True(far.Indices.Count < near.Indices.Count / 4);
    }

    static CityMirror City(int w, int h, params (int X, int Y)[] paths)
    {
        var pb = new byte[w * h * 2];
        foreach (var (x, y) in paths) pb[2 * (y * w + x)] = 1;
        var f = new FullState { Width = w, Height = h, WaterLevel = 1, Heights = ByteString.CopyFrom(Enumerable.Repeat((byte)4, w * h).ToArray()),
            Zones = ByteString.CopyFrom(new byte[w * h]), Paths = ByteString.CopyFrom(pb),
            PowerLines = ByteString.CopyFrom(new byte[w * h]), WaterPipes = ByteString.CopyFrom(new byte[w * h]), Catalog = new Catalog() };
        var c = new CityMirror();
        c.ApplyFull(f);
        return c;
    }

    [Fact]
    public void ABuildingFacesTheRoadBesideIt()
    {
        // A 1x1 lot at (3, 3): road south, then north, east and west of it.
        Assert.Equal(0, City(8, 8, (3, 4)).Facing(3, 3, 1, 1));
        Assert.Equal(2, City(8, 8, (3, 2)).Facing(3, 3, 1, 1));
        Assert.Equal(1, City(8, 8, (4, 3)).Facing(3, 3, 1, 1));
        Assert.Equal(3, City(8, 8, (2, 3)).Facing(3, 3, 1, 1));
        // No road, or roads on two sides: south first.
        Assert.Equal(0, City(8, 8).Facing(3, 3, 1, 1));
        Assert.Equal(0, City(8, 8, (3, 4), (4, 3)).Facing(3, 3, 1, 1));
        // The side with more road wins on a big lot.
        Assert.Equal(1, City(8, 8, (3, 5), (5, 3), (5, 4)).Facing(3, 3, 2, 2));
        // A lot that isn't square only turns round, never a quarter.
        Assert.Equal(0, City(8, 8, (4, 3), (4, 4)).Facing(3, 3, 1, 2));
        Assert.Equal(2, City(8, 8, (3, 2)).Facing(3, 3, 1, 2));
    }
}
