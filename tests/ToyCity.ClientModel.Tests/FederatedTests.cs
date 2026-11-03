using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;
using FedPlayer = ToyCity.ClientModel.Tests.FederatedTests.Player;
using CharterRule = ToyCity.Protocol.V0.CharterRule;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// A federated region (Beta 3 part 8): a region host with Toyville, and two tile hosts in their own processes running
/// Westbrook and Eastfield. The days go round with a barrier, the monthly hashes agree, players on a tile host sign in
/// against the region's accounts, and a tile host that dies is covered by a computer mayor.
/// </summary>
[Collection(nameof(FederatedTests))]
public class FederatedTests
{
    internal sealed class Player : IDisposable
    {
        public readonly CityClient Client;
        public readonly PanelModel Panels;
        public readonly List<ServerMessage> Seen = new();

        public Player(Uri ws, string name)
        {
            Client = new CityClient(ws, "test");
            Panels = new PanelModel(Client.City, Client.Connection.Send) { Me = () => name, Admin = () => Client.Connection.Admin };
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

/// <summary>
/// Beta 4, part 7: a tile of a growing region on its own host. The region grows around it, a neighbour is founded
/// beside it, the region's charter changes from the tile host and stays with the region's host, and a hosted city can't
/// annex or be taken in.
/// </summary>
[Collection(nameof(FederatedTests))]
public class GrowingTileHostTests
{
    const string Key = "sekrit";

    [Fact]
    public void ATileOfAGrowingRegionRunsOnItsOwnHost()
    {
        using var leader = new ServerProcess($$"""{ "name": "Fed", "join": "open", "linkKey": "{{Key}}", "takeOverSeconds": 4, "fastDayMs": 20 }""");
        ServerProcess? east = null;
        try
        {
            using var may = new FedPlayer(leader.Ws, "May");
            may.Panels.Click("dialog:new");
            may.Panels.SetInput("Firstford");
            may.Panels.Click("grow");
            may.Panels.Click("size:64");
            may.Panels.Click("new-ok");
            may.WaitFor(() => may.Client.City.Stats.CityName == "Firstford" && may.Region is { Grows: true, TileSize: 64 }, "the growing region");
            int c = may.Region.HereCol;
            may.Client.Connection.Send(new ClientMessage { Grow = new GrowRegion { Col = c + 1, Row = c } });
            may.WaitFor(() => may.Region.Tiles.Any(t => t.Col == c + 1 && t.Row == c && !t.Wild), "the east tile");
            may.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = c + 1, Row = c, Name = "Eastwick", StarterTown = true } });
            may.WaitFor(() => may.Region.Tiles.Any(t => t.Col == c + 1 && t.Row == c && t.Founded), "Eastwick");
            // Eastwick annexes the land north of it, so its host runs a city of two tiles, named by its lower one.
            may.Client.Connection.Send(new ClientMessage { Grow = new GrowRegion { Col = c + 1, Row = c - 1 } });
            may.WaitFor(() => may.Region.Tiles.Any(t => t.Col == c + 1 && t.Row == c - 1 && !t.Wild), "the tile north of Eastwick");
            may.Client.Connection.Send(new ClientMessage { GoTo = new GoToCity { Col = c + 1, Row = c } });
            may.WaitFor(() => may.Client.City.Stats.CityName == "Eastwick", "May in Eastwick");
            may.Client.Connection.Send(new ClientMessage { Annex = new AnnexRequest { Col = c + 1, Row = c - 1 } });
            may.WaitFor(() => may.Client.City.Height == 128, "Eastwick on two tiles");
            may.Client.Connection.Send(new ClientMessage { GoTo = new GoToCity { Col = c, Row = c } });
            may.WaitFor(() => may.Client.City.Stats.CityName == "Firstford", "May back in Firstford");

            east = new ServerProcess(null, $"--tile-host ws://127.0.0.1:{leader.Port}/tile-link --tile {c + 1},{c} --link-key {Key} --public ws://127.0.0.1:{{port}}/ws");
            may.WaitFor(() => may.Region.Tiles.Single(t => t.Col == c + 1 && t.Row == c - 1).Address.Length > 0, "Eastwick's host on the map");
            may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
            long start = may.Region.Tick;
            may.WaitFor(() => may.Region.Tick >= start + 40, "the days going by", 120);
            Assert.Equal("", may.Region.Stopped);

            // The region grows around the hosted tile, and a city is founded beside it.
            may.Client.Connection.Send(new ClientMessage { Grow = new GrowRegion { Col = c + 1, Row = c + 1 } });
            may.WaitFor(() => may.Region.Tiles.Any(t => t.Col == c + 1 && t.Row == c + 1 && !t.Wild), "the tile south of Eastwick");
            may.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = c + 1, Row = c + 1, Name = "Southby", StarterTown = true, Computer = "green" } });
            may.WaitFor(() => may.Region.Tiles.Any(t => t.Col == c + 1 && t.Row == c + 1 && t.Founded), "Southby");
            start = may.Region.Tick;
            may.WaitFor(() => may.Region.Tick >= start + 40, "the region carrying on", 120);
            Assert.Equal("", may.Region.Stopped);

            // A player on Eastwick's host sees the grown region and its charter, and asks for a new charter: the
            // region's host keeps it.
            using var tess = new FedPlayer(east.Ws, "Tess");
            Assert.Equal("Eastwick", tess.Client.City.Stats.CityName);
            Assert.Equal((64, 128), (tess.Client.City.Width, tess.Client.City.Height));
            long tessDay = tess.Client.City.Stats.Tick;
            tess.WaitFor(() => tess.Client.City.Stats.Tick > tessDay + 3, "Eastwick's days go by on its host");
            tess.WaitFor(() => tess.Region.Tiles.Any(t => t.Name == "Southby"), "Southby on Tess's map");
            Assert.True(tess.Region.Grows);
            Assert.NotNull(tess.Region.Charter);
            // Eastwick, on its own host, can't annex or be taken in.
            Assert.All(tess.Region.Annex, a => Assert.Contains("own host", a.Why));
            may.Client.Connection.Send(new ClientMessage { Takeover = new TakeoverRequest { Op = TakeoverRequest.Types.Op.Offer, Col = c + 1, Row = c - 1, Price = 1000 } });
            may.WaitFor(() => { lock (may.Seen) return may.Seen.Any(m => m.Error != null && m.Error.Message.Contains("own host")); }, "the refusal");
            // An admin's charter from the tile host is set on the region's host.
            may.Client.Connection.Send(new ClientMessage { Charter = new SetCharter { Charter = new RegionCharter { Grow = CharterRule.CharterAdmins, Annex = CharterRule.CharterAdmins, MaxCitySide = 2 } } });
            tess.WaitFor(() => tess.Region.Charter.MaxCitySide == 2, "the charter on Tess's map");
            may.WaitFor(() => may.Region.Charter.MaxCitySide == 2, "the charter on May's map");
        }
        catch (Exception e)
        {
            string reports = Directory.Exists(leader.Saves) ? string.Join("\n", Directory.GetFiles(leader.Saves, "desync-*.txt").Select(File.ReadAllText)) : "";
            throw new Exception($"{e.Message}\n{reports}\nREGION HOST:\n{leader.Output}\nEASTWICK:\n{east?.Output}", e);
        }
        finally { east?.Dispose(); }
    }
}

/// <summary>The federated run starts three servers at full speed: it runs on its own so the other server tests don't starve.</summary>
[CollectionDefinition(nameof(FederatedTests), DisableParallelization = true)]
public sealed class FederatedTestsAlone;
