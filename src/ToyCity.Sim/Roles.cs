namespace ToyCity.Sim;

/// <summary>What a deputy mayor may do on the mayor's behalf.</summary>
[Flags]
public enum Portfolio : byte { None = 0, Planning = 1, Budget = 2, Services = 4, Transit = 8 }

/// <summary>The mayor makes a player a deputy with these portfolios, or with none to stand them down.</summary>
public sealed record SetDeputyAction(string Player, Portfolio Portfolios) : SimAction;

/// <summary>The mayor switches the city council on or off.</summary>
public sealed record SetCouncilAction(bool On) : SimAction;

/// <summary>A council member votes on a proposal.</summary>
public sealed record VoteAction(int ProposalId, bool Yes) : SimAction;

public enum ProposalStatus : byte { Open = 0, Passed = 1, Failed = 2 }

/// <summary>A tax change, law or loan waiting on the council's vote.</summary>
public sealed class Proposal
{
    public int Id;
    public SimAction Action = null!;
    public string Proposer = "";
    public long Tick, Deadline;
    public ProposalStatus Status;
    /// <summary>Each member's vote, by name.</summary>
    public SortedDictionary<string, bool> Votes = new(StringComparer.Ordinal);
}

public sealed partial class World
{
    /// <summary>Days a proposal stays open before it is decided on the votes cast.</summary>
    public const int VoteDays = 7;

    readonly SortedDictionary<string, Portfolio> _deputies = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Portfolio> Deputies => _deputies;
    public bool CouncilOn { get; private set; }
    public readonly List<Proposal> Proposals = new();
    int _nextProposal = 1;
    /// <summary>Set while a passed proposal's action is applied, so it isn't sent back to the council.</summary>
    bool _passing;

    public static string PortfolioName(Portfolio p) => p switch
    {
        Portfolio.Planning => "planning (roads, zones, utilities, terraforming, bulldozing)",
        Portfolio.Budget => "the budget (taxes, funding, loans)",
        Portfolio.Services => "services (civic buildings, their upgrades and station leases)",
        Portfolio.Transit => "transit (stops, depots and their leases)",
        _ => "nothing",
    };

    /// <summary>The portfolio a city action falls under, or None when only the mayor may do it.</summary>
    Portfolio PortfolioFor(SimAction a) => a switch
    {
        PlacePathAction or PaintZoneAction or ZoneBlockAction or BulldozeAction or TerraformAction or PlaceUtilityAction => Portfolio.Planning,
        SetTaxAction or SetFundingAction or TakeLoanAction => Portfolio.Budget,
        PlaceBuildingAction p => Content.TypeIndex(p.TypeId) is >= 0 and var t && IsTransitLot(Content.Types[t].LotKind) ? Portfolio.Transit : Portfolio.Services,
        UpgradeBuildingAction u => u.BuildingId >= 0 && u.BuildingId < Buildings.Count && Buildings[u.BuildingId] is { } b && IsTransitLot(LotKindOf(b)) ? Portfolio.Transit : Portfolio.Services,
        LeaseLotAction l => l.BuildingId >= 0 && l.BuildingId < Buildings.Count && Buildings[l.BuildingId] is { } lb && !IsTransitLot(LotKindOf(lb)) ? Portfolio.Services : Portfolio.Transit,
        SetLeaseTermsAction or EndLeaseAction => LeaseKindPortfolio(a),
        OpenTenderAction => Portfolio.Transit,
        _ => Portfolio.None,
    };

    Portfolio LeaseKindPortfolio(SimAction a)
    {
        int id = a is SetLeaseTermsAction t ? t.LeaseId : ((EndLeaseAction)a).LeaseId;
        return LeaseById(id) is { } l && !IsTransitLot(l.Kind) ? Portfolio.Services : Portfolio.Transit;
    }

