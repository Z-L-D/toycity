using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>What a lease offer proposes: new terms, a buyout (the city pays the operator to leave), or a move to another lot.</summary>
public enum OfferKind { Terms = 0, Buyout = 1, Relocate = 2 }

public enum OfferStatus { Open = 0, Accepted = 1, Declined = 2, Countered = 3, Withdrawn = 4, Expired = 5, Failed = 6 }

/// <summary>One offer on a lease between the city and the side game that holds it, and what became of it.</summary>
public sealed class LeaseOffer
{
    public int Id, LeaseId, ToBuildingId = -1, CounterOf;
    public OfferKind Kind;
    /// <summary>True when the side game's operator made it; false when the city did.</summary>
    public bool FromOperator;
    /// <summary>Who made it: a player, or "" for the computer.</summary>
    public string By = "";
    public LeaseTerms? Terms;
    /// <summary>What the city pays the operator if it's accepted: the buyout price, or moving costs.</summary>
    public long Amount;
    public OfferStatus Status;
    public long Tick, Expires;
    /// <summary>Why it ended as it did, for both sides to read.</summary>
    public string Note = "";
}

/// <summary>
/// One side offers the other a deal on a lease: new terms, a buyout or a move. Counter names the open offer it answers.
/// FromOperator says which side the sender speaks for, since in solo play one player is both.
/// </summary>
public sealed record OfferLeaseAction(int LeaseId, bool FromOperator, OfferKind Kind, LeaseTerms? Terms, long Amount, int ToBuildingId, int CounterOf = 0) : SimAction;

/// <summary>The other side accepts or declines an offer; its maker declining it withdraws it.</summary>
public sealed record AnswerOfferAction(int OfferId, bool Accept) : SimAction;

/// <summary>One rung of the dispute ladder on a lease: whether it's open now, and the rule that says so.</summary>
public sealed record LadderStep(string Step, bool Open, string Rule);

/// <summary>
/// The dispute ladder between the city and a side game over a lease, gentlest first: negotiate new terms, buy the
/// operator out, move it to another lot, impose new terms after notice, evict. The first three need both sides to
/// agree; a side the computer plays answers by rule the next day. Each rung shows both players the rule behind it.
/// </summary>
public sealed partial class World
{
    public const int OfferDays = 30;
    /// <summary>What the computer running a side game wants for moving to another lot.</summary>
    public const long MovingCost = 1_000;

    public readonly List<LeaseOffer> Offers = new();
    int _nextOfferId = 1;

    public IEnumerable<LeaseOffer> OffersOn(int leaseId) => Offers.Where(o => o.LeaseId == leaseId);
    LeaseOffer? OpenOffer(int leaseId) => Offers.FirstOrDefault(o => o.LeaseId == leaseId && o.Status == OfferStatus.Open);

    /// <summary>Whether a player may speak for a side of a lease: its operator's owner, or the mayor or a deputy in its portfolio.</summary>
    bool Speaks(Lease lease, bool forOperator, string by)
    {
        if (by.Length == 0) return true;
        if (forOperator) return SeatOf(lease.GameId)?.Owner == by;
        if (MayorSeat.Owner == by) return true;
        var p = IsTransitLot(lease.Kind) ? Portfolio.Transit : Portfolio.Services;
        return _deputies.TryGetValue(by, out var mine) && (mine & p) != 0;
    }

    string? ForbiddenOffer(SimAction a, string by)
    {
        switch (a)
        {
            case OfferLeaseAction o:
            {
                var lease = LeaseById(o.LeaseId);
                if (lease == null || Speaks(lease, o.FromOperator, by)) return null;
                return o.FromOperator ? $"Only whoever runs {GameName(lease.GameId)} can make offers for it."
                    : $"Only the mayor ({MayorSeat.Owner}) or a deputy for {(IsTransitLot(lease.Kind) ? "transit" : "services")} can make the city's offers.";
            }
            case AnswerOfferAction o:
            {
                var offer = Offers.FirstOrDefault(x => x.Id == o.OfferId);
                if (offer == null || LeaseById(offer.LeaseId) is not { } lease) return null;
                return Speaks(lease, true, by) || Speaks(lease, false, by) ? null : "Only the city and the lease's operator deal over it.";
            }
        }
        return null;
    }

