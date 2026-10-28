using System.Text;
using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Protocol;

namespace ToyCity.Sim.Tests;

/// <summary>
/// The smallest side game that uses every part of the contract: it counts its days, remembers the incidents it hears
/// about, raises one of its own each month, and takes a "bump" command.
/// </summary>
public sealed class ProbeGame : ISideGame
{
    public const string Id = "probe";
    public Manifest Manifest { get; } = new(Id, "Probe Co.", ContractInfo.Version, new[] { "bus-stop" }, "A test side game.");
    public int Days, Bumps, Months;
    public ulong Seed;
    public readonly List<string> Heard = new();
    public OperatorLevel LastOperator;
    /// <summary>Lease behaviour for the lease tests: pay the bills, take fares and hire at each lot, ask for or give up lots.</summary>
    public bool PaysBills = true;
    public int FaresPerLot, JobsPerLot;
    public readonly List<int> Requests = new(), Releases = new();
    public readonly List<Bill> LastBills = new();
    public readonly List<LeasedLot> Lots = new();

    public static void Register() => SideGameCatalog.Register(Id, () => new ProbeGame());

    public void NewGame(ulong seed) => Seed = seed;

    public void Tick(CityToGame input, ICityQueries city, GameToCity output)
    {
        Days++;
        LastOperator = input.Operator;
        if (input.NewMonth) Months++;
        foreach (var i in input.Incidents) Heard.Add($"{i.Source}:{i.Kind}");
        if (input.Day == 5)
            output.Incidents.Add(new Incident(0, "breakdown", 10, 10, Id, "a probe", 2, $"A probe broke down on day {Days}."));
        output.Status = $"{Days} days";
        Lots.Clear();
        Lots.AddRange(input.Lots);
        foreach (var lot in input.Lots)
        {
            if (FaresPerLot > 0) output.RevenueByLease[lot.LeaseId] = FaresPerLot;
            if (JobsPerLot > 0) output.JobsByLease[lot.LeaseId] = JobsPerLot;
        }
        if (input.Bills.Count > 0) { LastBills.Clear(); LastBills.AddRange(input.Bills); }
        if (PaysBills)
            foreach (var b in input.Bills.Where(b => b.Amount > 0)) output.Paid[b.LeaseId] = output.Paid.GetValueOrDefault(b.LeaseId) + b.Amount;
        output.LeaseRequests.AddRange(Requests);
        output.LeaseReleases.AddRange(Releases);
        Requests.Clear();
        Releases.Clear();
    }

    public CommandResult Command(string seat, byte[] payload, ICityQueries city)
    {
        if (Encoding.UTF8.GetString(payload) != "bump") return new CommandResult(false, "The probe only knows how to bump.");
        Bumps++;
        return new CommandResult(true, "");
    }

    public byte[] View(string seat) => Encoding.UTF8.GetBytes($"{Days}/{Bumps}");

    public void Save(BinaryWriter w)
    {
        w.Write(Days); w.Write(Bumps); w.Write(Months); w.Write(Seed);
        w.Write(Heard.Count);
        foreach (var h in Heard) w.Write(h);
    }

    public void Load(BinaryReader r)
    {
        Days = r.ReadInt32(); Bumps = r.ReadInt32(); Months = r.ReadInt32(); Seed = r.ReadUInt64();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) Heard.Add(r.ReadString());
    }
}

public class SideGameTests
{
    static World WithProbe(ulong seed = 51)
    {
        ProbeGame.Register();
        var w = Fixture.Starter(seed);
        w.Enqueue(new AttachSideGameAction(ProbeGame.Id));
        w.Step();
        return w;
    }

    static ProbeGame Probe(World w) => (ProbeGame)w.SideGame(ProbeGame.Id)!.Game;

    [Fact]
    public void ASideGameTicksWithTheCityAndTakesCommands()
    {
        var w = WithProbe();
        w.Run(39);
        Assert.Equal(40, Probe(w).Days);
        Assert.Equal(2, Probe(w).Months);
        w.Enqueue(new SideGameAction(ProbeGame.Id, "operator", Encoding.UTF8.GetBytes("bump")));
        w.Enqueue(new SideGameAction(ProbeGame.Id, "operator", Encoding.UTF8.GetBytes("jump")));
        w.Step();
        Assert.Equal(1, Probe(w).Bumps);
        Assert.Contains(w.Notices, n => n.Text.Contains("only knows how to bump"));
        Assert.Equal("41 days", w.SideGame(ProbeGame.Id)!.Last.Status);
    }

    [Fact]
    public void IncidentsGoBothWays()
    {
        var w = WithProbe();
        // The city's fire reaches the side game; the side game's breakdown reaches the city's news.
        w.Run(60);
        var b = w.Buildings.First(x => x != null && x.Residents > 0)!;
        w.Enqueue(new TriggerDisasterAction(DisasterKind.Fire, b.X, b.Y));
        w.Run(10);
        Assert.Contains("city:fire", Probe(w).Heard);
        Assert.Contains(w.RecentIncidents, i => i.Source == ProbeGame.Id && i.Kind == "breakdown");
        Assert.Contains(w.Headlines, h => h.Text.Contains("probe broke down"));
        // A game never hears its own incidents back.
        Assert.DoesNotContain(Probe(w).Heard, h => h.StartsWith(ProbeGame.Id));
    }

