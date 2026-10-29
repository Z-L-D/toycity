using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Protocol;
using ToyCity.Transport;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>Side games on another process: the same city whether its games run here or there, and deadlines that miss.</summary>
public class RemoteSideGameTests
{
    /// <summary>A side-game host on a free local port, serving until disposed.</summary>
    sealed class Host : IDisposable
    {
        readonly CancellationTokenSource _stop = new();
        public readonly int Port;

        public Host(Func<string, ISideGame?> make)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            _ = new SideGameServer(make).ServeAsync(listener, _stop.Token);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>Until disposed, cities made here get the game with this id from the host.</summary>
        public IDisposable Serve(string id, TimeSpan deadline) =>
            SideGameCatalog.Scope(x => x == id ? new RemoteSideGame("127.0.0.1", Port, x, deadline) : null);

        public void Dispose() => _stop.Cancel();
    }

    static SideGameAction Cmd(TW.Command c) => new(TransportGame.Id, "operator", c.ToByteArray());

    /// <summary>A player leases every lot, draws a line through the stops and gives it buses; then two months pass.</summary>
    static World PlayedTown()
    {
        var w = TransportTests.Town(301);
        w.Enqueue(new SetSeatAction(TransportGame.Id, "player", OperatorLevel.Manual));
        foreach (var b in w.Buildings.Where(b => b != null && w.LotKindOf(b) != "").ToList()) w.Enqueue(new LeaseLotAction(TransportGame.Id, b!.Id));
        w.Run(2);
        var save = new TW.SaveLine { Name = "Crosstown", FareCents = 150 };
        save.StopLeaseIds.AddRange(w.Leases.Where(l => l.Kind == "bus-stop").Select(l => l.Id));
        w.Enqueue(Cmd(new TW.Command { SaveLine = save }));
        w.Step();
        int line = TransportTests.View(w).Lines.Single().Id;
        w.Enqueue(Cmd(new TW.Command { Buses = new TW.SetBuses { LineId = line, Buses = 3 } }));
        w.Run(60);
        return w;
    }

    [Fact]
    public void TheBusCompanyOnAnotherProcessEndsInTheSameState()
    {
        using var local = PlayedTown();
        Assert.True(TransportTests.View(local).Lines.Single().Riders > 0, "nobody rode");

        using var host = new Host(id => id == TransportGame.Id ? new TransportGame() : null);
        using (host.Serve(TransportGame.Id, TimeSpan.FromSeconds(10)))
        {
            using var remote = PlayedTown();
            Assert.IsType<RemoteSideGame>(remote.SideGame(TransportGame.Id)!.Game);
            Assert.Equal(local.StateHash(), remote.StateHash());
            Assert.Equal(TransportTests.View(local).ToByteArray(), TransportTests.View(remote).ToByteArray());
            Assert.DoesNotContain(remote.Log, l => l.Action is SideGameMissedAction);

            // Saved and loaded through the host, it carries on in step with the local city.
            using var loaded = Mapping.Load(Fixture.Content, Mapping.Save(remote));
            Assert.Equal(remote.StateHash(), loaded.StateHash());
            loaded.Run(30);
            local.Run(30);
            Assert.Equal(local.StateHash(), loaded.StateHash());

            // A forecast opens its own copy on the host and leaves the city as it was.
            ulong before = loaded.StateHash();
            loaded.Forecast(1);
            Assert.Equal(before, loaded.StateHash());
        }
    }

    /// <summary>The probe, but slow on one day and on one command: too slow for the city's deadline.</summary>
    sealed class SlowProbe : ISideGame
    {
        public static long SlowTick = -1;
        readonly ProbeGame _p = new();
        public Manifest Manifest => _p.Manifest;
        public void NewGame(ulong seed) => _p.NewGame(seed);
        public void Tick(CityToGame input, ICityQueries city, GameToCity output)
        {
            _p.Tick(input, city, output);  // the work happens, and must be undone
            if (input.Tick == SlowTick) Thread.Sleep(1500);
        }
        public CommandResult Command(string seat, byte[] payload, ICityQueries city)
        {
            if (Encoding.UTF8.GetString(payload) == "slow") { _p.Command(seat, Encoding.UTF8.GetBytes("bump"), city); Thread.Sleep(1500); }
            return _p.Command(seat, payload, city);
        }
        public byte[] View(string seat) => _p.View(seat);
        public void Save(BinaryWriter w) => _p.Save(w);
        public void Load(BinaryReader r) => _p.Load(r);
    }

    [Fact]
    public void AMissedDayOrCommandIsUndoneOnTheHostAndLoggedSoAReplayAgrees()
    {
        ProbeGame.Register();
        using var host = new Host(id => id == ProbeGame.Id ? new SlowProbe() : null);
        using var scope = host.Serve(ProbeGame.Id, TimeSpan.FromMilliseconds(300));
        using var w = Fixture.Starter(302);
        w.Enqueue(new AttachSideGameAction(ProbeGame.Id));
        w.Run(10);
        SlowProbe.SlowTick = w.Tick;
        w.Notices.Clear();
        w.Step();  // the probe's day runs late
        SlowProbe.SlowTick = -1;
        Assert.Contains(w.Log, l => l.Action is SideGameMissedAction { What: "day" });
        Assert.Contains(w.Notices, n => n.Text.Contains("didn't finish its day in time"));
        Assert.Contains("missed its day", w.SideGame(ProbeGame.Id)!.Last.Status);
        w.Step();
        Assert.Equal("11 days", w.SideGame(ProbeGame.Id)!.Last.Status);  // 12 days run, one of them missed

        w.Notices.Clear();
        w.Enqueue(new SideGameAction(ProbeGame.Id, "operator", Encoding.UTF8.GetBytes("slow")));
        w.Enqueue(new SideGameAction(ProbeGame.Id, "operator", Encoding.UTF8.GetBytes("bump")));
        w.Step();
        Assert.Contains(w.Notices, n => n.Text.Contains("didn't answer in time"));
        Assert.Contains(w.Log, l => l.Action is SideGameMissedAction { What: "command" });
        Assert.DoesNotContain(w.Log, l => l.Action is SideGameAction s && Encoding.UTF8.GetString(s.Payload) == "slow");
        Assert.Equal("12/1", Encoding.UTF8.GetString(w.SideGame(ProbeGame.Id)!.Game.View("operator")));
        w.Run(5);

        // The same log replayed with the probe in this process skips the same day and the same command.
        long until = w.Tick;
        ulong live = w.StateHash();
        scope.Dispose();
        using var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, until);
        Assert.IsType<ProbeGame>(replay.SideGame(ProbeGame.Id)!.Game);
        Assert.Equal(live, replay.StateHash());
    }
}