    static string Money(long n) => $"${n:N0}";

    public string DescribeOffer(LeaseOffer o, Lease lease)
    {
        var b = Buildings[lease.BuildingId];
        string where = b == null ? "its lot" : LotName(b);
        return o.Kind switch
        {
            OfferKind.Terms => $"new terms at {where}: {TermsText(o.Terms!)}",
            OfferKind.Buyout => $"a buyout of {GameName(lease.GameId)}'s lease on {where} for {Money(o.Amount)}",
            _ => $"moving {GameName(lease.GameId)} from {where} to {(o.ToBuildingId >= 0 && o.ToBuildingId < Buildings.Count && Buildings[o.ToBuildingId] is { } to ? LotName(to) : "another lot")}{(o.Amount > 0 ? $", the city paying {Money(o.Amount)} to move" : "")}",
        };
    }

    static string TermsText(LeaseTerms t) => t.Mode == LeaseMode.Business
        ? $"{Money(t.RentPerMonth)} a month and {t.TaxTenths / 10.0:0.#}% of fares"
        : $"a public service with a {Money(t.SubsidyPerMonth)} subsidy, fares capped at {Fare(t.FareCapCents)}";

    void ApplyOfferLease(OfferLeaseAction a)
    {
        var lease = LeaseById(a.LeaseId);
        if (lease == null) { Reject("That lease has ended."); return; }
        var b = Buildings[lease.BuildingId]!;
        var open = OpenOffer(lease.Id);
        if (a.CounterOf != 0)
        {
            if (open == null || open.Id != a.CounterOf) { Reject("That offer isn't on the table any more."); return; }
            if (open.FromOperator == a.FromOperator) { Reject("A counter-offer comes from the other side."); return; }
        }
        else if (open != null) { Reject($"There's already an offer on the table: {DescribeOffer(open, lease)}. Answer it, counter it or withdraw it first.", b.X, b.Y); return; }
        switch (a.Kind)
        {
            case OfferKind.Terms:
                if (a.Terms == null) { Reject("Say what the new terms are."); return; }
                if (CheckTerms(a.Terms) is { } err) { Reject(err); return; }
                break;
            case OfferKind.Buyout:
                if (a.Amount is < 0 or > 10_000_000) { Reject("A buyout runs from $0 to $10,000,000."); return; }
                break;
            case OfferKind.Relocate:
                if (a.Amount is < 0 or > 1_000_000) { Reject("Moving costs run from $0 to $1,000,000."); return; }
                if (RelocateProblem(lease, a.ToBuildingId) is { } why) { Reject(why, b.X, b.Y); return; }
                break;
            default: Reject("That isn't an offer."); return;
        }
        if (open != null) { open.Status = OfferStatus.Countered; open.Note = "Countered."; }
        var o = new LeaseOffer
        {
            Id = _nextOfferId++, LeaseId = lease.Id, Kind = a.Kind, FromOperator = a.FromOperator, By = _actor, Terms = a.Terms, Amount = a.Amount,
            ToBuildingId = a.Kind == OfferKind.Relocate ? a.ToBuildingId : -1, CounterOf = a.CounterOf, Tick = Tick, Expires = Tick + OfferDays,
        };
        Offers.Add(o);
        TrimOffers();
        string from = a.FromOperator ? GameName(lease.GameId) : "The city";
        Notices.Add(new SimNotice($"{from} {(a.CounterOf != 0 ? "counters with" : "offers")} {DescribeOffer(o, lease)}.", "info", b.X, b.Y));
    }

    string? RelocateProblem(Lease lease, int to)
    {
        if (to < 0 || to >= Buildings.Count || Buildings[to] is not { } t) return "Pick the lot to move to.";
        if (to == lease.BuildingId) return "That's the lot it's on.";
        if (LotKindOf(t) != lease.Kind) return $"A move has to be to another {(Buildings[lease.BuildingId] is { } b ? TypeOf(b).Name.ToLowerInvariant() : "lot of the same kind")}.";
        if (LeaseOn(to) is { } held) return $"{Cap(LotName(t))} is leased to {GameName(held.GameId)}.";
        return null;
    }

