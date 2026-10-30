using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>A dedicated server running a region: the map, founding a city, moving between cities, and the one clock.</summary>
public class RegionClientTests : IClassFixture<RegionClientTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public readonly ServerProcess Server = new("""{ "name": "Valley", "join": "open" }""");
        public void Dispose() => Server.Dispose();
    }

    readonly ServerProcess _server;
    public RegionClientTests(Fixture f) => _server = f.Server;

    sealed class Player : IDisposable
    {
        public readonly CityClient Client;
        public readonly PanelModel Panels;
        public readonly List<string> Errors = new();

        public Player(Uri ws, string name)
        {
            Client = new CityClient(ws, "test");
            Panels = new PanelModel(Client.City, Client.Connection.Send) { Me = () => name };
            Client.Message += Panels.Handle;
            Client.Message += m => { if (m.BodyCase == ServerMessage.BodyOneofCase.Error) Errors.Add(m.Error.Message); };
            WaitFor(() => Client.Connection.SignInNeeded, "the welcome");
            Client.Connection.SignIn(name, "secret1", create: true);
            WaitFor(() => Client.Connection.Player.Length > 0 || Client.Connection.SignInNeeded, "an answer");
            if (Client.Connection.SignInNeeded) Client.Connection.SignIn(name, "secret1");
            WaitFor(() => Client.City.Loaded && Panels.Region.State != null, "the city and its region");
        }

        public RegionModel Region => Panels.Region;

        public void WaitFor(Func<bool> done, string what, double seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"waiting for {what}");
                Client.Poll();
                Thread.Sleep(10);
            }
        }

        public void Dispose() => Client.Dispose();
    }

    [Fact]
    public void PlayersFoundCitiesMoveBetweenThemAndShareOneClock()
    {
        using var may = new Player(_server.Ws, "May");
        var r = may.Region.State!;
        Assert.Equal(3, r.Cols);
        Assert.Equal(9, r.Tiles.Count);
        Assert.Equal((1, 1), (r.HereCol, r.HereRow));
        Assert.Equal("Toyville", may.Client.City.Stats.CityName);
        Assert.Equal("May", r.Tiles[4].Mayor);  // the first account takes Toyville from the solo "player"
        Assert.All(r.Tiles, t => Assert.Equal(RegionModel.PictureSize * RegionModel.PictureSize * 3, t.Picture.Length));

        using var tess = new Player(_server.Ws, "Tess");
        Assert.Equal("Toyville", tess.Client.City.Stats.CityName);
        tess.Panels.Click("rg-open");
        Assert.True(tess.Region.Active);
        tess.Panels.Click("rg-pick:2:1");
        tess.Region.NewName = "Eastfield";
        tess.Panels.Click("rg-found");
        Assert.StartsWith("Founding", tess.Region.Pending);
        tess.WaitFor(() => tess.Region.TileAt(2, 1)?.Founded == true, "Eastfield founded");
        Assert.Equal("Tess", tess.Region.TileAt(2, 1)!.Mayor);
        Assert.Equal("", tess.Region.Pending);
        may.WaitFor(() => may.Region.TileAt(2, 1)?.Name == "Eastfield", "May's map shows Eastfield");

        // Tess opens her city on the same connection: the screen goes back to the city, which is Eastfield.
        tess.Panels.Click("rg-go");
        tess.WaitFor(() => tess.Client.City.Stats.CityName == "Eastfield" && !tess.Region.Active, "Eastfield opened");
        tess.WaitFor(() => tess.Region.State!.HereCol == 2, "the map says she's in Eastfield");
        may.WaitFor(() => may.Region.TileAt(2, 1)!.Players.Contains("Tess") && !may.Region.TileAt(1, 1)!.Players.Contains("Tess"), "May sees Tess move");

        // Open land can't be opened, and a city can't be founded twice.
        tess.Client.Connection.Send(new ClientMessage { GoTo = new GoToCity { Col = 0, Row = 0 } });
        tess.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = 1, Row = 1, Name = "Again" } });
        tess.WaitFor(() => tess.Errors.Count >= 2, "two refusals");

        // One clock: both cities are on the same day, and a mayor who pauses holds the whole region.
        long Tick(Player p) => p.Region.State!.Tick;
        tess.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 0 } });
        may.WaitFor(() => may.Region.State!.Speed == 0 && may.Region.State.SpeedSetBy == "Tess", "the region held by Tess");
        Thread.Sleep(500);
        var held = may.Client.City.Stats.Day;
        tess.WaitFor(() => tess.Client.City.Stats.Day == held && tess.Client.City.Stats.Month == may.Client.City.Stats.Month, "the same day in both cities");
        // The admin presses play: the region runs again.
        may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        may.WaitFor(() => may.Region.State!.Speed == 3, "the region running");
        long before = Tick(may);
        may.WaitFor(() => may.Client.City.Stats.Day != held, "a day passes");
        Assert.True(may.Region.State!.Tick >= before);

        // A deal needs the two cities joined at the border: May's offer of power to Eastfield is refused for now,
        // with the reason, and the "working" line goes.
        may.Panels.Click("rg-open");
        may.Panels.Click("rg-pick:2:1");
        Assert.Contains("A deal with Eastfield", may.Region.Render());
        int errors = may.Errors.Count;
        may.Panels.Click("rg-offer");
        Assert.StartsWith("Deal:", may.Region.Pending);
        may.WaitFor(() => may.Errors.Count > errors, "the refusal");
        Assert.Contains("power line", may.Errors[^1]);
        Assert.Equal("", may.Region.Pending);
    }

    [Fact]
    public void TheRegionScreenListsDealsWithWhatTheMayorCanDo()
    {
        var model = new RegionModel(_ => 0, () => { }) { Me = () => "Tess" };
        var s = new RegionState { Name = "V", Cols = 2, Rows = 1, HereCol = 1, HereRow = 0 };
        s.Tiles.Add(new RegionTileState { Col = 0, Row = 0, Founded = true, Name = "Westbrook", Mayor = "May" });
        s.Tiles.Add(new RegionTileState { Col = 1, Row = 0, Founded = true, Name = "Eastfield", Mayor = "Tess" });
        s.Deals.Add(new Deal { Id = 4, Kind = DealKind.DealPower, SellerCol = 0, SellerRow = 0, BuyerCol = 1, BuyerRow = 0, Seller = "Westbrook", Buyer = "Eastfield", Amount = 500, Price = 100, OfferedBySeller = true });
        s.Deals.Add(new Deal { Id = 5, Kind = DealKind.DealTransit, SellerCol = 1, SellerRow = 0, BuyerCol = 0, BuyerRow = 0, Seller = "Eastfield", Buyer = "Westbrook", Amount = 200, Price = 50, Active = true, OfferedBySeller = true });
        model.Handle(new ServerMessage { Region = s });
        var text = model.Render();
        Assert.Contains("Westbrook sells Eastfield 500 kW for $100 a month", text);
        Assert.Contains("rg-accept:4", text);
        Assert.Contains("rg-decline:4", text);
        Assert.Contains("rg-end:5", text);
        model.Click("rg-pick:0:0");
        Assert.Equal(3, model.SideOf(model.PickedTile!));
        Assert.Contains("A deal with Westbrook", model.Render());
    }
}
