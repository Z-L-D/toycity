using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>The build menu of the UI overhaul (variation A): the rail, its flyouts, and what picking a tile does.</summary>
public class BuildMenuTests : IClassFixture<ServerFixture>
{
    readonly ServerProcess _server;
    public BuildMenuTests(ServerFixture f) => _server = f.Server;

    [Fact]
    public void EverythingTheMayorBuildsIsInTheMenuOnceAndPickingItSetsTheTool()
    {
        using var client = new CityClient(_server.Ws, "test-buildmenu");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!client.City.Loaded) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); client.Poll(); Thread.Sleep(10); }
        var c = client.City;
        var menu = BuildMenu.For(c);
        Assert.Equal(new[] { "zones", "transport", "utilities", "civic", "parks", "landmarks", "land", "disasters", "bulldoze" }, menu.Select(m => m.Id));
        var items = menu.SelectMany(m => m.Groups).SelectMany(g => g.Items).ToList();
        Assert.All(menu, m => Assert.All(m.Groups, g => Assert.NotEmpty(g.Items)));

        // Every building the mayor places and every path type, once each.
        foreach (var t in c.Types.Where(t => t.Placeable)) Assert.Single(items, i => i.Kind == ToolKind.Building && i.Type == t.Id);
        foreach (var p in c.Profiles) Assert.Single(items, i => i.Kind == ToolKind.Path && i.Profile == p.Id);
        Assert.Equal(9, items.Count(i => i.Kind == ToolKind.Zone));
        Assert.Equal(3, items.Count(i => i.Kind == ToolKind.Utility));
        // Prices: buildings their cost, paths per cell.
        var police = items.Single(i => i.Type == "police-station");
        Assert.Equal($"${c.TypeById["police-station"].Cost:N0}", police.Price);
        Assert.EndsWith("/ cell", items.Single(i => i.Profile == "road").Price);
        // Highways have no frontage and sit in their own group.
        Assert.All(menu.Single(m => m.Id == "transport").Groups.Single(g => g.Name == "Highways").Items, i => Assert.False(c.Profiles.Single(p => p.Id == i.Profile).Frontage));

        // The flyout: a rail button opens it, a tile sets the tool and closes it, unless pinned.
        var bm = new BuildMenu();
        var tool = new ToolState();
        bm.Toggle("transport");
        Assert.Equal("transport", bm.Shown(menu)!.Value.Category.Id);
        bm.PickGroup(1);
        var street = bm.Shown(menu)!.Value.Category.Groups[1].Items.Single(i => i.Profile == "street");
        Assert.True(bm.Pick(street, tool));
        Assert.Equal((ToolKind.Path, "street"), (tool.Kind, tool.ProfileId));
        Assert.True(BuildMenu.IsCurrent(street, tool));
        Assert.Equal("transport", BuildMenu.CategoryOf(tool, menu));
        Assert.Null(bm.Open);
        bm.Toggle("zones");
        bm.Pin();
        var dense = items.Single(i => i.Kind == ToolKind.Zone && i.Zone == ZoneKind.ZoneCommercial && i.Density == Density.High);
        Assert.True(bm.Pick(dense, tool));
        Assert.Equal((ToolKind.Zone, ZoneKind.ZoneCommercial, Density.High), (tool.Kind, tool.Zone, tool.Density));
        Assert.Equal("zones", bm.Open);
        bm.Toggle("zones");
        Assert.Null(bm.Open);

        // A locked thing can't be picked and says why.
        if (items.FirstOrDefault(i => i.Locked.Length > 0) is { } locked)
        {
            Assert.False(bm.Pick(locked, tool));
            Assert.NotEqual(locked.Type, tool.BuildingTypeId);
        }
    }
}

public class UiIconTests
{
    [Fact]
    public void EveryIconTheMenuUsesIsInTheSharedSet()
    {
        Assert.True(UiIcons.Names.Count >= 40);
        foreach (var name in new[] { "query", "zones", "transport", "road", "tram", "bus", "rail", "subway", "utilities", "power", "water", "garbage", "civic",
                     "police", "fire", "health", "education", "parks", "landmarks", "land", "disasters", "bulldoze", "pin", "close", "menu", "layers", "underground" })
            Assert.True(UiIcons.Has(name), name);
        var svg = UiIcons.Svg("road", "#ffffff");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("stroke='#ffffff'", svg);
    }
}
