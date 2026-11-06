using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using Xunit;
using Xunit.Abstractions;

namespace ToyCity.ClientModel.Tests;

/// <summary>RC part 2.2: the photo importer straightens a facade, cuts it to voxels at scale, and builds it.</summary>
public class PhotoImportTests
{
    readonly ITestOutputHelper _out;
    public PhotoImportTests(ITestOutputHelper output) => _out = output;

    static (double R, double G, double B) Rgb(uint c) { var (r, g, b, _) = VoxModel.Unpack(c); return (r / 255.0, g / 255.0, b / 255.0); }

    [Fact]
    public void AHomographyTakesTheCornersWhereTheyGo()
    {
        var from = new[] { (10.0, 20.0), (300.0, 40.0), (280.0, 260.0), (30.0, 240.0) };
        var to = new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) };
        var h = PhotoImport.Homography(from, to)!;
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = PhotoImport.Map(h, from[i].Item1, from[i].Item2);
            Assert.Equal(to[i].Item1, x, 6);
            Assert.Equal(to[i].Item2, y, 6);
        }
        // And back again.
        var back = PhotoImport.Homography(to, from)!;
        var (mx, my) = PhotoImport.Map(h, 150, 140);
        var (bx, by) = PhotoImport.Map(back, mx, my);
        Assert.Equal(150, bx, 6);
        Assert.Equal(140, by, 6);
        // Three points in a line can't be a facade.
        Assert.Null(PhotoImport.Homography(new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 2.0), (0.0, 5.0) }, to));
    }

    [Fact]
    public void StraighteningAKeystonedPhotoPutsTheWindowsBackOnTheirGrid()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        // 4 pixels a metre: 48 × 36.
        var flat = PhotoImport.Straighten(photo, corners, (int)(fw * 4), (int)(fh * 4));
        Assert.Equal(48, flat.Width);
        // Glass in the middle of every upstairs window, brick between the windows, on every floor.
        for (int storey = 0; storey < 2; storey++)
            for (int bay = 0; bay < 4; bay++)
            {
                var glass = Rgb(flat[(int)((1.8 + 3 * bay) * 4), (int)((storey * 3 + 2.0) * 4)]);
                Assert.True(glass.B > glass.R + 0.1, $"storey {storey} bay {bay}: glass {glass}");
                var brick = Rgb(flat[(int)((3.0 + 3 * bay) * 4) % 48, (int)((storey * 3 + 1.6) * 4)]);
                Assert.True(brick.R > brick.B + 0.2, $"storey {storey} bay {bay}: brick {brick}");
            }
    }

    [Fact]
    public void ReadingAFacadeCutsItToVoxelsAtScaleAndFindsTheWindows()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var f = PhotoImport.Read(photo, corners, 2, new PhotoOptions { WidthMetres = fw, HeightMetres = fh });
        // 0.25 m voxels: 12 m is 48 across, 9 m is 36 up.
        Assert.Equal(48, f.Width);
        Assert.Equal(36, f.Height);
        _out.WriteLine(string.Join("; ", f.Colours.Select((c, i) => $"{AssetPalette.NameOf(f.Entry[i])} {f.Share[i]:P0}{(f.SetBack(i) ? " recessed" : "")}")));
        // The wall is the main colour, matched to brick, and stays on the face.
        Assert.Equal(AssetPalette.Brick, f.Entry[f.Main]);
        Assert.False(f.SetBack(f.Main));
        // The glass low in every window is set back; the brick between them isn't.
        for (int storey = 0; storey < 2; storey++)
            for (int bay = 0; bay < 4; bay++)
            {
                Assert.True(f.SetBack(f[(int)((1.8 + 3 * bay) * 4), (int)((storey * 3 + 2.0) * 4)]), $"storey {storey} bay {bay}");
                Assert.False(f.SetBack(f[(int)((3.0 + 3 * bay) * 4) % 48, (int)((storey * 3 + 1.6) * 4)]));
            }
        // The door is set back too.
        Assert.True(f.SetBack(f[(int)(4.5 * 4), (int)(7.8 * 4)]));
    }

    [Fact]
    public void BuildingAFacadeIsOneUndoStepWithTheWindowsSetBack()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh, DepthMetres = 0.5, RecessMetres = 0.25 };
        var doc = AssetDoc.New("Terrace", 2, 4, 4, 4);   // 64 × 64 × 48 voxels: the 48 × 36 facade fits
        var f = PhotoImport.Read(photo, corners, doc.Detail, o, doc.Model.Palette);
        var placed = PhotoImport.Build(doc, f, o)!;
        Assert.False(placed.Resized);
        Assert.Equal(2, placed.Depth);   // 0.5 m at 0.25 m voxels
        Assert.True(doc.CanUndo);
        int front = placed.FrontY, x0 = placed.X0;
        // Brick on the face between the windows; at a window, the face is open and the glass is a voxel back.
        int bx = x0 + 12, wx = x0 + 7, z = 36 - 1 - (int)(2.0 * 4);   // second floor up, 2 m into the storey
        Assert.Equal(AssetPalette.Brick, doc[bx, front, 36 - 1 - (int)(1.6 * 4)]);
        Assert.Equal(0, doc[wx, front, z]);
        Assert.NotEqual(0, doc[wx, front - 1, z]);
        Assert.NotEqual(AssetPalette.Brick, doc[wx, front - 1, z]);
        // Nothing behind a thin facade.
        Assert.Equal(0, doc[bx, front - 2, z]);
        doc.Undo();
        Assert.Null(doc.Bounds());
    }

    [Fact]
    public void ADeepFacadeIsASolidBlockInTheWallsColour()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh, DepthMetres = 8 };
        var doc = AssetDoc.New("Terrace", 2, 3, 3, 3);   // 48 × 48 × 36
        var f = PhotoImport.Read(photo, corners, doc.Detail, o, doc.Model.Palette);
        var placed = PhotoImport.Build(doc, f, o)!;
        Assert.Equal(32, placed.Depth);
        // The sides and the back are brick.
        Assert.Equal(AssetPalette.Brick, doc[placed.X0, placed.FrontY - 20, 10]);
        Assert.Equal(AssetPalette.Brick, doc[placed.X0 + 20, placed.FrontY - 31, 10]);
    }

    [Fact]
    public void TheModelGrowsToFitAndTooBigIsRefused()
    {
        var (photo, corners, _, _) = DemoAsset.FacadePhoto(320, 240);
        var doc = AssetDoc.New("Small", 2, 1, 1, 1);   // 16 × 16 × 12
        var o = new PhotoOptions { WidthMetres = 12, HeightMetres = 9 };
        var placed = PhotoImport.Build(doc, PhotoImport.Read(photo, corners, 2, o), o)!;
        Assert.True(placed.Resized);
        Assert.Equal(48, doc.Model.SizeX);
        Assert.Equal(36, doc.Model.SizeZ);
        // 40 m at 6.25 cm voxels is 640 voxels: more than a model can hold.
        var big = new PhotoOptions { WidthMetres = 40, HeightMetres = 9 };
        Assert.Null(PhotoImport.Build(AssetDoc.New("Big", 8), PhotoImport.Read(photo, corners, 8, big), big));
    }

    [Fact]
    public void SimplifyingMakesTheWallMeshInFewerPieces()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        int Triangles(int colours, int smooth)
        {
            var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh, Colours = colours, Smooth = smooth, RecessMetres = 0 };   // flat, so only the colours count
            var doc = AssetDoc.New("Terrace", 4, 4, 4, 4);   // 0.125 m voxels: 96 × 72
            PhotoImport.Build(doc, PhotoImport.Read(photo, corners, doc.Detail, o, doc.Model.Palette), o);
            return VoxMesher.Mesh(doc.Model).Indices.Count / 3;
        }
        int raw = Triangles(16, 0), fewer = Triangles(6, 0), simple = Triangles(6, 2);
        _out.WriteLine($"16 colours unfiltered: {raw} triangles; 6 colours: {fewer}; 6 colours, filtered twice: {simple}");
        Assert.True(fewer < raw, $"{fewer} against {raw}");
        Assert.True(simple < fewer, $"{simple} against {fewer}");
        Assert.True(simple < raw * 0.8, $"{simple} against {raw}");
    }

    [Fact]
    public void AColourCanStandOutOfTheFace()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh };
        var doc = AssetDoc.New("Terrace", 2, 4, 4, 4);
        var f = PhotoImport.Read(photo, corners, doc.Detail, o, doc.Model.Palette);
        // The cornice along the top stands 0.5 m proud: two voxels at 25 cm.
        byte cornice = f[24, 0];
        f.Offset[cornice] = 0.5;
        var placed = PhotoImport.Build(doc, f, o)!;
        Assert.Equal(doc.Model.SizeY - 3, placed.FrontY);
        int top = f.Height - 1, x = placed.X0 + 24;
        Assert.Equal(f.Entry[cornice], doc[x, placed.FrontY + 1, top]);
        Assert.Equal(f.Entry[cornice], doc[x, placed.FrontY + 2, top]);
        // Brick below it stays on the face.
        int brickRow = (int)(1.6 * 4), bx = placed.X0 + 12;
        Assert.Equal(AssetPalette.Brick, doc[bx, placed.FrontY, top - brickRow]);
        Assert.Equal(0, doc[bx, placed.FrontY + 1, top - brickRow]);
    }

    [Fact]
    public void APatchOfOneColourIsSelectedAndPushedInOrPulledOut()
    {
        // A brick wall facing south with two 2 × 2 patches of glass on it.
        var doc = new AssetDoc("Wall", 2, 12, 4, 10);
        doc.Box(0, 2, 0, 11, 3, 9, AssetPalette.Brick);
        doc.Box(2, 3, 2, 3, 3, 3, AssetPalette.Glass);
        doc.Box(7, 3, 2, 8, 3, 3, AssetPalette.Glass);
        var one = doc.SelectFace(2, 3, 2, 0, 1, 0);
        Assert.Equal(4, one.Count);
        Assert.Equal(8, doc.SelectFace(2, 3, 2, 0, 1, 0, everywhere: true).Count);
        // Brick round the patch isn't picked up, nor a face that's covered.
        Assert.Empty(doc.SelectFace(2, 2, 2, 0, 1, 0));
        // Pushed in a voxel: the patch moves back, still glass, and the wall isn't holed.
        var moved = doc.Extrude(one, 0, 1, 0, -1);
        Assert.Equal(4, moved.Count);
        Assert.Equal(0, doc[2, 3, 2]);
        Assert.Equal(AssetPalette.Glass, doc[2, 2, 2]);
        Assert.Empty(doc.Extrude(moved, 0, 1, 0, -1));   // the last voxel stays
        Assert.Equal(AssetPalette.Glass, doc[2, 2, 2]);
        doc.Undo(); doc.Undo();
        Assert.Equal(AssetPalette.Glass, doc[2, 3, 2]);
        // Pulled out: the model is 4 deep, so only to its edge (y = 3 is the last row): nothing to add.
        Assert.Empty(doc.Extrude(one, 0, 1, 0, 2));
        // The other way, up from the top of the wall, two voxels.
        var top = doc.SelectFace(5, 2, 9, 0, 0, 1);
        Assert.Equal(24, top.Count);   // 12 across, 2 deep
        Assert.Empty(doc.Extrude(top, 0, 0, 1, 2));   // 10 tall already: the model's top
        var side = doc.SelectFace(11, 2, 5, 1, 0, 0);
        Assert.Empty(doc.Extrude(side, 1, 0, 0, 1));   // and its east edge
        doc.Resize(16, 4, 10);
        var grown = doc.Extrude(doc.SelectFace(11, 2, 5, 1, 0, 0), 1, 0, 0, 2);
        Assert.Equal(20, grown.Count);   // 2 deep, 10 tall
        Assert.Equal(AssetPalette.Brick, doc[13, 2, 5]);
    }

    /// <summary>A tiny facade 4 across and 2 up: brick with a red column on its left as seen from outside.</summary>
    static Facade LeftRed() => new()
    {
        Width = 4, Height = 2, Cells = new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }, Colours = new[] { (0.6, 0.3, 0.2), (0.8, 0.2, 0.2) },
        Entry = new[] { AssetPalette.Brick, AssetPalette.Red }, Offset = new[] { 0.0, 0.0 }, Share = new[] { 0.75, 0.25 }, Light = new double[8],
    };

    [Theory]
    [InlineData(FacadeSide.South)]
    [InlineData(FacadeSide.North)]
    [InlineData(FacadeSide.East)]
    [InlineData(FacadeSide.West)]
    public void EachSideFacesOutwardTheRightWayRound(FacadeSide side)
    {
        var doc = new AssetDoc("Side", 2, 8, 8, 4);
        var p = PhotoImport.Build(doc, LeftRed(), new PhotoOptions { DepthMetres = 0.25 }, side)!;
        Assert.Equal(8, p.Voxels);
        // Seen from outside, the left of a south face is west, of a north face east, of an east face south, of a west face north.
        var (x, y) = side switch { FacadeSide.South => (2, 7), FacadeSide.North => (5, 0), FacadeSide.East => (7, 5), _ => (0, 2) };
        Assert.Equal(AssetPalette.Red, doc[x, y, 0]);
        Assert.Equal(AssetPalette.Red, doc[x, y, 1]);
        Assert.Equal(8, doc.Model.Count);
    }

    [Fact]
    public void ASecondPhotoGoesOnTheSideOfWhatsBuilt()
    {
        var doc = new AssetDoc("Block", 2, 16, 16, 4);
        doc.Box(4, 2, 0, 11, 9, 3, AssetPalette.Concrete);   // a block 8 × 8, its east face at x = 11
        var p = PhotoImport.Build(doc, LeftRed(), new PhotoOptions { DepthMetres = 0.25 }, FacadeSide.East)!;
        Assert.Equal(11, p.Face);
        // Centred on the block's 8 along y (2..9): the 4 wide facade covers y 4..7, its left (red) at the south end.
        Assert.Equal(AssetPalette.Red, doc[11, 7, 0]);
        Assert.Equal(AssetPalette.Brick, doc[11, 4, 0]);
        Assert.Equal(AssetPalette.Concrete, doc[11, 3, 0]);
        Assert.Equal(AssetPalette.Concrete, doc[10, 5, 0]);   // only the face changes on a thin facade
        Assert.Equal(16, doc.Model.SizeX);   // nothing stood out, so the model didn't grow
    }

    [Fact]
    public void OnePhotoWrapsRoundAllFourSides()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh, DepthMetres = 0.5 };
        var doc = AssetDoc.New("Terrace", 2, 4, 4, 4);   // 64 × 64 × 48
        var f = PhotoImport.Read(photo, corners, doc.Detail, o, doc.Model.Palette);
        var p = PhotoImport.Wrap(doc, f, o)!;
        var b = doc.Bounds()!.Value;
        // A 12 m square block (48 voxels a side), 9 m tall.
        Assert.Equal(47, b.X1 - b.X0);
        Assert.Equal(47, b.Y1 - b.Y0);
        Assert.Equal(35, b.Z1);
        // Windows set back on every side: the face is open at a window, brick beside it.
        int z = 36 - 1 - (int)(2.0 * 4);
        int u = (int)(1.8 * 4), brick = (int)(3.0 * 4);
        Assert.Equal(0, doc[b.X0 + u, b.Y1, z]);                  // south
        Assert.NotEqual(0, doc[b.X0 + u, b.Y1 - 1, z]);
        Assert.Equal(0, doc[b.X1 - u, b.Y0, z]);                  // north, seen from outside so mirrored
        Assert.Equal(0, doc[b.X1, b.Y1 - u, z]);                  // east
        Assert.Equal(0, doc[b.X0, b.Y0 + u, z]);                  // west
        Assert.Equal(AssetPalette.Brick, doc[b.X1, b.Y1 - brick, 36 - 1 - (int)(1.6 * 4)]);
        // Inside, the wall's colour; and it's one undo step.
        Assert.Equal(AssetPalette.Brick, doc[b.X0 + 24, b.Y0 + 24, 10]);
        doc.Undo();
        Assert.Null(doc.Bounds());
    }

    [Fact]
    public void ADepthMapOrTheLightPushesCellsInAndOut()
    {
        var (photo, corners, fw, fh) = DemoAsset.FacadePhoto();
        var o = new PhotoOptions { WidthMetres = fw, HeightMetres = fh };
        var f = PhotoImport.Read(photo, corners, 2, o);
        int glass = (int)(1.8 * 4) + f.Width * (int)(2.0 * 4), cornice = 24 + f.Width * 1, brick = 12 + f.Width * (int)(1.6 * 4);
        // Its depth map (lighter nearer): glass goes in, the cornice comes out, brick stays.
        var (depth, _, _, _) = DemoAsset.FacadePhoto(depthMap: true);
        var map = PhotoImport.ReliefFromDepthMap(f, photo, depth, corners, 0.5);
        Assert.True(map[glass] < -0.05, $"glass {map[glass]}");
        Assert.True(map[cornice] > 0.05, $"cornice {map[cornice]}");
        Assert.InRange(map[brick], -0.02, 0.02);
        Assert.All(map, v => Assert.InRange(v, -0.5, 0.5));
        // A depth map at half the photo's size reads the same places.
        var (half, _, _, _) = DemoAsset.FacadePhoto(480, 360, depthMap: true);
        var small = PhotoImport.ReliefFromDepthMap(f, photo, half, corners, 0.5);
        Assert.True(small[glass] < -0.05 && small[cornice] > 0.05);
        // From the light alone: the white cornice comes out, the dark door goes in (glass is about as light as the
        // brick here, which is why it's only a rough guess).
        var light = PhotoImport.ReliefFromLight(f, 0.25);
        int door = (int)(4.5 * 4) + f.Width * (int)(7.8 * 4);
        Assert.True(light[cornice] > 0 && light[door] < 0, $"cornice {light[cornice]}, door {light[door]}");
        // Built with relief, the cornice stands out of the face.
        f.Relief = light;
        var doc = AssetDoc.New("Terrace", 2, 4, 4, 4);
        var p = PhotoImport.Build(doc, f, o)!;
        Assert.True(p.Face < doc.Model.SizeY - 1);
        Assert.NotEqual(0, doc[p.X0 + 24, p.Face + 1, f.Height - 2]);
    }
}
