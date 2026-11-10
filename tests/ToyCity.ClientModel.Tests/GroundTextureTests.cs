using ToyCity.ClientModel.Graphics;
using ToyCity.ClientModel.Graphics.Ground;

namespace ToyCity.ClientModel.Tests;

public class GroundTextureTests
{
    static readonly Lazy<GroundTexture[]> At128 = new(() => GroundTextures.MakeAll(128));

    [Fact]
    public void EveryMaterialMakesAtItsSizeWithItsListedAverage()
    {
        var all = At128.Value;
        Assert.Equal(GroundTextures.List.Length, all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            Assert.Equal(GroundTextures.List[i].Id, t.Id);
            Assert.Equal(128 * 128 * 4, t.Albedo.Length);
            Assert.Equal(128 * 128 * 4, t.Detail.Length);
            uint want = GroundTextures.List[i].Average;
            for (int sh = 0; sh <= 16; sh += 8)
                Assert.InRange((int)(t.Average >> sh & 255) - (int)(want >> sh & 255), -3, 3);
        }
    }

    [Fact]
    public void TexturesTileWithoutASeam()
    {
        // Across the wrap, neighbouring pixels differ no more than neighbours inside the tile do.
        foreach (var t in At128.Value)
        {
            int n = t.Size;
            double Step(int x0, int x1)
            {
                double sum = 0;
                for (int y = 0; y < n; y++)
                    for (int ch = 0; ch < 4; ch++) sum += Math.Abs(t.Albedo[(y * n + x0) * 4 + ch] - t.Albedo[(y * n + x1) * 4 + ch]);
                return sum / n;
            }
            double inside = 0;
            for (int x = 1; x < n; x++) inside += Step(x - 1, x);
            inside /= n - 1;
            Assert.True(Step(n - 1, 0) < inside * 2.5 + 4, $"{t.Id}: seam {Step(n - 1, 0):0.0} vs {inside:0.0}");
        }
    }

    [Fact]
    public void TexturesAreTheSameEveryTime()
    {
        var a = GroundTextures.Make("granite", 64);
        var b = GroundTextures.Make("granite", 64);
        Assert.Equal(a.Albedo, b.Albedo);
        Assert.Equal(a.Detail, b.Detail);
    }

    [Fact]
    public void BadSizesAndNamesAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTextures.Make("meadow", 100));
        Assert.Throws<ArgumentException>(() => GroundTextures.Make("lava", 64));
        Assert.Equal(-1, GroundTextures.Index("lava"));
    }

    [Fact]
    public void TheGroundOptionPicksTheTextureSize()
    {
        Assert.Equal(GroundLook.Textured, GraphicsSettings.Low.Ground);
        Assert.Equal(GroundLook.Relief, GraphicsSettings.High.Ground);
        Assert.Equal(GroundLook.Fine, GraphicsSettings.Ultra.Ground);
        Assert.Equal(0, (GraphicsSettings.High with { Ground = GroundLook.Plain }).GroundPixels);
        Assert.Equal(512, GraphicsSettings.High.GroundPixels);
        Assert.Equal(1024, GraphicsSettings.Ultra.GroundPixels);
        var s = GraphicsSettings.Load("ground = Plain", GraphicsSettings.High);
        Assert.Equal(GroundLook.Plain, s.Ground);
    }
}
