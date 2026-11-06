using System.Numerics;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.ClientModel.Tests;

/// <summary>Smooth shapes (RC part 2.1): lathes, extrudes and sweeps as polygons and voxels, and the destruction swap.</summary>
public class LatheShapeTests
{
    readonly Xunit.Abstractions.ITestOutputHelper _out;
    public LatheShapeTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    /// <summary>A closed mesh's volume, from its triangles (positive when they all face out).</summary>
    static double Volume(MeshData m)
    {
        double v = 0;
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            var a = m.Positions[m.Indices[i]]; var b = m.Positions[m.Indices[i + 1]]; var c = m.Positions[m.Indices[i + 2]];
            v += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
        }
        return v;
    }

    /// <summary>
    /// The world axes swap y and z (a mirror), so a mesh wound counter-clockwise to its outward normals has a negative
    /// signed volume there: the magnitude is what's compared, and every triangle has to agree for it to come out right.
    /// </summary>
    static double Size(MeshData m) => Math.Abs(Volume(m));

    static int Voxels(Shape s, int detail, int cells = 1)
    {
        var m = new VoxModel(32 * detail * cells, 32 * detail * cells, Math.Min(256, 64 * detail));
        return s.Voxelize(m, detail);
    }

    [Fact]
    public void ACylinderIsClosedAndFacesOut()
    {
        var s = Shape.Cylinder(8, 8, 2, 0, 5, AssetPalette.Concrete);
        var m = s.Mesh(1f);
        double exact = Math.PI * 4 * 5;
        _out.WriteLine($"cylinder {m.Indices.Count / 3} triangles, volume {Volume(m):F2} (exact {exact:F2})");
        Assert.InRange(Size(m), exact * 0.95, exact * 1.001);
        // Every triangle faces out: its winding agrees with its normals.
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            var a = m.Positions[m.Indices[i]]; var b = m.Positions[m.Indices[i + 1]]; var c = m.Positions[m.Indices[i + 2]];
            var n = m.Normals[m.Indices[i]] + m.Normals[m.Indices[i + 1]] + m.Normals[m.Indices[i + 2]];
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), n) > 0);
        }
        // Side normals point away from the axis, the top's straight up: the rim is a crease, not rounded.
        foreach (var (p, n) in m.Positions.Zip(m.Normals))
        {
            var radial = new Vector3(p.X - 8, 0, p.Z - 8);
            if (Math.Abs(n.Y) < 0.01f && radial.Length() > 1.9f) Assert.True(Vector3.Dot(Vector3.Normalize(radial), n) > 0.95f);
            else Assert.True(Math.Abs(Math.Abs(n.Y) - 1) < 1e-4);
        }
        // Its voxels fill the same volume: 0.125 m voxels, 512 a cubic metre.
        Assert.InRange(Voxels(s, 4) / 512.0, exact * 0.95, exact * 1.05);
    }

    [Fact]
    public void ADomeIsSmoothAndHollowRingsAreRings()
    {
        var dome = ShapePresets.Dome(4, AssetPalette.Stone);
        dome.X = dome.Y = 8;
        var m = dome.Mesh(1f);
        double exact = Math.PI * 16 * 0.8 + 2.0 / 3 * Math.PI * 64;
        Assert.InRange(Size(m), exact * 0.95, exact * 1.001);
        // Smooth: on the curve, a vertex's normal points from the middle of the sphere.
        int curved = 0;
        foreach (var (p, n) in m.Positions.Zip(m.Normals))
        {
            var r = p - new Vector3(8, 0.8f, 8);
            if (p.Y > 1.2f && r.Length() > 3.5f) { Assert.True(Vector3.Dot(Vector3.Normalize(r), n) > 0.97f, $"{p} {n}"); curved++; }
        }
        Assert.True(curved > 50);
        var ring = ShapePresets.Ring(3, 0.5, 1, AssetPalette.Stone);
        ring.X = ring.Y = 8;
        Assert.False(ring.Inside(8, 8, 0.5, 0.125));
        Assert.True(ring.Inside(8 + 2.75, 8, 0.5, 0.125));
        Assert.InRange(Size(ring.Mesh(1f)), Math.PI * (9 - 6.25) * 0.95, Math.PI * (9 - 6.25) * 1.001);
    }

    [Fact]
    public void AnArchIsAWallWithAnOpening()
    {
        var a = ShapePresets.Arch(3, 4.5, 0.6, AssetPalette.Brick);
        a.X = 8; a.Y = 8;
        Assert.False(a.Inside(8, 8.3, 1, 0.125), "the opening is open");
        Assert.True(a.Inside(8 - 1.9, 8.3, 1, 0.125), "the jamb is solid");
        Assert.True(a.Inside(8, 8.3, 4.8, 0.125), "over the arch is solid");
        Assert.False(a.Inside(8 - 1.9, 9.5, 1, 0.125), "only as deep as the wall");
        double area = Math.Abs(Shape.Area(a.Profile));
        Assert.InRange(Size(a.Mesh(1f)), area * 0.6 * 0.99, area * 0.6 * 1.01);
        Assert.InRange(Voxels(a, 4) / 512.0, area * 0.6 * 0.9, area * 0.6 * 1.1);
    }

    [Fact]
    public void ACorniceSweepsRoundACorner()
    {
        var c = ShapePresets.Cornice(AssetPalette.Stone, new() { new(0, 0, 0), new(6, 0, 0), new(6, 6, 0) });
        c.X = 4; c.Y = 4; c.Z = 3;
        double area = Math.Abs(Shape.Area(c.Profile));
        double vol = Size(c.Mesh(1f));
        _out.WriteLine($"cornice {vol:F3} m³, section {area:F3} m² x 12 m");
        Assert.InRange(vol, area * 12 * 0.85, area * 12 * 1.15);
        int n = Voxels(c, 4);
        Assert.InRange(n / 512.0, area * 12 * 0.75, area * 12 * 1.25);
        // Along both legs.
        Assert.True(c.Inside(4 + 3, 4 + 0.1, 3.4, 0.125) || c.Inside(4 + 3, 4 - 0.1, 3.4, 0.125));
        Assert.True(c.Inside(4 + 6 + 0.1, 4 + 3, 3.4, 0.125) || c.Inside(4 + 6 - 0.1, 4 + 3, 3.4, 0.125));
    }

    [Fact]
    public void ShapesSaveAndComeBack()
    {
        foreach (string name in ShapePresets.Names)
        {
            var s = ShapePresets.Make(name, AssetPalette.Trim)!;
            s.X = 3; s.Y = 4; s.Z = 1; s.Turn = 0.5;
            var back = Shape.FromJson(System.Text.Json.Nodes.JsonNode.Parse(s.ToJson().ToJsonString()));
            Assert.NotNull(back);
            Assert.Equal(s.ToJson().ToJsonString(), back!.ToJson().ToJsonString());
            Assert.True(back.Mesh(1f).Indices.Count > 0, name);
        }
        Assert.Null(Shape.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{\"kind\":\"lathe\"}")));
    }

    [Fact]
    public void TheEditorAddsShapesAsStepsAndSavesThem()
    {
        var doc = AssetDoc.New("Test", 4, 4, 4, 2);
        var col = ShapePresets.Column(5, 0.4, AssetPalette.Trim);
        col.X = 8; col.Y = 8;
        int n = doc.AddShape(col);
        Assert.True(n > 100);
        Assert.Single(doc.Shapes);
        Assert.Equal(n, doc.Model.Count);
        doc.Undo();
        Assert.Empty(doc.Shapes);
        Assert.Equal(0, doc.Model.Count);
        doc.Redo();
        Assert.Single(doc.Shapes);
        // Mirrored, it moves across; cleared, it goes.
        doc.MirrorX();
        Assert.Equal(16 - 8, doc.Shapes[0].X, 3);
        var off = ShapePresets.Column(5, 0.4, AssetPalette.Trim);
        off.X = 3; off.Y = 8;
        doc.AddShape(off);
        doc.MirrorX();
        Assert.Contains(doc.Shapes, s => Math.Abs(s.X - 13) < 1e-6);
        // Saved beside the .vox, they come back.
        string dir = Path.Combine(Path.GetTempPath(), "toycity-shapes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            doc.Save(Path.Combine(dir, "a.vox"));
            var back = AssetDoc.Load(Path.Combine(dir, "a.vox"));
            Assert.Equal(doc.Shapes.Count, back.Shapes.Count);
            string used = doc.UseForBuilding(dir, "test-type");
            Assert.Equal(2, AssetDoc.ParseShapes(File.ReadAllText(AssetDoc.SidecarOf(used))).Count);
        }
        finally { Directory.Delete(dir, true); }
        doc.Clear();
        Assert.Empty(doc.Shapes);
        doc.Undo();
        Assert.Equal(2, doc.Shapes.Count);
    }

    [Fact]
    public void TheRotundaUsesEveryKindOfShape()
    {
        var r = DemoAsset.Rotunda(4);
        Assert.Contains(r.Shapes, s => s.Kind == ShapeKind.Lathe);
        Assert.Contains(r.Shapes, s => s.Kind == ShapeKind.Extrude);
        Assert.Contains(r.Shapes, s => s.Kind == ShapeKind.Sweep);
        Assert.True(r.Model.Count > 50_000);
    }

    [Fact]
    public void TheSetDrawsItsRoundThingsAsShapesForFewerTriangles()
    {
        int withShapes = 0;
        foreach (string id in RealSet.Types)
        {
            int w = id.Length == 5 ? 2 : 0, d = w;
            if (w == 0) (w, d) = id switch { "coal-plant" => (4, 4), "water-pump" => (1, 1), "city-hall" => (3, 3), _ => (0, 0) };
            if (w == 0) continue;
            var r = RealSet.Build(id, w, d, 0)!;
            if (r.Shapes.Count > 0) withShapes++;
        }
        Assert.True(withShapes > 5);
        foreach (var (id, w, d) in new[] { ("coal-plant", 4, 4), ("water-pump", 1, 1), ("city-hall", 3, 3) })
        {
            var r = RealSet.Build(id, w, d, 0)!;
            Assert.NotEmpty(r.Shapes);
            // Under the polygons, no voxel of a shape is left to poke through: they're ghosts, drawn with no faces.
            var boxy = r.Boxy;
            foreach (var s in r.Shapes)
                s.ForEachVoxel(boxy, r.Detail, (x, y, z) => Assert.False(boxy[x, y, z] == s.Colour && boxy[x, y, z] != 0, $"{id}: a {s.Kind} voxel left at {x},{y},{z}"));
            var drawn = VoxMesher.Mesh(boxy, 1f);
            foreach (var sh in r.Shapes) drawn.Append(sh.Mesh(2f * r.Detail, 1, r.Model.Palette));
            int voxelsOnly = VoxMesher.Mesh(r.Model, 1f).Indices.Count / 3, shaped = drawn.Indices.Count / 3;
            _out.WriteLine($"{id}: {voxelsOnly} triangles as voxels, {shaped} with shapes");
            Assert.True(shaped < voxelsOnly, id);
            // The whole model keeps the shapes' voxels for wrecking.
            Assert.True(r.Model.Count > boxy.Voxels.Count(v => v != 0 && !boxy.Ghost[v]));
        }
    }

    [Fact]
    public void GhostVoxelsHideFacesWithoutDrawingAny()
    {
        var m = new VoxModel(8, 8, 8);
        AssetPalette.Apply(m);
        m.Fill(0, 0, 0, 7, 7, 3, AssetPalette.Stone);
        m.Fill(2, 2, 4, 5, 5, 6, 200);
        int before = VoxMesher.Mesh(m).Indices.Count;
        m.Ghost[200] = true;
        var mesh = VoxMesher.Mesh(m);
        Assert.DoesNotContain(m.Palette[200], mesh.Colors);
        // The slab's top under the ghost isn't drawn either: the ghost is solid.
        Assert.True(mesh.Indices.Count < before);
        Assert.True(m.Downsample(2).Ghost[200]);
    }

    [Fact]
    public void AWreckIsHoledCharredAndTheSameEveryTime()
    {
        var r = RealSet.Build("r-m-m", 2, 2, 0)!;
        var a = Wreck.Carve(r.Model, r.Detail, 1234);
        var b = Wreck.Carve(r.Model, r.Detail, 1234);
        var c = Wreck.Carve(r.Model, r.Detail, 99);
        Assert.Equal(a.Voxels, b.Voxels);
        Assert.NotEqual(a.Voxels, c.Voxels);
        Assert.Contains(Wreck.Char, a.Voxels);
        // Holes through the building, not just its edges.
        int lost = 0;
        for (int i = 0; i < a.Voxels.Length; i++) if (r.Model.Voxels[i] != 0 && a.Voxels[i] == 0) lost++;
        Assert.True(lost > r.Model.Count / 50, $"only {lost} voxels knocked out");
        // Nothing left hanging in the air: every voxel is joined to the ground.
        var held = new bool[a.Voxels.Length];
        var q = new Queue<int>();
        int sx = a.SizeX, plane = a.SizeX * a.SizeY;
        for (int i = 0; i < plane; i++) if (a.Voxels[i] != 0) { held[i] = true; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % sx, y = i / sx % a.SizeY, z = i / plane;
            foreach (int j in new[] { x > 0 ? i - 1 : -1, x < sx - 1 ? i + 1 : -1, y > 0 ? i - sx : -1, y < a.SizeY - 1 ? i + sx : -1, z > 0 ? i - plane : -1, z < a.SizeZ - 1 ? i + plane : -1 })
                if (j >= 0 && !held[j] && a.Voxels[j] != 0) { held[j] = true; q.Enqueue(j); }
        }
        for (int i = 0; i < a.Voxels.Length; i++) Assert.False(a.Voxels[i] != 0 && !held[i]);
    }
}
