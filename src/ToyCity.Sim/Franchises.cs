using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// The mayor (or a deputy for transit) puts a district's transit franchise out to tender for some months. Transit
/// companies bid; the highest bid when it closes wins and pays it to the city.
/// </summary>
public sealed record OpenTenderAction(int District, int Months) : SimAction;

public enum TenderStatus : byte { Open = 0, Awarded = 1, NoBids = 2 }

/// <summary>A franchise tender, and once awarded, the franchise.</summary>
public sealed class FranchiseTender
{
    public int Id, District, Months;
    public long Tick, Closes, Until;
    public TenderStatus Status;
    public string Winner = "";
    public long Amount;
    /// <summary>Whether the franchise came with exclusive rights (every player allowed them when it was awarded).</summary>
    public bool Exclusive;
    public readonly SortedDictionary<string, long> Bids = new(StringComparer.Ordinal);
}

public sealed partial class World
{
    public const int TenderDays = 14, MinFranchiseMonths = 6, MaxFranchiseMonths = 36;
    /// <summary>The least a bid may offer, per month of the franchise.</summary>
    public const long MinBidPerMonth = 100;
    /// <summary>What winning lifts the holder's rating in the district.</summary>
    public const int FranchiseLift = 150;

    public readonly List<FranchiseTender> Tenders = new();
    int _nextTenderId = 1;

    public FranchiseTender? OpenTenderIn(int district) => Tenders.FirstOrDefault(t => t.District == district && t.Status == TenderStatus.Open);

    /// <summary>The franchise in force in a district, if any.</summary>
    public FranchiseTender? FranchiseIn(int district) =>
        Tenders.FirstOrDefault(t => t.District == district && t.Status == TenderStatus.Awarded && t.Until > Tick);

    /// <summary>Whether a side game other than this one holds the district's franchise, so this one may not lease new transit lots there.</summary>
    string? FranchiseBars(string gameId, int district, string kind) =>
        IsTransitLot(kind) && FranchiseIn(district) is { } f && f.Winner != gameId
            ? $"{GameName(f.Winner)} holds the transit franchise in {DistrictName(district)} for {(f.Until - Tick + 29) / 30} more month{((f.Until - Tick + 29) / 30 == 1 ? "" : "s")}."
            : null;

    void ApplyOpenTender(OpenTenderAction a)
    {
        if (a.District < 0 || a.District >= DistrictCount) { Reject("There is no such district."); return; }
        if (a.Months is < MinFranchiseMonths or > MaxFranchiseMonths) { Reject($"A franchise runs from {MinFranchiseMonths} to {MaxFranchiseMonths} months."); return; }
        string where = DistrictName(a.District);
        if (OpenTenderIn(a.District) != null) { Reject($"The franchise for {where} is already out to tender."); return; }
        if (FranchiseIn(a.District) is { } f) { Reject($"{GameName(f.Winner)} holds the franchise for {where} until it runs out in {(f.Until - Tick + 29) / 30} months."); return; }
        if (!SideGames.Any(g => g.Game.Manifest.LotKinds.Any(IsTransitLot))) { Reject("There's no transit company in the city to bid."); return; }
        var t = new FranchiseTender { Id = _nextTenderId++, District = a.District, Months = a.Months, Tick = Tick, Closes = Tick + TenderDays };
        Tenders.Add(t);
        TrimTenders();
        AddHeadline($"The city invites bids for a {a.Months}-month transit franchise in {where}. Bids close in {TenderDays} days.", "info");
    }

    /// <summary>A company's bid, from its paid actions: it replaces any earlier bid of its own.</summary>
    AuthorityResult Bid(string gameId, int district, long amount)
    {
        AuthorityResult No(string why) => new("bid", district, false, 0, why, RatingOf(gameId, district));
        if (SideGame(gameId) is not { } hg || !hg.Game.Manifest.LotKinds.Any(IsTransitLot)) return No("Only a transit company can bid for a transit franchise.");
        if (district < 0 || district >= DistrictCount || OpenTenderIn(district) is not { } t) return No("That franchise isn't out to tender.");
        long min = MinBidPerMonth * t.Months;
        if (amount < min || amount > 10_000_000) return No($"A bid for {t.Months} months runs from ${min:N0} to $10,000,000.");
        t.Bids[gameId] = amount;
        // Nothing is owed until the tender closes: the winner pays then.
        return new("bid", district, true, 0, $"{GameName(gameId)} bid ${amount:N0} for the {DistrictName(district)} franchise.", RatingOf(gameId, district));
    }