    void ApplyAnswerOffer(AnswerOfferAction a)
    {
        var o = Offers.FirstOrDefault(x => x.Id == a.OfferId);
        if (o == null || o.Status != OfferStatus.Open) { Reject("That offer isn't on the table any more."); return; }
        var lease = LeaseById(o.LeaseId);
        if (lease == null) { o.Status = OfferStatus.Failed; o.Note = "The lease had ended."; return; }
        // The maker answering their own offer withdraws it; in solo play the one player is both sides and answers.
        bool answerer = _actor.Length == 0 || Speaks(lease, !o.FromOperator, _actor);
        if (!answerer)
        {
            if (a.Accept) { Reject("The other side has to accept it."); return; }
            Close(o, lease, OfferStatus.Withdrawn, "Withdrawn.");
            return;
        }
        if (a.Accept) Accept(o, lease);
        else Close(o, lease, OfferStatus.Declined, "Declined.");
    }

    void Close(LeaseOffer o, Lease lease, OfferStatus status, string note)
    {
        o.Status = status;
        o.Note = note;
        var b = Buildings[lease.BuildingId];
        string who = status == OfferStatus.Withdrawn ? (o.FromOperator ? GameName(lease.GameId) : "The city")
            : o.FromOperator ? "The city" : GameName(lease.GameId);
        string verb = status switch { OfferStatus.Withdrawn => "withdrew", OfferStatus.Declined => "declined", OfferStatus.Expired => "let lapse", _ => "couldn't go ahead with" };
        bool why = status is OfferStatus.Declined or OfferStatus.Failed && note != "Declined.";
        Notices.Add(new SimNotice($"{who} {verb} {DescribeOffer(o, lease)}.{(why ? " " + note : "")}", "info", b?.X ?? -1, b?.Y ?? -1));
    }

    void Accept(LeaseOffer o, Lease lease)
    {
        var b = Buildings[lease.BuildingId]!;
        if (o.Amount > 0 && Funds < o.Amount)
        {
            Close(o, lease, OfferStatus.Failed, $"The city has {Money(Funds)} and the deal needs {Money(o.Amount)}.");
            return;
        }
        string what = DescribeOffer(o, lease);
        switch (o.Kind)
        {
            case OfferKind.Terms:
                // Agreed terms start at once: notice is for terms one side imposes.
                lease.Terms = o.Terms!;
                lease.PendingTerms = null;
                lease.PendingMonths = 0;
                break;
            case OfferKind.Buyout:
                Pay(lease.GameId, "Buyout", o.Amount);
                lease.EvictMonths = 0;
                EndLease(lease, $"The city bought out {GameName(lease.GameId)}'s lease on {{0}} for {Money(o.Amount)}.");
                break;
            case OfferKind.Relocate:
                if (RelocateProblem(lease, o.ToBuildingId) is { } why) { Close(o, lease, OfferStatus.Failed, why); return; }
                var to = Buildings[o.ToBuildingId]!;
                b.Jobs -= lease.Jobs;
                to.Jobs += lease.Jobs;
                lease.BuildingId = to.Id;
                lease.EvictMonths = 0;
                Pay(lease.GameId, "Moving costs", o.Amount);
                break;
        }
        o.Status = OfferStatus.Accepted;
        o.Note = "Accepted.";
        string who = o.FromOperator ? "The city" : GameName(lease.GameId);
        Notices.Add(new SimNotice($"{who} accepted {what}.", "good", b.X, b.Y));
    }

    /// <summary>The city pays a side game now, out of the treasury; the game gets it with its next month's bills.</summary>
    void Pay(string gameId, string label, long amount)
    {
        if (amount <= 0) return;
        Funds -= amount;
        _oneOffBills.Add((gameId, new Bill(0, label, -amount)));
    }

    void TrimOffers()
    {
        // Keep every open offer and the last few decided ones on each lease.
        var old = Offers.Where(o => o.Status != OfferStatus.Open).GroupBy(o => o.LeaseId).SelectMany(g => g.OrderByDescending(o => o.Id).Skip(4)).ToHashSet();
        Offers.RemoveAll(o => old.Contains(o) || o.Status != OfferStatus.Open && LeaseById(o.LeaseId) == null && o.Tick < Tick - OfferDays);
    }

