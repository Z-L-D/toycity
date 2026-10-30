using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// A player leaves the city for good, or the mayor cuts them loose: the player takes a copy of the city as it is now,
/// and the city carries on without them. The player may always leave; the mayor may remove them when one of their
/// leases could be ended anyway, when hostile evictions are on between the two, or when they hold no lots here.
/// </summary>
public sealed record DelinkAction(string Player) : SimAction;

/// <summary>
/// The copy's side of a de-link: the copy's first action, logged at the tick the copy was taken, so a replay of the
/// copy arrives at the same state. The departing player becomes its mayor and everyone else's seats go to the computer.
/// </summary>
public sealed record ForkAction(string Player, string HomeName) : SimAction;

/// <summary>What a de-link settled: the leases it ended, what was written off and paid, and when the two may link again.</summary>
public sealed record DelinkRecord(long Tick, string Player, string Mayor, string By, IReadOnlyList<string> Games, int LeasesEnded, long WrittenOff, long Paid, long CooldownUntil);

public sealed partial class World
{
    /// <summary>Days before a de-linked player and the mayor can share a city again.</summary>
    public const int RelinkCooldownDays = 90;

    public readonly List<DelinkRecord> Delinks = new();

    /// <summary>Set by the server: a de-link also builds the departing player's copy, into <see cref="Copies"/>.</summary>
    public bool MakeCopies;

    /// <summary>Copies made by de-links since the caller last took them; the caller saves and disposes them.</summary>
    public readonly List<(string Player, World Copy)> Copies = new();

    /// <summary>The tick until which two players may not share a city, after one de-linked from the other; 0 if none.</summary>
    public long CooldownUntil(string a, string b) =>
        Delinks.Where(d => (d.Player == a && d.Mayor == b) || (d.Player == b && d.Mayor == a)).Select(d => d.CooldownUntil).DefaultIfEmpty(0).Max();

    /// <summary>Why a player can't join in with the mayor yet, or null when they can.</summary>
    string? Cooldown(string player)
    {
        long until = CooldownUntil(player, MayorSeat.Owner);
        return until > Tick ? $"{player} de-linked from {MayorSeat.Owner}'s city; they can share a city again in {until - Tick} day{(until - Tick == 1 ? "" : "s")}." : null;
    }

    /// <summary>Whether a player has any part in the city: a seat, a deputy's portfolio or a consent setting.</summary>
    bool HasPart(string player) => Seats.Any(s => s.Owner == player) || _deputies.ContainsKey(player) || _consent.ContainsKey(player);

    IEnumerable<Lease> LeasesOf(string player) =>
        Leases.Where(l => SeatOf(l.GameId)?.Owner == player);

    /// <summary>Why the mayor may not cut a player loose, or null when they may.</summary>
    public string? DelinkGrounds(string player)
    {
        var leases = LeasesOf(player).ToList();
        if (leases.Count == 0 || leases.Any(l => l.Terminable)) return null;
        if (AllowedBetween(Mechanic.HostileEvictions, MayorSeat.Owner, player)) return null;
        return $"{player}'s leases are in good standing or still inside their cure period. {WhyOff(Mechanic.HostileEvictions, MayorSeat.Owner, player)}".TrimEnd();
    }