    [Fact]
    public void SideGamesSaveReplayAndForecastWithTheCity()
    {
        var w = WithProbe(52);
        w.Run(60);
        w.Enqueue(new SideGameAction(ProbeGame.Id, "operator", Encoding.UTF8.GetBytes("bump")));
        w.Run(30);

        var loaded = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Equal(Probe(w).Days, Probe(loaded).Days);
        w.Run(45);
        loaded.Run(45);
        Assert.Equal(w.StateHash(), loaded.StateHash());

        var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, w.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());

        // A forecast runs a copy: the side game in the live city doesn't move.
        int before = Probe(w).Days;
        w.Forecast(3);
        Assert.Equal(before, Probe(w).Days);
    }

    [Fact]
    public void ASaveFileKeepsTheSideGameAndItsCommands()
    {
        var w = WithProbe(53);
        w.Enqueue(new SideGameAction(ProbeGame.Id, "operator", Encoding.UTF8.GetBytes("bump")));
        w.Run(20);
        var loaded = Mapping.Load(Fixture.Content, Mapping.Save(w));
        Assert.Equal(w.StateHash(), loaded.StateHash());
        Assert.Contains(loaded.Log, l => l.Action is AttachSideGameAction { GameId: ProbeGame.Id });
        var replay = World.Replay(Fixture.Content, loaded.Seed, loaded.Width, loaded.Name, loaded.Log, loaded.Tick);
        Assert.Equal(w.StateHash(), replay.StateHash());
        Assert.Equal(1, Probe(replay).Bumps);
    }

    [Fact]
    public void TheContractRoundTripsThroughTheWire()
    {
        var c = new CityToGame { Tick = 99, Day = 10, Month = 4, Year = 2003, NewMonth = true, Operator = OperatorLevel.Autopilot, OwnerName = "Zac" };
        var terms = new LeaseTerms(LeaseMode.PublicService, 0, 0, 500, 100, 3);
        c.Lots.Add(new LeasedLot(1, 7, "bus-stop", 3, 4, 1, 1, 77, 2, terms, terms with { Mode = LeaseMode.Business }, 2, false));
        c.Districts.Add(new District(2, "Tinker Heights", 0, 0, 31, 31, 500, 200, 60, 30, 40, -120));
        c.Demand.Add(new TripDemand(2, 3, 150));
        c.Ridership.Add(new LineRidership(1, 80, new[] { 50, 30 }));
        c.Incidents.Add(new Incident(5, "fire", 1, 2, "city", "Shack", 2, "Fire!"));
        c.Bills.Add(new Bill(1, "Subsidy", -500));
        var back = ContractWire.FromWire(ToyCity.Protocol.Contract.CityToGame.Parser.ParseFrom(ContractWire.ToWire(c).ToByteArray()));
        Assert.Equal(c.Lots, back.Lots, new LotComparer());
        Assert.Equal(c.Districts, back.Districts);
        Assert.Equal(c.Demand, back.Demand);
        Assert.Equal(c.Incidents, back.Incidents);
        Assert.Equal(c.Bills, back.Bills);
        Assert.Equal(c.Ridership.Single().BoardingsByStop, back.Ridership.Single().BoardingsByStop);
        Assert.Equal((c.Tick, c.Operator, c.OwnerName), (back.Tick, back.Operator, back.OwnerName));

        var g = new GameToCity { ServiceQuality = 77, Status = "ok" };
        g.Lines.Add(new TransitLine(1, "Line 1", "#f80", new[] { 1, 2, 3 }, 12, 150, 400));
        g.JobsByLease[1] = 12;
        g.Paid[1] = 300;
        g.RevenueByLease[1] = 1234;
        g.LeaseRequests.Add(7);
        g.LeaseReleases.Add(2);
        var gb = ContractWire.FromWire(ToyCity.Protocol.Contract.GameToCity.Parser.ParseFrom(ContractWire.ToWire(g).ToByteArray()));
        Assert.Equal(g.Lines.Single() with { StopLeaseIds = gb.Lines.Single().StopLeaseIds }, gb.Lines.Single());
        Assert.Equal(new[] { 1, 2, 3 }, gb.Lines.Single().StopLeaseIds);
        Assert.Equal(g.JobsByLease, gb.JobsByLease);
        Assert.Equal(g.Paid, gb.Paid);
        Assert.Equal(g.RevenueByLease, gb.RevenueByLease);
        Assert.Equal(new[] { 7 }, gb.LeaseRequests);
        Assert.Equal(new[] { 2 }, gb.LeaseReleases);
        Assert.Equal((77, "ok"), (gb.ServiceQuality, gb.Status));
    }

    sealed class LotComparer : IEqualityComparer<LeasedLot>
    {
        public bool Equals(LeasedLot? a, LeasedLot? b) => a == b;
        public int GetHashCode(LeasedLot l) => l.LeaseId;
    }
}