    /// <summary>Daily: offers lapse after a month, and a side the computer plays answers offers by rule the day after they're made.</summary>
    void RunDisputes()
    {
        foreach (var o in Offers.Where(o => o.Status == OfferStatus.Open).ToList())
        {
            var lease = LeaseById(o.LeaseId);
            if (lease == null) { o.Status = OfferStatus.Failed; o.Note = "The lease had ended."; continue; }
            if (Tick >= o.Expires) { Close(o, lease, OfferStatus.Expired, "Nobody answered it within a month."); continue; }
            if (Tick <= o.Tick) continue;
            var seat = o.FromOperator ? MayorSeat : SeatOf(lease.GameId);
            // Only a side the computer plays answers by itself; a seat in stasis answers nothing.
            if (seat is { Level: OperatorLevel.Manual or OperatorLevel.Stasis }) continue;
            if (ComputerAnswer(o, lease) is { } no) Close(o, lease, OfferStatus.Declined, no);
            else Accept(o, lease);
        }
    }

    /// <summary>What the operator pays the city in a month on these terms, roughly; negative when the city pays it.</summary>
    static long Cost(LeaseTerms t, Lease l) => t.Mode == LeaseMode.Business ? t.RentPerMonth + t.TaxTenths * l.LastRevenue / 1000 : -t.SubsidyPerMonth;

    /// <summary>Null when the computer takes the offer; otherwise why it won't.</summary>
    string? ComputerAnswer(LeaseOffer o, Lease lease)
    {
        if (o.FromOperator)
        {
            // The city on automation: a deal no worse for the city than what it has, and it never pays more than an eviction would cost.
            return o.Kind switch
            {
                OfferKind.Terms when Cost(o.Terms!, lease) < Cost(lease.Terms, lease) => $"The computer running the city won't take less than it gets now ({Money(Cost(lease.Terms, lease))} a month).",
                OfferKind.Terms => null,
                OfferKind.Buyout when o.Amount > Compensation(lease) => $"The computer running the city pays at most {Money(Compensation(lease))} to end a lease, what an eviction would cost.",
                OfferKind.Relocate when o.Amount > 0 => "The computer running the city doesn't pay for moves the operator asks for.",
                _ => null,
            };
        }
        // The side game on automation: no worse terms, a buyout worth more than staying, a move with its costs paid.
        long keep = Compensation(lease) + 3 * lease.LastRevenue;
        return o.Kind switch
        {
            OfferKind.Terms when Cost(o.Terms!, lease) > Cost(lease.Terms, lease) => $"The computer running {GameName(lease.GameId)} won't pay more than it does now.",
            OfferKind.Terms => null,
            OfferKind.Buyout when o.Amount < keep => $"The computer running {GameName(lease.GameId)} wants at least {Money(keep)} to give the lot up.",
            OfferKind.Relocate when o.Amount < MovingCost => $"The computer running {GameName(lease.GameId)} moves when the city pays the {Money(MovingCost)} it costs.",
            _ => null,
        };
    }

    /// <summary>The free lots of the same kind a lease could move to.</summary>
    public IEnumerable<Building> MoveTargets(Lease lease) =>
        Buildings.Where(b => b != null && b.Id != lease.BuildingId && LotKindOf(b) == lease.Kind && LeaseOn(b.Id) == null)!;

