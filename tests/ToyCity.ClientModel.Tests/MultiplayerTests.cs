using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Several players on one dedicated server: who may act, who hears what, and seats while a player is away.</summary>
public class MultiplayerTests : IClassFixture<MultiplayerTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public readonly ServerProcess Server = new("""{ "name": "Shared town", "join": "open", "awayAfterSeconds": 2, "forbid": ["Sabotage"] }""");
        public void Dispose() => Server.Dispose();
    }

    readonly ServerProcess _server;
    public MultiplayerTests(Fixture f) => _server = f.Server;

    /// <summary>A signed-in player, with what the server has told them.</summary>
    sealed class Player : IDisposable
    {
        public readonly CityClient Client;
        public readonly List<string> Notices = new();
        public Presence? Presence;
        public CivicState? Civic;
        public SideGameView? View;

        public Player(Uri ws, string name, SignIn? token = null)
        {
            Client = new CityClient(ws, "test", token);
            Client.Message += m =>
            {
                if (m.BodyCase == ServerMessage.BodyOneofCase.Delta) Notices.AddRange(m.Delta.Notices.Select(n => n.Text));
                if (m.BodyCase == ServerMessage.BodyOneofCase.Presence) Presence = m.Presence;
                if (m.BodyCase == ServerMessage.BodyOneofCase.Civic) Civic = m.Civic;
                if (m.BodyCase == ServerMessage.BodyOneofCase.SideGame && m.SideGame.Game == "transport") View = m.SideGame;
            };
            if (token != null) { WaitFor(() => Client.City.Loaded, "the city"); return; }
            WaitFor(() => Client.Connection.SignInNeeded, "the welcome");
            Client.Connection.SignIn(name, "secret1", create: true);
            WaitFor(() => Client.Connection.Player.Length > 0 || Client.Connection.SignInNeeded, "an answer");
            if (Client.Connection.SignInNeeded) Client.Connection.SignIn(name, "secret1");
            WaitFor(() => Client.City.Loaded && Presence != null && Civic != null, "the city");
        }

        public void WaitFor(Func<bool> done, string what, double seconds = 20)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"waiting for {what}");
                Client.Poll();
                Thread.Sleep(10);
            }
        }

        public void Settle(double seconds = 0.5) { var until = DateTime.UtcNow.AddSeconds(seconds); WaitFor(() => DateTime.UtcNow > until, "time", seconds + 5); }

        public SeatState Seat(string id) => Civic!.Seats.First(s => s.Id == id);

        public void Dispose() => Client.Dispose();
    }

    [Fact]
    public void TheMayorRunsTheCityTheOthersWatchOrClaimASeatAndRefusalsArePrivate()
    {
        using var mayor = new Player(_server.Ws, "Mayor May");
        using var mo = new Player(_server.Ws, "Mo");
        Assert.Equal("Mayor May", mayor.Seat("mayor").Owner);  // the server's first account and its admin
        mo.WaitFor(() => mo.Presence!.Players.Count >= 2, "both players in the presence");
        var moHere = mo.Presence!.Players.First(p => p.Name == "Mo");
        Assert.True(moHere.Online);
        Assert.True(moHere.Spectator);

        mo.Client.Connection.Act(new Protocol.V0.Action { Tax = new SetTax { Zone = ZoneKind.ZoneResidential, Wealth = Wealth.Low, PercentTenths = 150 } });
        mo.WaitFor(() => mo.Notices.Any(n => n.Contains("Only the mayor (Mayor May)")), "the refusal");
        mayor.Settle();
        Assert.DoesNotContain(mayor.Notices, n => n.Contains("Only the mayor"));

        mo.Client.Connection.Act(new Protocol.V0.Action { Seat = new SetSeat { Seat = "transport", Owner = "Mo", Level = SeatLevel.SeatManual, SpendCap = 25000 } });
        mayor.WaitFor(() => mayor.Civic!.Seats.First(s => s.Id == "transport").Owner == "Mo", "Mo's claim");
        mayor.WaitFor(() => mayor.Presence!.Players.First(p => p.Name == "Mo").Seats.Contains("transport"), "Mo's seat in the presence");
        // The mayor can't take it from Mo; Mo can give it up.
        mayor.Client.Connection.Act(new Protocol.V0.Action { Seat = new SetSeat { Seat = "transport", Owner = "Mayor May", Level = SeatLevel.SeatManual, SpendCap = 25000 } });
        mayor.WaitFor(() => mayor.Notices.Any(n => n.Contains("Mo owns that seat")), "the refusal");
        mo.Client.Connection.Act(new Protocol.V0.Action { Seat = new SetSeat { Seat = "transport", Owner = "", Level = SeatLevel.SeatCaretaker, SpendCap = 25000 } });
        mayor.WaitFor(() => mayor.Seat("transport").Owner == "", "Mo to give it up");
    }

    [Fact]
    public void AnAbsentPlayersSeatGoesToTheComputerAndComesBackWithThem()
    {
        using var mayor = new Player(_server.Ws, "Mayor May");
        SignIn token;
        using (var jo = new Player(_server.Ws, "Jo"))
        {
            // Jo takes the bus company and leaves for a while.
            jo.Client.Connection.Act(new Protocol.V0.Action { Seat = new SetSeat { Seat = "transport", Owner = "Jo", Level = SeatLevel.SeatManual, SpendCap = 25000 } });
            jo.WaitFor(() => jo.Seat("transport") is { Owner: "Jo", Level: SeatLevel.SeatManual }, "the claim");
            token = jo.Client.Connection.Credentials!;
            mayor.WaitFor(() => mayor.Seat("transport").Owner == "Jo", "the mayor to see Jo's claim");
        }
        mayor.WaitFor(() => mayor.Presence!.Players.Any(p => p.Name == "Jo" && !p.Online), "Jo to show as away");
        mayor.WaitFor(() => mayor.Seat("transport") is { Level: SeatLevel.SeatCaretaker, AutoAway: true }, "the computer to take Jo's seat", 15);

        using var back = new Player(_server.Ws, "Jo", token);
        mayor.WaitFor(() => mayor.Seat("transport").Level == SeatLevel.SeatManual, "Jo's seat back");
        mayor.WaitFor(() => mayor.Presence!.Players.First(p => p.Name == "Jo").Online, "Jo online again");
    }

    [Fact]
    public void ConsentFollowsAPlayerToTheNextCityAndTheHostForbidsWhatItForbids()
    {
        using var mayor = new Player(_server.Ws, "Mayor May");
        var sabotage = mayor.Civic!.Consent.Rows.Single(r => r.Mechanic == Mechanic.MechSabotage);
        Assert.True(sabotage.HostForbids);
        Assert.False(sabotage.Effective);
        mayor.Client.Connection.Act(new Protocol.V0.Action { Consent = new SetConsent { Owner = "Mayor May", Mechanic = Mechanic.MechBribery, Allowed = true } });
        mayor.WaitFor(() => Bribery(mayor), "the setting");

        mayor.Client.Connection.Send(new ClientMessage { NewCity = new NewCityRequest { Name = "Second city", Size = 64 } });
        mayor.WaitFor(() => mayor.Client.City.Width == 64 && mayor.Civic!.Seats[0].Owner == "Mayor May", "the new city with May as mayor");
        mayor.WaitFor(() => Bribery(mayor), "her setting to follow her");
        Assert.True(mayor.Civic!.Consent.Rows.Single(r => r.Mechanic == Mechanic.MechSabotage).HostForbids);

        static bool Bribery(Player p)
        {
            var c = p.Civic!.Consent;
            int i = c.Owners.IndexOf("Mayor May");
            return i >= 0 && c.Rows.Single(r => r.Mechanic == Mechanic.MechBribery).ByOwner[i];
        }
    }

    [Fact]
    public void ARivalSeesTheRoutesNotTheBooksAndDeputiesAndTheCouncilWorkOverTheWire()
    {
        using var server = new ServerProcess("""{ "name": "Council town", "join": "open" }""");
        using var may = new Player(server.Ws, "Mayor May");
        using var olu = new Player(server.Ws, "Olu");
        using var jo = new Player(server.Ws, "Jo");

        olu.Client.Connection.Act(new Protocol.V0.Action { Seat = new SetSeat { Seat = "transport", Owner = "Olu", Level = SeatLevel.SeatManual, SpendCap = 25000 } });
        olu.WaitFor(() => olu.View is { Seat: "operator" } && olu.Seat("transport").Owner == "Olu", "Olu's own view");
        jo.WaitFor(() => jo.View is { Seat: "public" }, "the public view for Jo");
        may.WaitFor(() => may.View is { Seat: "public" }, "the public view for the mayor");
        var pub = Transport.Wire.View.Parser.ParseFrom(jo.View!.Payload);
        Assert.Equal(0, pub.Cash);
        Assert.Empty(pub.Offers);
        Assert.Empty(pub.Log);
        Assert.Empty(jo.Civic!.SideGames.Single(g => g.Id == "transport").Standings);
        Assert.All(may.Civic!.SideGames.Single(g => g.Id == "transport").Standings, st => Assert.Equal(0, st.BribeBanDays));

        // May makes Jo deputy for the budget and seats the council: Jo's tax change goes to a vote.
        may.Client.Connection.Act(new Protocol.V0.Action { Deputy = new SetDeputy { Player = "Jo", Portfolios = 2 } });
        may.Client.Connection.Act(new Protocol.V0.Action { Council = new SetCouncil { On = true } });
        jo.WaitFor(() => jo.Civic!.CouncilOn && jo.Civic.Deputies.Any(d => d.Player == "Jo"), "the council and Jo's portfolio");
        jo.WaitFor(() => jo.Presence!.Players.First(p => p.Name == "Jo").DeputyFor.Contains("budget"), "Jo's role in the presence");
        Assert.Equal(new[] { "Jo", "Mayor May", "Olu" }, jo.Civic!.Council.OrderBy(n => n, StringComparer.Ordinal));
        jo.Client.Connection.Act(new Protocol.V0.Action { Tax = new SetTax { Zone = ZoneKind.ZoneResidential, Wealth = Wealth.Low, PercentTenths = 120 } });
        may.WaitFor(() => may.Civic!.Proposals.Any(p => p.Status == "open" && p.Proposer == "Jo"), "Jo's proposal");
        int id = may.Civic!.Proposals.First(p => p.Status == "open").Id;
        may.Client.Connection.Act(new Protocol.V0.Action { Vote = new Vote { Proposal = id, Yes = true } });
        olu.Client.Connection.Act(new Protocol.V0.Action { Vote = new Vote { Proposal = id, Yes = false } });
        jo.WaitFor(() => jo.Civic!.Proposals.FirstOrDefault(p => p.Id == id)?.Status == "passed", "the vote");
    }
}
