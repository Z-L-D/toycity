using System.Text.RegularExpressions;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol;
using ToyCity.Protocol.V0;
using ToyCity.Sim;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// The side panel against a real city: each tab and dialog renders well-formed text, and every link it shows does
/// what it says, answered by the sim as the server would answer it.
/// </summary>
public class PanelTests
{
    /// <summary>A starter town a few years on, a mirror of it and a panel wired to answer from it in-process.</summary>
    sealed class Rig
    {
        public readonly World W;
        public readonly CityMirror City = new();
        public readonly PanelModel Panel;
        public readonly List<ClientMessage> Sent = new();
        uint _seq;

        public Rig(ulong seed = 601)
        {
            W = World.Create(ContentDb.LoadDefault(), seed, 128, "Paneltown");
            Assert.True(StarterTown.Apply(W));
            W.Run(30 * 30);
            City.ApplyFull(Mapping.FullState(W, 0));
            Panel = new PanelModel(City, Send);
            Panel.Handle(new ServerMessage { Civic = Mapping.Civic(W) });
            Panel.Handle(new ServerMessage { Alerts = Mapping.Alerts(W, W.Alerts()) });
            Pump();
        }

        uint Send(ClientMessage m)
        {
            m.Seq = ++_seq;
            Sent.Add(m);
            var reply = m.BodyCase switch
            {
                ClientMessage.BodyOneofCase.Why => new ServerMessage { Why = Mapping.Why(W, W.Why(m.Why.X, m.Why.Y)) },
                ClientMessage.BodyOneofCase.Inspect => new ServerMessage { Inspect = Mapping.Inspect(W, m.Inspect.X, m.Inspect.Y) },
                ClientMessage.BodyOneofCase.Demand => new ServerMessage { Demand = Mapping.Demand(W.DemandReport()) },
                ClientMessage.BodyOneofCase.Advisors => new ServerMessage { Advisors = Mapping.Advisors(W) },
                ClientMessage.BodyOneofCase.Ordinances => new ServerMessage { Ordinances = Mapping.Ordinances(W.OrdinanceViews()) },
                ClientMessage.BodyOneofCase.Encyclopedia => new ServerMessage { Encyclopedia = Mapping.Encyclopedia(W.Encyclopedia()) },
                ClientMessage.BodyOneofCase.Overlay => new ServerMessage { Overlay = Mapping.Overlay(W.Overlay((ToyCity.Sim.OverlayKind)(int)m.Overlay.Kind)) },
                ClientMessage.BodyOneofCase.Forecast => new ServerMessage
                {
                    Forecast = Mapping.Forecast(World.FromSnapshot(ContentDb.LoadDefault(), W.Snapshot())
                        .RunForecast(m.Forecast.Months, m.Forecast.Plan.Select(Mapping.ToSim).Where(a => a != null).Select(a => a!).ToList()), m.Forecast.Plan.Count > 0),
                },
                _ => null,
            };
            if (reply != null)
            {
                reply.ReplyTo = m.Seq;
                _replies.Enqueue(reply);
            }
            return m.Seq;
        }

        // Replies arrive after the request has gone, as they do over the socket.
        readonly Queue<ServerMessage> _replies = new();
        public void Pump() { while (_replies.TryDequeue(out var r)) Panel.Handle(r); }
        public void Click(string link) { Panel.Click(link); Pump(); }
        public void Open(string tab) { Panel.Open(tab); Pump(); }
        public void Query(int x, int y) { Panel.Query(x, y); Pump(); }
        public void OpenDialog(string d) { Panel.OpenDialog(d); Pump(); }

        public List<Protocol.V0.Action> Actions => Sent.Where(s => s.BodyCase == ClientMessage.BodyOneofCase.Action).Select(s => s.Action).ToList();
    }

    static readonly Regex Tag = new(@"\[(/?)(url|b|color|font_size|table|cell)(=[^\]]*)?\]");

    /// <summary>Every tag that opens closes, in order, and nothing that looks like a tag is left unescaped.</summary>
    static void WellFormed(string bb, string where)
    {
        var stack = new Stack<string>();
        foreach (Match m in Tag.Matches(bb))
        {
            if (m.Groups[1].Value == "") stack.Push(m.Groups[2].Value);
            else
            {
                Assert.True(stack.Count > 0, $"{where}: [/{m.Groups[2].Value}] closes nothing");
                Assert.Equal(stack.Pop(), m.Groups[2].Value);
            }
        }
        Assert.True(stack.Count == 0, $"{where}: [{string.Join(",", stack)}] left open");
        var stray = Regex.Matches(Tag.Replace(bb, ""), @"\[(?!lb\])[^\]]*\]");
        Assert.True(stray.Count == 0, $"{where}: stray tag {stray.FirstOrDefault()?.Value}");
    }

    static List<string> Links(string bb) => Regex.Matches(bb, @"\[url=([^\]]*)\]").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void EveryTabRendersWellFormedAndEveryLinkRuns()
    {
        var rig = new Rig();
        var p = rig.Panel;
        var town = rig.City.Buildings.Values.First(b => rig.City.TypeById[b.TypeId].Placeable && !rig.City.IsRoadside(b));
        rig.Query(town.X, town.Y);
        foreach (var (tab, _) in PanelModel.Tabs)
        {
            rig.Open(tab);
            string text = p.Render();
            Assert.False(string.IsNullOrWhiteSpace(text), tab);
            WellFormed(text, tab);
            // Each link must run without throwing, and leave the panel renderable.
            foreach (var link in Links(text).Distinct())
            {
                if (link.StartsWith("loan:")) continue;  // tested on its own; it would change the budget under the rest
                rig.Click(link);
                WellFormed(p.Render(), $"{tab} after {link}");
                if (p.Dialog != null) p.CloseDialog();
                rig.Open(tab);
            }
        }
    }