    /// <summary>The ladder on a lease, gentlest first, with whether each rung is open now and the rule behind it.</summary>
    public List<LadderStep> DisputeLadder(Lease lease)
    {
        string op = SeatOf(lease.GameId)?.Owner ?? "";
        string who = op.Length == 0 || SeatOf(lease.GameId)!.Level != OperatorLevel.Manual ? $"the computer running {GameName(lease.GameId)}" : op;
        int free = MoveTargets(lease).Count();
        bool hostile = AllowedBetween(Mechanic.HostileEvictions, MayorSeat.Owner, op);
        var steps = new List<LadderStep>
        {
            new("Negotiate", true, $"Either side offers new terms; they start as soon as the other accepts. {Cap(who)} answers {(who == op ? "when they choose" : "the next day, taking any terms no worse for it")}."),
            new("Buy out", true, $"The city offers money to end the lease; it ends the day it's accepted. {(who == op ? "" : $"The computer takes {Money(Compensation(lease) + 3 * lease.LastRevenue)} or more.")}".TrimEnd()),
            new("Relocate", free > 0, free > 0
                ? $"Move the lease to one of {free} free lot{(free == 1 ? "" : "s")} of the same kind, keeping its terms; the city may offer moving costs{(who == op ? "" : $" (the computer wants {Money(MovingCost)})")}."
                : "There's no free lot of the same kind to move to."),
            new("Impose terms", MayorSeat.Level == OperatorLevel.Manual, $"The mayor sets new terms alone; they start after {lease.Terms.NoticeMonths} month{(lease.Terms.NoticeMonths == 1 ? "" : "s")}' notice."),
        };
        if (lease.Terminable) steps.Add(new("Evict", true, "The lease has been in bad standing past its cure period: the mayor may end it now, with nothing owed."));
        else if (hostile) steps.Add(new("Evict", lease.EvictMonths == 0, lease.EvictMonths > 0
            ? $"Notice given: it ends in {lease.EvictMonths} month{(lease.EvictMonths == 1 ? "" : "s")}, the city paying {Money(Compensation(lease))}."
            : $"Hostile evictions are on: the mayor may end it after {lease.Terms.NoticeMonths} months' notice, paying {Money(Compensation(lease))}."));
        else steps.Add(new("Evict", false, lease.BadStanding
            ? $"In bad standing: the mayor may end it if it isn't put right in {lease.CureMonthsLeft} month{(lease.CureMonthsLeft == 1 ? "" : "s")}."
            : $"Only a lease in bad standing past its cure period. {WhyOff(Mechanic.HostileEvictions, MayorSeat.Owner, op)}".TrimEnd()));
        // The last rung: the two part ways for good, each with their own copy of the city.
        string copy = $"takes a copy of the city as it is and leaves; the city keeps its name and everyone else, and the two can't share a city for {RelinkCooldownDays} days.";
        if (op.Length == 0) steps.Add(new("De-link", false, $"Nobody runs {GameName(lease.GameId)}, so there's nobody to de-link."));
        else if (DelinkGrounds(op) == null) steps.Add(new("De-link", true, $"The mayor may cut {op} loose, or {op} may leave: {op} {copy}"));
        else steps.Add(new("De-link", true, $"{op} may leave at any time: {op} {copy} The mayor may cut them loose only on the same grounds as an eviction."));
        return steps;
    }

    void WriteOffers(BinaryWriter w)
    {
        w.Write(_nextOfferId);
        w.Write(Offers.Count);
        foreach (var o in Offers)
        {
            w.Write(o.Id); w.Write(o.LeaseId); w.Write((byte)o.Kind); w.Write(o.FromOperator); w.Write(o.By); w.Write(o.Terms != null);
            if (o.Terms != null) WriteTerms(w, o.Terms);
            w.Write(o.Amount); w.Write(o.ToBuildingId); w.Write(o.CounterOf); w.Write((byte)o.Status); w.Write(o.Tick); w.Write(o.Expires); w.Write(o.Note);
        }
    }

    void ReadOffers(BinaryReader r)
    {
        _nextOfferId = r.ReadInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var o = new LeaseOffer { Id = r.ReadInt32(), LeaseId = r.ReadInt32(), Kind = (OfferKind)r.ReadByte(), FromOperator = r.ReadBoolean(), By = r.ReadString() };
            if (r.ReadBoolean()) o.Terms = ReadTerms(r);
            o.Amount = r.ReadInt64(); o.ToBuildingId = r.ReadInt32(); o.CounterOf = r.ReadInt32(); o.Status = (OfferStatus)r.ReadByte();
            o.Tick = r.ReadInt64(); o.Expires = r.ReadInt64(); o.Note = r.ReadString();
            Offers.Add(o);
        }
    }
}
