using ToyCity.Protocol.V0;
using ToyCity.Sim;

namespace ToyCity.ClientModel.Tests;

public class MirrorTests : IClassFixture<ServerFixture>
{
    readonly ServerProcess _server;
    public MirrorTests(ServerFixture f) => _server = f.Server;

    static void PollUntil(CityClient c, Func<bool> done, int seconds = 20)
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
    public void AMirrorKeptByDeltasMatchesAFreshFullState()
    {
        using var a = new CityClient(_server.Ws, "test-a");
        PollUntil(a, () => a.City.Loaded);
        Assert.True(a.City.Width >= 64 && a.City.Buildings.Count > 0);

        // Build something, let the city run, then pause.
        var spot = Enumerable.Range(0, a.City.N).First(i => a.City.Paths[i] == 0 && a.City.BuildingAt[i] == 0 && !a.City.IsWater(i)
            && i % a.City.Width > 2 && i / a.City.Width > 2 && a.City.Heights[i] == a.City.Heights[i + 1]);
        a.Connection.Act(new Protocol.V0.Action { Path = new PlacePath { X0 = spot % a.City.Width, Y0 = spot / a.City.Width, X1 = spot % a.City.Width + 1, Y1 = spot / a.City.Width, ProfileId = a.City.Profiles[0].Id } });
        a.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        var t0 = a.City.Stats.Tick;
        PollUntil(a, () => a.City.Stats.Tick > t0 + 10, 60);
        a.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 0 } });
        long last = -1;
        PollUntil(a, () => { bool still = a.City.Stats.Tick == last && a.City.Stats.Speed == 0; last = a.City.Stats.Tick; Thread.Sleep(300); return still; });
        Assert.NotEqual(0, a.City.Paths[spot]);

        using var b = new CityClient(_server.Ws, "test-b");
        PollUntil(b, () => b.City.Loaded);
        a.Poll();
        Assert.Equal(b.City.Stats.Tick, a.City.Stats.Tick);
        Assert.Equal(b.City.Heights, a.City.Heights);
        Assert.Equal(b.City.Zones, a.City.Zones);
        Assert.Equal(b.City.Paths, a.City.Paths);
        Assert.Equal(b.City.BuildingAt, a.City.BuildingAt);
        Assert.Equal(b.City.Buildings.Keys.Order(), a.City.Buildings.Keys.Order());
        foreach (var (id, bs) in b.City.Buildings) Assert.Equal(bs, a.City.Buildings[id]);
    }

    [Fact]
    public void TheClientNamesRoadsAsTheServerDoes()
    {
        var content = ContentDb.Load(FindContent());
        var namer = new StreetNamer(content.Voice.StreetNames, content.Voice.StreetKinds);
        for (int line = 0; line < 300; line++)
        {
            Assert.Equal(content.Voice.RoadName(false, line), namer.RoadName(false, line));
            Assert.Equal(content.Voice.RoadName(true, line), namer.RoadName(true, line));
        }
    }

    static string FindContent()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content"))) return Path.Combine(d.FullName, "content");
        throw new DirectoryNotFoundException("content");
    }
}

public sealed class ServerFixture : IDisposable
{
    public readonly ServerProcess Server = new();
    public void Dispose() => Server.Dispose();
}
