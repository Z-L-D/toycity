using ToyCity.ClientModel.Gate;
using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;

namespace ToyCity.ClientModel.Tests;

/// <summary>The Beta 2 gate's live session on a dedicated server, with the planner standing in for the mayor's Godot tools.</summary>
public class TogetherRunTests
{
    [Fact]
    public void FourPlayersPlayALiveSessionAndOneDelinks()
    {
        using var server = new ServerProcess("""{ "name": "Together town", "join": "open" }""");
        using var client = new CityClient(server.Ws, "test-together");
        var panel = new PanelModel(client.City, client.Connection.Send) { Me = () => client.Connection.Player, OnServer = () => true };
        client.Message += panel.Handle;
        using var run = new TogetherRun(server.Ws, "Mayor May", client, panel, (tool, at) =>
        {
            var p = Planner.Plan(client.City, tool, at);
            if (p is { Ok: true }) foreach (var a in p.Actions) client.Connection.Act(a);
            return p;
        });
        // The mayor signs in first, as the Godot client does from its command line.
        bool asked = false;
        try
        {
            foreach (var wait in run.Play())
            {
                var deadline = DateTime.UtcNow.AddSeconds(wait.Seconds);
                while (!wait.Done())
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException($"waiting for {wait.What}");
                    client.Poll();
                    if (!asked && client.Connection.SignInNeeded) { client.Connection.SignIn("Mayor May", TogetherRun.Password, create: true); asked = true; }
                    panel.Tick(0.01);
                    run.Poll(0.01);
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            foreach (var (check, ok, seen) in run.Checks) Console.WriteLine($"{(ok ? "ok" : "FAILED")}  {check}: {seen}");
        }
        Assert.Equal("done", run.Phase);

        // Tess's copy is a city she is mayor of, with her company and its line.
        Sim.SideGameCatalog.Register(Transport.TransportGame.Id, () => new Transport.TransportGame());
        Sim.SideGameCatalog.Register(Emergency.EmergencyGame.Id, () => new Emergency.EmergencyGame());
        using var copy = Protocol.Mapping.Load(Sim.ContentDb.LoadDefault(), run.Tess.Copy!.Value.Save);
        Assert.Equal("Tess", copy.MayorSeat.Owner);
        Assert.Equal("Tess", copy.SeatOf("transport")!.Owner);
        Assert.Equal("", copy.SeatOf("emergency")!.Owner);
    }
}
