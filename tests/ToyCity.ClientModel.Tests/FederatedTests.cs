using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// A federated region (Beta 3 part 8): a region host with Toyville, and two tile hosts in their own processes running
/// Westbrook and Eastfield. The days go round with a barrier, the monthly hashes agree, players on a tile host sign in
/// against the region's accounts, and a tile host that dies is covered by a computer mayor.
/// </summary>
public class FederatedTests
{
    sealed class Player : IDisposable
    {
        public readonly CityClient Client;
        public readonly PanelModel Panels;
        public readonly List<ServerMessage> Seen = new();

        public Player(Uri ws, string name)
        {
            Client = new CityClient(ws, "test");
            Panels = new PanelModel(Client.City, Client.Connection.Send) { Me = () => name };
            Client.Message += Panels.Handle;
            Client.Message += m => { lock (Seen) Seen.Add(m); };
            WaitFor(() => Client.Connection.SignInNeeded, "the welcome");
            Client.Connection.SignIn(name, "secret1", create: true);
            WaitFor(() => Client.Connection.Player.Length > 0 || Client.Connection.SignInNeeded, "an answer");
            if (Client.Connection.SignInNeeded) Client.Connection.SignIn(name, "secret1");
            WaitFor(() => Client.City.Loaded && Panels.Region.State != null, "the city and its region");
        }

        public RegionState Region => Panels.Region.State!;

        public void WaitFor(Func<bool> done, string what, double seconds = 60)
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

    const string Key = "sekrit";

    static ServerProcess TileHost(ServerProcess leader, int col, int row) =>
        new(null, $"--tile-host ws://127.0.0.1:{leader.Port}/tile-link --tile {col},{row} --link-key {Key} --public ws://127.0.0.1:{{port}}/ws");

    [Fact]
    public void TileHostsRunTheirCitiesInStepAndAComputerCoversForOneThatDies()
    {
        using var leader = new ServerProcess($$"""{ "name": "Fed", "join": "open", "linkKey": "{{Key}}", "takeOverSeconds": 4, "fastDayMs": 20 }""");
        using var may = new Player(leader.Ws, "May");
        // The admin founds Westbrook for herself and Eastfield for a careful computer, then hands them to tile hosts.
        may.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = 0, Row = 1, Name = "Westbrook", StarterTown = true } });
        may.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = 2, Row = 1, Name = "Eastfield", StarterTown = true, Computer = "careful" } });
        may.WaitFor(() => may.Region.Tiles.Count(t => t.Founded) == 3, "three cities");
        using var west = TileHost(leader, 0, 1);
        var east = TileHost(leader, 2, 1);
        try
        {
            may.WaitFor(() => may.Region.Tiles.Single(t => t.Col == 0 && t.Row == 1).Address.Length > 0
                              && may.Region.Tiles.Single(t => t.Col == 2 && t.Row == 1).Address.Length > 0, "both tile hosts on the map");
            Assert.Equal($"ws://127.0.0.1:{west.Port}/ws", may.Region.Tiles.Single(t => t.Col == 0 && t.Row == 1).Address);

            // Full speed: two months go by with the tile hosts in step.
            may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
            long start = may.Region.Tick;
            may.WaitFor(() => may.Region.Tick >= start + 70, "two months", 120);
            Assert.Equal("", may.Region.Stopped);

            // A player on Westbrook's host signs in with the region's accounts and lands in Westbrook.
            using var tess = new Player(west.Ws, "Tess");
            Assert.Equal("Westbrook", tess.Client.City.Stats.CityName);
            Assert.Equal(0, tess.Region.HereCol);
            Assert.Equal($"ws://127.0.0.1:{leader.Port}/ws", tess.Region.Tiles.Single(t => t.Col == 1 && t.Row == 1).Address);
            long tessDay = tess.Client.City.Stats.Tick;
            tess.WaitFor(() => tess.Client.City.Stats.Tick > tessDay + 3, "Westbrook's days go by on its host");
            // Asking to open Toyville from there sends her to the region host.
            tess.Client.Connection.Send(new ClientMessage { GoTo = new GoToCity { Col = 1, Row = 1 } });
            tess.WaitFor(() => { lock (tess.Seen) return tess.Seen.Any(m => m.Redirect != null); }, "a redirect");
            lock (tess.Seen) Assert.Equal($"ws://127.0.0.1:{leader.Port}/ws", tess.Seen.Last(m => m.Redirect != null).Redirect.Address);
            // And May asking for Westbrook is sent to its host.
            may.Client.Connection.Send(new ClientMessage { GoTo = new GoToCity { Col = 0, Row = 1 } });
            may.WaitFor(() => { lock (may.Seen) return may.Seen.Any(m => m.Redirect != null); }, "May's redirect");

            // Eastfield's host dies: the region holds, then runs Eastfield itself and carries on.
            east.Kill();
            may.WaitFor(() => may.Region.Waiting.Contains("Eastfield"), "the region waiting for Eastfield", 30);
            may.WaitFor(() => may.Region.Waiting.Length == 0 && may.Region.Tiles.Single(t => t.Col == 2 && t.Row == 1).Address.Length == 0, "Eastfield taken over", 30);
            long after = may.Region.Tick;
            may.WaitFor(() => may.Region.Tick >= after + 40, "the region carrying on", 60);
            Assert.Equal("", may.Region.Stopped);
            Assert.EndsWith("(computer)", may.Region.Tiles.Single(t => t.Col == 2 && t.Row == 1).Mayor);
            // Westbrook stayed in step through it all: its host's day is the region's.
            tess.WaitFor(() => { may.Client.Poll(); return Math.Abs(tess.Client.City.Stats.Tick - may.Client.City.Stats.Tick) <= 3; }, "Westbrook in step", 20);
        }
        catch (Exception e)
        {
            string reports = Directory.Exists(leader.Saves) ? string.Join("\n", Directory.GetFiles(leader.Saves, "desync-*.txt").Select(File.ReadAllText)) : "";
            throw new Exception($"{e.Message}\n{reports}\nREGION HOST:\n{leader.Output}\nWESTBROOK:\n{west.Output}\nEASTFIELD:\n{east.Output}", e);
        }
        finally { east.Dispose(); }
    }
}
