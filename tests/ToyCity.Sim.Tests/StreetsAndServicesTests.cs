using ToyCity.Contract;

namespace ToyCity.Sim.Tests;

/// <summary>Real street names, petitions that clear as soon as they're met, and schools and clinics counting who they serve.</summary>
public class StreetsAndServicesTests
{
    static World Grown(ulong seed, int days = 120)
    {
        var w = Fixture.Starter(seed);
        w.Run(days);
        return w;
    }

    [Fact]
    public void AStraightRoadHasOneNameAndTheNewsUsesIt()
    {
        var w = Grown(401, 30);
        // The longest east-west run of road in some row: every cell of it has the same name.
        int bestY = -1, bestX0 = 0, bestLen = 0;
        for (int y = 0; y < w.HeightCells; y++)
            for (int x = 0, run = 0; x < w.Width; x++)
            {
                run = w.Path[w.Idx(x, y)] != 0 ? run + 1 : 0;
                if (run > bestLen) (bestLen, bestY, bestX0) = (run, y, x - run + 1);
            }
        Assert.True(bestLen >= 5);
        var names = Enumerable.Range(bestX0 + 1, bestLen - 2)
            .Where(x => !(w.Path[w.Idx(x, bestY - 1)] != 0 || w.Path[w.Idx(x, bestY + 1)] != 0))
            .Select(x => w.StreetAt(x, bestY)).Distinct().ToList();
        var name = Assert.Single(names);
        Assert.DoesNotContain("Avenue", name);
        Assert.Equal(name, w.Why(bestX0 + 1, bestY).Subject);
        Assert.Contains(w.Inspect(bestX0 + 1, bestY), f => f.Key == "Street" && f.Value.StartsWith(name));

        // A home names the road it faces, the same name a click on that road shows.
        var home = w.Buildings.First(b => b != null && b.Residents > 0 && b.Access >= 0)!;
        int ax = home.Access % w.Width, ay = home.Access / w.Width;
        Assert.Equal(w.Why(ax, ay).Subject, w.StreetNear(home.X, home.Y));
    }

    [Fact]
    public void NorthSouthRoadsAreNumberedAvenues()
    {
        var w = Grown(402, 30);
        for (int i = 0; i < w.N; i++)
        {
            int x = i % w.Width, y = i / w.Width;
            if (w.Path[i] == 0 || x == 0 || x == w.Width - 1 || y == 0 || y == w.HeightCells - 1) continue;
            bool ns = w.Path[i - w.Width] != 0 && w.Path[i + w.Width] != 0 && w.Path[i - 1] == 0 && w.Path[i + 1] == 0;
            if (!ns) continue;
            Assert.EndsWith(" Avenue", w.StreetAt(x, y));
            Assert.Equal($"{x + 1}", new string(w.StreetAt(x, y).TakeWhile(char.IsDigit).ToArray()));
            return;
        }
        Assert.Fail("no north-south road");
    }

