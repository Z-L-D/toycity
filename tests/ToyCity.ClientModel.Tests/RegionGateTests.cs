using System.Diagnostics;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;
using PAction = ToyCity.Protocol.V0.Action;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// The Beta 3 gate: a three-tile region, each tile on its own host process, with two human mayors and a computer
/// mayor, runs an in-game decade without desync. May is mayor of Toyville on the region host; Tess founds Westbrook
/// next door and plays it from its own tile host; a growth-hungry computer runs Eastfield on a third. May and Tess
/// join their towns by a road across their shared border and zone beside it, as players would. Then the region runs
/// at full speed for ten years: the monthly state hashes must agree (the region host replays each tile host's days on
/// a check copy) and every border ledger must balance every day (what one city sends, its neighbour receives), and
/// people and goods must actually cross.
/// </summary>
[Collection(nameof(FederatedTests))]
public class RegionGateTests
{
    sealed class Mayor : IDisposable
    {
        public readonly string Name;
        public readonly CityClient Client;
        public readonly PanelModel Panels;
        public CityMirror City => Client.City;

        public Mayor(Uri ws, string name)
        {
            Name = name;
            Client = new CityClient(ws, "gate");
            Panels = new PanelModel(Client.City, Client.Connection.Send) { Me = () => name };
            Client.Message += Panels.Handle;
            Client.Message += m =>
            {
                if (m.BodyCase == ServerMessage.BodyOneofCase.Error) Told.Add("refused: " + m.Error.Message);
                if (m.BodyCase == ServerMessage.BodyOneofCase.Delta) Told.AddRange(m.Delta.Notices.Select(n => n.Text));
            };
            WaitFor(() => Client.Connection.SignInNeeded, "the welcome");
            Client.Connection.SignIn(name, "secret1", create: true);
            WaitFor(() => Client.Connection.Player.Length > 0 || Client.Connection.SignInNeeded, "an answer");
            if (Client.Connection.SignInNeeded) Client.Connection.SignIn(name, "secret1");
            WaitFor(() => City.Loaded && Panels.Region.State != null, "the city and its region");
        }

        public RegionState Region => Panels.Region.State!;
        public readonly List<string> Told = new();

