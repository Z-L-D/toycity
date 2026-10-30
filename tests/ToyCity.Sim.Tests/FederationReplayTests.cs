using ToyCity.Emergency;
using ToyCity.Protocol;
using ToyCity.Transport;

namespace ToyCity.Sim.Tests;

/// <summary>
/// What federated hosting relies on (part 8): a city loaded from its save and fed the actions another copy applied
/// each day, through the wire's records, stays identical to that copy.
/// </summary>
public class FederationReplayTests
{
    [Fact]
    public void ACheckCopyFedTheDaysLogStaysIdentical()
    {
        using var r = Region.Create(Fixture.Content, "Fed", 61, 3, 1, 128);
        r.Found(1, 0, "Toyville", "May", starter: true);
        r.Found(0, 0, "Westbrook", "May", starter: true);
        r.Found(2, 0, "Eastfield", "", starter: true, computer: AutopilotStyle.Profit);
        SideGameCatalog.Register(TransportGame.Id, () => new TransportGame());
        SideGameCatalog.Register(EmergencyGame.Id, () => new EmergencyGame());
        foreach (var c in r.Cities)
        {
            c.Enqueue(new AttachSideGameAction(TransportGame.Id));
            c.Enqueue(new AttachSideGameAction(EmergencyGame.Id));
            c.ApplyPending();
        }
        r.Run(3);
        var t = r.TileAt(2, 0)!;
        var host = Mapping.Load(Fixture.Content, Mapping.Save(t.City!));
        var copy = t.City!;
        r.IsRemote = w => w == copy;
        for (int d = 0; d < 120; d++)
        {
            long tick = r.Tick;
            r.BeginDay();
            var inbound = copy.TakePending();
            foreach (var a in inbound) host.Enqueue(Mapping.Action(Mapping.Record(new LoggedAction(tick, a)))!);
            Region.StepCities(r.Cities.Where(c => c != copy).Append(host).ToList());
            int from = host.Log.Count;
            while (from > 0 && host.Log[from - 1].Tick >= tick) from--;
            foreach (var l in host.Log.Skip(from)) copy.Enqueue(Mapping.Action(Mapping.Record(l))!);
            // What a city's host does with it after each day: its commuters and its side games' views.
            var agents = new AgentView(host, AgentTiers.Default);
            agents.Select();
            agents.Daily();
            foreach (var g in host.SideGames) { g.Game.View("operator"); g.Game.View("public"); }
            copy.Step();
            r.EndDay();
            Assert.True(host.StateHash() == copy.StateHash(), $"day {tick}");
        }
    }
}
