using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 3.2, two clocks on a server: the region's calendar pace, who may set it, and the clock a player reads.</summary>
public class ClockClientTests
{
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
    public void AnAdminSetsTheRegionsPaceAndAnotherPlayerCannot()
    {
        using var server = new ServerProcess("""{ "name": "Hours", "join": "open", "admins": ["Ada"] }""");
        using var ada = new Player(server.Ws, "Ada");
        // A new server's region keeps a year a clock day; the city reads both clocks.
        Assert.Equal(360, ada.Panels.Region.State!.Pace);
        Assert.Equal(360, ada.Client.City.Stats.Pace);
        Assert.Equal(4, ada.Client.City.Stats.ClockMinutesPerTick);
        Assert.Matches(@"^(Mon|Tue|Wed|Thu|Fri|Sat|Sun) \d\d:\d\d · [A-Z][a-z]+, year \d+$", new GameClock().Text(ada.Client.City.Stats, DateTime.UtcNow));

        ada.Client.Connection.Send(new ClientMessage { Pace = new SetPace { DaysPerClockDay = 720 } });
        ada.WaitFor(() => ada.Panels.Region.State!.Pace == 720 && ada.Client.City.Stats.Pace == 720, "a pace of two years a day");
        Assert.Equal(2, ada.Client.City.Stats.ClockMinutesPerTick);
        Assert.Empty(ada.Errors);

        using var bo = new Player(server.Ws, "Bo");
        bo.Client.Connection.Send(new ClientMessage { Pace = new SetPace { DaysPerClockDay = 30 } });
        bo.WaitFor(() => bo.Errors.Count > 0, "the refusal");
        Assert.Contains("shares one calendar", bo.Errors[0]);
        ada.Client.Connection.Send(new ClientMessage { Pace = new SetPace { DaysPerClockDay = 100 } });
        ada.WaitFor(() => ada.Errors.Count > 0, "the refusal");
        Assert.Equal(720, bo.Panels.Region.State!.Pace);
    }

    [Fact]
    public void TheHandsRunOnBetweenTicksButNotPastTheNext()
    {
        var clock = new GameClock();
        var s = new CityStats { ClockMinute = 8 * 60, ClockMinutesPerTick = 48, Speed = 1, Month = 3, Year = 2002 };
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Mon 08:00 · March, year 3", clock.Text(s, t0));
        // At speed 1 a clock day is an hour: 2.5 real seconds a clock minute, so ten seconds is four minutes.
        Assert.Equal("Mon 08:04 · March, year 3", clock.Text(s, t0.AddSeconds(10)));
        // Never onto the next tick's minute.
        Assert.Equal("Mon 08:47 · March, year 3", clock.Text(s, t0.AddHours(1)));
        s.ClockMinute += 48;
        Assert.Equal("Mon 08:48 · March, year 3", clock.Text(s, t0.AddHours(1)));
        s.Speed = 0;
        s.ClockMinute = 6 * 24 * 60 + 23 * 60 + 59;
        Assert.Equal("Sun 23:59 · March, year 3", clock.Text(s, t0.AddHours(5)));
    }
}
