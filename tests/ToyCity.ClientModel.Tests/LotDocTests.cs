using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using Xunit;
using Xunit.Abstractions;
using static ToyCity.ClientModel.Voxels.Assets.AssetPalette;

namespace ToyCity.ClientModel.Tests;

/// <summary>RC part 3: the lot editor puts a building on a lot, dresses it, and builds the whole lot into one model.</summary>
public class LotDocTests
{
    readonly ITestOutputHelper _out;
    public LotDocTests(ITestOutputHelper output) => _out = output;

    static int Count(VoxModel m, Func<int, int, int, byte, bool> f, int z0 = 0, int z1 = int.MaxValue)
    {
        int n = 0;
        for (int z = z0; z < Math.Min(m.SizeZ, z1); z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                    if (f(x, y, z, m[x, y, z])) n++;
        return n;
    }

    [Fact]
    public void AnEmptyLotIsLawnTheSizeOfTheLot()
    {
        var lot = new LotDoc("Empty", 1, 1);
        var r = lot.Compose();
        Assert.Equal(4, r.Detail);
        Assert.Equal(128, r.Model.SizeX);
        Assert.Equal(128, r.Model.SizeY);
        Assert.Equal(128 * 128, Count(r.Model, (_, _, _, c) => c is Grass or > 31, 0, 1));
        Assert.Equal(0, Count(r.Model, (_, _, _, c) => c != 0, 1));
        Assert.Null(r.Roof);
        // Bigger lots are built coarser to fit 256 voxels: two cells at 12.5 cm, three or four at 25 cm.
        Assert.Equal(4, new LotDoc("", 2, 1).Compose().Detail);
        var big = new LotDoc("", 4, 3).Compose();
        Assert.Equal(2, big.Detail);
        Assert.Equal((256, 192), (big.Model.SizeX, big.Model.SizeY));
    }

    [Fact]
    public void TheBuildingStandsWhereItsPutUnderItsRoof()
    {
        var lot = new LotDoc("House", 1, 1);
        lot.SetBuilding(new LotBuilding("example:house", 4, 2));
        var house = lot.Model("example:house")!;
        _out.WriteLine($"house {house.Width} × {house.Depth} × {house.Height} m");
        Assert.InRange(house.Width, 7.5, 8.6);
        var r = lot.Compose();
        var (x0, y0, x1, y1) = lot.BoundsOf(-1);
        Assert.Equal((4.0, 2.0), (x0, y0));
        int I(double m) => (int)(m / 0.125);
        // Brick inside its footprint, none outside it.
        int inside = Count(r.Model, (x, y, _, c) => c == Brick && x >= I(x0) && x < I(x1) && y >= I(y0) && y < I(y1), 1);
        int outside = Count(r.Model, (x, y, _, c) => c == Brick && !(x >= I(x0) && x < I(x1) && y >= I(y0) && y < I(y1)), 1);
        Assert.True(inside > 1000, $"{inside} brick voxels in the footprint");
        Assert.Equal(0, outside);
        // The roof comes with it and finds its walls on the lot.
        Assert.NotNull(r.Roof);
        var found = AutoRoof.Footprint(r.Model, r.Detail);
        Assert.NotNull(found);
        int roofed = 0;
        for (int y = 0; y < r.Model.SizeY; y++)
            for (int x = 0; x < r.Model.SizeX; x++)
                if (found!.Value.Footprint[x, y]) { roofed++; Assert.InRange(x * 0.125, x0 - 0.2, x1 + 0.2); Assert.InRange(y * 0.125, y0 - 0.2, y1 + 0.2); }
        Assert.True(roofed > 2000);
    }

    [Fact]
    public void TurningTheBuildingTurnsItAboutItsMiddle()
    {
        var lot = new LotDoc("House", 1, 1);
        lot.SetBuilding(new LotBuilding("example:house", 3, 1));
        var before = lot.BoundsOf(-1);
        int bricks = Count(lot.Compose().Model, (_, _, _, c) => c == Brick);
        lot.TurnAt(-1);
        var after = lot.BoundsOf(-1);
        Assert.Equal(1, lot.Building!.Turn);
        Assert.Equal(before.X1 - before.X0, after.Y1 - after.Y0, 6);
        Assert.Equal(before.Y1 - before.Y0, after.X1 - after.X0, 6);
        Assert.Equal((before.X0 + before.X1) / 2, (after.X0 + after.X1) / 2, 6);
        Assert.Equal(bricks, Count(lot.Compose().Model, (_, _, _, c) => c == Brick));
        // Four turns bring it back.
        for (int i = 0; i < 3; i++) lot.TurnAt(-1);
        Assert.Equal(before, lot.BoundsOf(-1));
    }

    [Fact]
    public void GroundPatchesLieOnTheGround()
    {
        var lot = new LotDoc("Drive", 1, 1);
        lot.Add(new LotItem(LotKind.Ground, "concrete", 12, 4, 15, 16));
        lot.Add(new LotItem(LotKind.Ground, "paving", 4, 10, 8, 16));
        lot.Add(new LotItem(LotKind.Ground, "flowerbed", 0, 0, 3, 3));
        var m = lot.Compose().Model;
        Assert.Equal(24 * 96, Count(m, (x, y, _, c) => c == Concrete && x >= 96 && x < 120 && y >= 32, 0, 1));
        // Paving is slabs with joints; a bed is soil with flowers on it.
        Assert.True(Count(m, (_, _, _, c) => c == Mortar, 0, 1) > 100);
        Assert.True(Count(m, (_, _, _, c) => c == Soil, 0, 1) > 300);
        Assert.True(Count(m, (_, _, _, c) => c is Red or Yellow or Blue, 1, 3) > 20);
        Assert.Equal(0, Count(m, (_, _, _, c) => c != 0, 3));
    }

    [Fact]
    public void FencesRunAlongTheirLineAtTheirHeight()
    {
        var lot = new LotDoc("Fences", 1, 1);
        lot.Add(new LotItem(LotKind.Fence, "picket", 0, 15.8, 16, 15.8));
        lot.Add(new LotItem(LotKind.Fence, "hedge", 0.5, 0, 0.5, 14));
        lot.Add(new LotItem(LotKind.Fence, "brick", 2, 2, 12, 12));   // at a slant
        var m = lot.Compose().Model;
        // The pickets: white, along y = 15.8 m, no higher than a metre and a bit.
        int white = Count(m, (_, y, _, c) => c == Trim && y == 126, 1);
        Assert.True(white > 300, $"{white} white voxels");
        Assert.Equal(0, Count(m, (_, _, z, c) => c == Trim && z * 0.125 > 1.1, 1));
        // The hedge is thick and leafy, about 1.4 m.
        Assert.True(Count(m, (x, _, _, c) => c is Leaf or LeafDark && x < 8, 1) > 5000);
        Assert.Equal(0, Count(m, (_, _, z, c) => c is Leaf or LeafDark && z * 0.125 > 1.5, 1));
        // The slanting wall's bricks lie along its diagonal.
        Assert.True(Count(m, (x, y, _, c) => c == Brick && Math.Abs(x - y) <= 3, 1) > 1000);
        Assert.Equal(0, Count(m, (x, y, _, c) => c == Brick && Math.Abs(x - y) > 3, 1));
    }

    [Fact]
    public void ACarParkHasBaysAndCarsAsFullAsAsked()
    {
        int Cars(string style)
        {
            var lot = new LotDoc("Car park", 2, 1);
            lot.Add(new LotItem(LotKind.Parking, style, 2, 1, 30, 15));
            var m = lot.Compose().Model;
            Assert.True(Count(m, (_, _, _, c) => c == Trim, 0, 1) > 200);    // the painted lines
            return Count(m, (_, _, _, c) => c == Tyre, 1);
        }
        int full = Cars("full"), some = Cars("some"), empty = Cars("empty");
        _out.WriteLine($"tyre voxels: full {full}, some {some}, empty {empty}");
        Assert.Equal(0, empty);
        Assert.True(full > some && some > 0);
        // Two rows of eleven bays facing an aisle across 14 m.
        var p = new LotDoc("", 2, 1);
        p.Add(new LotItem(LotKind.Parking, "full", 2, 1, 30, 15, Seed: 7));
        var one = p.Compose();
        Assert.True(Count(one.Model, (_, y, _, c) => c == Tyre && y * 0.125 < 6.5, 1) > 0);
        Assert.True(Count(one.Model, (_, y, _, c) => c == Tyre && y * 0.125 > 9.5, 1) > 0);
        Assert.Equal(0, Count(one.Model, (_, y, _, c) => c == Tyre && y * 0.125 > 6.5 && y * 0.125 < 9.5, 1));
        // The same seed parks the same cars.
        Assert.Equal(Count(one.Model, (_, _, _, c) => c == Tyre, 1), Count(p.Compose().Model, (_, _, _, c) => c == Tyre, 1));
    }

    [Fact]
    public void PlantsAreThePlantLayersKinds()
    {
        var lot = new LotDoc("Garden", 1, 1);
        foreach (var (id, i) in LotDoc.Plants.Select((p, i) => (p.Id, i))) lot.Add(new LotItem(LotKind.Plant, id, 2 + 2.4 * i, 8));
        var r = lot.Compose();
        // The trees' trunks and crowns are smooth shapes; the reeds and flowers voxels.
        Assert.True(r.Shapes.Count >= 6, $"{r.Shapes.Count} shapes");
        Assert.True(Count(r.Model, (_, _, z, c) => c is Leaf or LeafDark && z * 0.125 > 4, 1) > 500);
        Assert.True(Count(r.Model, (_, _, _, c) => c == Red, 3) > 3);   // fruit
        Assert.True(Count(r.Model, (x, _, z, c) => c != 0 && x * 0.125 > 10.9 && x * 0.125 < 12.3 && z * 0.125 > 0.9, 1) > 20);   // reeds
    }

    [Fact]
    public void PropsComeFromTheLibraryAndTurn()
    {
        var lot = new LotDoc("Props", 1, 1);
        int car = lot.Add(new LotItem(LotKind.Prop, "car", 8, 8));
        var (x0, y0, x1, y1) = lot.BoundsOf(car);
        Assert.True(x1 - x0 > y1 - y0);
        lot.TurnAt(car);
        var t = lot.BoundsOf(car);
        Assert.True(t.Y1 - t.Y0 > t.X1 - t.X0);
        Assert.True(Count(lot.Compose().Model, (_, _, _, c) => c == Tyre, 1) > 10);
        Assert.Contains(LotDoc.Props, p => p.Id == "bench");
        Assert.DoesNotContain(LotDoc.Props, p => p.Id == "wall");
    }

    [Fact]
    public void ClickingFindsTheThingOnTop()
    {
        var lot = DemoAsset.HouseLot();
        Assert.Equal(-1, lot.At(7, 5));                                       // the house
        Assert.Equal(LotKind.Plant, lot.Items[lot.At(14, 3)!.Value].Kind);   // the conifer over the paving
        Assert.Equal(LotKind.Prop, lot.Items[lot.At(14, 11)!.Value].Kind);   // the car on the drive
        Assert.Equal("concrete", lot.Items[lot.At(13, 14)!.Value].Style);    // the drive
        Assert.Equal(LotKind.Fence, lot.Items[lot.At(9, 15.8)!.Value].Kind);
        Assert.Null(lot.At(8, 0.9));                                         // lawn behind the house
        int conifer = lot.At(14, 3)!.Value;
        lot.Move(conifer, -1, 0);
        Assert.Equal(13.0, lot.Items[conifer].X0);
    }

    [Fact]
    public void EveryChangeUndoesAndTheLotSavesAndOpens()
    {
        var lot = DemoAsset.HouseLot();
        string json = lot.ToJson();
        int n = lot.Items.Count;
        lot.Remove(0);
        lot.Move(-1, 1, 0);
        lot.SetGround("gravel");
        Assert.Equal(n - 1, lot.Items.Count);
        lot.Undo(); lot.Undo(); lot.Undo();
        Assert.Equal(json, lot.ToJson());
        lot.Redo();
        Assert.Equal(n - 1, lot.Items.Count);
        lot.Undo();
        Assert.Equal(json, lot.ToJson());
        // A change that changes nothing isn't a step, and the example starts with nothing to undo.
        int version = lot.Version;
        lot.SetGround("lawn");
        Assert.Equal(version, lot.Version);
        Assert.False(lot.CanUndo);

        string path = Path.Combine(Path.GetTempPath(), $"lot-{Guid.NewGuid():N}.lot.json");
        try
        {
            lot.Save(path);
            var back = LotDoc.Load(path);
            Assert.Equal(lot.ToJson(), back.ToJson());
            // It builds the same, voxel for voxel.
            Assert.Equal(lot.Compose().Model.Voxels, back.Compose().Model.Voxels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddingAThingLeavesTheRestAsTheyWere()
    {
        var lot = DemoAsset.HouseLot();
        var before = lot.Compose().Model;
        lot.Add(new LotItem(LotKind.Prop, "crate", 1.2, 1.2));
        var after = lot.Compose().Model;
        int changed = 0;
        for (int i = 0; i < before.Voxels.Length; i++) if (before.Voxels[i] != after.Voxels[i]) changed++;
        Assert.InRange(changed, 1, 2000);
    }

    [Fact]
    public void TheExampleLotIsUsedForABuildingWhole()
    {
        var lot = DemoAsset.HouseLot();
        var r = lot.Compose();
        _out.WriteLine($"example lot {r.Model.SizeX}×{r.Model.SizeY}×{r.Model.SizeZ} at detail {r.Detail}, {r.Shapes.Count} shapes, " +
            $"{Count(r.Model, (_, _, _, c) => c != 0):N0} voxels");
        string dir = Path.Combine(Path.GetTempPath(), $"lot-{Guid.NewGuid():N}");
        try
        {
            string path = lot.UseForBuilding(dir, "r-l-m");
            Assert.EndsWith("r-l-m-1x1.vox", path);
            var asset = AssetDoc.Load(path);
            // The whole lot, not cut to what's on it, so it lines up with the lot in the city.
            Assert.Equal((128, 128), (asset.Model.SizeX, asset.Model.SizeY));
            Assert.Equal(4, asset.Detail);
            Assert.Equal(RoofStyle.Gable, asset.Roof?.Style);
            Assert.Equal(r.Shapes.Count, asset.Shapes.Count);
            Assert.Equal((1, 1), AssetDoc.CellsOf(asset.Model, asset.Detail));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheSetsModelsComeWithoutTheirGround()
    {
        var shop = LotDoc.Resolve("set:c-l-m:1x1:0");
        Assert.NotNull(shop);
        // Nothing lies flat on the ground alone: the set's lawn and paths are taken up.
        int flat = 0;
        for (int y = 0; y < shop!.Model.SizeY; y++)
            for (int x = 0; x < shop.Model.SizeX; x++)
                if (shop.Model[x, y, 0] != 0 && (shop.Model.SizeZ < 2 || shop.Model[x, y, 1] == 0)) flat++;
        Assert.Equal(0, flat);
        Assert.Null(LotDoc.Resolve("set:no-such-type:1x1:0"));
        Assert.Null(LotDoc.Resolve("/no/such/file.vox"));
        var lot = DemoAsset.ShopLot();
        var r = lot.Compose();
        Assert.Equal((256, 128), (r.Model.SizeX, r.Model.SizeY));
        Assert.True(Count(r.Model, (_, _, _, c) => c == Tyre, 1) > 0);
    }
}
