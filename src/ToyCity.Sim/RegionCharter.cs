namespace ToyCity.Sim;

/// <summary>Who may do a thing the region's charter governs.</summary>
public enum CharterRule
{
    /// <summary>The server's admins only.</summary>
    Admins = 0,
    /// <summary>A mayor asks, and the region's mayors vote: more than half of them must agree.</summary>
    Vote = 1,
    /// <summary>Any mayor here, on their own say.</summary>
    Anyone = 2,
}

/// <summary>What a region vote decides.</summary>
public enum VoteKind
{
    /// <summary>A new charter.</summary>
    Charter = 0,
    /// <summary>Adding a tile of wild land to a growing region.</summary>
    Grow = 1,
    /// <summary>A city annexing the open land beside it (stage 2 of room to grow).</summary>
    Annex = 2,
}

/// <summary>
/// A region's charter (Beta 4, part 6): who may grow the region, who may let a city annex the land beside it, and how
/// big a city may get. An admin sets it when making the region; after that, an admin changes it at once, and mayors
/// change it by a vote.
/// </summary>
public sealed record RegionCharter(CharterRule Grow, CharterRule Annex, int MaxCitySide)
{
    /// <summary>A region made before charters: any mayor grows it, annexing goes to a vote, and cities may be as big as the server lets them.</summary>
    public static readonly RegionCharter Default = new(CharterRule.Anyone, CharterRule.Vote, 0);

    public static string Name(CharterRule r) => r switch
    {
        CharterRule.Admins => "admins only",
        CharterRule.Vote => "a vote of the mayors",
        _ => "any mayor",
    };
}

/// <summary>A question put to the region's mayors.</summary>
public sealed class RegionVote
{
    public int Id;
    public VoteKind Kind;
    /// <summary>The tile it's about: the wild tile to add, or the city annexing. -1 for a charter.</summary>
    public int Col = -1, Row = -1;
    /// <summary>For an annex: the open tile to take.</summary>
    public int ToCol = -1, ToRow = -1;
    /// <summary>For a charter vote: the charter asked for.</summary>
    public RegionCharter? Charter;
    /// <summary>Who asked.</summary>
    public string By = "";
    /// <summary>The day it was asked: it lapses <see cref="Region.VoteDays"/> days later.</summary>
    public long Day;
    public readonly SortedSet<string> Yes = new(StringComparer.Ordinal);
    public readonly SortedSet<string> No = new(StringComparer.Ordinal);
}

public sealed partial class Region
{
    /// <summary>The region's charter. The region's main server keeps it; a tile on its own host only reads it.</summary>
    public RegionCharter Charter { get; set; } = RegionCharter.Default;

    /// <summary>The votes open now.</summary>
    public readonly List<RegionVote> Votes = new();
    int _nextVote = 1;

    /// <summary>Days a vote stays open before it lapses.</summary>
    public const int VoteDays = 30;

    /// <summary>The largest a city may be, in cells a side, on any server (measured: a city 512 cells a side still keeps up).</summary>
    public const int MaxCityCells = 512;

    /// <summary>The largest a city may be here, in tiles a side: the charter's limit within the server's.</summary>
    public int MaxCitySide
    {
        get
        {
            int cap = Math.Max(1, MaxCityCells / TileSize);
            return Charter.MaxCitySide > 0 ? Math.Min(cap, Charter.MaxCitySide) : cap;
        }
    }

    /// <summary>Who votes: each player who is mayor of a city here, once however many cities they have. Computer mayors don't.</summary>
    public IReadOnlyList<string> Voters() =>
        Tiles.Where(t => t.City is { ComputerMayor: false } && t.Mayor.Length > 0).Select(t => t.Mayor).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>Why this charter can't be the region's, or null.</summary>
    public string? CantCharter(RegionCharter c)
    {
        if (!Enum.IsDefined(c.Grow) || !Enum.IsDefined(c.Annex)) return "That isn't a rule the charter knows.";
        if (c.MaxCitySide < 0 || c.MaxCitySide > Math.Max(1, MaxCityCells / TileSize))
            return $"A city here can be at most {Math.Max(1, MaxCityCells / TileSize)} tiles a side, the most this server can run.";
        return null;
    }

