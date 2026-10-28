using ToyCity.ClientModel.Gate;
using ToyCity.ClientModel.Panels;
using ToyCity.ClientModel.Tools;

namespace ToyCity.ClientModel.Tests;

/// <summary>The Beta 1 gate's loop against the real server, with the planner standing in for the Godot tools.</summary>
public class GateRunTests : IClassFixture<ServerFixture>
{
    readonly ServerProcess _server;
    public GateRunTests(ServerFixture f) => _server = f.Server;

    [Fact]
    public void TheAlpha3LoopPlaysThroughOnTheBenchmarkCity()
    {
        using var client = new CityClient(_server.Ws, "test-gate");
        var panel = new PanelModel(client.City, client.Connection.Send);
        client.Message += panel.Handle;
        var run = new GateRun(client, panel, (tool, at) =>
        {
            var p = Planner.Plan(client.City, tool, at);
            if (p is { Ok: true }) foreach (var a in p.Actions) client.Connection.Act(a);
            return p;
        });
        try
        {
            foreach (var wait in run.Found().Concat(run.Play()))
            {
                var deadline = DateTime.UtcNow.AddSeconds(wait.Seconds);
                while (!wait.Done())
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException($"waiting for {wait.What}");
                    client.Poll();
                    panel.Tick(0.01);
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            foreach (var (check, ok, seen) in run.Checks) Console.WriteLine($"{(ok ? "ok" : "FAILED")}  {check}: {seen}");
        }
        Assert.Equal("done", run.Phase);
        Assert.All(run.Checks, c => Assert.True(c.Ok, c.Check));
    }
}
