using System.Text.RegularExpressions;
using Google.Protobuf;
using ToyCity.ClientModel.Panels;
using ToyCity.Emergency.Wire;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>The dispatch board: against the real server, and a scene driven from the pointer.</summary>
public class EmergencyClientTests : IClassFixture<ServerFixture>
{
    readonly ServerProcess _server;
    public EmergencyClientTests(ServerFixture f) => _server = f.Server;

    static void PollUntil(CityClient c, Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            c.Poll();
            Thread.Sleep(10);
        }
    }

    static List<string> Links(string bb) => Regex.Matches(bb, @"\[url=([^\]]*)\]").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void ThePlayerCanRunTheServiceAndChangeHowMuchTheyCommand()
    {
        using var client = new CityClient(_server.Ws, "test-emergency");
        var panel = new PanelModel(client.City, client.Connection.Send);
        client.Message += panel.Handle;
        var em = panel.Emergency;
        PollUntil(client, () => client.City.Loaded && em.View != null);
        panel.OpenDialog("dispatch");
        foreach (var (tab, _) in EmergencyModel.Tabs)
        {
            panel.Click($"em-tab:{tab}");
            Assert.False(string.IsNullOrWhiteSpace(panel.Render()), tab);
        }
        if (!em.Mine)
        {
            Assert.Contains("em-claim", Links(panel.Render()));
            panel.Click("em-claim");
        }
        PollUntil(client, () => em.Mine);
        panel.Click("em-tab:settings");
        Assert.Contains("em-style:0", Links(panel.Render()));
        panel.Click("em-style:0");
        PollUntil(client, () => em.View!.Style == CommandStyle.FullCommand);
        panel.Click("em-switch:watch");
        PollUntil(client, () => !em.View!.DriversWatchOut);
        panel.Click("em-switch:watch");
        PollUntil(client, () => em.View!.DriversWatchOut);
    }

    /// <summary>A scene with one firefighter and an engine, as the server would send it.</summary>
    static SideGameView SceneView()
    {
        var scene = new ToyCity.Emergency.Wire.SceneView { Id = 4, CallId = 9, Kind = "fire", Width = 36, Height = 36, Tiles = ByteString.CopyFrom(new byte[36 * 36]), Fire = ByteString.CopyFrom(new byte[36 * 36]) };
        scene.People.Add(new PersonView { Id = 1_000_001, Role = Role.Firefighter, X = 10 * 16 + 8, Y = 10 * 16 + 8, Health = 100, UnitId = 3, State = "ok" });
        scene.People.Add(new PersonView { Id = 1_000_002, Role = Role.Bystander, X = 20 * 16 + 8, Y = 20 * 16 + 8, Health = 100, State = "ok" });
        scene.Vehicles.Add(new VehicleView { Id = 3, Kind = UnitKind.Engine, X = 2 * 16 + 8, Y = 2 * 16 + 8 });
        var v = new View { Name = "Rescue", Operator = "manual", Owner = "player", Style = CommandStyle.SceneCommand };
        v.Units.Add(new UnitView { Id = 3, Name = "Engine 1", Kind = UnitKind.Engine, State = "at scene", CallId = 9 });
        v.Calls.Add(new CallView { Id = 9, Kind = "fire", Text = "Fire at a house", Status = "scene", SceneId = 4 });
        v.Scenes.Add(scene);
        return new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = v.ToByteString() };
    }

    [Fact]
    public void TheSceneOpensByItselfAndThePointerGivesOrders()
    {
        var sent = new List<Command>();
        var em = new EmergencyModel(m => { sent.Add(Command.Parser.ParseFrom(m.Action.SideGame.Payload)); return 0; }, () => { });
        bool opened = false;
        em.SceneOpened += () => opened = true;
        em.Handle(SceneView());
        Assert.True(opened);
        Assert.Equal(4, em.SceneId);
        Assert.Contains("em-leave", Links(em.Render()));

        // Select the firefighter, send them somewhere.
        em.PointerDown(10, 10, add: false);
        Assert.Equal(new[] { 1_000_001 }, em.Selected);
        em.MoveTo(15, 12);
        var move = sent.Last().Move;
        Assert.Equal((4, 15, 12), (move.SceneId, move.X, move.Y));
        Assert.Equal(new[] { 1_000_001 }, move.Ids);

        // Hose a tile.
        em.Click("em-order:Hose");
        em.PointerDown(12, 14, add: false);
        Assert.Equal(TaskKind.Hose, sent.Last().Task.Task);

        // Draw a path for the engine: a fast drag fills in neighbouring tiles.
        em.Click("em-order:Path");
        em.PointerDown(2, 2, add: false);
        Assert.Equal(new[] { 3 }, em.Selected);
        em.PointerDown(2, 2, add: false);
        Assert.NotNull(em.Drawing);
        em.PointerMove(5, 2);
        em.PointerMove(5, 4);
        em.PointerUp();
        var path = sent.Last().Path;
        Assert.Equal(3, path.Id);
        Assert.Equal(new[] { 2 * 36 + 2, 2 * 36 + 3, 2 * 36 + 4, 2 * 36 + 5, 3 * 36 + 5, 4 * 36 + 5 }, path.Tiles);

        // The scene runs on twice a second while it's open, and holds when asked.
        int before = sent.Count;
        em.Tick(0.6);
        Assert.Equal(4, sent.Last().Step.SceneId);
        em.Click("em-run");
        em.Tick(0.6);
        Assert.Equal(before + 1, sent.Count);
    }

    [Fact]
    public void TheComputersPickShowsTickedEvenIfTheBoardWasOpenWhileTheCallWaited()
    {
        var sent = new List<Command>();
        var em = new EmergencyModel(m => { sent.Add(Command.Parser.ParseFrom(m.Action.SideGame.Payload)); return 0; }, () => { });
        SideGameView Board(string status, params int[] units)
        {
            var v = new View { Name = "Rescue", Operator = "manual", Owner = "player", Style = CommandStyle.SceneCommand, PickResponders = true };
            v.Units.Add(new UnitView { Id = 3, Name = "Patrol 1", Kind = UnitKind.Patrol, State = units.Length > 0 ? "proposed" : "ready", CallId = units.Length > 0 ? 9 : 0 });
            var call = new CallView { Id = 9, Kind = "crime", Text = "A mugging", Status = status };
            call.UnitIds.AddRange(units);
            v.Calls.Add(call);
            return new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = v.ToByteString() };
        }
        em.Handle(Board("waiting"));
        Assert.Contains("☐", em.Render());
        em.Handle(Board("proposed", 3));
        Assert.Contains("☑", em.Render());
        em.Click("em-send:9");
        Assert.Equal(new[] { 3 }, sent.Single().Dispatch.UnitIds);
    }

    [Fact]
    public void TheBoardLeasesFreeStationsAndHandsTheServiceBack()
    {
        var sent = new List<ClientMessage>();
        var em = new EmergencyModel(m => { sent.Add(m); return 0; }, () => { });
        var v = new View { Name = "Rescue", Operator = "manual", Owner = "player", Style = CommandStyle.Focus };
        v.OpenStations.Add(new OpenStation { BuildingId = 41, Kind = "hospital", X = 5, Y = 6 });
        em.Handle(new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = v.ToByteString() });
        em.Click("em-tab:units");
        var links = Links(em.Render());
        Assert.Contains("em-lease:41", links);
        Assert.Contains("em-release", links);
        em.Click("em-lease:41");
        Assert.Equal((EmergencyModel.Game, 41), (sent.Last().Action.Lease.Game, sent.Last().Action.Lease.BuildingId));
        // The board shows the lease is under way until the station leaves the free list, and takes no second click.
        Assert.Contains("⏳ Leasing the station", em.Render());
        Assert.DoesNotContain("em-release", Links(em.Render()));
        em.Click("em-lease:41");
        Assert.Single(sent);
        v.OpenStations.Clear();
        em.Handle(new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = v.ToByteString() });
        Assert.Null(em.Pending);
        em.Click("em-release");
        Assert.Equal(("", SeatLevel.SeatCaretaker), (sent.Last().Action.Seat.Owner, sent.Last().Action.Seat.Level));
        Assert.Contains("⏳ Handing the service back", em.Render());
    }

    [Fact]
    public void AnEndedSceneStaysUpUnderItsReportUntilThePlayerGoesBackToTheCalls()
    {
        var em = new EmergencyModel(_ => 0, () => { });
        int jumps = 0;
        (int, int, int, int)? flashed = null;
        em.JumpTo += (_, _) => jumps++;
        em.Flash += (a, b, c, d) => flashed = (a, b, c, d);
        em.Handle(SceneView());
        Assert.Equal(4, em.SceneId);

        // Where is this: the city map centres on the scene's patch, and the scene steps aside until asked back.
        em.Click("em-locate");
        Assert.Equal(1, jumps);
        Assert.Equal((0, 0, 8, 8), flashed);
        Assert.True(em.SceneHidden);
        Assert.Contains(em.ToolSections().SelectMany(x => x.Items), i => i.Meta == "em-scene");
        em.Click("em-scene");
        Assert.False(em.SceneHidden);

        // The scene ends: it stays as it last was, under its report card.
        var v = View.Parser.ParseFrom(SceneView().Payload);
        v.Scenes.Clear();
        v.Calls[0].Status = "done";
        v.Cards.Add(new ReportCard { SceneId = 4, Where = "Fire at a house", Score = 82, Outcome = "put out", ResponseMinutes = 6, Saved = 1 });
        em.Handle(new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = v.ToByteString() });
        Assert.True(em.SceneOver);
        Assert.True(em.ReportOpen);
        Assert.NotNull(em.Scene);
        Assert.Contains("82", em.RenderReport());
        Assert.Contains("Lives saved", em.RenderReport());
        // No orders on a scene that's over.
        em.PointerDown(10, 10, add: false);
        Assert.Empty(em.Selected);
        // Close the report to see the scene as it finished; then back to the calls.
        em.Click("em-report");
        Assert.False(em.ReportOpen);
        Assert.True(em.SceneOver);
        em.Click("em-board");
        Assert.Null(em.Scene);
        Assert.Equal(0, em.SceneId);
    }

    [Fact]
    public void TheServiceHasItsOwnScreenInPlaceOfTheCompanys()
    {
        var panel = new PanelModel(new CityMirror(), _ => 0);
        panel.Click("co-open");
        Assert.True(panel.Company.Active);
        panel.OpenDialog("dispatch");
        Assert.True(panel.Emergency.Active);
        Assert.False(panel.Company.Active);
        Assert.Null(panel.Dialog);
        panel.Click("co-open");
        Assert.False(panel.Emergency.Active);
        panel.Click("co-city");
        panel.Click("em-open");
        panel.Click("em-city");
        Assert.False(panel.Emergency.Active);
    }

    [Fact]
    public void TakingTheServiceShowsAtOnceUntilItHappensOrIsRefused()
    {
        var em = new EmergencyModel(_ => 0, () => { });
        var v = new View { Name = "Rescue", Operator = "caretaker" };
        em.Handle(new SideGameView { Game = EmergencyModel.Game, Seat = "public", Payload = v.ToByteString() });
        em.Click("em-claim");
        Assert.Contains("⏳ Taking over the service", em.Render());
        Assert.DoesNotContain("em-claim", Links(em.Render()));
        // A refusal ends the wait at once.
        em.Refused();
        Assert.Null(em.Pending);
        Assert.Contains("em-claim", Links(em.Render()));
        // The seat arriving ends it too.
        em.Click("em-claim");
        v.Operator = "manual"; v.Owner = "player";
        em.Handle(new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = v.ToByteString() });
        Assert.Null(em.Pending);
    }

    [Fact]
    public void ABoxSelectsCrewsTheNewOrdersGoOutAndTheBoardOffersGearShiftsAndCrookedWays()
    {
        var sent = new List<Command>();
        var em = new EmergencyModel(m => { sent.Add(Command.Parser.ParseFrom(m.Action.SideGame.Payload)); return 0; }, () => { });
        var view = View.Parser.ParseFrom(SceneView().Payload);
        view.Stations.Add(new StationView { LeaseId = 7, Kind = "police-station", X = 40, Y = 70, Name = "Police station at 40,70" });
        view.Units.Add(new UnitView { Id = 5, Name = "SWAT 1", Kind = UnitKind.Swat, StationLeaseId = 7, State = "at scene", CallId = 9, Crew = 4 });
        view.Units[0].StationLeaseId = 7;
        view.Scenes[0].People.Add(new PersonView { Id = 1_000_003, Role = Role.Officer, X = 12 * 16, Y = 12 * 16, Health = 100, UnitId = 5, State = "ok" });
        view.Shifts = 3;
        view.Ratings.Add(new ResponseRating { Kind = "fire", Calls = 4, Minutes = 7, Grade = "B" });
        view.Crooked = new CrookedView { Allowed = true, SlowWalk = -1, Racket = 2, LookAway = -1 };
        em.Handle(new SideGameView { Game = EmergencyModel.Game, Seat = "operator", Payload = view.ToByteString() });

        // A box over the firefighter, the officer and the bystander picks the two crew.
        em.SelectBox(new[] { 1_000_001, 1_000_002, 1_000_003 }, false);
        Assert.Equal(new[] { 1_000_001, 1_000_003 }, em.Selected);
        Assert.Contains(EmergencyModel.Order.Breach, em.Orders());
        em.SelectBox(new[] { 1_000_001 }, false);
        Assert.DoesNotContain(EmergencyModel.Order.Breach, em.Orders());
        Assert.DoesNotContain(EmergencyModel.Order.Cordon, em.Orders());

        em.SelectBox(new[] { 1_000_003 }, false);
        em.Click("em-order:Breach");
        em.PointerDown(20, 20, null, false);
        Assert.Equal(TaskKind.Breach, sent[^1].Task.Task);
        Assert.Equal(new[] { 1_000_003 }, sent[^1].Task.Ids);

        em.Click("em-board");
        em.Click("em-tab:units");
        var units = em.Render();
        Assert.Contains("em-gear:3:breathing-gear", Links(units));
        Assert.DoesNotContain("em-buy:7:3", Links(units));  // the station has its SWAT team
        em.Click("em-gear:3:breathing-gear");
        Assert.Equal("breathing-gear", sent[^1].Gear.Item);

        em.Click("em-tab:settings");
        var settings = em.Render();
        Assert.Contains("em-shifts:2", Links(settings));
        Assert.Contains("em-crooked:racket:0:off", Links(settings));
        Assert.Contains("em-crooked:slow-walk:", string.Join(" ", Links(settings)));
        em.Click("em-shifts:2");
        Assert.Equal(2, sent[^1].Settings.Shifts);
        em.Click("em-crooked:strike:0:on");
        Assert.True(sent[^1].Crooked.On);
        Assert.Equal("strike", sent[^1].Crooked.Kind);
        em.Click("em-tab:reports");
        Assert.Contains("Response, last clock day", em.Render());
    }
}
