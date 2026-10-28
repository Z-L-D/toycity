using ToyCity.ClientModel.Voxels;
using ToyCity.Protocol;
using ToyCity.Protocol.V0;
using ToyCity.Sim;
using Xunit.Abstractions;

namespace ToyCity.ClientModel.Tests;

/// <summary>The voxel pipeline: .vox files, greedy meshing, placeholder models and the dense benchmark city.</summary>
public class VoxelTests
{
    readonly ITestOutputHelper _out;
    public VoxelTests(ITestOutputHelper o) => _out = o;

    static readonly Catalog Cat = Mapping.Catalog(ContentDb.LoadDefault());

    static VoxModel Random(int seed, int sx = 20, int sy = 14, int sz = 9)
    {
        var r = new System.Random(seed);
        var m = new VoxModel(sx, sy, sz);
        for (int i = 1; i < 256; i++) m.Palette[i] = (uint)r.Next();
        for (int i = 0; i < m.Voxels.Length; i++) m.Voxels[i] = r.Next(3) == 0 ? (byte)r.Next(1, 4) : (byte)0;
        return m;
    }

    /// <summary>
    /// Faces between a voxel and the open air: what any correct mesh must cover, one unit square each. Air is the
    /// empty space reachable from beyond the model, found here by repeated sweeps rather than a queue.
    /// </summary>
    static int ExposedFaces(VoxModel m)
    {
        var air = new bool[m.SizeX + 2, m.SizeY + 2, m.SizeZ + 2];
        bool Empty(int x, int y, int z) => m[x - 1, y - 1, z - 1] == 0;
        for (int z = 0; z < m.SizeZ + 2; z++)
            for (int y = 0; y < m.SizeY + 2; y++)
                for (int x = 0; x < m.SizeX + 2; x++)
                    air[x, y, z] = x == 0 || y == 0 || z == 0 || x == m.SizeX + 1 || y == m.SizeY + 1 || z == m.SizeZ + 1;
        for (bool grew = true; grew;)
        {
            grew = false;
            for (int z = 1; z <= m.SizeZ; z++)
                for (int y = 1; y <= m.SizeY; y++)
                    for (int x = 1; x <= m.SizeX; x++)
                        if (!air[x, y, z] && Empty(x, y, z) && (air[x - 1, y, z] || air[x + 1, y, z] || air[x, y - 1, z] || air[x, y + 1, z] || air[x, y, z - 1] || air[x, y, z + 1]))
                            grew = air[x, y, z] = true;
        }
        int n = 0;
        for (int z = 1; z <= m.SizeZ; z++)
            for (int y = 1; y <= m.SizeY; y++)
                for (int x = 1; x <= m.SizeX; x++)
                    if (!Empty(x, y, z))
                        n += (air[x + 1, y, z] ? 1 : 0) + (air[x - 1, y, z] ? 1 : 0) + (air[x, y + 1, z] ? 1 : 0)
                           + (air[x, y - 1, z] ? 1 : 0) + (air[x, y, z + 1] ? 1 : 0) + (air[x, y, z - 1] ? 1 : 0);
        return n;
    }

