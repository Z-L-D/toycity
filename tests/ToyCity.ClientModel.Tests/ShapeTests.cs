using System.Numerics;
using System.Text;
using ToyCity.ClientModel.Graphics;
using ToyCity.ClientModel.Voxels;
using ToyCity.Protocol;
using ToyCity.Sim;
using Xunit.Abstractions;

namespace ToyCity.ClientModel.Tests;

/// <summary>Smooth shapes: smooth and sharp materials, the dual-contoured mesher, its disk cache, glTF props and smooth terrain.</summary>
public class ShapeTests
{
    readonly ITestOutputHelper _out;
    public ShapeTests(ITestOutputHelper o) => _out = o;

    /// <summary>A sharp block 24 × 24 × 10 with a smooth dome on top.</summary>
    static VoxModel Domed()
    {
        var m = new VoxModel(32, 32, 24);
        m.Palette[1] = 0xff3366aa;
        m.Palette[2] = 0xff22cc44;
        m.Smooth[2] = true;
        m.Fill(4, 4, 0, 27, 27, 9, 1);
        for (int z = 10; z < 22; z++)
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    float dx = x + 0.5f - 16, dy = y + 0.5f - 16, dz = z - 10;
                    if (dx * dx + dy * dy + dz * dz <= 100) m[x, y, z] = 2;
                }
        return m;
    }

    [Fact]
    public void SmoothMaterialsRoundTripInTheFileAndAShapeFileCanSetThem()
    {
        var m = Domed();
        m.Smooth[7] = true;
        using var s = new MemoryStream();
        VoxFile.Write(m, s);
        s.Position = 0;
        var back = VoxFile.Read(s);
        Assert.Equal(m.Smooth, back.Smooth);
        Assert.Equal(m.Voxels, back.Voxels);

        var problems = VoxFile.ApplyShapes(back, "# a comment\nsmooth = 1 10-12\nsharp = 2\nround = 4\nsmooth = 300\n");
        Assert.True(back.Smooth[1] && back.Smooth[10] && back.Smooth[11] && back.Smooth[12] && back.Smooth[7]);
        Assert.False(back.Smooth[2]);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("line 4"));
        Assert.Contains(problems, p => p.StartsWith("line 5") && p.Contains("300"));
    }

    [Fact]
    public void SharpPartsKeepTheirCornersAndSmoothPartsCurve()
    {
        var m = Domed();
        var mesh = ShapeMesher.Mesh(m, 1, 100_000);
        Assert.True(mesh.Indices.Count > 0);
        // The block's bottom corners are exactly where the voxels' are (model x, y → world x, z; up is world y).
        foreach (var corner in new[] { new Vector3(4, 0, 4), new Vector3(28, 0, 4), new Vector3(28, 0, 28), new Vector3(4, 0, 28) })
            Assert.Contains(mesh.Positions, p => Vector3.Distance(p, corner) < 1e-4f);
        // Its walls stay flat: every wall vertex below the dome sits on one of the four wall planes.
        foreach (var p in mesh.Positions.Where(p => p.Y < 9))
            Assert.True(p.Y == 0 || Math.Abs(p.X - 4) < 1e-4 || Math.Abs(p.X - 28) < 1e-4 || Math.Abs(p.Z - 4) < 1e-4 || Math.Abs(p.Z - 28) < 1e-4, $"{p} is off the walls");
        // The dome: within its voxels, rounded rather than stepped, and many-faceted.
        var dome = mesh.Positions.Where(p => p.Y > 11).ToList();
        Assert.NotEmpty(dome);
        Assert.All(dome, p => Assert.InRange(Vector3.Distance(p, new Vector3(16, 10, 16)), 3, 11));
        var radii = dome.Where(p => p.Y > 13).Select(p => Vector3.Distance(p, new Vector3(16, 10, 16))).ToList();
        Assert.True(radii.Max() - radii.Min() < 2.5f, $"radius {radii.Min():F2}..{radii.Max():F2}: not round");
        int domeNormals = mesh.Normals.Where((n, i) => mesh.Positions[i].Y > 11).Select(n => (MathF.Round(n.X, 2), MathF.Round(n.Y, 2), MathF.Round(n.Z, 2))).Distinct().Count();
        Assert.True(domeNormals > 100, $"{domeNormals} distinct normals on the dome");
        Assert.Contains(m.Palette[1], mesh.Colors);
        Assert.Contains(m.Palette[2], mesh.Colors);
        // Faces out: each triangle's winding agrees with its normals and points away from the middle.
        var centre = new Vector3(16, 6, 16);
        int outward = 0, tris = mesh.Indices.Count / 3;
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Positions[mesh.Indices[i]]; var b = mesh.Positions[mesh.Indices[i + 1]]; var c = mesh.Positions[mesh.Indices[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, mesh.Normals[mesh.Indices[i]]) > 0 && Vector3.Dot((a + b + c) / 3 - centre, n) > 0) outward++;
        }
        Assert.True(outward > tris * 0.95, $"{outward} of {tris} triangles face out");
        _out.WriteLine($"domed block: {tris:N0} triangles");
    }

    [Fact]
    public void AShapeFitsItsTriangleBudget()
    {
        var m = Domed();
        int full = ShapeMesher.Mesh(m, 1, int.MaxValue).Indices.Count / 3;
        foreach (int budget in new[] { 5_000, 1_000, 200 })
        {
            var mesh = ShapeMesher.Mesh(m, 1, budget);
            int tris = mesh.Indices.Count / 3;
            _out.WriteLine($"budget {budget}: {tris} of {full}");
            Assert.InRange(tris, 1, budget);
            // Still the same size of thing.
            Assert.InRange(mesh.Positions.Max(p => p.Y), 18, 23);
        }
        // A model with nothing smooth is the plain blocks.
        var plain = new VoxModel(8, 8, 8);
        plain.Palette[1] = 0xff808080;
        plain.Fill(0, 0, 0, 7, 7, 7, 1);
        Assert.Equal(12, ShapeMesher.Mesh(plain, 1, 1000).Indices.Count / 3);
    }

    [Fact]
    public void EveryPlaceholderHasASmoothShapeWithinItsBudget()
    {
        var cat = Mapping.Catalog(ContentDb.LoadDefault());
        var (start, budget) = GraphicsSettings.High.Shapes;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int models = 0;
        long tris = 0;
        foreach (var t in cat.BuildingTypes.Where(t => t.Placeable || t.Category == "zoned"))
        {
            int w = Math.Max(1, t.Width), d = Math.Max(1, t.Height);
            var m = Placeholder.Build(t, w, d);
            var mesh = ShapeMesher.Mesh(m, 1f / VoxModel.PerCell, budget, start);
            Assert.InRange(mesh.Indices.Count / 3, 1, budget);
            Assert.InRange(mesh.Positions.Max(p => p.X), 0, w + 0.01f);
            models++;
            tris += mesh.Indices.Count / 3;
        }
        _out.WriteLine($"{models} placeholders: {tris:N0} triangles, {clock.ElapsedMilliseconds} ms");
        Assert.True(clock.Elapsed.TotalSeconds < 60, $"{clock.Elapsed.TotalSeconds:F1} s to make {models} shapes");
    }

    [Fact]
    public void ShapesAreMadeOnceAndReadBackFromDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "toycity-shapes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var m = Domed();
            var first = new ShapeCache(dir);
            var made = first.Get(m, 0.5f, 4_000, 1);
            Assert.Equal(1, first.Builds);
            Assert.Same(made, first.Get(m, 0.5f, 4_000, 1));
            Assert.Single(Directory.GetFiles(dir, "*.mesh"));

            var second = new ShapeCache(dir);
            var read = second.Get(m, 0.5f, 4_000, 1);
            Assert.Equal((1, 0), (second.Hits, second.Builds));
            Assert.Equal(made.Positions, read.Positions);
            Assert.Equal(made.Normals, read.Normals);
            Assert.Equal(made.Colors, read.Colors);
            Assert.Equal(made.Indices, read.Indices);

            // Another budget or another material is another shape; a broken file is made again.
            Assert.NotEqual(ShapeCache.Key(m, 0.5f, 4_000, 1), ShapeCache.Key(m, 0.5f, 2_000, 1));
            var sharp = Domed();
            sharp.Smooth[2] = false;
            Assert.NotEqual(ShapeCache.Key(m, 0.5f, 4_000, 1), ShapeCache.Key(sharp, 0.5f, 4_000, 1));
            File.WriteAllText(Directory.GetFiles(dir, "*.mesh")[0], "junk");
            var third = new ShapeCache(dir);
            Assert.Equal(made.Indices, third.Get(m, 0.5f, 4_000, 1).Indices);
            Assert.Equal(1, third.Builds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>A unit cube as glTF JSON: 8 corners, 12 triangles, the buffer embedded, one red material.</summary>
    static (string Json, byte[] Bin) Cube(string nodeExtra)
    {
        var bin = new MemoryStream();
        var w = new BinaryWriter(bin);
        for (int i = 0; i < 8; i++) { w.Write((float)(i & 1)); w.Write((float)(i >> 1 & 1)); w.Write((float)(i >> 2 & 1)); }
        ushort[] idx = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
        foreach (var i in idx) w.Write(i);
        w.Flush();
        string json = $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0, "scenes": [ { "nodes": [0] } ],
          "nodes": [ { "mesh": 0 {{nodeExtra}} } ],
          "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1, "material": 0 } ] } ],
          "materials": [ { "pbrMetallicRoughness": { "baseColorFactor": [1, 0, 0, 1] } } ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 8, "type": "VEC3" },
            { "bufferView": 1, "componentType": 5123, "count": 36, "type": "SCALAR" }
          ],
          "bufferViews": [ { "buffer": 0, "byteLength": 96 }, { "buffer": 0, "byteOffset": 96, "byteLength": 72 } ],
          "buffers": [ { "byteLength": 168, "uri": "BUFFER" } ]
        }
        """;
        return (json, bin.ToArray());
    }

    [Fact]
    public void AGltfModelReadsWithItsTransformAndColour()
    {
        var (json, bin) = Cube(""", "translation": [10, 0, 0], "scale": [2, 2, 2]""");
        // Embedded as a data uri.
        var mesh = GltfFile.Read(Encoding.UTF8.GetBytes(json.Replace("BUFFER", "data:application/octet-stream;base64," + Convert.ToBase64String(bin))));
        Assert.Equal(8, mesh.Positions.Count);
        Assert.Equal(12, mesh.Indices.Count / 3);
        Assert.Equal(10, mesh.Positions.Min(p => p.X));
        Assert.Equal(12, mesh.Positions.Max(p => p.X));
        Assert.All(mesh.Colors, c => Assert.Equal(VoxModel.Rgba(255, 0, 0), c));
        Assert.All(mesh.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
        // Beside it in a .bin file.
        var beside = GltfFile.Read(Encoding.UTF8.GetBytes(json.Replace("BUFFER", "cube.bin")), name => name == "cube.bin" ? bin : null);
        Assert.Equal(mesh.Positions, beside.Positions);
        Assert.Throws<InvalidDataException>(() => GltfFile.Read(Encoding.UTF8.GetBytes(json.Replace("BUFFER", "missing.bin"))));

        // As one .glb: the JSON chunk (with no uri: the buffer is the binary chunk) then the binary chunk.
        var glbJson = Encoding.UTF8.GetBytes(json.Replace(", \"uri\": \"BUFFER\"", ""));
        var glb = new MemoryStream();
        var w = new BinaryWriter(glb);
        int jsonLen = (glbJson.Length + 3) / 4 * 4, binLen = (bin.Length + 3) / 4 * 4;
        w.Write(0x46546C67u); w.Write(2); w.Write(12 + 8 + jsonLen + 8 + binLen);
        w.Write(jsonLen); w.Write(0x4E4F534Au); w.Write(glbJson); for (int i = glbJson.Length; i < jsonLen; i++) w.Write((byte)' ');
        w.Write(binLen); w.Write(0x004E4942u); w.Write(bin); for (int i = bin.Length; i < binLen; i++) w.Write((byte)0);
        var fromGlb = GltfFile.Read(glb.ToArray());
        Assert.Equal(mesh.Positions, fromGlb.Positions);

        // Set on a 1 × 1 lot: 32 m wide is two cells, so it shrinks to fit; it stands at the lot's corner.
        var big = GltfFile.Read(Encoding.UTF8.GetBytes(Cube(""", "scale": [32, 32, 32]""").Json.Replace("BUFFER", "data:application/octet-stream;base64," + Convert.ToBase64String(bin))));
        var fitted = GltfFile.FitToLot(big, 1, 1);
        Assert.Equal(0, fitted.Positions.Min(p => p.X));
        Assert.Equal(1, fitted.Positions.Max(p => p.X), 4);
        Assert.Equal(0, fitted.Positions.Min(p => p.Y));
    }

    [Fact]
    public void TheGroundIsTheServersCornersAndBuiltCellsStandFlat()
    {
        var w = World.Create(ContentDb.LoadDefault(), 501, 128, "Hilltown");
        Assert.True(StarterTown.Apply(w));
        w.Run(30);
        var c = new CityMirror();
        c.ApplyFull(Mapping.FullState(w, 0));
        Assert.Equal(w.Corner, c.Corners);
        int sloped = 0;
        for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
            {
                var k = TerrainShape.Corners(c, x, y);
                if (TerrainShape.Slope(c, x, y) > 0) sloped++;
                // Under a building the ground is its pad: flat.
                if (c.BuildingAt[c.Idx(x, y)] != 0 && !c.TypeById[c.BuildingAtCell(x, y)!.TypeId].Roadside)
                    Assert.True(k.NW == k.NE && k.NE == k.SE && k.SE == k.SW, $"the ground under the building at {x},{y} isn't flat");
            }
        _out.WriteLine($"{sloped} sloped cells");
        Assert.True(sloped > 1000, $"only {sloped} cells sloped");
        // The surface lies on the same two triangles the views draw: along the top edge, between the top corners;
        // across the diagonal, between north-west and south-east.
        var (nw, ne, se, _) = TerrainShape.Corners(c, 10, 10);
        Assert.Equal((nw + ne) / 2, TerrainShape.Surface(c, 10.5f, 10f), 4);
        Assert.Equal((nw + se) / 2, TerrainShape.Surface(c, 10.5f, 10.5f), 4);
        // A moved corner reaches the client in a delta.
        var tracker = new DeltaTracker();
        tracker.Reset(w);
        w.Enqueue(new TerraformAction(new CellRect(2, 2, 4, 4), TerraformMode.Raise));
        w.Step();
        c.ApplyDelta(tracker.Next(w, 0, Array.Empty<SimNotice>()));
        Assert.Equal(w.Corner, c.Corners);
    }

    [Fact]
    public void TheShapeOptionsGetFinerFromLowToUltra()
    {
        var order = new[] { GraphicsSettings.Low, GraphicsSettings.Medium, GraphicsSettings.High, GraphicsSettings.Ultra };
        for (int k = 1; k < order.Length; k++)
        {
            Assert.True(order[k].ShapeDetail >= order[k - 1].ShapeDetail);
            Assert.True(order[k].Shapes.Budget >= order[k - 1].Shapes.Budget);
        }
        Assert.False(GraphicsSettings.Low.FullTerrain);
        Assert.True(GraphicsSettings.High.FullTerrain);
        var s = GraphicsSettings.Load("shape_detail = high\nterrain_detail = false\n", GraphicsSettings.High);
        Assert.Equal((ShapeDetail.High, false), (s.ShapeDetail, s.FullTerrain));
        Assert.Equal(s, GraphicsSettings.Load(s.Save(), GraphicsSettings.Low));
    }
}