    /// <summary>Opens a vote, with the asker's own yes. Returns it, or throws why not.</summary>
    public RegionVote Ask(VoteKind kind, string by, int col = -1, int row = -1, RegionCharter? charter = null, int toCol = -1, int toRow = -1)
    {
        if (kind == VoteKind.Charter && (charter == null || CantCharter(charter) is { } bad)) throw new InvalidOperationException(charter == null ? "A charter vote needs a charter." : CantCharter(charter)!);
        if (Votes.Any(v => v.Kind == kind && v.Col == col && v.Row == row && v.ToCol == toCol && v.ToRow == toRow && (kind != VoteKind.Charter || v.Charter == charter)))
            throw new InvalidOperationException("That's already being voted on.");
        if (Votes.Count(v => v.By == by) >= 3) throw new InvalidOperationException("You have three votes open already.");
        var v = new RegionVote { Id = _nextVote++, Kind = kind, Col = col, Row = row, ToCol = toCol, ToRow = toRow, Charter = charter, By = by, Day = Tick };
        v.Yes.Add(by);
        Votes.Add(v);
        return v;
    }

    /// <summary>Puts back a vote from a save.</summary>
    public void RestoreVote(RegionVote v)
    {
        Votes.Add(v);
        _nextVote = Math.Max(_nextVote, v.Id + 1);
    }

    /// <summary>A mayor's vote. Returns why not, or null.</summary>
    public string? Cast(int id, string voter, bool yes)
    {
        if (Votes.FirstOrDefault(v => v.Id == id) is not { } v) return "That vote is over.";
        if (!Voters().Contains(voter)) return "Only the region's mayors vote.";
        (yes ? v.No : v.Yes).Remove(voter);
        (yes ? v.Yes : v.No).Add(voter);
        return null;
    }

    /// <summary>
    /// How a vote stands: passed when more than half the mayors say yes, failed when half or more say no, or when it
    /// has lapsed; else still open (null). Votes of players who are no longer mayors don't count.
    /// </summary>
    public bool? Outcome(RegionVote v)
    {
        var voters = Voters();
        int yes = v.Yes.Count(voters.Contains), no = v.No.Count(voters.Contains), n = voters.Count;
        if (n > 0 && yes * 2 > n) return true;
        if (n == 0 || no * 2 >= n || Tick - v.Day >= VoteDays) return false;
        return null;
    }

    /// <summary>Takes each decided vote off the list: those passed, to be carried out, and those failed or lapsed.</summary>
    public (List<RegionVote> Passed, List<RegionVote> Failed) Decide()
    {
        var passed = new List<RegionVote>();
        var failed = new List<RegionVote>();
        foreach (var v in Votes.ToList())
            switch (Outcome(v))
            {
                case true: passed.Add(v); Votes.Remove(v); break;
                case false: failed.Add(v); Votes.Remove(v); break;
            }
        return (passed, failed);
    }

    /// <summary>What a vote asks, in words.</summary>
    public string Describe(RegionVote v) => v.Kind switch
    {
        VoteKind.Charter => $"a new charter: growing by {RegionCharter.Name(v.Charter!.Grow)}, annexing by {RegionCharter.Name(v.Charter.Annex)}, cities up to {(v.Charter.MaxCitySide > 0 ? $"{v.Charter.MaxCitySide} × {v.Charter.MaxCitySide} tiles" : "the server's limit")}",
        VoteKind.Grow => $"adding the wild land {Beside(v.Col, v.Row)} to the region",
        _ => TileAt(v.Col, v.Row) is { } t ? $"{t.Name} annexing the open land beside it" : "an annex",
    };

    /// <summary>Where a wild tile lies, by a neighbour in the region ("east of Larkhaven").</summary>
    string Beside(int col, int row)
    {
        if (TileAt(col, row) is not { } t) return "";
        string[] from = { "south of", "west of", "north of", "east of" };
        for (int side = 0; side < 4; side++)
            if (Across(t, side) is { Wild: false } n) return $"{from[side]} {(n.Founded ? n.Name : "the open land")}";
        return "";
    }
}
