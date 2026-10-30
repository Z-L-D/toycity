using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Emergency;
using ToyCity.Protocol;
using EW = ToyCity.Emergency.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>The emergency services: the city's calls, the computer answering them, and scenes a player runs.</summary>
public class EmergencyTests
{
    /// <summary>A starter town with the emergency services attached and a fire station, police station and clinic leased to them.</summary>
    internal static World Town(ulong seed, string player = "")
    {
        SideGameCatalog.Register(EmergencyGame.Id, () => new EmergencyGame());
        var w = Fixture.Starter(seed);
        w.Run(60);
        w.Enqueue(new AttachSideGameAction(EmergencyGame.Id));
        w.Step();
        var stations = new List<int>();
        foreach (var kind in new[] { "fire-station", "police-station", "clinic" })
            stations.AddRange(TransportTests.PlaceAlongRoads(w, kind, 1, spacing: 0));
        if (player.Length > 0) w.Enqueue(new SetSeatAction(EmergencyGame.Id, player, OperatorLevel.Manual));
        foreach (int b in stations) w.Enqueue(new LeaseLotAction(EmergencyGame.Id, b));
        w.Step();
        return w;
    }

    internal static EW.View View(World w) => EW.View.Parser.ParseFrom(w.SideGame(EmergencyGame.Id)!.Game.View("operator"));

    static SideGameAction Cmd(EW.Command c) => new(EmergencyGame.Id, "operator", c.ToByteArray()) { By = "player" };