    [Fact]
    public void APetitionClearsWithinDaysOfTheStationOpening()
    {
        var w = Grown(405, 90);
        var homes = w.Buildings.Where(b => b != null && b.Residents > 0).ToList();
        int ev = w.Content.Events.FindIndex(e => e.Id == "petition-police");
        // A site for a police station next to some home, and that home's petition.
        foreach (var home in homes)
        {
            int i = w.Idx(home!.X, home.Y);
            if (w.Coverage[(int)Service.Police][i] * 100 / 255 >= 15) continue;
            for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                if (w.ValidatePlacement(new PlaceBuildingAction(home.X + dx, home.Y + dy, "police-station"), out _) != null) continue;
                while (w.Day >= 25) w.Step(); // stay inside a month: the petition must clear on a day, not at the month's turn
                var p = new Prompt { Id = 900, Event = ev, Kind = EventKind.Petition, X = home.X, Y = home.Y, Created = w.Tick, Deadline = w.Tick + 360, Petitioner = "Pat Smith", Text = "More police!" };
                w.Prompts.Add(p);
                Assert.Contains("Police coverage here (%): ", w.PromptProgress(p));
                w.Enqueue(new PlaceBuildingAction(home.X + dx, home.Y + dy, "police-station"));
                w.Run(3);
                Assert.Equal(PromptStatus.Fulfilled, p.Status);
                Assert.Equal("", w.PromptProgress(p));
                return;
            }
        }
        Assert.Fail("no site for a station");
    }

    [Fact]
    public void SchoolsAndClinicsCountWhoTheyServeOnce()
    {
        var w = Grown(410, 360);
        var homes = w.Buildings.Where(b => b != null && b.Residents > 0).ToList();
        int cx = (int)homes.Average(b => b!.X), cy = (int)homes.Average(b => b!.Y);
        Building PlaceNear(string type, int skip)
        {
            for (int r = 0; r < 40; r++)
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != r) continue;
                if (w.ValidatePlacement(new PlaceBuildingAction(x, y, type), out _) != null || skip-- > 0) continue;
                int before = w.Buildings.Count;
                w.Enqueue(new PlaceBuildingAction(x, y, type));
                w.ApplyPending();
                Assert.Equal(before + 1, w.Buildings.Count);
                return w.Buildings[^1]!;
            }
            throw new InvalidOperationException("no site");
        }
        var school = PlaceNear("elementary-school", 0);
        var clinic = PlaceNear("clinic", 0);
        w.Run(35);
        Assert.True(school.Operating && clinic.Operating);
        var one = w.ServiceLoad(school)!;
        Assert.True(one.Served > 0, "no pupils");
        Assert.Equal(400, one.Capacity);
        Assert.Contains("ages 5–11", one.Unit);
        Assert.True(w.ServiceLoad(clinic)!.Served > 0, "no patients");
        Assert.Matches(@"Teaching [\d,]+ of 400 pupils", w.Why(school.X, school.Y).Summary);
        Assert.Contains(w.Inspect(clinic.X, clinic.Y), f => f.Key == "Patients" && f.Value.EndsWith("of 60 a month"));
        Assert.Null(w.ServiceLoad(homes[0]!));
        // The panel's gauges: the load first, then a quirky statistic that rotates by month.
        var gauges = w.Gauges(school);
        Assert.Equal(2, gauges.Count);
        Assert.Equal(("Pupils", one.Served, 400L, false), (gauges[0].Label, (int)gauges[0].Value, gauges[0].Max, gauges[0].Fun));
        Assert.True(gauges[1].Fun && gauges[1].Value <= gauges[1].Max && gauges[1].Max > 0);
        w.Run(World.DaysPerMonth);
        Assert.NotEqual(gauges[1].Label, w.Gauges(school)[1].Label);
        Assert.Empty(w.Gauges(homes[0]!));

        // Cutting the budget cuts the seats and the reach; raising it adds both.
        w.Enqueue(new SetFundingAction(school.Id, 50));
        w.Step();
        Assert.Equal(200, w.ServiceLoad(school)!.Capacity);
        Assert.True(w.EffectiveRadius(school) < 16);
        w.Enqueue(new SetFundingAction(school.Id, 150));
        w.Step();
        Assert.Equal(600, w.ServiceLoad(school)!.Capacity);
        w.Enqueue(new SetFundingAction(school.Id, 100));
        w.Step();

        // A second school shares the pupils; nobody is counted twice.
        var second = PlaceNear("elementary-school", 6);
        w.Run(2);
        int both = w.ServiceLoad(school)!.Served + w.ServiceLoad(second)!.Served;
        Assert.True(w.ServiceLoad(second)!.Served > 0);
        Assert.True(both >= one.Served && w.ServiceLoad(school)!.Served <= one.Served, $"{one.Served} then {both}");
    }
}
