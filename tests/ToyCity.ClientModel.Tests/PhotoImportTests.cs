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
        _out.WriteLine(string.Join("; ", f.Colours.Select((c, i) => $"{AssetPalette.NameOf(f.Entry[i])} {f.Share[i]:P0}{(f.Recessed[i] ? " recessed" : "")}")));
        // The wall is the main colour, matched to brick, and stays on the face.
        Assert.Equal(AssetPalette.Brick, f.Entry[f.Main]);
        Assert.False(f.Recessed[f.Main]);
        // The glass low in every window is set back; the brick between them isn't.
        for (int storey = 0; storey < 2; storey++)
            for (int bay = 0; bay < 4; bay++)
            {
                Assert.True(f.Recessed[f[(int)((1.8 + 3 * bay) * 4), (int)((storey * 3 + 2.0) * 4)]], $"storey {storey} bay {bay}");
                Assert.False(f.Recessed[f[(int)((3.0 + 3 * bay) * 4) % 48, (int)((storey * 3 + 1.6) * 4)]]);
            }
        // The door is set back too.
        Assert.True(f.Recessed[f[(int)(4.5 * 4), (int)(7.8 * 4)]]);
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
}