    static Building Home(World w) => w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => b!.Id).Skip(3).First()!;

    static EW.Settings Settings(EW.CommandStyle style, bool heads = true, bool watch = true, bool pick = false) =>
        new() { Style = style, CrewsUseHeads = heads, DriversWatchOut = watch, PickResponders = pick };

    [Fact]
    public void NewStationsComeWithUnitsAndTheComputerAnswersTheCitysCalls()
    {
        using var w = Town(701);
        var v = View(w);
        Assert.Equal(3, v.Stations.Count);
        Assert.Equal(2, v.Units.Count(u => u.Kind == EW.UnitKind.Engine));
        Assert.Equal(2, v.Units.Count(u => u.Kind == EW.UnitKind.Patrol));
        Assert.Equal(1, v.Units.Count(u => u.Kind == EW.UnitKind.Ambulance));
        // The crews work at the stations.
        Assert.All(w.Leases.Where(l => l.GameId == EmergencyGame.Id), l => Assert.True(l.Jobs > 0));

        var home = Home(w);
        w.RaiseIncident("crime", home.X, home.Y, "city", "a mugging", 1, "");
        w.Step();
        var call = View(w).Calls.Single(c => c.Kind == "crime" && c.X == home.X && c.Y == home.Y);
        // The city may have calls of its own taking the patrols: this one goes when a car is free.
        bool rolled = false;
        for (int d = 0; d < 10 && call.Status != "done"; d++)
        {
            if (call.Status == "rolling")
            {
                rolled = true;
                Assert.Contains(call.UnitIds, id => View(w).Units.Single(u => u.Id == id).Kind == EW.UnitKind.Patrol);
                Assert.True(call.Minutes > 2);
            }
            w.Step();
            call = View(w).Calls.Single(c => c.Id == call.Id);
        }
        Assert.True(rolled);
        Assert.Equal("done", call.Status);
        Assert.Contains(call.Outcome, new[] { "arrest", "escaped" });
        // The city has its answer and forgets the incident.
        Assert.Empty(w.SideGame(EmergencyGame.Id)!.Last.Holding);
        w.Run(40);
        Assert.True(View(w).Calls.Count(c => c.Status == "done") >= 1);
    }

    [Fact]
    public void AnEscapeHeatsTheDistrictUpAndAnArrestCoolsItDown()
    {
        using var w = Town(702);
        var home = Home(w);
        int d = w.DistrictOf(home.X, home.Y);
        int before = w.CrimeHeat(d);
        // Answers straight from a game: one escape, then one arrest.
        w.RaiseIncident("crime", home.X, home.Y, "city", "a mugging", 1, "");
        long id = w.RecentIncidents.Last().Id;
        w.TakeResponsesForTest(new GameToCity { Responses = { new IncidentResponse(id, "escaped", "The suspect got away.") } });
        Assert.Equal(before + 15, w.CrimeHeat(d));
        Assert.Contains(w.Notices, n => n.Text == "The suspect got away." && n.Severity == "warning");
        w.RaiseIncident("crime", home.X, home.Y, "city", "a mugging", 1, "");
        id = w.RecentIncidents.Last().Id;
        w.TakeResponsesForTest(new GameToCity { Responses = { new IncidentResponse(id, "arrest", ""), new IncidentResponse(id, "arrest", "") } });
        Assert.Equal(before + 5, w.CrimeHeat(d));  // applied once, however often it's sent
    }

    [Fact]
    public void InFullCommandAnUnansweredCallGetsWorseAndIsLost()
    {
        using var w = Town(703, player: "player");
        w.Enqueue(Cmd(new EW.Command { Settings = Settings(EW.CommandStyle.FullCommand) }));
        var home = Home(w);
        w.RaiseIncident("injury", home.X, home.Y, "city", "a fall", 1, "");
        w.Step();
        var call = View(w).Calls.Single();
        Assert.Equal("waiting", call.Status);
        int respect = View(w).Respect;
        w.Run(2);
        Assert.Equal(2, View(w).Calls.Single().Severity);
        w.Run(2);
        call = View(w).Calls.Single();
        Assert.Equal("done", call.Status);
        Assert.Equal("died", call.Outcome);
        Assert.True(View(w).Respect < respect);
        Assert.Contains(w.Notices.Concat(w.Headlines.Select(h => new SimNotice(h.Text, h.Severity, h.X, h.Y))), n => n.Text.Contains("nobody came"));
    }

    [Fact]
    public void APlayersSceneHoldsTheFireUntilItEnds()
    {
        using var w = Town(704, player: "player");
        var home = Home(w);
        w.Ignite(home);
        w.Step();
        var v = View(w);
        var call = v.Calls.Single(c => c.Kind == "fire");
        Assert.Equal("scene", call.Status);
        var scene = v.Scenes.Single();
        Assert.Contains(w.SideGame(EmergencyGame.Id)!.Last.Holding, id => id > 0);
        Assert.True(scene.Burning > 0);
        // Days pass with the scene open: the city neither spreads nor puts out a fire a player is fighting.
        w.Run(2);
        Assert.True(home.BurningDays > 0);
        // The player hands it to the crews: an engine was sent, so it goes out (or spreads), and the city hears.
        w.Enqueue(Cmd(new EW.Command { Leave = new EW.LeaveScene { SceneId = scene.Id } }));
        w.Step();
        Assert.Empty(View(w).Scenes);
        Assert.Single(View(w).Cards);
        Assert.Empty(w.SideGame(EmergencyGame.Id)!.Last.Holding);
        if (View(w).Cards[0].Outcome == "out") Assert.Equal(0, home.BurningDays);
    }

    [Fact]
    public void ADriverToldNotToWatchOutHitsSomeoneAndTheUnitIsSuspended()
    {
        using var w = Town(705, player: "player");
        w.Enqueue(Cmd(new EW.Command { Settings = Settings(EW.CommandStyle.SceneCommand, heads: false, watch: false) }));
        var home = Home(w);
        w.RaiseIncident("injury", home.X, home.Y, "city", "a fall", 1, "");
        w.Step();
        var scene = View(w).Scenes.Single();
        while (View(w).Scenes.Single().Vehicles.Count == 0)
        {
            w.Enqueue(Cmd(new EW.Command { Step = new EW.SceneStep { SceneId = scene.Id, Steps = 10 } }));
            w.ApplyPending();
        }
        scene = View(w).Scenes.Single();
        var amb = scene.Vehicles.Single();
        var hurt = scene.People.Single(p => p.Role == EW.Role.Casualty);
        w.Enqueue(Cmd(new EW.Command { Move = new EW.MoveOrder { SceneId = scene.Id, Ids = { amb.Id }, X = hurt.X / 16, Y = hurt.Y / 16 } }));
        for (int i = 0; i < 20 && View(w).Scenes.Count > 0 && !View(w).Scenes[0].Events.Any(e => e.Contains("hit")); i++)
        {
            w.Enqueue(Cmd(new EW.Command { Step = new EW.SceneStep { SceneId = scene.Id, Steps = 10 } }));
            w.ApplyPending();
        }
        w.Enqueue(Cmd(new EW.Command { Leave = new EW.LeaveScene { SceneId = scene.Id } }));
        w.Step();
        var v = View(w);
        var card = v.Cards.Single();
        Assert.True(card.HarmedByCrews >= 1, string.Join("; ", card.Notes));
        Assert.Contains(card.Notes, n => n.Contains("drove into"));
        var unit = v.Units.Single(u => u.Id == amb.Id);
        Assert.Equal("suspended", unit.State);
        Assert.True(unit.BackInDays > 20);
        Assert.Contains(w.Notices.Concat(w.Headlines.Select(h => new SimNotice(h.Text, h.Severity, h.X, h.Y))), n => n.Text.Contains("drove into someone"));
    }

    [Fact]
    public void AShotHitsTheFirstPersonOnTheLineAndForceIsJudged()
    {
        var road = Enumerable.Repeat(CellKind.Road, Scene.CellsAcross * Scene.CellsAcross).ToArray();
        var s = Scene.Create(1, 1, 99, "crime", 1, 50, 50, "Report of a mugging", 5, road, 0, 0, 12345);
        s.Arriving.Add((1, EW.UnitKind.Patrol, 2, 1));
        var settings = new Settings { CrewsUseHeads = false };
        s.Run(1, settings, _ => "Patrol 1");
        var officers = s.People.Where(p => p.Role == EW.Role.Officer).ToList();
        var suspect = s.People.Single(p => p.Role == EW.Role.Suspect);
        foreach (var b in s.People.Where(p => p.Role == EW.Role.Bystander)) { b.X = 0; b.Y = Scene.Size - 1; }
        // One officer in front of the other, on the line to the suspect.
        suspect.X = 20; suspect.Y = 10;
        officers[0].X = 10; officers[0].Y = 10;
        officers[1].X = 14; officers[1].Y = 10;
        s.Order(officers[0].Id, EW.TaskKind.Shoot, suspect.X, suspect.Y, suspect.Id);
        s.Run(1, settings, _ => "Patrol 1");
        Assert.Equal(1, s.FriendlyFire);
        Assert.Equal(40, officers[1].Health);
        Assert.Equal(100, suspect.Health);

        // With a clear line, shooting an unarmed suspect who was never warned is unauthorised force.
        officers[1].X = 10; officers[1].Y = 20;
        officers[0].LastShot = -1000;
        s.Order(officers[0].Id, EW.TaskKind.Shoot, suspect.X, suspect.Y, suspect.Id);
        s.Run(1, settings, _ => "Patrol 1");
        Assert.Equal(40, suspect.Health);
        Assert.Equal(1, s.Unauthorized);
        var card = s.Card();
        Assert.Equal(1, card.FriendlyFire);
        Assert.Equal(1, card.UnauthorizedForce);
        Assert.True(card.Score < 40);
    }

    [Fact]
    public void AScenePlayedThroughCommandsSavesLoadsAndReplaysExactly()
    {
        using var w = Town(706, player: "player");
        // Only logged actions here, so a replay sees the same city: wait for a call of the city's own.
        for (int d = 0; d < 400 && View(w).Scenes.Count == 0; d++) w.Step();
        int scene = View(w).Scenes.First().Id;
        for (int i = 0; i < 30; i++)
        {
            w.Enqueue(Cmd(new EW.Command { Step = new EW.SceneStep { SceneId = scene, Steps = 4 } }));
            w.ApplyPending();
        }
        ulong mid = w.StateHash();
        using (var loaded = Mapping.Load(Fixture.Content, Mapping.Save(w)))
            Assert.Equal(mid, loaded.StateHash());
        w.Run(5);
        long until = w.Tick;
        ulong live = w.StateHash();
        using var replay = World.Replay(Fixture.Content, w.Seed, w.Width, w.Name, w.Log, until);
        Assert.Equal(live, replay.StateHash());
        Assert.Equal(View(w).ToByteArray(), View(replay).ToByteArray());
    }

    [Fact]
    public void TheEmergencyServicesOnAnotherProcessEndInTheSameState()
    {
        static World Played()
        {
            var w = Town(707, player: "player");
            var home = Home(w);
            w.Ignite(home);
            w.Step();
            int scene = View(w).Scenes.Single().Id;
            for (int i = 0; i < 10; i++) w.Enqueue(Cmd(new EW.Command { Step = new EW.SceneStep { SceneId = scene, Steps = 8 } }));
            w.Run(20);
            return w;
        }
        using var local = Played();
        using var host = new RemoteSideGameTests.Host(id => id == EmergencyGame.Id ? new EmergencyGame() : null);
        using (host.Serve(EmergencyGame.Id, TimeSpan.FromSeconds(10)))
        {
            using var remote = Played();
            Assert.IsType<ToyCity.Protocol.RemoteSideGame>(remote.SideGame(EmergencyGame.Id)!.Game);
            Assert.DoesNotContain(remote.Log, l => l.Action is SideGameMissedAction);
            Assert.Equal(local.StateHash(), remote.StateHash());
            Assert.Equal(View(local).ToByteArray(), View(remote).ToByteArray());
        }
    }
}
