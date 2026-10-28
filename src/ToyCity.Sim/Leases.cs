using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// A lot the city has leased to a side game. The city keeps the building; inside it the side game decides. Business
/// lease: the operator pays rent and a tax on its fares and keeps the profit. Public service: the city pays a subsidy
/// and caps fares. Changed terms wait out the notice period. A lease falls into bad standing when the operator is
/// behind on what it owes or its rating in the district stays low; it then gets a cure period with clear targets, and
/// only after that may the mayor end it.
/// </summary>
public sealed class Lease
{
    public int Id;
    public string GameId = "";
    public int BuildingId;
    public string Kind = "";
    public LeaseTerms Terms = World.StandardTerms("bus-stop");
    public LeaseTerms? PendingTerms;
    public int PendingMonths;
    public long Arrears;
    /// <summary>Fares taken at this lot so far this month; the fare tax is billed on it.</summary>
    public long MonthRevenue;
    /// <summary>What the last month's fares came to, for the panels.</summary>
    public long LastRevenue;
    public int Jobs;
    public int LowRatingMonths;
    public bool BadStanding;
    public int CureMonthsLeft;
    public long Since;
    /// <summary>Months until a hostile eviction ends the lease; 0 when none is under way.</summary>
    public int EvictMonths;

    /// <summary>Bad standing and the cure period has run out: the mayor may end the lease.</summary>
    public bool Terminable => BadStanding && CureMonthsLeft == 0;
}

/// <summary>The mayor leases a lot to a side game. Null terms: the city's standard business terms for that kind of lot.</summary>
public sealed record LeaseLotAction(string GameId, int BuildingId, LeaseTerms? Terms = null) : SimAction;

/// <summary>The mayor changes a lease's terms. They take effect after the current terms' notice period.</summary>
public sealed record SetLeaseTermsAction(int LeaseId, LeaseTerms Terms) : SimAction;

/// <summary>Ends a lease. The operator may give one up at any time; the mayor only once it is past its cure period.</summary>
public sealed record EndLeaseAction(int LeaseId, bool ByOperator) : SimAction;

public sealed partial class World
{
    public readonly List<Lease> Leases = new();
    int _nextLeaseId = 1;

    /// <summary>A rating below this, three months running, puts a lease in bad standing.</summary>
    public const int BadRating = -200;
    public const int LowRatingMonthsToBadStanding = 3;
    public const int CureMonths = 3;

    /// <summary>What kind of lot a building is, for leasing: its type id when side games may lease it, else empty.</summary>
    public string LotKindOf(Building b) => TypeOf(b).LotKind;

    /// <summary>The terms the city offers anyone who asks: a business lease, rent by size, a 10% fare tax, three months' notice.</summary>
    public static LeaseTerms StandardTerms(string kind) =>
        new(LeaseMode.Business, kind == "bus-depot" ? 300 : 40, 100, 0, 0, 3);

    /// <summary>The standard terms for a particular building: rent by its size (a bus bay costs more than a stop).</summary>
    public LeaseTerms StandardTermsFor(Building b) =>
        TypeOf(b).LeaseRent > 0 ? StandardTerms(LotKindOf(b)) with { RentPerMonth = TypeOf(b).LeaseRent } : StandardTerms(LotKindOf(b));

    public Lease? LeaseOn(int buildingId) => Leases.FirstOrDefault(l => l.BuildingId == buildingId);
    public Lease? LeaseById(int id) => Leases.FirstOrDefault(l => l.Id == id);

    static string? CheckTerms(LeaseTerms t)
    {
        if (t.RentPerMonth is < 0 or > 100_000) return "Rent must be between $0 and $100,000 a month.";
        if (t.TaxTenths is < 0 or > 500) return "The fare tax must be between 0% and 50%.";
        if (t.SubsidyPerMonth is < 0 or > 100_000) return "The subsidy must be between $0 and $100,000 a month.";
        if (t.FareCapCents is < 0 or > 10_000) return "The fare cap must be between free and $100.";
        if (t.NoticeMonths is < 1 or > 12) return "Notice must be between 1 and 12 months.";
        return null;
    }

    /// <summary>Whether terms are at least as good for the city as its standard ones (what the computer may grant).</summary>
    static bool AtLeastStandard(LeaseTerms t, LeaseTerms std) =>
        t.Mode == LeaseMode.Business && t.RentPerMonth >= std.RentPerMonth && t.TaxTenths >= std.TaxTenths;