    static double Area(MeshData mesh)
    {
        double a = 0;
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var p = mesh.Positions[mesh.Indices[i]]; var q = mesh.Positions[mesh.Indices[i + 1]]; var r = mesh.Positions[mesh.Indices[i + 2]];
            a += System.Numerics.Vector3.Cross(q - p, r - p).Length() / 2;
        }
        return a;
    }

    [Fact]
    public void AVoxFileReadsBackAsWritten()
    {
        var m = Random(1);
        var s = new MemoryStream();
        VoxFile.Write(m, s);
        s.Position = 0;
        var back = VoxFile.Read(s);
        Assert.Equal((m.SizeX, m.SizeY, m.SizeZ), (back.SizeX, back.SizeY, back.SizeZ));
        Assert.Equal(m.Voxels, back.Voxels);
        Assert.Equal(m.Palette.Skip(1).Take(254), back.Palette.Skip(1).Take(254));
        // The header MagicaVoxel expects.
        Assert.Equal("VOX "u8.ToArray(), s.ToArray()[..4]);
        Assert.Equal(150, BitConverter.ToInt32(s.ToArray(), 4));
    }

    [Fact]
    public void AFarModelKeepsTheShapeAtAQuarterTheResolution()
    {
        var m = new VoxModel(64, 64, 40);
        m.Fill(4, 4, 0, 59, 59, 31, 1);
        m.Fill(4, 4, 32, 59, 59, 32, 2);   // a one-voxel roof
        var far = m.Downsample(4);
        Assert.Equal((16, 16, 10), (far.SizeX, far.SizeY, far.SizeZ));
        Assert.Equal(1, far[8, 8, 4]);
        Assert.Equal(2, far[8, 8, 8]);      // the roof's layer, a quarter solid, survives
        Assert.Equal(0, far[0, 0, 0]);
        Assert.Equal(0, far[8, 8, 9]);
    }

    [Fact]
    public void ASolidBoxIsSixQuadsAndEveryFaceFacesOut()
    {
        var m = new VoxModel(32, 32, 20);
        m.Fill(0, 0, 0, 31, 31, 19, 1);
        var mesh = VoxMesher.Mesh(m, 1);
        Assert.Equal(6, mesh.Quads);
        Assert.Equal(ExposedFaces(m), Area(mesh), 3);
        // Counter-clockwise from outside: each triangle's winding agrees with its normal, which points away from the centre.
        var centre = new System.Numerics.Vector3(16, 10, 16);
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var p = mesh.Positions[mesh.Indices[i]]; var q = mesh.Positions[mesh.Indices[i + 1]]; var r = mesh.Positions[mesh.Indices[i + 2]];
            var n = mesh.Normals[mesh.Indices[i]];
            Assert.True(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(q - p, r - p), n) > 0);
            Assert.True(System.Numerics.Vector3.Dot(p - centre, n) > 0);
        }
        // Model z is world up.
        Assert.Equal(20, mesh.Positions.Max(p => p.Y));
        // Saved hollow, the inside isn't drawn.
        Assert.Equal(6, VoxMesher.Mesh(m.Hollow(), 1).Quads);
    }

    [Fact]
    public void GreedyMeshingCoversExactlyTheExposedFacesWithFewerQuads()
    {
        for (int seed = 0; seed < 5; seed++)
        {
            var m = Random(seed);
            var mesh = VoxMesher.Mesh(m, 1);
            int faces = ExposedFaces(m);
            Assert.Equal(faces, Area(mesh), 3);
            Assert.True(mesh.Quads < faces);
        }
    }

    [Fact]
    public void EveryBuildingTypeHasAPlaceholderOnEveryLotItGrowsOn()
    {
        int quads = 0, models = 0;
        foreach (var t in Cat.BuildingTypes)
        {
            var lots = t.Category == "zoned" ? new[] { (1, 1), (1, 2), (2, 2), (2, 3), (3, 3), (3, 4), (4, 4) } : new[] { (t.Width, t.Height) };
            foreach (var (w, d) in lots)
            {
                var m = Placeholder.Build(t, w, d);
                Assert.Equal((w * 32, d * 32), (m.SizeX, m.SizeY));
                Assert.True(m.Count > 0, t.Id);
                var mesh = VoxMesher.Mesh(m);
                Assert.True(mesh.Quads * 4 < ExposedFaces(m), $"{t.Id} {w}x{d}: greedy meshing saved little");
                Assert.True(mesh.Positions.All(p => p.X >= 0 && p.Z >= 0 && p.X <= w + 0.001f && p.Z <= d + 0.001f && p.Y >= 0), $"{t.Id} spills off its lot");
                quads += mesh.Quads;
                models++;
            }
        }
        _out.WriteLine($"{models} placeholder models, {quads:N0} quads");
        // Round trip through a file, as an artist's replacement would arrive.
        // Saved hollow, as the placeholder writer saves them, a model meshes the same.
        var solid = Placeholder.Build(Cat.BuildingTypes[0], 2, 2);
        var s = new MemoryStream();
        VoxFile.Write(solid.Hollow(), s);
        s.Position = 0;
        var back = VoxFile.Read(s);
        Assert.True(back.Count < solid.Count);
        Assert.Equal(VoxMesher.Mesh(solid).Positions, VoxMesher.Mesh(back).Positions);
    }

    [Fact]
    public void TheDenseCityBuildsUpEveryDryBlock()
    {
        var map = World.Create(ContentDb.LoadDefault(), 42, 128, "Bench");
        var full = Mapping.FullState(map, 0);
        var dense = DenseCity.Make(full);
        var mirror = new CityMirror();
        mirror.ApplyFull(dense);
        Assert.True(mirror.Buildings.Count > 1000, $"{mirror.Buildings.Count} buildings");
        Assert.Contains(mirror.Buildings.Values, b => b.Density == Density.High && b.Width == 4);
        Assert.All(mirror.Buildings.Values, b => Assert.True(mirror.TypeById.ContainsKey(b.TypeId)));
        // No building on water or a road.
        foreach (var b in mirror.Buildings.Values)
            for (int y = b.Y; y < b.Y + b.Height; y++)
                for (int x = b.X; x < b.X + b.Width; x++)
                    Assert.True(!mirror.IsWater(mirror.Idx(x, y)) && mirror.Paths[mirror.Idx(x, y)] == 0);
        // What the renderer will draw: one mesh per type and lot, instanced.
        var meshes = mirror.Buildings.Values.Select(b => (b.TypeId, b.Width, b.Height)).Distinct()
            .ToDictionary(k => k, k => VoxMesher.Mesh(Placeholder.Build(mirror.TypeById[k.TypeId], k.Width, k.Height)).Quads);
        var far = meshes.Keys.ToDictionary(k => k, k => VoxMesher.Mesh(Placeholder.Build(mirror.TypeById[k.TypeId], k.Width, k.Height).Downsample(4), 4f / VoxModel.PerCell).Quads);
        long tris = mirror.Buildings.Values.Sum(b => (long)meshes[(b.TypeId, b.Width, b.Height)] * 2);
        long farTris = mirror.Buildings.Values.Sum(b => (long)far[(b.TypeId, b.Width, b.Height)] * 2);
        _out.WriteLine($"dense city: {mirror.Buildings.Count:N0} buildings, {meshes.Count} distinct models, {tris:N0} building triangles near, {farTris:N0} far");
        Assert.True(farTris * 3 < tris, "the far level of detail should cost a third or less");
    }

    [Fact]
    public void SmoothedGeometryIsClosedFacesOutAndKeepsTheShape()
    {
        var m = new VoxModel(32, 32, 20);
        m.Fill(4, 4, 0, 27, 27, 15, 1);
        m.Fill(10, 10, 16, 21, 21, 19, 2);  // a smaller block on top
        m.Palette[1] = 0xff3366aa;
        m.Palette[2] = 0xff22cc44;
        var mesh = SurfaceNets.Mesh(m.Hollow(), 1);
        Assert.True(mesh.Indices.Count > 0);
        // Closed: every edge is shared by exactly two triangles, once each way.
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i < mesh.Indices.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                var e = (mesh.Indices[i + k], mesh.Indices[i + (k + 1) % 3]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        Assert.All(edges, e => Assert.Equal(1, e.Value));
        Assert.All(edges.Keys, e => Assert.True(edges.ContainsKey((e.Item2, e.Item1)), "an edge with no twin: a hole"));
        // Faces out: winding agrees with the vertex normals, which point away from the middle of the base block.
        var centre = new System.Numerics.Vector3(16, 8, 16);
        int outward = 0, tris = mesh.Indices.Count / 3;
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var p = mesh.Positions[mesh.Indices[i]]; var q = mesh.Positions[mesh.Indices[i + 1]]; var r = mesh.Positions[mesh.Indices[i + 2]];
            var n = System.Numerics.Vector3.Cross(q - p, r - p);
            if (System.Numerics.Vector3.Dot(n, mesh.Normals[mesh.Indices[i]]) > 0 && System.Numerics.Vector3.Dot((p + q + r) / 3 - centre, n) > 0) outward++;
        }
        Assert.True(outward > tris * 0.95, $"{outward} of {tris} triangles face out");
        // The same footprint and height, give or take the rounding, and the roof keeps its colour.
        Assert.InRange(mesh.Positions.Max(p => p.Y), 19, 20.5);
        Assert.InRange(mesh.Positions.Min(p => p.X), 3.5, 5);
        Assert.Contains(m.Palette[2], mesh.Colors);
    }
}