        public void WaitFor(Func<bool> done, string what, double seconds = 60)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"{Name} waiting for {what}");
                Client.Poll();
                Thread.Sleep(5);
            }
        }

        public void Road(int x0, int y0, int x1, int y1) =>
            Client.Connection.Act(new PAction { Path = new PlacePath { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, ProfileId = "road" } });

        public bool RoadAt(int x, int y) => City.Paths[City.Idx(x, y)] != 0;

        bool Clear(int i) => City.Paths[i] == 0 && City.BuildingAt[i] == 0 && !City.IsWater(i);

        /// <summary>
        /// Plans a road from the town out to the border at row <paramref name="y"/>: from a street along its row to a
        /// column two cells beyond everything built, along that column to row y, then straight to the edge, all over
        /// open land. Null when no street can reach it that way.
        /// </summary>
        public (int Fx, int Fy, int Ox)? PlanToBorder(int side, int y)
        {
            var c = City;
            int w = c.Width, edge = side == 1 ? w - 1 : 0, dir = side == 1 ? 1 : -1;
            var built = Enumerable.Range(0, w * c.Height).Where(i => (c.Paths[i] != 0 || c.BuildingAt[i] != 0) && i % w > 3 && i % w < w - 4).Select(i => i % w).ToList();
            if (built.Count == 0) return null;
            int ox = side == 1 ? built.Max() + 2 : built.Min() - 2;
            if (ox < 1 || ox > w - 2) return null;
            bool Row(int yy, int a, int b) => Enumerable.Range(Math.Min(a, b), Math.Abs(b - a) + 1).All(x => Clear(c.Idx(x, yy)));
            if (!Row(y, ox, edge)) return null;
            // Only streets joined to the town: an earlier attempt refused halfway can leave a stub that leads nowhere.
            var joined = new bool[w * c.Height];
            var queue = new Queue<int>(Enumerable.Range(0, w * c.Height).Where(i => c.Paths[i] != 0 && Around(i).Any(j => c.BuildingAt[j] != 0)));
            foreach (int i in queue) joined[i] = true;
            while (queue.Count > 0)
                foreach (int j in Around(queue.Dequeue()))
                    if (!joined[j] && c.Paths[j] != 0) { joined[j] = true; queue.Enqueue(j); }
            var streets = Enumerable.Range(0, w * c.Height).Where(i => joined[i] && i % w > 3 && i % w < w - 4)
                .Select(i => (X: i % w, Y: i / w)).OrderBy(p => Math.Abs(p.Y - y)).ThenBy(p => Math.Abs(ox - p.X));
            foreach (var (fx, fy) in streets)
            {
                if (!Row(fy, fx + dir, ox)) continue;
                if (fy != y && !Enumerable.Range(Math.Min(fy, y), Math.Abs(y - fy) + 1).Where(yy => yy != fy && yy != y).All(yy => Clear(c.Idx(ox, yy)))) continue;
                return (fx, fy, ox);
            }
            return null;
        }

        IEnumerable<int> Around(int i)
        {
            int w = City.Width, x = i % w, y = i / w;
            if (x > 0) yield return i - 1;
            if (x < w - 1) yield return i + 1;
            if (y > 0) yield return i - w;
            if (y < City.Height - 1) yield return i + w;
        }

        /// <summary>What the road a plan lays would cost, leg by leg, as the server prices it (grading and rock too).</summary>
        public long Price((int Fx, int Fy, int Ox) plan, int side, int y)
        {
            var (fx, fy, ox) = plan;
            int edge = side == 1 ? City.Width - 1 : 0;
            var t = new Tools.ToolState { Kind = Tools.ToolKind.Path, ProfileId = "road" };
            long Leg(int x0, int y0, int x1, int y1) => Tools.Planner.PlanPath(City, t, Tools.Planner.LineCells(x0, y0, x1, y1)) is { Problem: null } p ? p.Cost : long.MaxValue / 8;
            // A straight run from the street prices exactly; with a turn, each leg is graded against the one before,
            // which can cost as much again as it priced alone, so a turn counts double.
            if (fy == y) return Leg(fx, y, edge, y);
            long legs = Leg(fx, fy, ox, fy) + Leg(ox, fy, ox, y) + Leg(ox, y, edge, y);
            return legs >= long.MaxValue / 8 ? legs : legs * 2;
        }

        public bool Build((int Fx, int Fy, int Ox) plan, int side, int y)
        {
            var (fx, fy, ox) = plan;
            int edge = side == 1 ? City.Width - 1 : 0;
            if (fy == y) { Road(fx, y, edge, y); ox = fx; }
            else Road(fx, fy, ox, fy);
            if (fy != y) { Road(ox, fy, ox, y); Road(ox, y, edge, y); }
            // Actions apply at once, paused or not: the road shows in a moment, or it was refused.
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && !(RoadAt(edge, y) && RoadAt(ox, y) && RoadAt(ox, fy))) { Client.Poll(); Thread.Sleep(10); }
            return RoadAt(edge, y) && RoadAt(ox, y) && RoadAt(ox, fy);
        }

        public void Dispose() => Client.Dispose();
    }

    const string Key = "gatekey";
    const int Decade = 10 * 12 * 30;

    [Fact, Trait("Category", "Gate")]
    public void ThreeTilesOnTheirOwnHostsRunADecadeWithoutDesyncAndTheLedgersBalance()
    {
        using var leader = new ServerProcess($$"""{ "name": "Gate", "join": "open", "linkKey": "{{Key}}", "takeOverSeconds": 120, "fastDayMs": 0 }""");
        using var may = new Mayor(leader.Ws, "May");
        Assert.Equal(1, may.Region.HereCol);
        // Tess founds Westbrook west of Toyville for herself; May, the admin, founds Eastfield for a computer mayor.
        using (var tessHere = new Mayor(leader.Ws, "Tess"))
        {
            tessHere.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = 0, Row = 1, Name = "Westbrook", StarterTown = true } });
            tessHere.WaitFor(() => tessHere.Region.Tiles.Single(t => t.Col == 0 && t.Row == 1).Founded, "Westbrook");
        }
        may.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = 2, Row = 1, Name = "Eastfield", StarterTown = true, Computer = "growth-hungry" } });
        may.WaitFor(() => may.Region.Tiles.Count(t => t.Founded) == 3, "three cities");
        using var west = new ServerProcess(null, $"--tile-host ws://127.0.0.1:{leader.Port}/tile-link --tile 0,1 --link-key {Key} --public ws://127.0.0.1:{{port}}/ws");
        using var east = new ServerProcess(null, $"--tile-host ws://127.0.0.1:{leader.Port}/tile-link --tile 2,1 --link-key {Key} --public ws://127.0.0.1:{{port}}/ws");
        var clock = Stopwatch.StartNew();
        try
        {
            may.WaitFor(() => may.Region.Tiles.Where(t => t.Row == 1 && t.Col != 1).All(t => t.Address.Length > 0), "both tile hosts on the map");
            using var tess = new Mayor(west.Ws, "Tess");
            Assert.Equal("Westbrook", tess.City.Stats.CityName);
            Assert.Equal("Tess", tess.Region.Tiles.Single(t => t.Col == 0 && t.Row == 1).Mayor);
            Assert.EndsWith("(computer)", may.Region.Tiles.Single(t => t.Col == 2 && t.Row == 1).Mayor);

            // The region holds while the mayors build.
            may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 0 } });
            // A road across the border, at a row both towns can reach: May's from Toyville's west side, Tess's from
            // Westbrook's east side, meeting at the edge.
            // Graded over hills a road to the border costs more than a starter town has in hand, so both mayors borrow.
            may.Client.Connection.Act(new PAction { Loan = new TakeLoan { Amount = 400_000 } });
            tess.Client.Connection.Act(new PAction { Loan = new TakeLoan { Amount = 400_000 } });
            int row = -1;
            // Rows through the towns first, as commuters want them; each priced first with the client's planner (the
            // server's own grading, rock and all), so a road over the hills isn't started where the money would run out.
            may.WaitFor(() => may.City.Stats.Funds > 300_000, "May's loan");
            tess.WaitFor(() => tess.City.Stats.Funds > 300_000, "Tess's loan");
            var townRows = Enumerable.Range(0, may.City.Width * may.City.Height).Where(i => may.City.Paths[i] != 0).Select(i => i / may.City.Width).Distinct().OrderBy(r => r).ToList();
            var rows = new List<(int Y, (int, int, int) Mine, (int, int, int) Hers, long Worst)>();
            foreach (int y in townRows.Concat(townRows.Select(r => r + 6)).Concat(Enumerable.Range(4, may.City.Height - 8)).Distinct())
            {
                if (y < 4 || y > may.City.Height - 5) continue;
                if (may.PlanToBorder(3, y) is not { } mine || tess.PlanToBorder(1, y) is not { } hers) continue;
                long a = may.Price(mine, 3, y), b = tess.Price(hers, 1, y);
                // A tenth to spare: grading can come out a little dearer than it priced.
                if (a * 11 / 10 > may.City.Stats.Funds || b * 11 / 10 > tess.City.Stats.Funds) continue;
                rows.Add((y, mine, hers, Math.Max(a, b)));
            }
            Console.WriteLine($"GATE {rows.Count} affordable rows; May has {may.City.Stats.Funds:N0}, Tess {tess.City.Stats.Funds:N0}");
            foreach (var (y, mine, hers, worst) in rows.Take(3))
            {
                bool built = may.Build(mine, 3, y) & tess.Build(hers, 1, y);
                Console.WriteLine($"GATE row {y}: Toyville {mine}, Westbrook {hers}, priced {worst:N0}, built {built}; May told: {string.Join(" | ", may.Told.TakeLast(3))}; Tess told: {string.Join(" | ", tess.Told.TakeLast(3))}");
                if (built) { row = y; break; }
            }
            Assert.True(row >= 0, "a road joins Toyville and Westbrook across their border");
            Console.WriteLine($"GATE border road on row {row}");
            // Homes beside the new road on both sides, so there are people to commute.
            may.Client.Connection.Act(new PAction { Zone = new PaintZone { Area = new Rect { X0 = 3, Y0 = row + 1, X1 = 20, Y1 = row + 3 }, Zone = ZoneKind.ZoneResidential } });
            tess.Client.Connection.Act(new PAction { Zone = new PaintZone { Area = new Rect { X0 = tess.City.Width - 21, Y0 = row - 3, X1 = tess.City.Width - 4, Y1 = row - 1 }, Zone = ZoneKind.ZoneResidential } });

            // Then a decade at full speed.
            long start = may.Region.Tick;
            may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
            int mostCrossing = 0;
            long lastTess = 0;
            may.WaitFor(() =>
            {
                tess.Client.Poll();
                lastTess = tess.City.Stats.Tick;
                mostCrossing = Math.Max(mostCrossing, may.Region.CrossBorder);
                return may.Region.Tick >= start + Decade || may.Region.Stopped.Length > 0;
            }, "a decade", 1500);
            var seen = $"day {may.Region.Tick - start} of {Decade} in {clock.Elapsed.TotalSeconds:F0} s; ledger months {may.Region.LedgerMonths}; most crossing a day {mostCrossing}; stopped \"{may.Region.Stopped}\"";
            Console.WriteLine($"GATE {seen}");
            string RowGaps(Mayor m, int y) => string.Concat(Enumerable.Range(0, m.City.Width).Select(x => m.RoadAt(x, y) ? '=' : m.City.IsWater(m.City.Idx(x, y)) ? '~' : '.'));
            Console.WriteLine($"GATE DIAG May pop {may.City.Stats.Population}; Tess pop {tess.City.Stats.Population}");
            int Lots(Mayor m, int x0, int y0, int x1, int y1) => Enumerable.Range(y0, y1 - y0 + 1).Sum(yy => Enumerable.Range(x0, x1 - x0 + 1).Count(xx => m.City.BuildingAt[m.City.Idx(xx, yy)] != 0));
            Console.WriteLine($"GATE DIAG built cells by the border road: Toyville {Lots(may, 3, row + 1, 20, row + 3)} of 54, Westbrook {Lots(tess, tess.City.Width - 21, row - 3, tess.City.Width - 4, row - 1)} of 54");
            Console.WriteLine($"GATE DIAG May row {RowGaps(may, row)}");
            Console.WriteLine($"GATE DIAG Tess row {RowGaps(tess, row)}");
            Assert.True(may.Region.Stopped == "", seen);
            Assert.True(may.Region.LedgerMonths >= Decade / 30 - 1, seen);
            Assert.True(mostCrossing > 0, seen);
            // Every host was there all along: nobody's city was taken over, and Westbrook's host kept the region's day.
            Assert.Equal("", may.Region.Waiting);
            Assert.Equal("Tess", may.Region.Tiles.Single(t => t.Col == 0 && t.Row == 1).Mayor);
            Assert.True(may.Region.Tiles.Where(t => t.Row == 1 && t.Col != 1).All(t => t.Address.Length > 0), "both tile hosts still serve their cities");
            tess.WaitFor(() => { may.Client.Poll(); return Math.Abs(tess.City.Stats.Tick - may.City.Stats.Tick) <= 3; }, "Westbrook in step", 30);
            Assert.True(tess.City.Stats.Population > 0 && may.City.Stats.Population > 0);
        }
        catch (Exception e)
        {
            string reports = Directory.Exists(leader.Saves) ? string.Join("\n", Directory.GetFiles(leader.Saves, "*.txt").Where(f => Path.GetFileName(f).StartsWith("desync") || Path.GetFileName(f).StartsWith("ledger")).Select(File.ReadAllText)) : "";
            throw new Exception($"{e.Message}\n{reports}\nREGION HOST:\n{leader.Output}\nWESTBROOK:\n{west.Output}\nEASTFIELD:\n{east.Output}", e);
        }
    }
}