    [Fact]
    public void DialogsRenderWellFormed()
    {
        var rig = new Rig();
        var p = rig.Panel;
        foreach (var d in new[] { "seat", "consent", "new", "save", "load" })
        {
            rig.OpenDialog(d);
            WellFormed(p.Render(), d);
            Assert.Contains("close", Links(p.Render()));
        }
        rig.OpenDialog("seat");
        rig.Click("level:3");
        Assert.Contains("Autopilot style", p.Render());
        WellFormed(p.Render(), "seat at autopilot");
    }

    [Fact]
    public void TaxFundingAndLoanLinksSendTheActions()
    {
        var rig = new Rig();
        var p = rig.Panel;
        rig.Open("budget");
        var tax = Links(p.Render()).First(l => l.StartsWith("tax:4:"));
        rig.Click(tax);
        var t = rig.Actions[^1].Tax;
        Assert.Equal(ZoneKind.ZoneCommercial, t.Zone);
        Assert.Equal(Wealth.Medium, t.Wealth);
        Assert.Equal(int.Parse(tax.Split(':')[2]), t.PercentTenths);
        Assert.Contains(Math.Abs(t.PercentTenths - rig.City.Stats.TaxTenths[4]), new[] { 5 });

        rig.Click("loan:10000");
        Assert.Equal(10000, rig.Actions[^1].Loan.Amount);

        var civic = rig.City.Buildings.Values.First(b => rig.City.TypeById[b.TypeId].Placeable && !rig.City.IsRoadside(b));
        rig.Query(civic.X, civic.Y);
        var fund = Links(p.Render()).First(l => l.StartsWith($"fund:{civic.Id}:"));
        rig.Click(fund);
        Assert.Equal(civic.Id, rig.Actions[^1].Funding.BuildingId);
    }

    [Fact]
    public void ATopicLinkOpensItsArticleAndAlertsJumpToTheirSource()
    {
        var rig = new Rig();
        var p = rig.Panel;
        rig.Open("demand");
        var topic = Links(p.Render()).First(l => l.StartsWith("topic:"));
        rig.Click(topic);
        Assert.Equal("guide", p.Tab);
        var article = p.Articles.Single(a => a.Id == topic[6..]);
        Assert.Contains(Bb.Esc(article.Title), p.Render());
        Assert.Null(p.InputPrompt);  // no search box over an article
        rig.Click("guide-back");
        Assert.NotNull(p.InputPrompt);

        (int, int)? jumped = null;
        p.JumpTo += (x, y) => jumped = (x, y);
        if (p.Alerts.FirstOrDefault(a => a.X >= 0) is { } alert)
        {
            rig.Open("alerts");
            rig.Click(Links(p.Render()).First(l => l.StartsWith($"jump:{alert.X}:{alert.Y}")));
            Assert.Equal((alert.X, alert.Y), jumped);
            Assert.Equal((alert.X, alert.Y), p.Selected);
            Assert.Equal("why", p.Tab);
        }
    }

    [Fact]
    public void PlanningCollectsActionsAndForecastsThem()
    {
        var rig = new Rig();
        var p = rig.Panel;
        rig.Open("forecast");
        rig.Click("plan");
        var zone = new Protocol.V0.Action { Zone = new PaintZone { Area = new Rect { X0 = 10, Y0 = 10, X1 = 14, Y1 = 14 }, Zone = ZoneKind.ZoneResidential } };
        Assert.True(p.Act(zone, "Zone 5×5 residential"));
        Assert.Empty(rig.Actions);
        Assert.Contains("Zone 5×5 residential", p.Render());
        rig.Click("plan-run");
        Assert.True(p.Forecast!.WithPlan);
        Assert.Single(rig.Sent.Last(s => s.BodyCase == ClientMessage.BodyOneofCase.Forecast).Forecast.Plan);
        rig.Click("plan-build");
        Assert.Equal(zone, rig.Actions.Single());
        Assert.False(p.Planning);
    }

    [Fact]
    public void ComparingALawForecastsTheCityWithAndWithoutIt()
    {
        var rig = new Rig();
        var p = rig.Panel;
        rig.Open("laws");
        var law = p.Ordinances.First(o => o.Status == ToyCity.Protocol.V0.OrdinanceStatus.OrdinanceAvailable);
        rig.Click($"law:{law.Id}");
        rig.Click($"compare:{law.Id}");
        var forecasts = rig.Sent.Where(s => s.BodyCase == ClientMessage.BodyOneofCase.Forecast).TakeLast(2).ToList();
        Assert.Empty(forecasts[0].Forecast.Plan);                         // without
        Assert.True(forecasts[1].Forecast.Plan.Single().Ordinance.Enact);  // with
        string text = p.Render();
        Assert.Contains("Without", text);
        Assert.Contains("Population", text);
        WellFormed(text, "compare");
    }
}
