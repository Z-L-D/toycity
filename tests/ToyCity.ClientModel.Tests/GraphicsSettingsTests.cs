using ToyCity.ClientModel.Graphics;

namespace ToyCity.ClientModel.Tests;

public class GraphicsSettingsTests
{
    [Fact]
    public void PresetsAreNamedAndGetCheaperFromUltraToLow()
    {
        foreach (var (name, p) in GraphicsSettings.Presets) Assert.Equal(name, p.PresetName);
        Assert.Equal("Custom", (GraphicsSettings.High with { Shadows = ShadowQuality.Off }).PresetName);
        // A preset ignores the player's own frame cap, v-sync, labels and city life.
        Assert.Equal("Low", (GraphicsSettings.Low with { MaxFps = 30, VSync = false, StreetLabels = false, CityLife = false }).PresetName);
        var order = new[] { GraphicsSettings.Low, GraphicsSettings.Medium, GraphicsSettings.High, GraphicsSettings.Ultra };
        for (int k = 1; k < order.Length; k++)
        {
            Assert.True(order[k].RenderScale >= order[k - 1].RenderScale);
            Assert.True(order[k].Shadows >= order[k - 1].Shadows);
            Assert.True(order[k].Detail >= order[k - 1].Detail);
            Assert.True(order[k].Smoothing >= order[k - 1].Smoothing);
            Assert.True(order[k].DetailDistance >= order[k - 1].DetailDistance);
        }
    }

    [Fact]
    public void ApplyingAPresetKeepsThePlayersOwnChoices()
    {
        var mine = GraphicsSettings.Ultra with { MaxFps = 60, StreetLabels = false };
        var low = mine.WithPreset(GraphicsSettings.Low);
        Assert.Equal("Low", low.PresetName);
        Assert.Equal(60, low.MaxFps);
        Assert.False(low.StreetLabels);
    }

    [Fact]
    public void EveryOptionRoundTripsEveryChoiceAndSaving()
    {
        foreach (var o in GraphicsSettings.Options)
            for (int i = 0; i < o.Choices.Length; i++)
            {
                var s = o.Set(GraphicsSettings.High, i);
                Assert.Equal(i, o.Get(s));
                var back = GraphicsSettings.Load(s.Save(), GraphicsSettings.Low);
                Assert.Equal(s, back);
            }
    }

    [Fact]
    public void LoadingKeepsDefaultsForMissingOrBadLines()
    {
        var s = GraphicsSettings.Load("render_scale = 60\nshadows = sideways\nnonsense\nfuture_option = 3\nmax_fps = 30", GraphicsSettings.High);
        Assert.Equal(60, s.RenderScale);
        Assert.Equal(GraphicsSettings.High.Shadows, s.Shadows);
        Assert.Equal(30, s.MaxFps);
        Assert.Equal(GraphicsSettings.High.Detail, s.Detail);
    }

    [Theory]
    [InlineData("Intel(R) HD Graphics 620", "Medium")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Medium")]
    [InlineData("AMD Radeon(TM) Graphics", "Medium")]
    [InlineData("llvmpipe (LLVM 15.0.7, 256 bits)", "Medium")]
    [InlineData("Quadro P4200 with Max-Q Design", "High")]
    [InlineData("NVIDIA GeForce RTX 3060", "High")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "High")]
    [InlineData("AMD Radeon RX 6700 XT", "High")]
    public void IntegratedGraphicsStartOnMedium(string adapter, string preset) =>
        Assert.Equal(preset, GraphicsSettings.Suggest(adapter).PresetName);

    [Fact]
    public void DetailFollowsTheSettingAndTheCamera()
    {
        Assert.Equal(ModelDetail.Far, GraphicsSettings.High.ClassicDetail(2));
        Assert.Equal(ModelDetail.Near, GraphicsSettings.High.ClassicDetail(3));
        Assert.Equal(ModelDetail.Mid, GraphicsSettings.Medium.ClassicDetail(3));
        Assert.Equal(ModelDetail.Far, GraphicsSettings.Low.ClassicDetail(3));
        Assert.Equal(ModelDetail.Mid, GraphicsSettings.Low.ClassicDetail(4));
        Assert.Equal(ModelDetail.Smooth, GraphicsSettings.High.FreeDetail(18));
        Assert.Equal(ModelDetail.Near, GraphicsSettings.High.FreeDetail(60));
        Assert.Equal(ModelDetail.Far, GraphicsSettings.High.FreeDetail(160));
        Assert.Equal(ModelDetail.Mid, GraphicsSettings.Medium.FreeDetail(18));
        Assert.Equal(ModelDetail.Far, GraphicsSettings.Medium.FreeDetail(100));
        Assert.Equal(ModelDetail.Far, GraphicsSettings.Low.FreeDetail(18));
    }
}