    string LotName(Building b) => $"the {TypeOf(b).Name.ToLowerInvariant()} on {Content.Voice.Street(b.X, b.Y)}";

    string GameName(string id) => SideGame(id)?.Game.Manifest.Name ?? id;

    void ApplyLeaseLot(LeaseLotAction a) => GrantLease(a.GameId, a.BuildingId, a.Terms, byHand: true);

    Lease? GrantLease(string gameId, int buildingId, LeaseTerms? terms, bool byHand)
    {
        var hg = SideGame(gameId);
        if (hg == null) { Reject($"There is no side game called '{gameId}' in this city."); return null; }
        if (buildingId < 0 || buildingId >= Buildings.Count || Buildings[buildingId] is not { } b) { Reject("That lot isn't there any more."); return null; }
        string kind = LotKindOf(b);
        if (kind.Length == 0 || !hg.Game.Manifest.LotKinds.Contains(kind))
        {
            Reject($"{hg.Game.Manifest.Name} can't lease a {TypeOf(b).Name.ToLowerInvariant()}.", b.X, b.Y);
            return null;
        }
        if (LeaseOn(b.Id) is { } held)
        {
            Reject(held.GameId == gameId ? $"{GameName(gameId)} already leases {LotName(b)}." : $"{Cap(LotName(b))} is leased to {GameName(held.GameId)}.", b.X, b.Y);
            return null;
        }
        int district = DistrictOf(b.X, b.Y);
        if (!byHand && RatingOf(gameId, district) < RefuseRating)
        {
            Reject($"{DistrictName(district)} won't have more of {GameName(gameId)}: its rating there is {RatingOf(gameId, district)}.", b.X, b.Y);
            return null;
        }
        var std = StandardTermsFor(b);
        var t = terms ?? std;
        if (CheckTerms(t) is { } err) { Reject(err, b.X, b.Y); return null; }
        // Only the mayor, playing by hand, can agree to terms worse for the city than its standard offer.
        if (!(byHand && MayorSeat.Level == OperatorLevel.Manual) && !AtLeastStandard(t, std))
        {
            Reject($"The city is on {LevelName(MayorSeat.Level)}: it only grants leases on its standard terms.", b.X, b.Y);
            return null;
        }
        var lease = new Lease { Id = _nextLeaseId++, GameId = gameId, BuildingId = b.Id, Kind = kind, Terms = t, Since = Tick };
        Leases.Add(lease);
        string how = t.Mode == LeaseMode.Business
            ? $"${t.RentPerMonth:N0} a month and {t.TaxTenths / 10.0:0.#}% of fares"
            : $"a ${t.SubsidyPerMonth:N0} subsidy a month, fares capped at {Fare(t.FareCapCents)}";
        Notices.Add(new SimNotice($"Leased {LotName(b)} to {GameName(gameId)}: {how}.", "info", b.X, b.Y));
        return lease;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string Fare(int cents) => cents == 0 ? "free" : $"${cents / 100.0:0.00}";

    void ApplySetLeaseTerms(SetLeaseTermsAction a)
    {
        var lease = LeaseById(a.LeaseId);
        if (lease == null) { Reject("That lease has ended."); return; }
        if (MayorSeat.Level != OperatorLevel.Manual) { Reject("Take the city back before changing a lease."); return; }
        if (CheckTerms(a.Terms) is { } err) { Reject(err); return; }
        var b = Buildings[lease.BuildingId]!;
        lease.PendingTerms = a.Terms;
        lease.PendingMonths = lease.Terms.NoticeMonths;
        string what = a.Terms.Mode != lease.Terms.Mode
            ? (a.Terms.Mode == LeaseMode.PublicService ? "becomes a public service" : "becomes a business lease")
            : "gets new terms";
        Notices.Add(new SimNotice($"{GameName(lease.GameId)}'s lease on {LotName(b)} {what} in {lease.PendingMonths} month{(lease.PendingMonths == 1 ? "" : "s")}, after notice.", "info", b.X, b.Y));
    }

    void ApplyEndLease(EndLeaseAction a)
    {
        var lease = LeaseById(a.LeaseId);
        if (lease == null) { Reject("That lease has already ended."); return; }
        if (!a.ByOperator && !lease.Terminable && Allowed(Mechanic.HostileEvictions) && !lease.BadStanding)
        {
            if (MayorSeat.Level != OperatorLevel.Manual) { Reject("Take the city back before evicting anyone."); return; }
            if (lease.EvictMonths > 0) { Reject($"{GameName(lease.GameId)} is already leaving, in {lease.EvictMonths} month{(lease.EvictMonths == 1 ? "" : "s")}."); return; }
            lease.EvictMonths = lease.Terms.NoticeMonths;
            var lot = Buildings[lease.BuildingId]!;
            Notices.Add(new SimNotice($"{GameName(lease.GameId)} has {lease.EvictMonths} months' notice to leave {LotName(lot)}. The city pays ${Compensation(lease):N0} compensation when it goes.", "info", lot.X, lot.Y));
            RaiseIncident("eviction-notice", lot.X, lot.Y, "city", "hostile eviction", 2, "");
            return;
        }
        if (!a.ByOperator && !lease.Terminable)
        {
            Reject(lease.BadStanding
                ? $"{GameName(lease.GameId)} has {lease.CureMonthsLeft} more month{(lease.CureMonthsLeft == 1 ? "" : "s")} to put things right before the lease can end."
                : $"{GameName(lease.GameId)}'s lease is in good standing, so the city can't end it: hostile evictions are switched off ({string.Join(", ", Blockers(Mechanic.HostileEvictions))} won't allow them).");
            return;
        }
        EndLease(lease, a.ByOperator ? $"{GameName(lease.GameId)} gave up its lease on {{0}}." : $"The city ended {GameName(lease.GameId)}'s lease on {{0}}.");
    }

    void EndLease(Lease lease, string text)
    {
        Leases.Remove(lease);
        if (lease.BuildingId < Buildings.Count && Buildings[lease.BuildingId] is { } b)
        {
            b.Jobs -= lease.Jobs;
            Notices.Add(new SimNotice(string.Format(text, LotName(b)), "info", b.X, b.Y));
        }
        else Notices.Add(new SimNotice(string.Format(text, "a lot that is gone"), "info"));
    }

    /// <summary>The lots leased to a side game, as it sees them. Leases on lots that are gone (a fire, a tornado) end here.</summary>
    void FillLeases(HostedGame hg, CityToGame input)
    {
        foreach (var lease in Leases.Where(l => l.GameId == hg.Id).ToList())
        {
            if (lease.BuildingId >= Buildings.Count || Buildings[lease.BuildingId] is not { } b)
            {
                EndLease(lease, $"{GameName(lease.GameId)} lost {{0}}: the lease has ended.");
                continue;
            }
            input.Lots.Add(new LeasedLot(lease.Id, b.Id, lease.Kind, b.X, b.Y, b.W, b.H, b.Access, DistrictOf(b.X, b.Y),
                lease.Terms, lease.PendingTerms, lease.PendingMonths, lease.BadStanding));
        }
        var kinds = hg.Game.Manifest.LotKinds;
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating || LeaseOn(b.Id) != null) continue;
            string kind = LotKindOf(b);
            if (kind.Length == 0 || !kinds.Contains(kind)) continue;
            input.OpenLots.Add(new OpenLot(b.Id, kind, b.X, b.Y, b.W, b.H, b.Access, DistrictOf(b.X, b.Y), StandardTermsFor(b)));
        }
    }

    /// <summary>First of the month: rent, fare tax and anything overdue, or the subsidy the city pays.</summary>
    void BillLeases(HostedGame hg, CityToGame input)
    {
        foreach (var lease in Leases.Where(l => l.GameId == hg.Id))
        {
            var t = lease.Terms;
            if (t.Mode == LeaseMode.Business)
            {
                if (t.RentPerMonth > 0) input.Bills.Add(new Bill(lease.Id, "Rent", t.RentPerMonth));
                long tax = lease.MonthRevenue * t.TaxTenths / 1000;
                if (tax > 0) input.Bills.Add(new Bill(lease.Id, "Fare tax", tax));
            }
            else if (t.SubsidyPerMonth > 0) input.Bills.Add(new Bill(lease.Id, "Subsidy", -t.SubsidyPerMonth));
            if (lease.Arrears > 0) input.Bills.Add(new Bill(lease.Id, "Overdue", lease.Arrears));
        }
        foreach (var (id, bill) in _oneOffBills) if (id == hg.Id) input.Bills.Add(bill);
        _oneOffBills.RemoveAll(o => o.GameId == hg.Id);
    }

    /// <summary>
    /// After the side game's day: the jobs and fares at each lot, lots it asked for or gave up, and on the first of the
    /// month what it paid, the subsidies, notice periods and standing.
    /// </summary>
    void SettleLeases(HostedGame hg, CityToGame input, GameToCity output)
    {
        foreach (var lease in Leases.Where(l => l.GameId == hg.Id))
        {
            var b = Buildings[lease.BuildingId]!;
            int jobs = Math.Clamp(output.JobsByLease.GetValueOrDefault(lease.Id), 0, 500);
            b.Jobs += jobs - lease.Jobs;
            lease.Jobs = jobs;
            // In stasis the city's money holds still: no fare tax accrues, no bills, no standing changes.
            if (!InStasis) lease.MonthRevenue += Math.Max(0, output.RevenueByLease.GetValueOrDefault(lease.Id));
        }
        foreach (int id in output.LeaseReleases.Distinct().ToList())
            if (LeaseById(id) is { } l && l.GameId == hg.Id) EndLease(l, $"{GameName(hg.Id)} gave up its lease on {{0}}.");
        foreach (int bid in output.LeaseRequests.Distinct().ToList())
            GrantLease(hg.Id, bid, null, byHand: false);
        if (input.NewMonth && !InStasis) SettleMonth(hg, input, output);
    }

    /// <summary>What the city pays a side game it evicts: six months of the lot's standard rent, or of its rent if higher.</summary>
    static long Compensation(Lease l) => 6 * Math.Max(l.Terms.Mode == LeaseMode.Business ? l.Terms.RentPerMonth : 0, StandardTerms(l.Kind).RentPerMonth);

    /// <summary>One-off money for side games, handed over with their next month's bills.</summary>
    readonly List<(string GameId, Bill Bill)> _oneOffBills = new();

    void SettleMonth(HostedGame hg, CityToGame input, GameToCity output)
    {
        long collected = 0, subsidies = 0;
        var evicted = new List<Lease>();
        foreach (var lease in Leases.Where(l => l.GameId == hg.Id))
        {
            var b = Buildings[lease.BuildingId]!;
            long due = input.Bills.Where(x => x.LeaseId == lease.Id && x.Amount > 0).Sum(x => x.Amount);
            long paid = Math.Clamp(output.Paid.GetValueOrDefault(lease.Id), 0, due);
            collected += paid;
            lease.Arrears = due - paid;
            subsidies += input.Bills.Where(x => x.LeaseId == lease.Id && x.Amount < 0).Sum(x => -x.Amount);
            lease.LastRevenue = lease.MonthRevenue;
            lease.MonthRevenue = 0;

            if (lease.PendingTerms is { } next && --lease.PendingMonths <= 0)
            {
                lease.Terms = next;
                lease.PendingTerms = null;
                lease.PendingMonths = 0;
                Notices.Add(new SimNotice($"New terms for {GameName(hg.Id)} at {LotName(b)} start today.", "info", b.X, b.Y));
            }

            if (lease.EvictMonths > 0 && --lease.EvictMonths == 0) { evicted.Add(lease); continue; }

            lease.LowRatingMonths = RatingOf(hg.Id, DistrictOf(b.X, b.Y)) < BadRating ? lease.LowRatingMonths + 1 : 0;
            bool failing = lease.Arrears > 0 || lease.LowRatingMonths >= LowRatingMonthsToBadStanding;
            if (failing && !lease.BadStanding)
            {
                lease.BadStanding = true;
                lease.CureMonthsLeft = CureMonths;
                string why = lease.Arrears > 0 ? $"${lease.Arrears:N0} behind on what it owes" : "rated poorly in the district for months";
                Notices.Add(new SimNotice($"{GameName(hg.Id)}'s lease on {LotName(b)} is in bad standing: {why}. It has {CureMonths} months to put it right.", "warning", b.X, b.Y));
                RaiseIncident("lease-bad-standing", b.X, b.Y, "city", why, 2, "");
            }
            else if (!failing && lease.BadStanding)
            {
                lease.BadStanding = false;
                lease.CureMonthsLeft = 0;
                Notices.Add(new SimNotice($"{GameName(hg.Id)}'s lease on {LotName(b)} is back in good standing.", "good", b.X, b.Y));
            }
            else if (failing && lease.CureMonthsLeft > 0 && --lease.CureMonthsLeft == 0)
                Notices.Add(new SimNotice($"{GameName(hg.Id)} didn't put things right at {LotName(b)}: the mayor may now end the lease.", "warning", b.X, b.Y));
        }
        foreach (var lease in evicted)
        {
            long comp = Compensation(lease);
            _oneOffBills.Add((hg.Id, new Bill(0, "Compensation", -comp)));
            subsidies += comp;
            EndLease(lease, $"The city evicted {GameName(hg.Id)} from {{0}} and paid ${comp:N0} compensation.");
        }
        if (collected == 0 && subsidies == 0) return;
        Funds += collected - subsidies;
        // On the budget as its own lines, next to the month that was just budgeted.
        if (collected > 0) { LastBudget.Add(($"Leases: {GameName(hg.Id)}", collected)); LastIncome += collected; }
        if (subsidies > 0) { LastBudget.Add(($"Subsidies: {GameName(hg.Id)}", -subsidies)); LastExpenses += subsidies; }
    }

    /// <summary>What a lease needs to get back into good standing, for the why-panel.</summary>
    public List<string> LeaseTargets(Lease lease)
    {
        var list = new List<string>();
        if (!lease.BadStanding) return list;
        var b = Buildings[lease.BuildingId]!;
        if (lease.Arrears > 0) list.Add($"Pay the ${lease.Arrears:N0} owed.");
        int rating = RatingOf(lease.GameId, DistrictOf(b.X, b.Y));
        if (rating < BadRating) list.Add($"Raise the rating in {DistrictName(DistrictOf(b.X, b.Y))} from {rating} to {BadRating} or more.");
        list.Add(lease.CureMonthsLeft > 0 ? $"{lease.CureMonthsLeft} month{(lease.CureMonthsLeft == 1 ? "" : "s")} left to do it." : "The cure period is over: the mayor may end the lease.");
        return list;
    }

    /// <summary>Restores leased jobs onto buildings after a load: building totals are rebuilt from their types.</summary>
    void ApplyLeaseJobs()
    {
        foreach (var l in Leases)
            if (l.BuildingId < Buildings.Count && Buildings[l.BuildingId] is { } b) b.Jobs += l.Jobs;
    }

    static void WriteTerms(BinaryWriter w, LeaseTerms t)
    {
        w.Write((byte)t.Mode); w.Write(t.RentPerMonth); w.Write(t.TaxTenths); w.Write(t.SubsidyPerMonth); w.Write(t.FareCapCents); w.Write(t.NoticeMonths);
    }

    static LeaseTerms ReadTerms(BinaryReader r) =>
        new((LeaseMode)r.ReadByte(), r.ReadInt64(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32());

    void WriteLeases(BinaryWriter w)
    {
        w.Write(_nextLeaseId);
        w.Write(Leases.Count);
        foreach (var l in Leases)
        {
            w.Write(l.Id); w.Write(l.GameId); w.Write(l.BuildingId); w.Write(l.Kind);
            WriteTerms(w, l.Terms);
            w.Write(l.PendingTerms != null);
            if (l.PendingTerms != null) WriteTerms(w, l.PendingTerms);
            w.Write(l.PendingMonths); w.Write(l.Arrears); w.Write(l.MonthRevenue); w.Write(l.LastRevenue); w.Write(l.Jobs);
            w.Write(l.LowRatingMonths); w.Write(l.BadStanding); w.Write(l.CureMonthsLeft); w.Write(l.Since); w.Write(l.EvictMonths);
        }
        w.Write(_oneOffBills.Count);
        foreach (var (id, b) in _oneOffBills) { w.Write(id); w.Write(b.LeaseId); w.Write(b.Label); w.Write(b.Amount); }
    }

    void ReadLeases(BinaryReader r)
    {
        _nextLeaseId = r.ReadInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var l = new Lease { Id = r.ReadInt32(), GameId = r.ReadString(), BuildingId = r.ReadInt32(), Kind = r.ReadString(), Terms = ReadTerms(r) };
            if (r.ReadBoolean()) l.PendingTerms = ReadTerms(r);
            l.PendingMonths = r.ReadInt32(); l.Arrears = r.ReadInt64(); l.MonthRevenue = r.ReadInt64(); l.LastRevenue = r.ReadInt64(); l.Jobs = r.ReadInt32();
            l.LowRatingMonths = r.ReadInt32(); l.BadStanding = r.ReadBoolean(); l.CureMonthsLeft = r.ReadInt32(); l.Since = r.ReadInt64(); l.EvictMonths = r.ReadInt32();
            Leases.Add(l);
        }
        int nb = r.ReadInt32();
        for (int i = 0; i < nb; i++) _oneOffBills.Add((r.ReadString(), new Bill(r.ReadInt32(), r.ReadString(), r.ReadInt64())));
        ApplyLeaseJobs();
    }
}