    /// <summary>The players who vote on the council: everyone who owns a seat, and the deputies.</summary>
    public List<string> CouncilMembers() => Owners().Concat(_deputies.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Matters the council votes on while it sits.</summary>
    static bool CouncilMatter(SimAction a) => a is SetTaxAction or SetOrdinanceAction or TakeLoanAction;

    /// <summary>Why the player may not run this city action, or null if they may: the mayor, or a deputy with its portfolio.</summary>
    string? CityRole(SimAction a, string by)
    {
        if (MayorSeat.Owner == by) return null;
        var p = PortfolioFor(a);
        if (p != Portfolio.None && _deputies.TryGetValue(by, out var mine) && (mine & p) != 0) return null;
        return p == Portfolio.None
            ? $"Only the mayor ({MayorSeat.Owner}) can do that."
            : $"Only the mayor ({MayorSeat.Owner}) or a deputy for {PortfolioName(p)} can do that.";
    }

    void ApplySetDeputy(SetDeputyAction a)
    {
        if (a.Player.Length is 0 or > 40 || a.Player == MayorSeat.Owner) { Reject("Pick another player to be deputy."); return; }
        if (a.Portfolios == Portfolio.None)
        {
            if (_deputies.Remove(a.Player)) Notices.Add(new SimNotice($"{a.Player} is no longer a deputy mayor.", "info"));
            return;
        }
        _deputies[a.Player] = a.Portfolios;
        var names = Enum.GetValues<Portfolio>().Where(p => p != Portfolio.None && (a.Portfolios & p) != 0).Select(p => p.ToString().ToLowerInvariant());
        Notices.Add(new SimNotice($"{a.Player} is deputy mayor for {string.Join(", ", names)}.", "info"));
    }

    void ApplySetCouncil(SetCouncilAction a)
    {
        if (CouncilOn == a.On) return;
        CouncilOn = a.On;
        if (!a.On)
            foreach (var p in Proposals.Where(p => p.Status == ProposalStatus.Open)) p.Status = ProposalStatus.Failed;
        Notices.Add(new SimNotice(a.On
            ? "The city council sits: tax changes, laws and loans now go to a vote of every seat owner and deputy. Ties go to the mayor."
            : "The council is dissolved: the mayor decides alone again.", "info"));
    }

    /// <summary>
    /// A council matter while the council sits becomes a proposal, with its proposer voting yes. Returns false when the
    /// action should simply apply: the council isn't sitting, or the proposer is its only member.
    /// </summary>
    bool SendToCouncil(SimAction a)
    {
        if (!CouncilOn || _passing || !CouncilMatter(a) || a.By.Length == 0) return false;
        if (CouncilMembers().Count <= 1) return false;
        var p = new Proposal { Id = _nextProposal++, Action = a, Proposer = a.By, Tick = Tick, Deadline = Tick + VoteDays };
        p.Votes[a.By] = true;
        Proposals.Add(p);
        if (Proposals.Count > 40) Proposals.RemoveAll(x => x.Status != ProposalStatus.Open && Proposals.Count > 40);
        Notices.Add(new SimNotice($"{a.By} puts to the council: {Describe(a)}. Members have {VoteDays} days to vote.", "info"));
        return true;
    }

    void ApplyVote(VoteAction a)
    {
        var p = Proposals.FirstOrDefault(x => x.Id == a.ProposalId);
        if (p == null || p.Status != ProposalStatus.Open) { Reject("That vote is over."); return; }
        if (!CouncilMembers().Contains(a.By) && a.By.Length > 0) { Reject("Only council members vote: seat owners and deputies."); return; }
        p.Votes[a.By] = a.Yes;
        if (CouncilMembers().All(p.Votes.ContainsKey)) Decide(p);
    }

    /// <summary>Daily: proposals whose time is up are decided on the votes cast.</summary>
    void RunCouncil()
    {
        foreach (var p in Proposals.Where(p => p.Status == ProposalStatus.Open && Tick >= p.Deadline).ToList()) Decide(p);
    }

    /// <summary>More yes than no passes; a tie goes the way the mayor voted (and fails if the mayor didn't).</summary>
    void Decide(Proposal p)
    {
        var members = CouncilMembers();
        int yes = p.Votes.Count(v => v.Value && members.Contains(v.Key)), no = p.Votes.Count(v => !v.Value && members.Contains(v.Key));
        bool pass = yes != no ? yes > no : p.Votes.TryGetValue(MayorSeat.Owner, out var mayor) && mayor;
        p.Status = pass ? ProposalStatus.Passed : ProposalStatus.Failed;
        string tally = $"{yes} for, {no} against{(yes == no ? ", the mayor deciding" : "")}";
        Notices.Add(new SimNotice($"The council {(pass ? "passed" : "turned down")} {Describe(p.Action)} ({tally}).", pass ? "good" : "info"));
        if (!pass) return;
        string actor = _actor;
        _passing = true;
        _actor = p.Proposer;
        Apply(p.Action);
        _passing = false;
        _actor = actor;
    }

    /// <summary>Changes whenever the deputies, the council or a vote does, for deciding when to resend them.</summary>
    public string RolesSig() =>
        $"{string.Join(",", _deputies.Select(d => $"{d.Key}={(int)d.Value}"))}/{CouncilOn}/{string.Join(",", Proposals.Select(p => $"{p.Id}:{(int)p.Status}:{string.Join("", p.Votes.Select(v => v.Key + (v.Value ? "+" : "-")))}:{(p.Status == ProposalStatus.Open ? p.Deadline - Tick : 0)}"))}";

    /// <summary>A proposal in words.</summary>
    public string Describe(SimAction a) => a switch
    {
        SetTaxAction t => $"{t.Zone} {t.Wealth switch { Level.Low => "$", Level.Medium => "$$", _ => "$$$" }} tax to {t.PercentTenths / 10.0:0.0}%",
        SetOrdinanceAction o => $"{(o.Enact ? "enact" : "repeal")} {Content.Ordinances.FirstOrDefault(x => x.Id == o.Id)?.Name ?? o.Id}",
        TakeLoanAction l => $"a loan of ${l.Amount:N0}",
        _ => a.GetType().Name,
    };

    void WriteRoles(BinaryWriter w)
    {
        w.Write(_deputies.Count);
        foreach (var (n, p) in _deputies) { w.Write(n); w.Write((byte)p); }
        w.Write(CouncilOn);
        w.Write(_nextProposal);
        w.Write(Proposals.Count);
        foreach (var p in Proposals)
        {
            w.Write(p.Id); w.Write(p.Proposer); w.Write(p.Tick); w.Write(p.Deadline); w.Write((byte)p.Status);
            WriteCouncilAction(w, p.Action);
            w.Write(p.Votes.Count);
            foreach (var (n, v) in p.Votes) { w.Write(n); w.Write(v); }
        }
        WriteDelinks(w);
    }

    void ReadRoles(BinaryReader r)
    {
        _deputies.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _deputies[r.ReadString()] = (Portfolio)r.ReadByte();
        CouncilOn = r.ReadBoolean();
        _nextProposal = r.ReadInt32();
        Proposals.Clear();
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++)
        {
            var p = new Proposal { Id = r.ReadInt32(), Proposer = r.ReadString(), Tick = r.ReadInt64(), Deadline = r.ReadInt64(), Status = (ProposalStatus)r.ReadByte() };
            p.Action = ReadCouncilAction(r) with { By = p.Proposer };
            int nv = r.ReadInt32();
            for (int k = 0; k < nv; k++) p.Votes[r.ReadString()] = r.ReadBoolean();
            Proposals.Add(p);
        }
        ReadDelinks(r);
    }

    static void WriteCouncilAction(BinaryWriter w, SimAction a)
    {
        switch (a)
        {
            case SetTaxAction t: w.Write((byte)0); w.Write((byte)t.Zone); w.Write((byte)t.Wealth); w.Write(t.PercentTenths); break;
            case SetOrdinanceAction o: w.Write((byte)1); w.Write(o.Id); w.Write(o.Enact); break;
            case TakeLoanAction l: w.Write((byte)2); w.Write(l.Amount); break;
            default: throw new InvalidOperationException($"not a council matter: {a.GetType().Name}");
        }
    }

    static SimAction ReadCouncilAction(BinaryReader r) => r.ReadByte() switch
    {
        0 => new SetTaxAction((Zone)r.ReadByte(), (Level)r.ReadByte(), r.ReadInt32()),
        1 => new SetOrdinanceAction(r.ReadString(), r.ReadBoolean()),
        2 => new TakeLoanAction(r.ReadInt64()),
        _ => throw new InvalidDataException("unknown council matter"),
    };
}
