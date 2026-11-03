using System.Diagnostics;
using ToyCity.ClientModel.Panels;
using ToyCity.Protocol.V0;
using ToyCity.Sim;
using CharterRule = ToyCity.Protocol.V0.CharterRule;
using VoteKind = ToyCity.Protocol.V0.VoteKind;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 3.2, G4: a growing region on a dedicated server: starting one, adding tiles, and who may.</summary>
public class GrowingRegionClientTests : IDisposable
{
    readonly ServerProcess _server = new("""{ "name": "Frontier", "join": "open", "fastDayMs": 0 }""");
    public void Dispose() => _server.Dispose();
    static readonly ContentDb Content = ContentDb.LoadDefault();

    sealed class Player : IDisposable
    {
        public readonly CityClient Client;
        public readonly PanelModel Panels;
        public readonly List<string> Errors = new();
        public int Fulls;

        public Player(Uri ws, string name)
        {
            Client = new CityClient(ws, "test");
            Panels = new PanelModel(Client.City, Client.Connection.Send) { Me = () => name, Admin = () => Client.Connection.Admin };
            Client.Message += Panels.Handle;
            Client.Message += m => { if (m.BodyCase == ServerMessage.BodyOneofCase.Error) Errors.Add(m.Error.Message); };
            Client.Message += m => { if (m.BodyCase == ServerMessage.BodyOneofCase.Full) Fulls++; };
            WaitFor(() => Client.Connection.SignInNeeded, "the welcome");
            Client.Connection.SignIn(name, "secret1", create: true);
            WaitFor(() => Client.Connection.Player.Length > 0 || Client.Connection.SignInNeeded, "an answer");
            if (Client.Connection.SignInNeeded) Client.Connection.SignIn(name, "secret1");
            WaitFor(() => Client.City.Loaded && Panels.Region.State != null, "the city and its region");
        }

        public RegionModel Region => Panels.Region;