    /// <summary>Daily: tenders that have closed are decided, and franchises that have run out end.</summary>
    void RunTenders()
    {
        foreach (var t in Tenders.Where(t => t.Status == TenderStatus.Open && t.Closes <= Tick).ToList())
        {
            string where = DistrictName(t.District);
            // Highest bid from a company still in the city; ties go to the earlier name.
            var bids = t.Bids.Where(b => SideGame(b.Key) != null).OrderByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();
            if (bids.Count == 0)
            {
                t.Status = TenderStatus.NoBids;
                AddHeadline($"Nobody bid for the transit franchise in {where}.", "info");
                continue;
            }
            var (winner, amount) = bids[0];
            t.Status = TenderStatus.Awarded;
            t.Winner = winner;
            t.Amount = amount;
            t.Until = Tick + t.Months * 30L;
            t.Exclusive = Allowed(Mechanic.ExclusiveRights);
            var s = StandingFor(winner, t.District);
            s.Rating = Math.Min(1000, s.Rating + FranchiseLift);
            if (t.Exclusive) s.ExclusiveUntil = Math.Max(s.ExclusiveUntil, t.Until);
            Funds += amount;
            LastBudget.Add(($"Franchise: {GameName(winner)}", amount));
            LastIncome += amount;
            _authorityResults.Add((winner, new AuthorityResult("franchise", t.District, true, amount,
                $"{GameName(winner)} won the {t.Months}-month transit franchise for {where} with ${amount:N0}.", s.Rating)));
            foreach (var (loser, bid) in bids.Skip(1))
                _authorityResults.Add((loser, new AuthorityResult("franchise", t.District, false, 0,
                    $"{GameName(winner)} won the {where} franchise; {GameName(loser)}'s ${bid:N0} wasn't enough.", RatingOf(loser, t.District))));
            AddHeadline($"{GameName(winner)} wins the transit franchise for {where}: ${amount:N0} for {t.Months} months"
                + (t.Exclusive ? ", with exclusive rights." : $". Rivals keep the stops they have but can't lease more there."), "info");
        }
    }

    void TrimTenders()
    {
        // Keep everything open or in force, and the last few decided.
        var old = Tenders.Where(t => t.Status != TenderStatus.Open && !(t.Status == TenderStatus.Awarded && t.Until > Tick)).OrderByDescending(t => t.Id).Skip(8).ToHashSet();
        Tenders.RemoveAll(old.Contains);
    }

    /// <summary>What a side game hears each day: the open tenders, with its own bid, and every franchise and exclusive right in force.</summary>
    void FillTenders(HostedGame hg, CityToGame input)
    {
        foreach (var t in Tenders.Where(t => t.Status == TenderStatus.Open))
            input.Tenders.Add(new Tender(t.District, t.Months, (int)Math.Max(0, t.Closes - Tick), Allowed(Mechanic.ExclusiveRights),
                MinBidPerMonth * t.Months, t.Bids.GetValueOrDefault(hg.Id)));
        foreach (var t in Tenders.Where(t => t.Status == TenderStatus.Awarded && t.Until > Tick))
            input.Franchises.Add(new Franchise(t.District, t.Winner, t.Exclusive, (int)(t.Until - Tick)));
        foreach (var s in Standings.Where(s => s.ExclusiveUntil > Tick && !Tenders.Any(t => t.Status == TenderStatus.Awarded && t.Until > Tick && t.District == s.District && t.Winner == s.GameId)))
            input.Franchises.Add(new Franchise(s.District, s.GameId, true, (int)(s.ExclusiveUntil - Tick)));
    }

    void WriteTenders(BinaryWriter w)
    {
        w.Write(_nextTenderId);
        w.Write(Tenders.Count);
        foreach (var t in Tenders)
        {
            w.Write(t.Id); w.Write(t.District); w.Write(t.Months); w.Write(t.Tick); w.Write(t.Closes); w.Write(t.Until);
            w.Write((byte)t.Status); w.Write(t.Winner); w.Write(t.Amount); w.Write(t.Exclusive);
            w.Write(t.Bids.Count);
            foreach (var (g, b) in t.Bids) { w.Write(g); w.Write(b); }
        }
        w.Write(FreightExportMonth); w.Write(FreightExportDaily); w.Write(FreightDeliveredMonth); w.Write(FreightDeliveredDaily);
    }

    void ReadTenders(BinaryReader r)
    {
        Tenders.Clear();
        _nextTenderId = r.ReadInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var t = new FranchiseTender
            {
                Id = r.ReadInt32(), District = r.ReadInt32(), Months = r.ReadInt32(), Tick = r.ReadInt64(), Closes = r.ReadInt64(), Until = r.ReadInt64(),
                Status = (TenderStatus)r.ReadByte(), Winner = r.ReadString(), Amount = r.ReadInt64(), Exclusive = r.ReadBoolean(),
            };
            int nb = r.ReadInt32();
            for (int k = 0; k < nb; k++) t.Bids[r.ReadString()] = r.ReadInt64();
            Tenders.Add(t);
        }
        FreightExportMonth = r.ReadInt32(); FreightExportDaily = r.ReadInt32(); FreightDeliveredMonth = r.ReadInt32(); FreightDeliveredDaily = r.ReadInt32();
    }
}
