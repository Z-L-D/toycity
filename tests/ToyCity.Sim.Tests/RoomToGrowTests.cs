using System.Diagnostics;
using ToyCity.Protocol;
using Xunit.Abstractions;

namespace ToyCity.Sim.Tests;

/// <summary>Beta 4, part 6: region sizes up to huge, growing regions without the 8 × 8 limit, and the region charter.</summary>
public class RoomToGrowTests(ITestOutputHelper output)
{
    const int S = 64;

    [Fact]
    public void RegionsComeInFourSizesAndAHugeOneIsMadeATileAtATime()
    {
        Assert.Equal(new[] { 3, 8, 16, 32 }, Region.Sizes.Select(z => z.Side));
        Assert.Equal(Region.MaxSide, Region.Sizes[^1].Side);
        Assert.Throws<ArgumentOutOfRangeException>(() => Region.Create(Fixture.Content, "Too big", 1, Region.MaxSide + 1, 2, S));

        using var small = Region.Create(Fixture.Content, "Small", 7, 3, 3, S);
        Assert.False(small.PerTile);
        Assert.Equal((0, 0, 3, 3), (small.BoxCol, small.BoxRow, small.BoxCols, small.BoxRows));

        var sw = Stopwatch.StartNew();
        using var huge = Region.Create(Fixture.Content, "Huge", 7, 32, 32, S);
        output.WriteLine($"a huge region made in {sw.ElapsedMilliseconds} ms");
        Assert.True(huge.TileByTile);
        Assert.False(huge.Grows);
        Assert.Equal(32 * 32, huge.Tiles.Length);
        Assert.All(huge.Tiles, t => Assert.False(t.Wild));
        Assert.All(huge.Tiles, t => Assert.Null(huge.GrownGround(t.Col, t.Row)));
        Assert.True(sw.ElapsedMilliseconds < 2000, "a huge region makes no ground until it's needed");

        // Founding surveys the tile; its neighbour, founded later, meets it exactly.
        var a = huge.Found(16, 16, "Midtown", "May");
        Assert.NotNull(huge.GrownGround(16, 16));
        Assert.Null(huge.GrownGround(17, 16));
        var b = huge.Found(17, 16, "Eastend", "Tess");
        var ga = huge.GrownGround(16, 16)!;
        var gb = huge.GrownGround(17, 16)!;
        for (int k = 0; k <= S; k++) Assert.Equal(ga.Corners[k * (S + 1) + S], gb.Corners[k * (S + 1)]);
        Assert.Equal(ga.Corners, a.Corner);
        Assert.Equal((16, 16, 2, 1), (huge.BoxCol, huge.BoxRow, huge.BoxCols, huge.BoxRows));
        huge.Run(20);
        Assert.Null(huge.Unbalanced());

        // The map: a rough picture of every tile not looked at yet, without making its ground.
        sw.Restart();
        var pictures = new Dictionary<int, Google.Protobuf.ByteString>();
        var s = RegionMapping.State(huge, 16, 16, 1, _ => Array.Empty<string>(), pictures);
        output.WriteLine($"the huge region's map in {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        RegionMapping.State(huge, 16, 16, 1, _ => Array.Empty<string>(), pictures);
        output.WriteLine($"and again, with its pictures kept, in {sw.ElapsedMilliseconds} ms");
        Assert.True(s.TileByTile);
        Assert.Equal(32 * 32, s.Tiles.Count);
        Assert.Equal(32 * 32 - 2, s.Tiles.Count(t => t.Unsurveyed));
        Assert.All(s.Tiles, t => Assert.Equal(RegionMapping.PictureSize * RegionMapping.PictureSize * 3, t.Picture.Length));
        Assert.Null(huge.GrownGround(0, 0));
        // Some of it is sea and some land, as on a region made at once.
        var colours = s.Tiles.Where(t => t.Unsurveyed).SelectMany(t => Enumerable.Range(0, t.Picture.Length / 3).Select(i => t.Picture[i * 3 + 2] > t.Picture[i * 3 + 1])).ToList();
        double sea = colours.Count(x => x) / (double)colours.Count;
        output.WriteLine($"sea in the rough pictures: {sea:P0}");
        Assert.InRange(sea, 0.03, 0.6);

        // Saved and loaded: still made a tile at a time, with the two tiles' ground and the same cities.
        using var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(huge));
        Assert.True(back.TileByTile);
        Assert.Equal(huge.StateHash(), back.StateHash());
        Assert.Equal(ga.Corners, back.GrownGround(16, 16)!.Corners);
        Assert.Null(back.GrownGround(0, 0));
        huge.Run(10);
        back.Run(10);
        Assert.Equal(huge.StateHash(), back.StateHash());
        Assert.Equal(b.StateHash(), back.TileAt(17, 16)!.City!.StateHash());
    }

