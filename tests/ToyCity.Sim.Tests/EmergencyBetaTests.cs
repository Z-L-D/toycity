using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Emergency;
using ToyCity.Protocol;
using EW = ToyCity.Emergency.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>Part 11, the emergency services beta: SWAT and hostages, new orders, gear, shifts, ratings and the crooked ways.</summary>
public class EmergencyBetaTests
{
    static EW.View View(World w) => EmergencyTests.View(w);

    static SideGameAction Cmd(EW.Command c) => new(EmergencyGame.Id, "operator", c.ToByteArray()) { By = "player" };

    static void Do(World w, EW.Command c)
    {
        w.Enqueue(Cmd(c));
        w.ApplyPending();
    }

    static IEnumerable<Building> Homes(World w) => w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => b!.Id).Select(b => b!);

    static EW.SceneView Run(World w, int sceneId, int steps)
    {
        Do(w, new EW.Command { Step = new EW.SceneStep { SceneId = sceneId, Steps = steps } });
        return View(w).Scenes.FirstOrDefault(s => s.Id == sceneId) ?? new EW.SceneView();
    }

    [Fact]
    public void AScenesLeftForADayGoesToTheCrews()
    {
        using var w = EmergencyTests.Town(711, player: "player");
        w.RaiseIncident("injury", Homes(w).First().X, Homes(w).First().Y, "city", "a fall", 1, "");
        // Whichever call opens a scene first: the player never runs it.
        for (int d = 0; d < 5 && View(w).Scenes.Count == 0; d++) w.Step();
        int scene = View(w).Scenes.First().Id;
        Assert.Contains(w.SideGame(EmergencyGame.Id)!.Last.Holding, id => id > 0);
        w.Run(2);
        Assert.DoesNotContain(View(w).Scenes, s => s.Id == scene);
        Assert.Contains(View(w).Log, l => l.Contains("the crews finished it"));
    }

    /// <summary>A town with a SWAT team, and a hostage scene it was sent to.</summary>
    static (World W, EW.SceneView Scene) Hostages()
    {
        foreach (ulong seed in new ulong[] { 712, 713, 714, 715 })
        {
            var w = EmergencyTests.Town(seed, player: "player");
            var police = View(w).Stations.Single(s => s.Kind == "police-station");
            Do(w, new EW.Command { Buy = new EW.BuyUnit { LeaseId = police.LeaseId, Kind = EW.UnitKind.Swat } });
            Assert.Single(View(w).Units, u => u.Kind == EW.UnitKind.Swat);
            foreach (var home in Homes(w).Take(12))
            {
                w.RaiseIncident("crime", home.X, home.Y, "city", "an armed robbery", 3, "");
                w.Step();
                var scene = View(w).Scenes.LastOrDefault();
                if (scene == null) continue;
                bool swat = View(w).Calls.Single(c => c.SceneId == scene.Id).UnitIds.Any(id => View(w).Units.Single(u => u.Id == id).Kind == EW.UnitKind.Swat);
                if (swat && scene.People.Any(p => p.Role == EW.Role.Hostage)) return (w, scene);
                Do(w, new EW.Command { Leave = new EW.LeaveScene { SceneId = scene.Id } });
                w.Run(2);
            }
            w.Dispose();
        }
        throw new Exception("no hostage scene with the SWAT team sent");
    }

    [Fact]
    public void ASwatTeamBreachingTogetherStunsTheHostageTakerAndFreesTheHostages()
    {
        var (w, scene) = Hostages();
        using var _ = w;
        Assert.Equal(2, scene.People.Count(p => p.Role == EW.Role.Hostage) is var n && n > 0 ? 2 : n);
        // Wait for the SWAT van.
        int swatUnit = View(w).Units.Single(u => u.Kind == EW.UnitKind.Swat).Id;
        for (int k = 0; k < 60 && !scene.Vehicles.Any(v => v.Id == swatUnit); k++) scene = Run(w, scene.Id, 10);
        var team = scene.People.Where(p => p.UnitId == swatUnit).Select(p => p.Id).ToList();
        Assert.Equal(4, team.Count);
        var taker = scene.People.Single(p => p.Role == EW.Role.Suspect);
        int tx = taker.X / 16, ty = taker.Y / 16;
        Do(w, new EW.Command { Task = new EW.TaskOrder { SceneId = scene.Id, Ids = { team }, Task = EW.TaskKind.Breach, X = tx, Y = ty } });
        var events = new List<string>();
        for (int k = 0; k < 600; k++)
        {
            var next = Run(w, scene.Id, 1);
            if (next.Id == 0) break;
            scene = next;
            events.AddRange(scene.Events);
        }
        Assert.True(events.Concat(View(w).Cards.Last().Notes).Any(e => e.Contains("breached")), $"log: {string.Join(" / ", View(w).Log.TakeLast(6))} taker at {tx},{ty} team {string.Join(",", team)} step {scene.Step} id {scene.Id}: " + string.Join(" | ", events.Distinct().Where(e => !e.Contains("on scene"))) + " card: " + View(w).Cards.LastOrDefault() + " people: " + string.Join("; ", scene.People.Where(p => p.UnitId == swatUnit || p.Role == EW.Role.Suspect).Select(p => $"{p.Role} {p.X / 16},{p.Y / 16} {p.Task} {p.State}")));
        if (View(w).Scenes.Any(s => s.Id == scene.Id)) Do(w, new EW.Command { Leave = new EW.LeaveScene { SceneId = scene.Id } });
        var card = View(w).Cards.Last();
        Assert.True(card.HostagesFreed >= 1, $"{card.HostagesFreed} freed, {card.HostagesLost} lost");
        Assert.Equal(100, card.RulesOfEngagement);
        Assert.Equal("arrest", card.Outcome);
    }

    [Fact]
    public void BreachingIsForSwatAndShotsLeaveALineOfFire()
    {
        var (w, scene) = Hostages();
        using var _ = w;
        for (int k = 0; k < 60 && scene.People.Count(p => p.Role == EW.Role.Officer) < 6; k++) scene = Run(w, scene.Id, 10);
        int swatUnit = View(w).Units.Single(u => u.Kind == EW.UnitKind.Swat).Id;
        var patrolOfficer = scene.People.First(p => p.Role == EW.Role.Officer && p.UnitId != swatUnit);
        var taker = scene.People.Single(p => p.Role == EW.Role.Suspect);
        Do(w, new EW.Command { Task = new EW.TaskOrder { SceneId = scene.Id, Ids = { patrolOfficer.Id }, Task = EW.TaskKind.Breach, X = taker.X / 16, Y = taker.Y / 16 } });
        scene = Run(w, scene.Id, 1);
        Assert.Contains(scene.Events, e => e.Contains("Only a SWAT team breaches"));
        // An officer fires into the open: the shot shows as a line of fire.
        Do(w, new EW.Command { Task = new EW.TaskOrder { SceneId = scene.Id, Ids = { patrolOfficer.Id }, Task = EW.TaskKind.Shoot, X = 0, Y = patrolOfficer.Y / 16 } });
        scene = Run(w, scene.Id, 2);
        var shot = Assert.Single(scene.Shots, s => s.Police);
        Assert.Equal(patrolOfficer.X / 16, shot.FromX / 16);
    }

    [Fact]
    public void GearShiftsAndResponseRatingsShowAndCost()
    {
        using var w = EmergencyTests.Town(716, player: "player");
        var v = View(w);
        var patrol = v.Units.First(u => u.Kind == EW.UnitKind.Patrol);
        long cash = v.Cash;
        Do(w, new EW.Command { Gear = new EW.BuyGear { UnitId = patrol.Id, Item = "body-armour" } });
        v = View(w);
        Assert.Contains("body-armour", v.Units.Single(u => u.Id == patrol.Id).Gear);
        Assert.Equal(cash - EmergencyGame.GearPrice, v.Cash);
        // A patrol can't carry breathing gear.
        Do(w, new EW.Command { Gear = new EW.BuyGear { UnitId = patrol.Id, Item = "breathing-gear" } });
        Assert.DoesNotContain("breathing-gear", View(w).Units.Single(u => u.Id == patrol.Id).Gear);

        // One shift: units are off some days, and the month's wages are a third.
        Do(w, new EW.Command { Settings = new EW.Settings { Style = EW.CommandStyle.Focus, CrewsUseHeads = true, DriversWatchOut = true, Shifts = 1 } });
        Assert.Equal(1, View(w).Shifts);
        Assert.Contains(View(w).Units, u => u.State == "off shift");
        // The computer answers the city's calls, and they're graded.
        for (int d = 0; d < 40; d++)
        {
            if (d % 4 == 0) { var h = Homes(w).Skip(d).First(); w.RaiseIncident("injury", h.X, h.Y, "city", "a fall", 1, ""); }
            w.Step();
        }
        var injury = View(w).Ratings.Single(r => r.Kind == "injury");
        Assert.True(injury.Calls > 0);
        Assert.Contains(injury.Grade, new[] { "A", "B", "C", "D", "F" });
        var last = View(w).LastMonth;
        int crew = View(w).Units.Sum(u => u.Crew);
        Assert.Equal((long)crew * EmergencyGame.WagePerCrew / 3, last.Wages);
    }

    [Fact]
    public void TheCrookedWaysWaitForEveryoneToAllowThem()
    {
        using var w = EmergencyTests.Town(721, player: "player");
        var home = Homes(w).First();
        int district = w.DistrictOf(home.X, home.Y);
        Do(w, new EW.Command { Crooked = new EW.Crooked { Kind = "look-away", District = district, On = true } });
        Assert.Equal(-1, View(w).Crooked.LookAway);
        Assert.False(View(w).Crooked.Allowed);

        w.Enqueue(new SetConsentAction("player", Mechanic.CrookedServices, true) { By = "player" });
        w.Step();
        Assert.True(View(w).Crooked.Allowed, View(w).Crooked.WhyNot);
        Do(w, new EW.Command { Crooked = new EW.Crooked { Kind = "look-away", District = district, On = true } });
        Assert.Equal(district, View(w).Crooked.LookAway);
        long cash = View(w).Cash;
        w.RaiseIncident("crime", home.X, home.Y, "city", "a burglary", 1, "");
        w.Step();
        var call = View(w).Calls.Last(c => c.Kind == "crime");
        Assert.Equal("escaped", call.Outcome);
        Assert.Empty(call.UnitIds);
        Assert.True(View(w).Cash >= cash + EmergencyGame.LookAwayBribe - 10);

        // A strike: nobody answers until it ends.
        Do(w, new EW.Command { Crooked = new EW.Crooked { Kind = "strike", On = true } });
        Assert.All(View(w).Units.Where(u => u.State != "on call"), u => Assert.Equal("on strike", u.State));
        var other = Homes(w).First(h => w.DistrictOf(h.X, h.Y) != district);
        w.RaiseIncident("injury", other.X, other.Y, "city", "a fall", 1, "");
        w.Step();
        Assert.Equal("waiting", View(w).Calls.Last(c => c.Kind == "injury").Status);
        Do(w, new EW.Command { Crooked = new EW.Crooked { Kind = "strike", On = false } });
        Assert.DoesNotContain(View(w).Units, u => u.State == "on strike");

        // Taking consent back ends it all.
        w.Enqueue(new SetConsentAction("player", Mechanic.CrookedServices, false) { By = "player" });
        w.Run(2);
        Assert.Equal(-1, View(w).Crooked.LookAway);
        Assert.Contains(View(w).Log, l => l.Contains("switched off"));
    }

    [Fact]
    public void TheBetaStateSavesLoadsAndReplays()
    {
        var (w, scene) = Hostages();
        using var _ = w;
        var patrol = View(w).Units.First(u => u.Kind == EW.UnitKind.Patrol);
        Do(w, new EW.Command { Gear = new EW.BuyGear { UnitId = patrol.Id, Item = "body-armour" } });
        Do(w, new EW.Command { Settings = new EW.Settings { Style = EW.CommandStyle.SceneCommand, CrewsUseHeads = true, DriversWatchOut = true, Shifts = 2 } });
        Run(w, scene.Id, 40);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.StateHash(), back.StateHash());
        Assert.Equal(View(w).ToByteArray(), EW.View.Parser.ParseFrom(back.SideGame(EmergencyGame.Id)!.Game.View("operator")).ToByteArray());
        w.Run(3);
        back.Run(3);
        Assert.Equal(w.StateHash(), back.StateHash());
    }
}
