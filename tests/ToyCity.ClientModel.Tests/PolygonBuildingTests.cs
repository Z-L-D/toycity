using System.Numerics;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// Polygon and textured buildings (RC part 5): finishes, walls with openings, windows and doors as parts, the
/// textured auto-roof, and the voxels kept under the polygons for when a building is wrecked.
/// </summary>
public class PolygonBuildingTests
{
    readonly Xunit.Abstractions.ITestOutputHelper _out;
    public PolygonBuildingTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    static double Volume(MeshData m)
    {
        double v = 0;
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            var a = m.Positions[m.Indices[i]]; var b = m.Positions[m.Indices[i + 1]]; var c = m.Positions[m.Indices[i + 2]];
            v += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
        }
        return Math.Abs(v);
    }

    [Fact]
    public void EveryFinishIsMadeTilesAndHasAColour()
    {
        Assert.Equal(23, Finishes.List.Length);
        foreach (var (id, _, _) in Finishes.List)
        {
            var f = Finishes.Get(id);
            Assert.NotNull(f);
            Assert.True(f!.Size > 0 && f.SizeY > 0 && f.Rgba.Length == f.Size * f.SizeY * 4, id);
            Assert.True(f.TileW > 0 && f.TileH > 0, id);
            // A finish repeats every tile, both ways.
            foreach (var (u, v) in new[] { (0.13, 0.41), (0.77, 0.05), (0.5, 0.93) })
            {
                Assert.Equal(f.Sample(u, v), f.Sample(u + f.TileW, v));
                Assert.Equal(f.Sample(u, v), f.Sample(u, v + 2 * f.TileH));
            }
            Assert.True(f.Palette > 0, id);
            Assert.True(Finishes.Has(id));
            // A textured vertex's own colour is the finish's average, opaque, for a far mesh that's lost its texture.
            uint vc = Finishes.VertexColour(id), avg = f.Average();
            Assert.Equal(0xFFu, vc >> 24);
            Assert.Equal(avg >> 16 & 255, vc & 255);
            Assert.Equal(avg & 255, vc >> 16 & 255);
        }
        uint brick = Finishes.Get("brick-red")!.Average();
        Assert.True((brick >> 16 & 255) > (brick >> 8 & 255) + 30, "red brick is red");
        Assert.True(Finishes.Get("glass")!.Glassy);
        Assert.True(Finishes.Get("metal-seam")!.Metallic > 0.5);
        Assert.Null(Finishes.Get("no-such-finish"));
        Assert.False(Finishes.Has("no-such-finish"));
        // The brick texture has mortar: its lightest and darkest pixels are well apart.
        var b = Finishes.Get("brick-red")!;
        var lum = Enumerable.Range(0, b.Size * b.SizeY).Select(i => b.Rgba[i * 4] + b.Rgba[i * 4 + 1] + b.Rgba[i * 4 + 2]).ToArray();
        Assert.True(lum.Max() - lum.Min() > 150);
    }

    [Fact]
    public void AWallIsASlabWithItsOpeningsCutThrough()
    {
        var wall = Parts.Wall(1, 1, 5, 1, 0, 3, 0.3, "brick-red");
        Assert.Equal(4, wall.Width, 6);
        Assert.Equal(0, wall.Turn, 6);
        var plain = wall.Mesh(1);
        Assert.Equal(4 * 3 * 0.3, Volume(plain), 2);
        wall.Openings.Add((1, 0.9, 1, 1.3));
        var holed = wall.Mesh(1);
        Assert.Equal(0.3 * (12 - 1.3), Volume(holed), 2);
        // Every vertex is drawn with the brick, at a UV in metres.
        Assert.True(holed.Textured);
        Assert.Equal(holed.Positions.Count, holed.UVs.Count);
        Assert.Equal(holed.Positions.Count, holed.Finish.Count);
        Assert.All(holed.Finish, f => Assert.Equal("brick-red", holed.FinishIds[f]));
        float uMax = holed.UVs.Max(u => u.X), vMax = holed.UVs.Max(u => u.Y);
        Assert.InRange(uMax, 3.9f, 4.1f);
        Assert.InRange(vMax, 2.9f, 3.1f);
        // In the opening is outside the wall; beside it is in.
        var (ox, oy, oz) = wall.At(1.5, 0.15, 1.5);
        Assert.False(wall.Inside(ox, oy, oz, 0.125));
        var (wx, wy, wz) = wall.At(0.5, 0.15, 1.5);
        Assert.True(wall.Inside(wx, wy, wz, 0.125));
        // The front faces out to the left of the way it runs: a wall running east faces north (smaller y).
        var (fx, fy, _) = wall.At(1, -1, 0);
        Assert.True(fy < 1 && Math.Abs(fx - 2) < 1e-9);
        // A door's opening at the foot has no sill lining.
        var door = Parts.Wall(0, 0, 3, 0, 0, 3, 0.3, "render-cream");
        door.Openings.Add((1, 0, 1, 2.2));
        Assert.Equal(0.3 * (9 - 2.2), Volume(door.Mesh(1)), 2);
    }

    [Fact]
    public void AFinishedWallsVoxelsAreItsTexturesColoursAndTheCityLeavesThemToThePolygons()
    {
        var m = new VoxModel(128, 128, 64);
        AssetPalette.Apply(m);
        var wall = Parts.Wall(2, 4, 10, 4, 0, 3, 0.3, "brick-red");
        wall.Openings.Add((3, 1, 1, 1.2));
        int n = wall.Voxelize(m, 4);
        Assert.True(n > 0);
        var colours = m.Voxels.Where(c => c != 0).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        _out.WriteLine($"{n} voxels in {colours.Count} colours");
        // Brick and mortar: more than one colour, the most of them reddish.
        Assert.True(colours.Count >= 2);
        uint top = m.Palette[colours.MaxBy(kv => kv.Value).Key];
        Assert.True((top & 255) > (top >> 8 & 255), "the commonest voxel is reddish");
        // None in the opening.
        var (ox, oy, oz) = wall.At(3.5, 0.15, 1.6);
        Assert.Equal(0, m[(int)(ox / 0.125), (int)(oy / 0.125), (int)(oz / 0.125)]);
        // The model drawn beside the polygons has none of them left to show.
        var boxy = Draw.Without(m, 4, new[] { wall });
        Assert.All(boxy.Voxels.Where(c => c != 0), c => Assert.True(boxy.Ghost[c]));
    }

    [Fact]
    public void WallsSaveWithTheirFinishSizeAndOpenings()
    {
        var wall = Parts.Wall(1, 2, 1, 8, 0.25, 5.5, 0.35, "stucco");
        wall.Openings.Add((1, 0, 1, 2.2));
        wall.Openings.Add((3.5, 0.9, 1.2, 1.3));
        var back = Shape.FromJson(System.Text.Json.Nodes.JsonNode.Parse(wall.ToJson().ToJsonString()))!;
        Assert.Equal(ShapeKind.Wall, back.Kind);
        Assert.Equal("stucco", back.Finish);
        Assert.Equal(wall.Width, back.Width, 6);
        Assert.Equal(5.5, back.Height, 6);
        Assert.Equal(0.35, back.Depth, 6);
        Assert.Equal(wall.Turn, back.Turn, 3);
        Assert.Equal(wall.Openings, back.Openings);
        Assert.Equal(Volume(wall.Mesh(1)), Volume(back.Mesh(1)), 4);
        // A clone's openings are its own.
        var copy = wall.Clone();
        copy.Openings.Clear();
        Assert.Equal(2, wall.Openings.Count);
    }

    [Fact]
    public void PartsGoIntoAWallWhereTheyFit()
    {
        var wall = Parts.Wall(0, 0, 6, 0, 0, 3, 0.3, "brick-red");
        var window = Parts.Insert(wall, PartKind.Window, 1);
        Assert.NotNull(window);
        Assert.Single(wall.Openings);
        Assert.Equal((1.0, 0.9, 1.0, 1.3), wall.Openings[0]);
        // A frame (a ring of its own), glass, glazing bars and a sill.
        Assert.Contains(window!, s => s.Finish == "glass");
        Assert.Contains(window!, s => s.Kind == ShapeKind.Wall && s.Openings.Count == 1 && s.Finish == "trim-white");
        Assert.Contains(window!, s => s.Finish == "stone-ashlar");
        // Overlapping, past the end, or too tall: refused, the wall as it was.
        Assert.Null(Parts.Insert(wall, PartKind.Window, 1.5));
        Assert.Null(Parts.Insert(wall, PartKind.Window, 5.5));
        Assert.Null(Parts.Insert(wall, PartKind.TallWindow, 3, sill: 1.5));
        Assert.Single(wall.Openings);
        // A door: its leaf and a step; a garage door its panel; a shopfront glass with mullions and no sill.
        var door = Parts.Insert(wall, PartKind.Door, 3)!;
        Assert.Contains(door, s => s.Finish == "wood-door");
        Assert.Equal(0, wall.Openings[1].Z);
        var shop = Parts.Wall(0, 0, 8, 0, 0, 3.5, 0.3, "render-cream");
        var front = Parts.Insert(shop, PartKind.Shopfront, 1)!;
        Assert.Contains(front, s => s.Finish == "glass");
        Assert.DoesNotContain(front, s => s.Finish == "stone-ashlar");
        Assert.Null(Parts.Insert(Shape.Cylinder(1, 1, 1, 0, 2, AssetPalette.Stone), PartKind.Window, 0.5));
        // Taking one out takes the nearest opening.
        Assert.True(Parts.Remove(wall, 3.4));
        Assert.Single(wall.Openings);
        Assert.Equal(1, wall.Openings[0].X);
    }

    [Fact]
    public void FinishFacesLaysATextureAlongTheSlopesAndKeepsWhatItLeaves()
    {
        // A slope and an upright face.
        var m = new MeshData();
        void Tri(Vector3 a, Vector3 b, Vector3 c, uint colour)
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            int s = m.Positions.Count;
            foreach (var p in new[] { a, b, c }) { m.Positions.Add(p); m.Normals.Add(n); m.Colors.Add(colour); }
            m.Indices.AddRange(new[] { s, s + 1, s + 2 });
        }
        Tri(new(0, 0, 0), new(1, 0, 0), new(0, 1, -1), 0xFF0000FF);
        Tri(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), 0xFF00FF00);
        var roofed = m.FinishFaces("slate", 2, n => n.Y > 0.2f);
        Assert.Equal(new[] { "", "slate" }, roofed.FinishIds);
        Assert.Equal(new[] { 1, 1, 1, 0, 0, 0 }, roofed.Finish);
        Assert.Equal(0xFF00FF00, roofed.Colors[3]);
        // Across the slope is level: the two points at the same height share a v.
        Assert.Equal(roofed.UVs[0].Y, roofed.UVs[1].Y, 4);
        // Up the slope is its true length, √2 world units at 2 m a unit.
        Assert.Equal(2 * MathF.Sqrt(2), MathF.Abs(roofed.UVs[2].Y - roofed.UVs[0].Y), 3);
        // A second finish on the other face keeps the first.
        var both = roofed.FinishFaces("brick-red", 2, (i, _) => i == 3);
        Assert.Equal(new[] { "slate", "slate", "slate", "brick-red", "brick-red", "brick-red" }, both.Finish.Select(f => both.FinishIds[f]));
        Assert.Equal(roofed.UVs[1], both.UVs[1]);
        // Appending a plain mesh to a textured one lines the lists up and remaps the finishes.
        var plain = new MeshData();
        plain.Positions.Add(Vector3.Zero); plain.Normals.Add(Vector3.UnitY); plain.Colors.Add(1);
        var joined = new MeshData().Append(plain).Append(both).Append(plain);
        Assert.Equal(joined.Positions.Count, joined.UVs.Count);
        Assert.Equal(joined.Positions.Count, joined.Finish.Count);
        Assert.Equal(0, joined.Finish[0]);
        Assert.Equal("brick-red", joined.FinishIds[joined.Finish[4]]);
        Assert.Equal(0, joined.Finish[^1]);
    }

    [Fact]
    public void TheBrickHouseExampleIsWallsAndPartsWithATexturedRoofAndVoxelsUnderneath()
    {
        var doc = DemoAsset.PolyHouse();
        var walls = doc.Shapes.Where(s => s.Kind == ShapeKind.Wall).ToList();
        int openings = walls.Sum(w => w.Openings.Count);
        var finishes = doc.Shapes.Select(s => s.Finish).Where(f => f.Length > 0).Distinct().ToList();
        _out.WriteLine($"{doc.Shapes.Count} shapes, {openings} openings, finishes {string.Join(", ", finishes)}, {doc.Model.Count} voxels");
        Assert.True(walls.Count >= 80);
        Assert.True(openings >= 26, $"{openings} openings");
        Assert.Contains("brick-red", finishes);
        Assert.Contains("glass", finishes);
        Assert.Contains("wood-door", finishes);
        Assert.True(doc.Model.Count > 50_000);
        // The roof: slate on the slopes, brick on the gable ends, both textured, and the fascia its colour.
        Assert.Equal("slate", doc.Roof!.Finish);
        Assert.Equal("brick-red", doc.Roof.GableFinish);
        var roof = doc.RoofMesh(1f / 32);
        Assert.Contains("slate", roof.FinishIds);
        Assert.Contains("brick-red", roof.FinishIds);
        Assert.Contains(roof.Finish, f => f == 0);
        // The gable ends are the brick: upright faces, at the house's east and west ends.
        int brickIx = roof.FinishIds.IndexOf("brick-red");
        var gable = Enumerable.Range(0, roof.Indices.Count / 3).Where(t => roof.Finish[roof.Indices[3 * t]] == brickIx).ToList();
        _out.WriteLine($"{gable.Count} gable triangles of {roof.Indices.Count / 3}");
        Assert.True(gable.Count >= 2);
        Assert.All(gable, t => Assert.True(MathF.Abs(roof.Normals[roof.Indices[3 * t]].Y) < 0.2f));
        // The whole house meshes textured, its lists in step.
        var all = new MeshData();
        foreach (var s in doc.Shapes) all.Append(s.Mesh(1f / 16));
        all.Append(roof).Pad();
        Assert.Equal(all.Positions.Count, all.UVs.Count);
        Assert.True(all.FinishIds.Count >= 6);
        // Saved and loaded, it's the same house.
        string path = Path.Combine(Path.GetTempPath(), $"polyhouse-{Guid.NewGuid():N}.vox");
        try
        {
            doc.Save(path);
            var back = AssetDoc.Load(path);
            Assert.Equal(doc.Shapes.Count, back.Shapes.Count);
            Assert.Equal(openings, back.Shapes.Where(s => s.Kind == ShapeKind.Wall).Sum(w => w.Openings.Count));
            Assert.Equal(doc.Roof, back.Roof);
            Assert.Equal(doc.Model.Voxels, back.Model.Voxels);
        }
        finally { File.Delete(path); File.Delete(AssetDoc.SidecarOf(path)); }
    }

    [Fact]
    public void SomeOfTheCitysHousesAreBuiltOfTexturedWallsAndWreckToVoxels()
    {
        foreach (string id in new[] { "r-l-m", "r-l-h" })
        {
            var r = RealSet.Build(id, 1, 1, 1)!;
            var walls = r.Shapes.Where(s => s.Kind == ShapeKind.Wall && s.Finish.Length > 0).ToList();
            _out.WriteLine($"{id}: {walls.Count} walls, {walls.Sum(w => w.Openings.Count)} openings, {r.Roof}");
            Assert.True(walls.Count > 20);
            Assert.True(walls.Sum(w => w.Openings.Count) >= 6);
            Assert.NotNull(r.Roof);
            Assert.NotEqual("", r.Roof!.Finish);
            // The wreck is the voxels, holed: the walls' voxels are there to knock through.
            var wreck = Wreck.Carve(r.Model, r.Detail, 7);
            Assert.True(wreck.Count < r.Model.Count);
            Assert.True(r.Boxy.Voxels.Count(c => c != 0 && !r.Boxy.Ghost[c]) < r.Model.Count / 2);
            // The same on every client.
            Assert.Equal(r.Model.Voxels, RealSet.Build(id, 1, 1, 1)!.Model.Voxels);
        }
        // The cheapest houses and the other variants are still the voxel ones.
        Assert.DoesNotContain(RealSet.Build("r-l-l", 1, 1, 1)!.Shapes, s => s.Kind == ShapeKind.Wall);
        Assert.DoesNotContain(RealSet.Build("r-l-m", 1, 1, 0)!.Shapes, s => s.Kind == ShapeKind.Wall);
    }

    [Fact]
    public void ACarIsOneColour()
    {
        var r = RealSet.Build("r-l-m", 1, 1, 0)!;
        // The car stands on the drive: count the car colours present; a speckled car would bring in most of them.
        var carColours = new[] { AssetPalette.Red, AssetPalette.Blue, AssetPalette.Black, AssetPalette.Yellow, AssetPalette.Chrome };
        int present = carColours.Count(c => r.Model.Voxels.Contains(c));
        Assert.True(present <= 3, $"{present} car colours");
    }

    [Fact]
    public void PartsGoInAndComeOutOfAnAssetsWallsAsUndoSteps()
    {
        var doc = AssetDoc.New("Cottage", 4, 4, 4, 3);
        foreach (var (ax, ay, bx, by) in new[] { (5.0, 7.0, 14.0, 7.0), (14.0, 7.0, 14.0, 13.0), (14.0, 13.0, 5.0, 13.0), (5.0, 13.0, 5.0, 7.0) })
            doc.AddShape(Parts.Wall(ax, ay, bx, by, 0, 3.2, 0.3, "render-cream"));
        var south = doc.Shapes[2];
        int before = doc.Model.Count;
        Assert.True(doc.InsertPart(south, PartKind.Window, 2, 0.9));
        var holed = doc.Shapes.First(s => s.Kind == ShapeKind.Wall && s.Openings.Count == 1 && s.Depth > 0.2);
        Assert.DoesNotContain(south, doc.Shapes);
        Assert.Contains(doc.Shapes, s => s.Finish == "glass");
        // Its opening's voxels went (the glass and frame put some back, thinner).
        var (ox, oy, oz) = holed.At(2.5, 0.26, 1.5);
        Assert.Equal(0, doc.Model[(int)(ox / 0.125), (int)(oy / 0.125), (int)(oz / 0.125)]);
        // From behind: it faces the other way, so its sill stands proud inside.
        var north = doc.Shapes[0];
        Assert.True(doc.InsertPart(north, PartKind.Door, 3, fromBack: true));
        Assert.Contains(doc.Shapes, s => s.Kind == ShapeKind.Wall && s.Openings.Any(o => o.Z == 0) && Math.Abs(Math.Cos(s.Turn) + 1) < 1e-6);
        // Taken out: its opening filled and its frame, glass and sill gone.
        int shapes = doc.Shapes.Count;
        Assert.True(doc.RemovePart(holed, 2.5));
        Assert.True(doc.Shapes.Count < shapes - 3);
        Assert.Equal(0, doc.Shapes.Where(s => s.Kind == ShapeKind.Wall && s.Depth > 0.2 && Math.Abs(s.Y - 13) < 1e-6).Sum(w => w.Openings.Count));
        Assert.Equal(0, doc.Shapes.Count(s => s.Finish == "glass"));
        // Painted: one wall stone; the roof's gables take the walls' commonest finish.
        var east = doc.Shapes.First(s => s.Kind == ShapeKind.Wall && Math.Abs(s.Turn - Math.PI / 2) < 1e-6 && s.Depth > 0.2);
        var stone = doc.SetFinish(east, "stone-rubble");
        Assert.Equal("stone-rubble", stone.Finish);
        Assert.Equal("render-cream", doc.WallFinish());
        // Every step undoes.
        doc.Undo(); doc.Undo(); doc.Undo(); doc.Undo();
        Assert.Equal(4, doc.Shapes.Count);
        Assert.All(doc.Shapes, s => Assert.Empty(s.Openings));
        Assert.Equal(before, doc.Model.Count);
        // A wall turned round runs back along its back, its openings where they were.
        var w = Parts.Wall(0, 0, 4, 0, 0, 3, 0.3, "brick-red");
        w.Openings.Add((0.5, 1, 1, 1));
        var f = Parts.Flipped(w);
        var (fx, fy, _) = f.At(0, 0, 0);
        Assert.Equal(4, fx, 6); Assert.Equal(0.3, fy, 6);
        Assert.Equal(2.5, f.Openings[0].X, 6);
        Assert.Equal(Volume(w.Mesh(1)), Volume(f.Mesh(1)), 4);
    }

    [Fact]
    public void APhotoGoesOnAWallItsSizeAndIsSavedWithTheAsset()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh, DepthMetres = 0.3 };
        var doc = AssetDoc.New("Front", 4, 4, 4, 4);
        var wall = PhotoImport.AsWall(doc, photo, corners, o, FacadeSide.South)!;
        Assert.NotNull(wall);
        Assert.StartsWith("photo:", wall.Finish);
        Assert.Equal(fw, wall.Width, 6);
        Assert.Equal(fh, wall.Height, 6);
        var f = Finishes.Get(wall.Finish)!;
        Assert.Equal(fw, f.TileW, 6);
        Assert.True(f.Size <= 768 && f.SizeY <= 768);
        // The same picture is the same finish.
        Assert.Equal(wall.Finish, PhotoImport.AsFinish(photo, corners, o).Id);
        // The wall's front faces south (out of the model), at its south edge.
        var (_, fy, _) = wall.At(0, -1, 0);
        Assert.True(fy > wall.Y);
        Assert.True(doc.Model.Count > 1000);
        // Its voxels have the brick's red and the windows' glass in them.
        var colours = doc.Model.Voxels.Where(c => c != 0).Distinct().Count();
        Assert.True(colours >= 3, $"{colours} colours");
        // Saved, the photo's pixels go in the asset's file; loaded, the finish is there for the wall.
        string path = Path.Combine(Path.GetTempPath(), $"front-{Guid.NewGuid():N}.vox");
        try
        {
            doc.Save(path);
            var json = File.ReadAllText(AssetDoc.SidecarOf(path));
            Assert.Contains("\"finishes\"", json);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!["finishes"]![0]!;
            node["id"] = "photo:reloaded";
            var back = Finishes.FromJson(node)!;
            Assert.Equal(f.Rgba, back.Rgba);
            Assert.Equal(f.TileH, back.TileH, 6);
            Assert.Single(AssetDoc.Load(path).Shapes);
        }
        finally { File.Delete(path); File.Delete(AssetDoc.SidecarOf(path)); }
        // Too big for the model: refused, nothing changed.
        var small = AssetDoc.New("Small", 4, 2, 2, 2);
        Assert.Null(PhotoImport.AsWall(small, photo, corners, o));
        Assert.Empty(small.Shapes);
    }

    [Fact]
    public void PngsReadAndWriteTheSamePixels()
    {
        var f = Finishes.Get("brick-red")!;
        var png = ImageDecode.WritePng(f.Size, f.SizeY, f.Rgba);
        var back = ImageDecode.Png(png)!.Value;
        Assert.Equal((f.Size, f.SizeY), (back.W, back.H));
        Assert.Equal(f.Rgba, back.Rgba);
        Assert.Null(ImageDecode.Png(new byte[] { 1, 2, 3 }));
        Assert.Null(ImageDecode.Read(new byte[40]));
    }

    [Fact]
    public void AModelExportsAsGlbAndComesBackWithItsTextures()
    {
        var doc = DemoAsset.PolyHouse();
        var mesh = new MeshData();
        foreach (var s in doc.Shapes) mesh.Append(s.Mesh(1f));
        mesh.Append(doc.RoofMesh(0.125f)).Pad();
        var glb = GltfFile.Write(mesh);
        var back = GltfFile.Read(glb);
        _out.WriteLine($"{mesh.Indices.Count / 3} triangles, {glb.Length / 1024} KB, {back.FinishIds.Count - 1} textures back");
        Assert.Equal(mesh.Indices.Count, back.Indices.Count);
        Assert.True(back.Textured);
        Assert.All(back.FinishIds.Skip(1), id => Assert.StartsWith("model:", id));
        Assert.True(back.FinishIds.Count - 1 >= mesh.FinishIds.Count - 1 - 1);
        // The same place and size.
        var lo = mesh.Positions.Aggregate(Vector3.Min); var hi = mesh.Positions.Aggregate(Vector3.Max);
        var blo = back.Positions.Aggregate(Vector3.Min); var bhi = back.Positions.Aggregate(Vector3.Max);
        Assert.True(Vector3.Distance(lo, blo) < 1e-3 && Vector3.Distance(hi, bhi) < 1e-3);
        // A brick wall's colour comes back from its texture: sampled by the model's own coordinates, it's red.
        int brick = back.FinishIds.FindIndex(id => id.Length > 0 && Finishes.Get(id)?.Name == "brick-red");
        Assert.True(brick > 0);
        var reds = Enumerable.Range(0, back.Positions.Count).Where(i => back.Finish[i] == brick).Take(50)
            .Select(i => Finishes.Get(back.FinishIds[brick])!.Sample(back.UVs[i].X + 0.013, back.UVs[i].Y + 0.017)).ToList();
        Assert.True(reds.Count(c => (c >> 16 & 255) > (c >> 8 & 255) + 20) > reds.Count / 2);
    }

    [Fact]
    public void AnImportedModelIsAShapeDrawnAsItIsAndKeptAsVoxels()
    {
        // An OBJ shed with a texture and a plain roof, read with its .mtl and picture.
        var buff = Finishes.Get("brick-buff")!;
        var files = new Dictionary<string, byte[]>
        {
            ["shed.mtl"] = System.Text.Encoding.UTF8.GetBytes("newmtl brick\nKd 0.8 0.7 0.5\nmap_Kd buff.png\nnewmtl roof\nKd 0.25 0.27 0.3\n"),
            ["buff.png"] = ImageDecode.WritePng(buff.Size, buff.SizeY, buff.Rgba),
        };
        string obj = "mtllib shed.mtl\nv 0 0 0\nv 6 0 0\nv 6 0 4\nv 0 0 4\nv 0 3 0\nv 6 3 0\nv 6 3 4\nv 0 3 4\nv 0 4.2 2\nv 6 4.2 2\n" +
            "vt 0 0\nvt 6.667 0\nvt 6.667 5\nvt 0 5\nvt 4.444 0\nvt 4.444 5\n" +
            "usemtl brick\nf 4/1 3/2 7/3 8/4\nf 2/1 1/2 5/3 6/4\nf 3/1 2/5 6/6 7/4\nf 1/1 4/5 8/6 5/4\nusemtl roof\nf 8 7 10 9\nf 6 5 9 10\nf 5 8 9\nf 7 6 10\n";
        var mesh = ObjFile.Read(obj, name => files.GetValueOrDefault(name));
        Assert.Equal(14, mesh.Indices.Count / 3);
        Assert.True(mesh.Textured);
        Assert.Contains(mesh.Finish, f => f > 0);
        Assert.Contains(mesh.Finish, f => f == 0);
        Assert.All(mesh.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
        var doc = AssetDoc.New("Shed", 4, 3, 3, 3);
        var (shape, shrunk) = doc.ImportModel(mesh)!.Value;
        Assert.Equal(1, shrunk, 6);
        Assert.Equal(ShapeKind.Imported, shape.Kind);
        // Drawn: its triangles where it stands, in the model's middle, on the ground.
        var drawn = shape.Mesh(1f);
        Assert.Equal(14, drawn.Indices.Count / 3);
        var lo = drawn.Positions.Aggregate(Vector3.Min); var hi = drawn.Positions.Aggregate(Vector3.Max);
        Assert.Equal(6, hi.X - lo.X, 3); Assert.Equal(4.2f, hi.Y - lo.Y, 3); Assert.Equal(4, hi.Z - lo.Z, 3);
        Assert.Equal(doc.WidthMetres / 2, (lo.X + hi.X) / 2, 2);
        // Its voxels: its surface, buff brick where it's brick and grey on the roof.
        Assert.True(doc.Model.Count > 3000, $"{doc.Model.Count} voxels");
        var colours = doc.Model.Voxels.Where(c => c != 0).GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => doc.Model.Palette[g.Key]).Take(3).ToList();
        Assert.Contains(colours, c => (c & 255) > (c >> 16 & 255) + 20);  // something warm: the brick (r > b)
        // The city draws none of its voxels beside its polygons.
        var boxy = Draw.Without(doc.Model, doc.Detail, doc.Shapes);
        Assert.All(boxy.Voxels.Where(c => c != 0), c => Assert.True(boxy.Ghost[c]));
        // Saved and loaded, triangles, textures and all; mirrored, it still faces out.
        var back = Shape.FromJson(System.Text.Json.Nodes.JsonNode.Parse(shape.ToJson().ToJsonString()))!;
        Assert.Equal(shape.Triangles!.Positions, back.Triangles!.Positions);
        Assert.Equal(shape.Triangles.UVs, back.Triangles.UVs);
        Assert.Equal(shape.Triangles.FinishIds, back.Triangles.FinishIds);
        Assert.Equal(Volume(drawn), Volume(back.Mesh(1f)), 3);
        Assert.Equal(Volume(drawn), Volume(shape.MirroredX(doc.WidthMetres).Mesh(1f)), 3);
        // A model too big for the asset is shrunk to fit.
        var big = new MeshData().Append(mesh);
        for (int i = 0; i < big.Positions.Count; i++) big.Positions[i] *= 10;
        var (_, k) = AssetDoc.New("Small", 4, 2, 2, 2).ImportModel(big)!.Value;
        Assert.True(k < 0.2);
    }

    [Fact]
    public void AGltfModelInTheCityWrecksToVoxelsInItsTexturesColours()
    {
        var doc = DemoAsset.PolyHouse();
        var mesh = new MeshData();
        foreach (var s in doc.Shapes) mesh.Append(s.Mesh(1f));
        var fitted = GltfFile.FitToLot(GltfFile.Read(GltfFile.Write(mesh)), 1, 1);
        Assert.True(fitted.Textured);
        Assert.Equal(fitted.Positions.Count, fitted.UVs.Count);
        var vox = MeshVoxels.Surface(fitted, 64, 64, 64, 1f / 64);
        _out.WriteLine($"{vox.Count} voxels");
        Assert.True(vox.Count > 5_000);
        // More of it is reddish (the brick) than anything else.
        int red = vox.Voxels.Count(c => c != 0 && (vox.Palette[c] & 255) > (vox.Palette[c] >> 8 & 255) + 20);
        _out.WriteLine($"{red} reddish");
        Assert.True(red > vox.Count / 3, "mostly brick red");
        Assert.True(Wreck.Carve(vox, 2, 5).Count < vox.Count);
    }
}