    [Fact]
    public void AGrowingRegionGrowsPastEightTiles()
    {
        using var r = Region.CreateGrowing(Fixture.Content, "Longshore", 9, S);
        int c = Region.GrowStart;
        r.Found(c, c, "Root", "May");
        for (int k = 1; k <= 10; k++) r.Grow(c + k, c);
        for (int k = 1; k <= 3; k++) r.Grow(c, c - k);
        Assert.Equal(14, r.Tiles.Count(t => !t.Wild));
        Assert.Equal((c, c - 3, 11, 4), (r.BoxCol, r.BoxRow, r.BoxCols, r.BoxRows));
        Assert.Equal(11 * S * 4 * S, r.Rivers.Length);
        r.Run(5);
        Assert.Null(r.Unbalanced());
        var s = RegionMapping.State(r, c, c, 1, _ => Array.Empty<string>());
        Assert.Equal(13 * 6, s.Tiles.Count);
        Assert.Equal(14, s.Tiles.Count(t => !t.Wild));
        using var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r));
        Assert.Equal(r.StateHash(), back.StateHash());
        Assert.Equal(r.TileCorners(c + 10, c), back.TileCorners(c + 10, c));
    }

    [Fact]
    public void TheCharterIsChangedByAVoteOfTheMayors()
    {
        using var r = Region.Create(Fixture.Content, "Council", 4, 3, 1, S);
        Assert.Equal(RegionCharter.Default, r.Charter);
        Assert.Equal(512 / S, r.MaxCitySide);
        r.Found(0, 0, "Ayton", "Ann");
        r.Found(1, 0, "Beeby", "Ben");
        r.Found(2, 0, "Ceeford", "", computer: AutopilotStyle.Green);
        // Computer mayors don't vote.
        Assert.Equal(new[] { "Ann", "Ben" }, r.Voters());

        Assert.NotNull(r.CantCharter(new RegionCharter(CharterRule.Vote, CharterRule.Vote, 512 / S + 1)));
        var asked = new RegionCharter(CharterRule.Admins, CharterRule.Anyone, 2);
        Assert.Null(r.CantCharter(asked));
        var v = r.Ask(VoteKind.Charter, "Ann", charter: asked);
        Assert.Throws<InvalidOperationException>(() => r.Ask(VoteKind.Charter, "Ben", charter: asked));
        // One yes of two isn't more than half.
        Assert.Null(r.Outcome(v));
        Assert.Contains("annexing by any mayor", r.Describe(v));
        Assert.NotNull(r.Cast(v.Id, "Zed", true));

        // It survives a save.
        using (var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r)))
        {
            Assert.Single(back.Votes);
            Assert.Equal(asked, back.Votes[0].Charter);
            Assert.Equal(new[] { "Ann" }, back.Votes[0].Yes);
        }

        Assert.Null(r.Cast(v.Id, "Ben", true));
        var (passed, failed) = r.Decide();
        Assert.Single(passed);
        Assert.Empty(failed);
        Assert.Empty(r.Votes);
        r.Charter = passed[0].Charter!;
        Assert.Equal(2, r.MaxCitySide);
        using (var back = RegionMapping.Load(Fixture.Content, RegionMapping.Save(r))) Assert.Equal(asked, back.Charter);

        // Half saying no turns it down; one nobody answers lapses.
        var no = r.Ask(VoteKind.Charter, "Ben", charter: RegionCharter.Default);
        r.Cast(no.Id, "Ann", false);
        Assert.False(r.Outcome(no));
        r.Decide();
        var quiet = r.Ask(VoteKind.Charter, "Ann", charter: RegionCharter.Default);
        r.Run(Region.VoteDays);
        Assert.False(r.Outcome(quiet));
        Assert.Contains(quiet, r.Decide().Failed);
    }
}
