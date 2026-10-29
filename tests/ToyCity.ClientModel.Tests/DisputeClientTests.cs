using System.Text.RegularExpressions;
using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>The dispute ladder from the lease panel, against the real server: the mayor offers a buyout and a move to the computer running the buses.</summary>
public class DisputeClientTests : IClassFixture<ServerFixture>
{
    readonly ServerProcess _server;
    public DisputeClientTests(ServerFixture f) => _server = f.Server;

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
    public void TheMayorCanOfferABuyoutAndAMoveFromTheLeasePanel()
    {
        using var client = new CityClient(_server.Ws, "test-dispute");
        var panel = new PanelModel(client.City, client.Connection.Send);
        client.Message += panel.Handle;
        PollUntil(client, () => client.City.Loaded && panel.Civic != null);

        // Two stops a few streets apart.
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
        PollUntil(client, () => c.Buildings.Values.Count(b => b.TypeId == "bus-stop") >= 2);
        var stop = c.Buildings.Values.First(b => b.TypeId == "bus-stop" && panel.Civic!.Leases.All(l => l.BuildingId != b.Id));

        // Lease it to the bus company from its panel, on the standard terms.
        panel.Query(stop.X, stop.Y);
        PollUntil(client, () => Links(panel.Render()).Any(l => l.StartsWith("lease-to:")));
        panel.Click(Links(panel.Render()).First(l => l.StartsWith("lease-to:")));
        panel.Click("terms:ok");
        int leaseId = -1;
        LeaseState Lease() => panel.Civic!.Leases.Single(l => leaseId < 0 ? l.BuildingId == stop.Id : l.Id == leaseId);
        PollUntil(client, () => panel.Civic!.Leases.Any(l => l.BuildingId == stop.Id));
        leaseId = Lease().Id;
        PollUntil(client, () => panel.Render().Contains("Dispute ladder"));
        var text = panel.Render();
        foreach (var step in new[] { "Negotiate", "Buy out", "Relocate", "Impose terms", "Evict" }) Assert.Contains(step, text);

        // A buyout for nothing: too little for the computer, which says so the next day.
        client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        panel.Click($"offer-make:{leaseId}:1");
        Assert.Equal("offer", panel.Dialog);
        panel.Click("offer:ok");
        PollUntil(client, () => Lease().Offers.Any(o => o.Kind == OfferKind.OfferBuyout && o.Status == "declined"), 60);
        Assert.Contains("wants at least", Lease().Offers.First(o => o.Kind == OfferKind.OfferBuyout).Note);

        // A move to the other stop, the city paying the moving costs: the computer takes it.
        Assert.NotEmpty(Lease().MoveTo);
        int to = Lease().MoveTo[0].BuildingId;
        panel.Click($"offer-make:{leaseId}:2");
        panel.Click($"offer:to:{to}");
        panel.Click("offer:ok");
        PollUntil(client, () => Lease().BuildingId == to, 60);
        Assert.Equal("accepted", Lease().Offers.First(o => o.Kind == OfferKind.OfferRelocate).Status);
    }
}
