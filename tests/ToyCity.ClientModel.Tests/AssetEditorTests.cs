using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.ClientModel.Tests;

/// <summary>The asset library and editor (RC part 1): pieces, placing, undo, mirroring, picking and files.</summary>
public class AssetEditorTests
{
    [Fact]
    public void EveryPieceBuildsInEveryMaterialAndDetail()
    {
        foreach (var p in Pieces.All)
            foreach (int k in new[] { 1, 2, 4 })
                foreach (var mat in Enum.GetValues<Material>())
                {
                    var m = p.Build(mat, k);
                    Assert.True(m.Count > 0, $"{p.Id} at {k} in {mat} is empty");
                    if (p.Snap)
                    {
                        // A snap piece fills exactly its frame: a module square and a storey tall.
                        Assert.Equal((Pieces.Module * k, Pieces.Module * k, Pieces.Storey * k), (m.SizeX, m.SizeY, m.SizeZ));
                    }
                    // Only named colours of the palette are used.
                    Assert.All(m.Voxels.Where(v => v != 0), v => Assert.InRange(v, 1, 31));
                    if (!p.HasMaterial) break;
                }
        Assert.Equal(Pieces.All.Count, Pieces.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void AWallTurnsOntoEachEdgeOfItsFrame()
    {
        var wall = Pieces.Find("wall")!;
        int M = Pieces.Module;
        for (int r = 0; r < 4; r++)
        {
            var doc = AssetDoc.New("t", 1, 2, 2, 1);
            doc.PlacePiece(wall, Material.Brick, 1, 0, 0, r);
            // The wall stands in frame (1, 0): x 8..15, y 0..7.
            var b = doc.Bounds()!.Value;
            Assert.True(b.X0 >= M && b.X1 < 2 * M && b.Y0 >= 0 && b.Y1 < M, $"rotation {r}: {b}");
            var (x0, y0, x1, y1) = r switch
            {
                0 => (M, 0, 2 * M - 1, 0),          // north
                1 => (2 * M - 1, 0, 2 * M - 1, M - 1), // east
                2 => (M, M - 1, 2 * M - 1, M - 1),    // south
                _ => (M, 0, M, M - 1),              // west
            };
            Assert.NotEqual(0, doc[x0, y0, 2]);
            Assert.NotEqual(0, doc[x1, y1, 2]);
            // The middle of the frame stays open.
            Assert.Equal(0, doc[M + M / 2, M / 2, 2]);
        }
    }

    [Fact]
    public void PiecesInOneFrameStandTogether()
    {
        var doc = AssetDoc.New("t", 2, 1, 1, 1);
        doc.PlacePiece(Pieces.Find("floor")!, Material.Wood, 0, 0, 0, 0);
        int floor = doc.Model.Count;
        doc.PlacePiece(Pieces.Find("wall-window")!, Material.Wood, 0, 0, 0, 0);
        // The wall's empty voxels (its window) don't punch through the floor.
        Assert.True(doc.Model.Count > floor);
        for (int x = 0; x < doc.Model.SizeX; x++) Assert.NotEqual(0, doc[x, doc.Model.SizeY / 2, 0]);
    }

    [Fact]
    public void UndoAndRedoStepThroughEveryChange()
    {
        var doc = AssetDoc.New("t", 1);
        var empty = (byte[])doc.Model.Voxels.Clone();
        doc.Set(1, 1, 0, AssetPalette.Red);
        doc.Box(0, 0, 0, 5, 5, 2, AssetPalette.Stone);
        doc.PlacePiece(Pieces.Find("roof-hip")!, Material.Wood, 1, 1, 1, 0);
        doc.Paint(3, 3, 1, AssetPalette.Blue);
        doc.MirrorX();
        var full = (byte[])doc.Model.Voxels.Clone();
        Assert.Equal(AssetPalette.Blue, doc[doc.Model.SizeX - 1 - 3, 3, 1]);
        while (doc.Undo()) { }
        Assert.Equal(empty, doc.Model.Voxels);
        while (doc.Redo()) { }
        Assert.Equal(full, doc.Model.Voxels);

        // A new change after an undo drops the redo history; several changes in Begin/End undo as one.
        doc.Undo();
        doc.Begin();
        doc.Set(0, 0, 10, AssetPalette.Lamp);
        doc.Set(1, 0, 10, AssetPalette.Lamp);
        doc.End();
        Assert.False(doc.CanRedo);
        doc.Undo();
        Assert.Equal(0, doc[0, 0, 10]);
        Assert.Equal(0, doc[1, 0, 10]);
    }

    [Fact]
    public void PaintOnlyRecoloursWhatsThere()
    {
        var doc = AssetDoc.New("t", 1);
        doc.Box(0, 0, 0, 3, 3, 0, AssetPalette.Grass);
        doc.Paint(10, 10, 10, AssetPalette.Red);
        Assert.Equal(0, doc[10, 10, 10]);
        Assert.Equal(16, doc.FillColour(0, 0, 0, AssetPalette.Sand));
        Assert.All(Enumerable.Range(0, 4), x => Assert.Equal(AssetPalette.Sand, doc[x, 3, 0]));
        doc.Box(0, 0, 0, 9, 9, 9, AssetPalette.Red, paintOnly: true);
        Assert.Equal(16, doc.Model.Count);
    }

    [Fact]
    public void ARayFindsTheFaceItHitsOrTheGround()
    {
        var m = new VoxModel(10, 10, 10);
        m[5, 5, 0] = 1;
        // Straight down onto the voxel: its top face.
        var hit = VoxRay.Cast(m, 5.5, 5.5, 20, 0, 0, -1)!.Value;
        Assert.Equal((5, 5, 0, 0, 0, 1, false), (hit.X, hit.Y, hit.Z, hit.Nx, hit.Ny, hit.Nz, hit.Ground));
        Assert.Equal((5, 5, 1), hit.Front);
        // From the west, level: its west face.
        hit = VoxRay.Cast(m, -5, 5.5, 0.5, 1, 0, 0)!.Value;
        Assert.Equal((5, 5, 0, -1, 0, 0), (hit.X, hit.Y, hit.Z, hit.Nx, hit.Ny, hit.Nz));
        // Missing it: the ground, so an added voxel lands on z 0.
        hit = VoxRay.Cast(m, 2.5, 2.5, 20, 0.01, 0.01, -1)!.Value;
        Assert.True(hit.Ground);
        Assert.Equal((2, 2, 0), hit.Front);
        // Looking up and away: nothing.
        Assert.Null(VoxRay.Cast(m, 5, 5, 20, 0, 0, 1));
        // Slanting in from a corner hits the voxel and not the empty space before it.
        hit = VoxRay.Cast(m, -3, -3, 8.5, 1, 1, -1)!.Value;
        Assert.False(hit.Ground);
        Assert.Equal((5, 5, 0), (hit.X, hit.Y, hit.Z));
    }

    [Fact]
    public void AnAssetSavesAndOpensWithItsNameAndDetail()
    {
        string dir = Path.Combine(Path.GetTempPath(), "toycity-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var doc = AssetDoc.New("Corner shop", 2, 2, 2, 2);
            doc.PlacePiece(Pieces.Find("wall-door")!, Material.Brick, 0, 0, 0, 0);
            doc.PlaceProp(Pieces.Find("bench")!, 20, 20, 0, 1);
            string path = Path.Combine(dir, "corner-shop.vox");
            Directory.CreateDirectory(dir);
            doc.Save(path);
            Assert.False(doc.Dirty);
            var back = AssetDoc.Load(path);
            Assert.Equal(("Corner shop", 2), (back.Name, back.Detail));
            Assert.Equal(doc.Model.Voxels, back.Model.Voxels);
            Assert.Equal(doc.Model.Palette[1..], back.Model.Palette[1..]);

            // A plain MagicaVoxel file opens at detail 1, named after the file.
            VoxFile.Write(doc.Model, Path.Combine(dir, "plain.vox"));
            var plain = AssetDoc.Load(Path.Combine(dir, "plain.vox"));
            Assert.Equal(("plain", 1), (plain.Name, plain.Detail));

            // As a building: cut down to what's there, standing on z 0, with its detail beside it.
            string used = doc.UseForBuilding(Path.Combine(dir, "vox"), "shop-small");
            var model = VoxFile.Read(used);
            var b = doc.Bounds()!.Value;
            Assert.Equal((b.X1 - b.X0 + 1, b.Y1 - b.Y0 + 1, b.Z1 + 1), (model.SizeX, model.SizeY, model.SizeZ));
            Assert.Equal(2, AssetDoc.ReadSidecar(used)!.Value.Detail);
            Assert.Null(AssetDoc.ParseSidecar("not json"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void AModelOfAnotherDetailIsScaledAndRecolouredToFit()
    {
        var coarse = AssetDoc.New("c", 1, 1, 1, 1);
        coarse.PlacePiece(Pieces.Find("pillar")!, Material.Stone, 0, 0, 0, 0);
        var fine = AssetDoc.New("f", 4, 1, 1, 1);
        fine.PlaceModel(coarse.Cropped(), 1, 0, 0, 0, 0);
        Assert.Equal(coarse.Model.Count * 64, fine.Model.Count);

        // A model on another palette has its colours matched to the nearest entry here.
        var other = new VoxModel(2, 2, 2);
        other.Palette[1] = VoxModel.Rgba(0xC2, 0x3B, 0x33);
        other.Fill(0, 0, 0, 1, 1, 1, 1);
        var doc = AssetDoc.New("d", 1);
        doc.PlaceModel(other, 1, 0, 0, 0, 0);
        Assert.Equal(AssetPalette.Red, doc[0, 0, 0]);

        // And a fine one shrinks into a coarse model.
        var back = AssetDoc.New("b", 1, 1, 1, 1);
        back.PlaceModel(fine.Cropped(), 4, 0, 0, 0, 0);
        Assert.Equal(coarse.Model.Voxels, back.Model.Voxels);
    }

    [Fact]
    public void ADemoHouseBuildsFromThePieces()
    {
        var doc = DemoAsset.House(2);
        Assert.True(doc.Model.Count > 10_000, $"{doc.Model.Count}");
        var mesh = VoxMesher.Mesh(doc.Model);
        Assert.True(mesh.Quads > 50);
        // It undoes all the way back to an empty model.
        while (doc.Undo()) { }
        Assert.Equal(0, doc.Model.Count);
    }
}
