using System.Text.RegularExpressions;
using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>The bus company's view against the real server: take the company, lease stops, draw a line through them.</summary>
public class CompanyTests : IClassFixture<ServerFixture>
{
    readonly ServerProcess _server;
    public CompanyTests(ServerFixture f) => _server = f.Server;

    static void PollUntil(CityClient c, Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            c.Poll();
            Thread.Sleep(10);
        }
    }

    static List<string> Links(string bb) => Regex.Matches(bb, @"\[url=([^\]]*)\]").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void ThePlayerCanTakeTheCompanyLeaseStopsAndRunALine()
    {
        using var client = new CityClient(_server.Ws, "test-company");
        var panel = new PanelModel(client.City, client.Connection.Send);
        client.Message += panel.Handle;
        var co = panel.Company;
        PollUntil(client, () => client.City.Loaded && co.View != null);
        client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });

        panel.Open("buses");
        Assert.Contains("co-open", Links(panel.Render()));
        panel.Click("co-open");
        Assert.True(co.Active);
        foreach (var (tab, _) in CompanyModel.Tabs)
        {
            panel.Click($"co-tab:{tab}");
            Assert.False(string.IsNullOrWhiteSpace(panel.Render()), tab);
        }

        if (!co.Mine) panel.Click("co-claim");
        PollUntil(client, () => co.Mine);
        Assert.Contains("co-tool:Line", Links(co.RenderTools()));

        // The mayor puts up two stops a few streets apart, as the place tool would.
        var c = client.City;
        var tool = new ToolState { Kind = ToolKind.Building, BuildingTypeId = "bus-stop" };
        var placed = new List<(int X, int Y)>();
        for (int i = 0; i < c.N && placed.Count < 2; i++)
        {
            int x = i % c.Width, y = i / c.Width;
            if (c.Paths[i] == 0 || placed.Any(p => Math.Abs(p.X - x) + Math.Abs(p.Y - y) < 8)) continue;
            if (Planner.Plan(c, tool, new Pointer(x, y, 0.5f, 0.1f)) is not { Ok: true } plan) continue;
            foreach (var a in plan.Actions) client.Connection.Act(a);
            placed.Add((x, y));
        }
        PollUntil(client, () => co.View!.OpenLots.Count(l => l.Kind == "bus-stop") + co.View.Lots.Count(l => l.Kind == "bus-stop") >= 2);

        // Two stops leased, by clicking them on the map with the Lease tool.
        var free = co.View!.OpenLots.Where(l => l.Kind == "bus-stop").ToList();
        int have = co.View.Lots.Count(l => l.Kind == "bus-stop");
        panel.Click("co-tool:Lease");
        foreach (var l in free.Take(Math.Max(0, 2 - have))) co.MapClick(l.X, l.Y);
        PollUntil(client, () => co.View!.Lots.Count(l => l.Kind == "bus-stop") >= 2);

        // A line through them, drawn on the map, named in the text box.
        int lines = co.View!.Lines.Count;
        panel.Click("co-tool:Line");
        var stops = co.View.Lots.Where(l => l.Kind == "bus-stop").Take(2).ToList();
        foreach (var s in stops) co.MapClick(s.X, s.Y);
        Assert.Equal(stops.Select(s => s.LeaseId), co.Draft!.Value.Stops);
        Assert.NotNull(panel.InputPrompt);
        panel.SetInput("Test Line");
        panel.Click("co-fare:25");
        Assert.Equal(175, co.Draft!.Value.Fare);
        panel.Click("co-save");
        PollUntil(client, () => co.View!.Lines.Count > lines);
        var line = co.View!.Lines.Single(l => l.Name == "Test Line");
        Assert.Equal(175, line.FareCents);
        panel.Click("co-tab:lines");
        Assert.Contains("Test Line", panel.Render());

        panel.Click("co-city");
        Assert.False(co.Active);
        client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 0 } });
    }
}
