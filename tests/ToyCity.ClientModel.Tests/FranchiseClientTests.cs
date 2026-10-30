using System.Text.RegularExpressions;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// Franchise competition on a dedicated server: the mayor invites a rival company in and puts a district out to
/// tender, two players running the two companies bid, and the higher bid wins.
/// </summary>
public class FranchiseClientTests : IClassFixture<FranchiseClientTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public readonly ServerProcess Server = new("""{ "name": "Rival town", "join": "open" }""");
        public void Dispose() => Server.Dispose();
    }

    readonly ServerProcess _server;
    public FranchiseClientTests(Fixture f) => _server = f.Server;

    static List<string> Links(string bb) => Regex.Matches(bb, @"\[url=([^\]]*)\]").Select(m => m.Groups[1].Value).ToList();

    static (CityClient Client, PanelModel Panel) SignIn(Uri ws, string name)
    {
        var c = new CityClient(ws, "test");
        var panel = new PanelModel(c.City, c.Connection.Send) { Me = () => c.Connection.Player, OnServer = () => true };
        c.Message += panel.Handle;
        Wait(() => c.Connection.SignInNeeded, c);
        c.Connection.SignIn(name, "secret1", create: true);
        Wait(() => c.Connection.Player.Length > 0 || c.Connection.SignInNeeded, c);
        if (c.Connection.SignInNeeded) c.Connection.SignIn(name, "secret1");
        Wait(() => c.City.Loaded && panel.Civic != null, c);
        return (c, panel);
    }

    static void Wait(Func<bool> done, params CityClient[] clients)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            foreach (var c in clients) c.Poll();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void TheMayorInvitesARivalAndTheTwoCompaniesBidForADistrict()
    {
        var (mayor, may) = SignIn(_server.Ws, "Mayor May");
        using var _1 = mayor;
        var (annClient, ann) = SignIn(_server.Ws, "Ann");
        using var _2 = annClient;
        var (bobClient, bob) = SignIn(_server.Ws, "Bob");
        using var _3 = bobClient;
        var all = new[] { mayor, annClient, bobClient };
        Assert.Equal("Mayor May", may.Mayor!.Owner);

        // The mayor invites the rival company in from the seat dialog.
        may.OpenDialog("seat");
        Assert.Contains("invite:transport-2", Links(may.Render()));
        may.Click("invite:transport-2");
        Wait(() => bob.Civic!.Seats.Any(s => s.Id == "transport-2") && bob.Company.Companies.Count == 2, all);
        Assert.DoesNotContain(may.Civic!.Invitable, g => g.Id == "transport-2");

        // Ann takes the first company and Bob the rival; each client shows its player's own.
        ann.OpenDialog("seat");
        ann.Click("claim:transport");
        bob.OpenDialog("seat");
        bob.Click("claim:transport-2");
        Wait(() => ann.Company.Mine && bob.Company.Mine, all);
        Assert.Equal("transport", ann.Company.GameId);
        Assert.Equal("transport-2", bob.Company.GameId);
        Assert.Contains("co-game:transport", Links(bob.Company.RenderTools()));

        // The mayor puts a district out to tender.
        may.OpenDialog("seat");
        var tender = Links(may.Render()).First(l => l.StartsWith("tender:") && l.EndsWith(":12"));
        int district = int.Parse(tender.Split(':')[1]);
        may.Click(tender);
        Wait(() => may.Civic!.Franchises.Single(f => f.District == district).Status == "open", all);
        Wait(() => ann.Company.View!.Tenders.Any(t => t.District == district) && bob.Company.View!.Tenders.Any(t => t.District == district), all);

        // Each bids from the Standing tab: Ann twice the minimum, Bob the minimum.
        ann.Click("co-open");
        ann.Click("co-tab:standing");
        var annBids = Links(ann.Render()).Where(l => l.StartsWith($"co-bid:{district}:")).ToList();
        Assert.Equal(4, annBids.Count);
        ann.Click(annBids[2]);
        bob.Click("co-open");
        bob.Click("co-tab:standing");
        bob.Click(Links(bob.Render()).First(l => l.StartsWith($"co-bid:{district}:")));
        Wait(() => ann.Company.View!.Tenders.Single(t => t.District == district).YourBid > 0 && bob.Company.View!.Tenders.Single(t => t.District == district).YourBid > 0, all);
        Wait(() => may.Civic!.Franchises.Single(f => f.District == district).Bids == 2, all);

        // Fast forward to the close: Ann's bid wins.
        mayor.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        Wait(() => may.Civic!.Franchises.Single(f => f.District == district).Status == "held", all);
        var held = may.Civic!.Franchises.Single(f => f.District == district);
        Assert.Equal("transport", held.Holder);
        Assert.Equal(2 * 12 * 100, held.Amount);
        Wait(() => bob.Company.View!.Franchises.Any(f => f.District == district), all);
        bob.Click("co-tab:standing");
        Assert.Contains("held by Toy Transit Co.", bob.Render());
        may.OpenDialog("seat");
        Assert.Contains("Toy Transit Co. holds it", may.Render());
    }
}
