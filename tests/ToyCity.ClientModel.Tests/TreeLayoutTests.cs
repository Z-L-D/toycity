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

    [Fact]
    public void CrownsKeepOffTheRoadsAndBuildingsBeside()
    {
        foreach (int kind in new[] { TreeLayout.Broadleaf, TreeLayout.Conifer, TreeLayout.Scrub, TreeLayout.Orchard })
            for (int cell = 0; cell < 200; cell++)
            {
                var spots = new List<TreeSpot>();
                int all = TreeLayout.West | TreeLayout.East | TreeLayout.North | TreeLayout.South;
                TreeLayout.Spots(cell % 64, cell / 64, 64, (byte)(kind * 16 + 8), BuildingDetail.Low, spots, all);
                foreach (var s in spots)
                {
                    float r = TreeLayout.Reach(kind) * s.Scale, fx = s.X - cell % 64, fy = s.Y - cell / 64;
                    Assert.True(fx - r >= -1e-4f && fx + r <= 1 + 1e-4f && fy - r >= -1e-4f && fy + r <= 1 + 1e-4f, $"kind {kind} at {fx:0.00},{fy:0.00} reach {r:0.00}");
                }
            }
    }
}