        public void WaitFor(Func<bool> done, string what, double seconds = 60)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"waiting for {what}");
                Client.Poll();
                Thread.Sleep(10);
            }
        }

        public void Dispose() => Client.Dispose();
    }

    [Fact]
    public void ARegionStartsAsOneTileAndMayorsGrowItBesideTheirCities()
    {
        // May, the server's first account and so its admin, starts a growing region from the new-city dialog.
        using var may = new Player(_server.Ws, "May");
        may.Panels.Click("dialog:new");
        may.Panels.SetInput("Firstford");
        may.Panels.Click("grow");
        may.Panels.Click("starter");
        may.Panels.Click("size:64");
        may.Panels.Click("new-ok");
        may.WaitFor(() => may.Client.City.Stats.CityName == "Firstford" && may.Region.State?.Grows == true, "the growing region");
        var s = may.Region.State!;
        int c = s.HereCol;
        Assert.Equal((Region.GrowStart, Region.GrowStart), (s.HereCol, s.HereRow));
        Assert.Single(s.Tiles, t => !t.Wild);
        Assert.Equal((c - 1, c - 1, c + 1, c + 1), may.Region.Shown);
        Assert.Contains("1 tile, growing", may.Region.Title());

        // She adds the land east of her city.
        may.Panels.Click("rg-open");
        may.Panels.Click($"rg-pick:{c + 1}:{c}");
        Assert.Contains("Add this tile to the region", may.Region.Render());
        may.Panels.Click("rg-grow");
        Assert.StartsWith("Adding", may.Region.Pending);
        may.WaitFor(() => may.Region.TileAt(c + 1, c)?.Wild == false, "the new tile");
        Assert.Equal("", may.Region.Pending);
        Assert.Equal((c - 1, c - 1, c + 2, c + 1), may.Region.Shown);
        Assert.Contains("Open land", may.Region.Render());
        Assert.Equal(RegionModel.PictureSize * RegionModel.PictureSize * 3, may.Region.TileAt(c + 1, c)!.Picture.Length);

        // Tess has no city yet, so she can't grow the region; she founds one on the open land instead.
        using var tess = new Player(_server.Ws, "Tess");
        tess.Panels.Click("rg-open");
        tess.Panels.Click($"rg-pick:{c - 1}:{c}");
        tess.Panels.Click("rg-grow");
        tess.WaitFor(() => tess.Errors.Count > 0, "the refusal");
        Assert.Contains("Only a mayor here", tess.Errors[0]);
        Assert.True(tess.Region.TileAt(c - 1, c)!.Wild);
        tess.Panels.Click($"rg-pick:{c + 1}:{c}");
        tess.Region.NewName = "Eastwick";
        tess.Panels.Click("rg-found");
        tess.WaitFor(() => tess.Region.TileAt(c + 1, c)?.Founded == true, "Eastwick founded");
        // Now the land beside Eastwick is hers to add.
        tess.Panels.Click($"rg-pick:{c + 2}:{c}");
        tess.Panels.Click("rg-grow");
        tess.WaitFor(() => tess.Region.TileAt(c + 2, c)?.Wild == false, "the tile east of Eastwick");
        may.WaitFor(() => may.Region.TileAt(c + 2, c)?.Wild == false, "May's map shows it");
        Assert.Single(tess.Errors);
    }

    [Fact]
    public void UnderAVotingCharterTheMayorsDecideWhatJoinsTheRegion()
    {
        // May, the admin, makes a growing region whose charter puts new land to a vote.
        using var may = new Player(_server.Ws, "May");
        may.Panels.Click("dialog:new");
        may.Panels.SetInput("Firstford");
        may.Panels.Click("grow");
        Assert.Contains("Adding land", may.Panels.Render());
        may.Panels.Click("new-grow:CharterVote");
        may.Panels.Click("size:64");
        may.Panels.Click("new-ok");
        may.WaitFor(() => may.Client.City.Stats.CityName == "Firstford" && may.Region.State is { Grows: true, TileSize: 64 }, "the growing region");
        var s = may.Region.State!;
        int c = s.HereCol;
        Assert.Equal(CharterRule.CharterVote, s.Charter.Grow);
        Assert.Equal(512 / 64, s.MaxCitySide);
        Assert.Contains("a vote of the mayors", may.Region.Render());
        // An admin adds land at once whatever the charter says; Tess founds a city on it.
        may.Client.Connection.Send(new ClientMessage { Grow = new GrowRegion { Col = c + 1, Row = c } });
        may.WaitFor(() => may.Region.TileAt(c + 1, c)?.Wild == false, "the east tile");
        using var tess = new Player(_server.Ws, "Tess");
        tess.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = c + 1, Row = c, Name = "Eastwick" } });
        tess.WaitFor(() => tess.Region.State!.Voters.Count == 2, "two mayors");

        // Tess asks to add the land east of Eastwick: it goes to a vote, and May's yes carries it.
        tess.Panels.Click("rg-open");
        tess.Panels.Click($"rg-pick:{c + 2}:{c}");
        Assert.Contains("Ask the mayors to add this tile", tess.Region.Render());
        tess.Panels.Click("rg-grow");
        tess.WaitFor(() => tess.Region.State!.Votes.Count == 1, "the vote");
        Assert.Equal("", tess.Region.Pending);
        Assert.True(tess.Region.TileAt(c + 2, c)!.Wild);
        Assert.Contains("The mayors are voting on it now", tess.Region.Render());
        may.WaitFor(() => may.Region.State!.Votes.Count == 1, "May hears of the vote");
        var v = may.Region.State!.Votes[0];
        Assert.Contains("east of Eastwick", v.Text);
        may.Panels.Click("rg-open");
        Assert.Contains($"rg-vote:{v.Id}:yes", may.Region.Render());
        may.Panels.Click($"rg-vote:{v.Id}:yes");
        tess.WaitFor(() => tess.Region.TileAt(c + 2, c)?.Wild == false, "the tile, voted in");
        Assert.Empty(tess.Region.State!.Votes);

        // Tess asks for a charter where anyone annexes; May says no, and with half against it's turned down.
        tess.Panels.Click("rg-charter");
        tess.Panels.Click("rg-ch-annex:CharterAnyone");
        tess.Panels.Click("rg-ch-max:2");
        Assert.Contains("Put it to a vote", tess.Region.Render());
        tess.Panels.Click("rg-ch-send");
        tess.WaitFor(() => tess.Region.Pending == "" || tess.Errors.Count > 0, "the charter asked");
        Assert.Empty(tess.Errors);
        may.WaitFor(() => may.Region.State!.Votes is { Count: 1 } vs && vs[0].Kind == VoteKind.VoteCharter, "the charter vote");
        may.Panels.Click($"rg-vote:{may.Region.State!.Votes[0].Id}:no");
        tess.WaitFor(() => tess.Region.State!.Votes.Count == 0, "the vote over");
        Assert.Equal(CharterRule.CharterVote, tess.Region.State!.Charter.Annex);

        // An admin's change takes effect at once.
        may.Panels.Click("rg-charter");
        may.Panels.Click("rg-ch-max:2");
        Assert.Contains("Set it", may.Region.Render());
        may.Panels.Click("rg-ch-send");
        tess.WaitFor(() => tess.Region.State!.MaxCitySide == 2, "May's charter");
        Assert.Empty(may.Errors);
        Assert.Empty(tess.Errors);
    }

    /// <summary>A seed whose first tile has a river running off one of its sides, and that side.</summary>
    static (ulong Seed, int Side) RiverSeed(int size)
    {
        for (ulong seed = 1; seed < 80; seed++)
        {
            using var r = Region.CreateGrowing(Content, "probe", seed, size);
            var g = r.GrownGround(Region.GrowStart, Region.GrowStart)!;
            var steps = World.StepsFromCorners(g.Corners, size, size);
            for (int i = 0; i < size * size; i++)
            {
                if (g.Rivers[i] <= steps[i] || g.Downstream[i] >= 0) continue;
                int x = i % size, y = i / size;
                int side = y == 0 ? 0 : x == size - 1 ? 1 : y == size - 1 ? 2 : x == 0 ? 3 : -1;
                if (side >= 0) return (seed, side);
            }
        }
        throw new InvalidOperationException("no seed with a river leaving its first tile");
    }

    static (int, int) Beside(int c, int side) => side switch { 0 => (c, c - 1), 1 => (c + 1, c), 2 => (c, c + 1), _ => (c - 1, c) };

    [Fact, Trait("Category", "Gate")]
    public void AGrowingRegionGrowsEveryWayItsRiversRunOnAndItPlaysAYear()
    {
        const int Size = 128;
        var (seed, riverSide) = RiverSeed(Size);
        int c = Region.GrowStart;
        using var may = new Player(_server.Ws, "May");
        may.Client.Connection.Send(new ClientMessage { NewRegion = new NewRegionRequest { Name = "Frontier", CityName = "Firstford", Seed = seed, TileSize = Size, StarterTown = true, Grows = true } });
        may.WaitFor(() => may.Client.City.Stats.CityName == "Firstford" && may.Region.State is { Grows: true, TileSize: Size }, "the growing region");
        Console.WriteLine($"GATE seed {seed}: one {Size}-cell tile at {c},{c}; a river leaves it on side {riverSide}");

        // May adds a tile on every side of her city, the river's side first.
        var order = new[] { riverSide, (riverSide + 1) % 4, (riverSide + 2) % 4, (riverSide + 3) % 4 };
        foreach (int side in order)
        {
            var (col, row) = Beside(c, side);
            var clock = Stopwatch.StartNew();
            may.Client.Connection.Send(new ClientMessage { Grow = new GrowRegion { Col = col, Row = row } });
            may.WaitFor(() => may.Region.TileAt(col, row)?.Wild == false, $"tile {col},{row}");
            Console.WriteLine($"GATE added tile {col},{row} ({"NESW"[side]}) in {clock.ElapsedMilliseconds} ms");
        }
        Assert.Equal(5, may.Region.State!.Tiles.Count(t => !t.Wild));
        Assert.Empty(may.Errors);

        // The same region made here: the server's tiles are this ground (it's made from the seed and the order).
        using var local = Region.CreateGrowing(Content, "Frontier", seed, Size);
        foreach (int side in order) { var (col, row) = Beside(c, side); local.Grow(col, row); }
        var mine = may.Client.City;
        int same = 0;
        for (int k = 0; k < mine.Corners.Length; k++) if (mine.Corners[k] == local.GrownGround(c, c)!.Corners[k]) same++;
        // The starter town levels its own blocks (on this seed's ground, about an eighth of the tile).
        Assert.True(same > mine.Corners.Length * 8 / 10, $"Firstford's ground isn't the region's: {same} of {mine.Corners.Length} corners agree");
        foreach (int side in order)
        {
            var (col, row) = Beside(c, side);
            var a = local.GrownGround(c, c)!;
            var b = local.GrownGround(col, row)!;
            int diff = 0, cw = Size + 1;
            double jump = 0;
            var sa = World.StepsFromCorners(a.Corners, Size, Size);
            var sb = World.StepsFromCorners(b.Corners, Size, Size);
            for (int k = 0; k <= Size; k++)
            {
                int ia = side switch { 0 => k, 1 => k * cw + Size, 2 => Size * cw + k, _ => k * cw };
                int ib = side switch { 0 => Size * cw + k, 1 => k * cw, 2 => k, _ => k * cw + Size };
                if (a.Corners[ia] != b.Corners[ib]) diff++;
                if (k == Size) continue;
                int ca = side switch { 0 => k, 1 => k * Size + Size - 1, 2 => (Size - 1) * Size + k, _ => k * Size };
                int cb = side switch { 0 => (Size - 1) * Size + k, 1 => k * Size, 2 => k, _ => k * Size + Size - 1 };
                jump += Math.Abs(sa[ca] - sb[cb]);
            }
            Assert.Equal(0, diff);
            Console.WriteLine($"GATE seam {"NESW"[side]}: all {Size + 1} corners meet; neighbouring cells across it differ by {jump / Size:0.00} steps on average");
        }
        // The river runs on across the seam in the whole region's map.
        var ground = local.Ground;
        int wide = local.BoxCols * Size, crossings = 0;
        for (int i = 0; i < ground.Rivers.Length; i++)
        {
            int d = ground.Downstream[i];
            if (d >= 0 && (i % wide / Size != d % wide / Size || i / wide / Size != d / wide / Size)) crossings++;
        }
        Assert.True(crossings > 0, "no river runs on from one tile into another");
        var (rc, rr) = Beside(c, riverSide);
        int riverCells = local.GrownGround(rc, rr)!.Rivers.Count(v => v != 0);
        Console.WriteLine($"GATE rivers cross {crossings} seam(s); the tile on side {"NESW"[riverSide]} carries {riverCells} river cells");
        if (System.Environment.GetEnvironmentVariable("TOYCITY_SHOTS") is { Length: > 0 } dir) Picture(local, Path.Combine(dir, "growing-region-ground.ppm"));

        // Tess founds a city on the river's tile, and the region plays a year.
        using var tess = new Player(_server.Ws, "Tess");
        tess.Client.Connection.Send(new ClientMessage { FoundCity = new FoundCity { Col = rc, Row = rr, Name = "Rivermouth", StarterTown = true } });
        tess.WaitFor(() => tess.Region.TileAt(rc, rr)?.Founded == true, "Rivermouth");
        Assert.Contains(Enumerable.Range(0, tess.Client.City.Width * tess.Client.City.Height), i => tess.Client.City.IsRiver(i));
        long start = may.Region.State!.Tick;
        may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
        var year = Stopwatch.StartNew();
        may.WaitFor(() => may.Region.State!.Tick >= start + 360, "a year", 600);
        var s = may.Region.State!;
        Assert.Equal("", s.Stopped);
        Console.WriteLine($"GATE a year in {year.Elapsed.TotalSeconds:0} s: {s.Tiles.Count(t => t.Founded)} cities, {s.Tiles.Sum(t => t.Population):N0} people, ledgers balanced {s.LedgerMonths} months running");
        Assert.True(s.LedgerMonths >= 11, $"the ledgers were checked {s.LedgerMonths} months");

        // Saved and loaded, it's still growing, with the same land.
        may.Client.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 0 } });
        may.Client.Connection.Send(new ClientMessage { Save = new SaveRequest { Name = "frontier" } });
        may.WaitFor(() => Directory.GetFiles(_server.Saves, "frontier*").Length > 0, "the save");
        int fulls = may.Fulls;
        may.Client.Connection.Send(new ClientMessage { Load = new LoadRequest { Name = "frontier" } });
        may.WaitFor(() => may.Fulls > fulls, "the load");
        may.Client.Connection.Send(new ClientMessage { Region = new RegionRequest() });
        may.WaitFor(() => may.Region.State is { Grows: true } r && r.Tiles.Count(t => !t.Wild) == 5, "the loaded region");
        Console.WriteLine($"GATE saved and loaded: {may.Region.State!.Tiles.Count(t => !t.Wild)} tiles, {may.Region.State.Tiles.Count(t => t.Founded)} cities, still growing");
    }

    /// <summary>
    /// The tiles around the first, from above: the ground shaded by its slopes, the sea and the rivers blue, wild land
    /// black. For the gate report.
    /// </summary>
    static void Picture(Region r, string path)
    {
        // The region's ground covers the box of its tiles.
        int s = r.TileSize, c0 = Region.GrowStart - 1 - r.BoxCol, r0 = Region.GrowStart - 1 - r.BoxRow, n = 3 * s, scale = 2;
        var g = r.Ground;
        int wide = r.BoxCols * s, cw = wide + 1;
        var px = new byte[n * scale * n * scale * 3];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int gx = c0 * s + x, gy = r0 * s + y, i = gy * wide + gx, k = gy * cw + gx;
            byte cr, cg, cb;
            if (r.TileAt(r.BoxCol + gx / s, r.BoxRow + gy / s)!.Wild) (cr, cg, cb) = ((byte)14, (byte)16, (byte)22);
            else
            {
                int h = (g.Corners[k] + g.Corners[k + 1] + g.Corners[k + cw] + g.Corners[k + cw + 1]) / 4;
                double slope = (g.Corners[k + 1] + g.Corners[k + cw + 1] - g.Corners[k] - g.Corners[k + cw]) / 2.0 + (g.Corners[k + cw] + g.Corners[k + cw + 1] - g.Corners[k] - g.Corners[k + 1]) / 2.0;
                double light = Math.Clamp(0.75 - slope / 60.0, 0.25, 1.15);
                if (g.Rivers[i] > 0) (cr, cg, cb) = ((byte)70, (byte)150, (byte)230);
                else if (h < 8 * World.StepDm) (cr, cg, cb) = ((byte)30, (byte)70, (byte)130);
                else
                {
                    double t = Math.Clamp((h - 160) / 1200.0, 0, 1);
                    (cr, cg, cb) = ((byte)Math.Clamp((90 + 90 * t) * light, 0, 255), (byte)Math.Clamp((130 + 40 * t) * light, 0, 255), (byte)Math.Clamp((70 + 90 * t) * light, 0, 255));
                }
                // The seams, faintly.
                if (gx % s == 0 || gy % s == 0) (cr, cg, cb) = ((byte)(cr * 0.8 + 50), (byte)(cg * 0.8 + 50), (byte)(cb * 0.8 + 50));
            }
            for (int dy = 0; dy < scale; dy++)
            for (int dx = 0; dx < scale; dx++)
            {
                int o = ((y * scale + dy) * n * scale + x * scale + dx) * 3;
                px[o] = cr; px[o + 1] = cg; px[o + 2] = cb;
            }
        }
        using var f = File.Create(path);
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{n * scale} {n * scale}\n255\n");
        f.Write(head);
        f.Write(px);
    }
}
