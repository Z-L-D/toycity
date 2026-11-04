using ToyCity.ClientModel.Graphics;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 4.1, part 1: where the views draw each cell's trees.</summary>
public class TreeLayoutTests
{
    [Fact]
    public void ACellsCrownsStayPutInsideItAndThinOutByDetail()
    {
        var a = new List<TreeSpot>();
        var b = new List<TreeSpot>();
        byte wood = (byte)(TreeLayout.Broadleaf * 16 + 8);
        TreeLayout.Spots(5, 7, 128, wood, BuildingDetail.High, a);
        TreeLayout.Spots(5, 7, 128, wood, BuildingDetail.High, b);
        Assert.Equal(8, a.Count);
        Assert.Equal(a, b);
        Assert.All(a, s => { Assert.InRange(s.X, 5f, 6f); Assert.InRange(s.Y, 7f, 8f); Assert.Equal(TreeLayout.Broadleaf, s.Kind); });
        var low = new List<TreeSpot>();
        TreeLayout.Spots(5, 7, 128, wood, BuildingDetail.Low, low);
        Assert.Equal(2, low.Count);
        // Fewer crowns, each bigger, so the wood still looks closed.
        Assert.True(low.Average(s => s.Scale) > a.Take(2).Average(s => s.Scale));
        var none = new List<TreeSpot>();
        TreeLayout.Spots(5, 7, 128, 0, BuildingDetail.High, none);
        Assert.Empty(none);
    }
}
