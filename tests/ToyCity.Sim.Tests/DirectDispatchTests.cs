using Google.Protobuf;
using ToyCity.Contract;
using ToyCity.Emergency;
using EW = ToyCity.Emergency.Wire;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 4, part 1: direct dispatch. Units on the city's streets, ordered about on the map.</summary>
public class DirectDispatchTests
{
    static EW.View View(World w) => EmergencyTests.View(w);

    static string Do(World w, EW.Command c)
    {
        int before = w.Notices.Count;
        w.Enqueue(new SideGameAction(EmergencyGame.Id, "operator", c.ToByteArray()) { By = "player" });
        w.ApplyPending();
        return string.Join(" ", w.Notices.Skip(before).Select(n => n.Text));
    }

    static string Order(World w, string kind, int[] ids, int x = 0, int y = 0, int call = 0, int follow = 0, bool queue = false) =>
        Do(w, new EW.Command { Order = new EW.UnitOrder { Kind = kind, UnitIds = { ids }, X = x, Y = y, CallId = call, FollowId = follow, Queue = queue } });

    static EW.UnitView Unit(World w, int id) => View(w).Units.Single(u => u.Id == id);

    static int Dist(World w, int a, int b) => Math.Abs(a % w.Width - b % w.Width) + Math.Abs(a / w.Width - b / w.Width);

    /// <summary>Road cells of the town, farthest from a cell first.</summary>
    static List<int> RoadsFarFrom(World w, int from) =>
        Enumerable.Range(0, w.N).Where(i => w.ProfileAt(i) is { Frontage: true, Cars: true }).OrderByDescending(i => Dist(w, i, from)).ThenBy(i => i).ToList();

    /// <summary>Units of a kind free at their stations, stepping until there are enough (the town's own calls keep them busy at times).</summary>
    static int[] Ready(World w, EW.UnitKind kind, int count)
    {
        for (int k = 0; k < 30; k++)
        {
            var ready = View(w).Units.Where(u => u.Kind == kind && u.State == "ready" && u.Cell == u.Home).Select(u => u.Id).ToArray();
            if (ready.Length >= count) return ready.Take(count).ToArray();
            w.Step();
        }
        throw new Exception($"no {count} {kind} ready");
    }

    /// <summary>Steps until a unit has finished driving (or a while passes).</summary>
    static void Arrive(World w, int id)
    {
        for (int k = 0; k < 20 && Unit(w, id).Leg.Count > 0; k++) w.Step();
        w.Step();
    }

