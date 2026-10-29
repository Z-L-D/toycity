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
}
