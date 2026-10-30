using Google.Protobuf;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;
using TW = ToyCity.Transport.Wire;

namespace ToyCity.ClientModel.Tests;

/// <summary>Part 12: every action that waits on the server says so from the moment it's asked for.</summary>
public class WaitingTests
{
    static (PanelModel Panel, List<ClientMessage> Sent) Panel()
    {
        var sent = new List<ClientMessage>();
        var panel = new PanelModel(new CityMirror(), m => { sent.Add(m); return (uint)sent.Count; }) { Me = () => "zac" };
        return (panel, sent);
    }

    [Fact]
    public void NewSaveAndLoadShowWorkingUntilTheServerAnswers()
    {
        var (panel, sent) = Panel();
        panel.SetInput("harbour");
        panel.Click("save-ok");
        Assert.Equal("Saving \"harbour\"…", panel.Working);
        // A full state (someone else's load) doesn't end a save; the notice does.
        panel.Handle(new ServerMessage { Notice = new Notice { Text = "Rain tomorrow." } });
        Assert.NotNull(panel.Working);
        panel.Handle(new ServerMessage { Notice = new Notice { Text = "Saved as \"harbour\"." } });
        Assert.Null(panel.Working);

        panel.Click("load:harbour");
        Assert.Equal("Loading \"harbour\"…", panel.Working);
        panel.Handle(new ServerMessage { Error = new Error { Message = "Only this server's admins can load." } });
        Assert.Null(panel.Working);

        panel.Click("load:harbour");
        panel.Handle(new ServerMessage { Full = new FullState() });
        Assert.Null(panel.Working);

        // A server that never answers doesn't leave it up for good.
        panel.Click("load:harbour");
        panel.Tick(61);
        Assert.Null(panel.Working);
    }

    [Fact]
    public void TakingTheCompanyShowsAtOnceAndAsksOnlyOnce()
    {
        var (panel, sent) = Panel();
        var co = panel.Company;
        SideGameView View(string op, string owner) => new()
        {
            Game = CompanyModel.Game, Seat = "operator",
            Payload = new TW.View { Name = "Bus Co", Operator = op, Owner = owner }.ToByteString(),
        };
        panel.Handle(new ServerMessage { SideGame = View("autopilot", "") });
        panel.Click("co-claim");
        Assert.Equal("Taking over the company…", co.Pending);
        int asked = sent.Count;
        panel.Click("co-claim");
        Assert.Equal(asked, sent.Count);
        Assert.Contains("Taking over the company", co.Render());
        panel.Handle(new ServerMessage { SideGame = View("manual", "zac") });
        Assert.Null(co.Pending);
        Assert.True(co.Mine);
    }
}