    /// <summary>Every player with a part here besides the mayor, with why the mayor may not cut them loose (null when they may).</summary>
    public List<(string Player, string? Why)> DelinkOptions() =>
        Seats.Select(s => s.Owner).Concat(_deputies.Keys).Concat(_consent.Keys)
            .Where(p => p.Length > 0 && p != MayorSeat.Owner).Distinct().OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (p, DelinkGrounds(p))).ToList();

    /// <summary>The rule that lets the mayor cut a player loose, said for that player.</summary>
    public string DelinkRule(string player)
    {
        var leases = LeasesOf(player).ToList();
        if (leases.Count == 0) return $"{player} holds no lots here, so the mayor may de-link them at any time.";
        if (leases.Any(l => l.Terminable)) return $"A lease of {player}'s has been in bad standing past its cure period, so the mayor may de-link them.";
        return $"Hostile evictions are on between the mayor and {player}, so the mayor may de-link them.";
    }

    string? ForbiddenDelink(DelinkAction a, string by)
    {
        if (by == a.Player) return null;
        if (by != MayorSeat.Owner) return $"Only {a.Player} or the mayor can de-link {a.Player}.";
        return DelinkGrounds(a.Player);
    }

    void ApplyDelink(DelinkAction a)
    {
        string player = a.Player, mayor = MayorSeat.Owner;
        if (player.Length == 0 || !HasPart(player)) { Reject($"{(player.Length == 0 ? "Nobody" : player)} has no part in this city."); return; }
        if (player == mayor) { Reject("The mayor can't de-link from their own city. Hand the city to someone else first."); return; }
        if (_actor == mayor && MayorSeat.Level != OperatorLevel.Manual) { Reject("Take the city back before de-linking anyone."); return; }

        // The copy is the city as it is now, before anything is settled here.
        if (MakeCopies) Copies.Add((player, Fork(player)));

        var games = Seats.Where(s => s.Owner == player && s.Id != Seat.Mayor).Select(s => s.Id).ToList();
        long writtenOff = 0, paid = 0;
        int ended = 0;
        foreach (var lease in LeasesOf(player).ToList())
        {
            writtenOff += lease.Arrears;
            // A hostile eviction under way was already owed its compensation.
            if (lease.EvictMonths > 0) { long comp = Compensation(lease); Pay(lease.GameId, "Compensation", comp); paid += comp; }
            foreach (var o in OffersOn(lease.Id).Where(o => o.Status == OfferStatus.Open)) { o.Status = OfferStatus.Withdrawn; o.Note = $"{player} de-linked."; }
            EndLease(lease, $"{GameName(lease.GameId)} left with {player}: its lease on {{0}} has ended.");
            ended++;
        }
        foreach (var s in Seats.Where(s => s.Owner == player))
        {
            s.Owner = "";
            s.Level = OperatorLevel.Caretaker;
            s.AutoAway = false;
            s.Away = null;
            s.Report = null;
        }
        ForgetPlayer(player);
        long until = Tick + RelinkCooldownDays;
        Delinks.Add(new DelinkRecord(Tick, player, mayor, _actor, games, ended, writtenOff, paid, until));
        string settled = ended == 0 ? "" : $" {ended} lease{(ended == 1 ? "" : "s")} ended{(writtenOff > 0 ? $", {Money(writtenOff)} owed written off" : "")}{(paid > 0 ? $", {Money(paid)} compensation paid" : "")}.";
        Notices.Add(new SimNotice($"{player} {(_actor == player ? "left" : "was de-linked from")} the city and took a copy of it as it was.{settled} {player} and {mayor} can't share a city for {RelinkCooldownDays} days.", "warning"));
    }

    /// <summary>Drops a player's settings, portfolio and pacts; their seats are already handed on.</summary>
    void ForgetPlayer(string player)
    {
        _consent.Remove(player);
        _deputies.Remove(player);
        _pacts.RemoveAll(p => p.A == player || p.B == player);
        _offers.RemoveAll(o => o.From == player || o.To == player);
    }

    /// <summary>The departing player's copy of the city as it is now, with its log and the fork that makes it theirs.</summary>
    World Fork(string player)
    {
        var copy = FromSnapshot(Content, Snapshot());
        copy.Log.AddRange(Log.Take(_logging));
        var fork = new ForkAction(player, Name);
        copy.Log.Add(new LoggedAction(Tick, fork));
        copy.ApplyFork(fork);
        return copy;
    }

    void ApplyFork(ForkAction a)
    {
        string mayor = MayorSeat.Owner;
        string name = $"{a.HomeName} ({a.Player})";
        Name = name.Length > 60 ? name[..60] : name;
        foreach (var s in Seats)
        {
            if (s.Owner == a.Player) continue;
            s.Owner = "";
            s.Level = OperatorLevel.Caretaker;
            s.AutoAway = false;
            s.Away = null;
            s.Report = null;
        }
        var ms = MayorSeat;
        ms.Owner = a.Player;
        ms.Level = OperatorLevel.Manual;
        foreach (var other in _consent.Keys.Where(k => k != a.Player).ToList()) _consent.Remove(other);
        _deputies.Clear();
        _pacts.Clear();
        _offers.Clear();
        CouncilOn = false;
        foreach (var p in Proposals.Where(p => p.Status == ProposalStatus.Open)) p.Status = ProposalStatus.Failed;
        foreach (var o in Offers.Where(o => o.Status == OfferStatus.Open)) { o.Status = OfferStatus.Withdrawn; o.Note = $"{a.Player} de-linked."; }
        var games = Seats.Where(s => s.Owner == a.Player && s.Id != Seat.Mayor).Select(s => s.Id).ToList();
        if (mayor.Length > 0) Delinks.Add(new DelinkRecord(Tick, a.Player, mayor, a.Player, games, 0, 0, 0, Tick + RelinkCooldownDays));
        Notices.Add(new SimNotice($"This is {a.Player}'s copy of {a.HomeName}, taken when they de-linked. Everyone else's seats are on caretaker.", "info"));
    }

    void WriteDelinks(BinaryWriter w)
    {
        w.Write(Delinks.Count);
        foreach (var d in Delinks)
        {
            w.Write(d.Tick); w.Write(d.Player); w.Write(d.Mayor); w.Write(d.By);
            w.Write(d.Games.Count);
            foreach (var g in d.Games) w.Write(g);
            w.Write(d.LeasesEnded); w.Write(d.WrittenOff); w.Write(d.Paid); w.Write(d.CooldownUntil);
        }
    }

    void ReadDelinks(BinaryReader r)
    {
        Delinks.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            long tick = r.ReadInt64();
            string player = r.ReadString(), mayor = r.ReadString(), by = r.ReadString();
            var games = new List<string>();
            int ng = r.ReadInt32();
            for (int k = 0; k < ng; k++) games.Add(r.ReadString());
            Delinks.Add(new DelinkRecord(tick, player, mayor, by, games, r.ReadInt32(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64()));
        }
    }
}