    [Fact]
    public void UnitsStartAtTheirStationsDriveToACallAndComeHome()
    {
        using var w = EmergencyTests.Town(801);
        var v = View(w);
        Assert.All(v.Units, u => { Assert.True(u.Home >= 0); Assert.Equal(u.Home, u.Cell); Assert.Equal("", u.Order); });

        var home = w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => b!.Id).Skip(3).First()!;
        w.RaiseIncident("crime", home.X, home.Y, "city", "a mugging", 1, "");
        EW.CallView? call = null;
        for (int k = 0; k < 4 && call == null; k++) { w.Step(); call = View(w).Calls.FirstOrDefault(c => c.Status is "rolling" or "done"); }
        Assert.NotNull(call);
        var patrol = Unit(w, call!.UnitIds[0]);
        // It went by road to the street by the call.
        Assert.True(Dist(w, patrol.Cell, w.Idx(home.X, home.Y)) <= 6 || patrol.Leg.Count > 0 || patrol.Cell == patrol.Home, $"{patrol.Cell} for {home.X},{home.Y}");
        for (int k = 0; k < 10 && !(Unit(w, patrol.Id).State == "ready" && Unit(w, patrol.Id).Leg.Count == 0); k++) w.Step();
        w.Run(3);
        var back = Unit(w, patrol.Id);
        Assert.Equal("ready", back.State);
        Assert.Equal(back.Home, back.Cell);
    }

    [Fact]
    public void APostedUnitWaitsWhereItWasSentAndAnswersCallsFromThere()
    {
        using var w = EmergencyTests.Town(802, player: "player");
        var posted = Unit(w, Ready(w, EW.UnitKind.Patrol, 1)[0]);
        int target = RoadsFarFrom(w, posted.Home).First();
        Assert.Equal("", Order(w, "go", new[] { posted.Id }, target % w.Width, target / w.Width));
        var going = Unit(w, posted.Id);
        Assert.Equal("posted", going.Order);
        Assert.True(going.Leg.Count > 1, "it drives a route");
        Assert.True(going.LegMinutes > 0);
        Assert.Equal(target, going.Cell);
        // It leaves at the next day's minute, so the clients see it drive even when the leg is shorter than a day's step.
        Assert.Equal(-1, going.LegStart);
        w.Step();
        var driving = Unit(w, posted.Id);
        Assert.True(driving.Leg.Count > 1 && driving.LegStart == w.ClockMinute && driving.LegMinutes > 0, $"{driving.Leg.Count} cells from {driving.LegStart} at {w.ClockMinute}");
        Arrive(w, posted.Id);
        var there = Unit(w, posted.Id);
        Assert.Equal(target, there.Cell);
        Assert.Empty(there.Leg);
        Assert.Equal("posted", there.Order);
        Assert.Equal("ready", there.State);

        // A crime by where it waits: the computer sends the posted unit, not the one at the station.
        w.RaiseIncident("crime", target % w.Width, target / w.Width, "city", "a mugging", 1, "");
        EW.CallView? call = null;
        for (int k = 0; k < 4 && call == null; k++) { w.Step(); call = View(w).Calls.FirstOrDefault(c => c.Status != "waiting" && c.Kind == "crime" && c.X == target % w.Width && c.Y == target / w.Width); }
        Assert.NotNull(call);
        Assert.Contains(posted.Id, call!.UnitIds);
        Assert.True(call.Minutes <= 3, $"{call.Minutes} minutes from where it stood");
    }

    [Fact]
    public void HeldUnitsAreLeftAloneByTheComputerButCanBeSentByHand()
    {
        using var w = EmergencyTests.Town(803, player: "player");
        var patrols = Ready(w, EW.UnitKind.Patrol, 2);
        Assert.Equal("", Order(w, "hold", patrols));
        Assert.All(patrols, id => Assert.Equal("held", Unit(w, id).Order));
        var home = w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => b!.Id).First()!;
        w.RaiseIncident("crime", home.X, home.Y, "city", "a mugging", 1, "");
        w.Run(2);
        var call = View(w).Calls.Single(c => c.X == home.X && c.Y == home.Y && c.Kind == "crime");
        Assert.Equal("waiting", call.Status);
        // Sent by hand to the call, from its hold.
        Assert.Equal("", Order(w, "scene", new[] { patrols[1] }, call: call.Id));
        Assert.Contains(patrols[1], View(w).Calls.Single(c => c.Id == call.Id).UnitIds);
        Assert.Equal("", Unit(w, patrols[1]).Order);
    }

    [Fact]
    public void QueuedPlacesFollowersAndGoingHome()
    {
        using var w = EmergencyTests.Town(804, player: "player");
        var engines = Ready(w, EW.UnitKind.Engine, 2);
        var roads = RoadsFarFrom(w, Unit(w, engines[0]).Home);
        int a = roads[0], b = roads[roads.Count / 3];
        Assert.Equal("", Order(w, "go", new[] { engines[0] }, a % w.Width, a / w.Width));
        Assert.Equal("", Order(w, "go", new[] { engines[0] }, b % w.Width, b / w.Width, queue: true));
        Assert.Equal(new[] { b }, Unit(w, engines[0]).Waypoints.ToArray());
        Assert.Equal("", Order(w, "follow", new[] { engines[1] }, follow: engines[0]));
        Assert.Equal("following", Unit(w, engines[1]).Order);
        for (int k = 0; k < 30 && (Unit(w, engines[0]).Leg.Count > 0 || Unit(w, engines[0]).Waypoints.Count > 0); k++) w.Step();
        w.Run(3);
        Assert.Equal(b, Unit(w, engines[0]).Cell);
        Assert.True(Dist(w, Unit(w, engines[1]).Cell, b) <= 2, "the follower kept up");

        Assert.Equal("", Order(w, "return", engines));
        Arrive(w, engines[0]);
        Arrive(w, engines[1]);
        Assert.All(engines, id => { var u = Unit(w, id); Assert.Equal(u.Home, u.Cell); Assert.Equal("", u.Order); });
    }

    [Fact]
    public void BusyUnitsAndNonsenseAreRefused()
    {
        using var w = EmergencyTests.Town(805, player: "player");
        var amb = Unit(w, Ready(w, EW.UnitKind.Ambulance, 1)[0]);
        Assert.Contains("don't know", Order(w, "dance", new[] { amb.Id }));
        Assert.Contains("off the map", Order(w, "go", new[] { amb.Id }, -5, 3));
        Assert.Contains("Pick at least one", Order(w, "go", new[] { 999 }, 3, 3));
        var home = w.Buildings.Where(b => b != null && b.Residents > 0).OrderBy(b => b!.Id).First()!;
        w.RaiseIncident("injury", home.X, home.Y, "city", "a fall", 1, "");
        for (int k = 0; k < 4 && Unit(w, amb.Id).State == "ready"; k++) w.Step();
        Assert.NotEqual("ready", Unit(w, amb.Id).State);
        Assert.Contains("busy", Order(w, "go", new[] { amb.Id }, home.X, home.Y));
    }

    [Fact]
    public void WhereUnitsAreIsSaved()
    {
        using var w = EmergencyTests.Town(806, player: "player");
        var units = Ready(w, EW.UnitKind.Engine, 2);
        int target = RoadsFarFrom(w, Unit(w, units[0]).Home).First();
        Order(w, "go", new[] { units[0] }, target % w.Width, target / w.Width);
        Order(w, "hold", new[] { units[1] });
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(View(w).ToByteArray(), View(back).ToByteArray());
        Assert.Equal(w.StateHash(), back.StateHash());
        w.Run(5);
        back.Run(5);
        Assert.Equal(w.StateHash(), back.StateHash());
        Assert.Equal(target, Unit(back, units[0]).Cell);
    }
}
