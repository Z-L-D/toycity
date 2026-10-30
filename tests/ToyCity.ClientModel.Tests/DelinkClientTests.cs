using System.Text.RegularExpressions;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>De-linking on a dedicated server: a player leaves from the seat dialog and gets their copy over the wire.</summary>
public class DelinkClientTests : IClassFixture<DelinkClientTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public readonly ServerProcess Server = new("""{ "name": "Parting town", "join": "open" }""");
        public void Dispose() => Server.Dispose();
    }

    readonly ServerProcess _server;
    public DelinkClientTests(Fixture f) => _server = f.Server;

    static List<string> Links(string bb) => Regex.Matches(bb, @"\[url=([^\]]*)\]").Select(m => m.Groups[1].Value).ToList();

    static (CityClient Client, PanelModel Panel) SignIn(Uri ws, string name)
    {
        var c = new CityClient(ws, "test");
        var panel = new PanelModel(c.City, c.Connection.Send) { Me = () => c.Connection.Player, OnServer = () => true };
        c.Message += panel.Handle;
        Wait(c, () => c.Connection.SignInNeeded);
        c.Connection.SignIn(name, "secret1", create: true);
        Wait(c, () => c.Connection.Player.Length > 0 || c.Connection.SignInNeeded);
        if (c.Connection.SignInNeeded) c.Connection.SignIn(name, "secret1");
        Wait(c, () => c.City.Loaded && panel.Civic != null);
        return (c, panel);
    }

    static void Wait(CityClient c, Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            c.Poll();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void APlayerLeavesWithACopyAndCantComeStraightBack()
    {
        var (mayor, mayorPanel) = SignIn(_server.Ws, "Mayor May");
        using var _ = mayor;
        var (olu, panel) = SignIn(_server.Ws, "Olu");
        using var __ = olu;
        Assert.Equal("Mayor May", panel.Mayor!.Owner);

        // Olu takes the bus company, then leaves from the seat dialog.
        panel.OpenDialog("seat");
        panel.Click("claim:transport");
        Wait(olu, () => panel.Civic!.Seats.First(s => s.Id == "transport").Owner == "Olu");
        Wait(olu, () => panel.Civic!.DelinkOptions.Any(o => o.Player == "Olu"));
        panel.OpenDialog("seat");
        Assert.Contains("delink:Olu", Links(panel.Render()));
        string? name = null;
        byte[]? save = null;
        panel.CopyArrived += (n, s) => (name, save) = (n, s);
        panel.Click("delink:Olu");
        Assert.Equal("confirm", panel.Dialog);
        panel.Click("yes");
        Wait(olu, () => save != null);

        // The copy is a city Olu is mayor of; the server's city carries on with the company on caretaker.
        Sim.SideGameCatalog.Register(Transport.TransportGame.Id, () => new Transport.TransportGame());
        Sim.SideGameCatalog.Register(Emergency.EmergencyGame.Id, () => new Emergency.EmergencyGame());
        using (var copy = Protocol.Mapping.Load(Sim.ContentDb.LoadDefault(), save!))
        {
            Assert.Equal("Olu", copy.MayorSeat.Owner);
            Assert.Equal("Olu", copy.SeatOf("transport")!.Owner);
            Assert.Contains("Olu", name);
        }
        Wait(mayor, () => mayorPanel.Civic!.Delinks.Count == 1);
        Assert.Equal("", mayorPanel.Civic!.Seats.First(s => s.Id == "transport").Owner);
        mayorPanel.OpenDialog("seat");
        Assert.Contains("Olu left", mayorPanel.Render());

        // Olu can't take the company back during the cooldown.
        olu.Connection.Act(new Protocol.V0.Action { Seat = new SetSeat { Seat = "transport", Owner = "Olu", Level = SeatLevel.SeatManual, SpendCap = 25000 } });
        var until = DateTime.UtcNow.AddSeconds(1);
        Wait(mayor, () => DateTime.UtcNow > until);
        Assert.Equal("", mayorPanel.Civic!.Seats.First(s => s.Id == "transport").Owner);
    }
}
