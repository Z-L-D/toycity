namespace ToyCity.Sim;

/// <summary>
/// Mechanics each player opts into. One that reaches everyone (an absurd disaster, a district closed to rivals) is on
/// only while every player who owns a seat allows it. One whose effects stay between two players (a bribe between an
/// operator and the mayor, an eviction) needs only those two to allow it, or a pact between them. Either way the host
/// and the city's owner can forbid it outright.
/// </summary>
public enum Mechanic : byte { Bribery = 0, ExclusiveRights = 1, Sabotage = 2, HostileEvictions = 3, AbsurdDisasters = 4, TargetedCrime = 5 }

/// <summary>Who has to agree to a mechanic: every seat owner, or just the two players it happens between.</summary>
public enum ConsentScope : byte { Everyone = 0, Pair = 1 }

/// <summary>A player switches a mechanic on or off for themselves.</summary>
public sealed record SetConsentAction(string Owner, Mechanic Mechanic, bool Allowed) : SimAction;

/// <summary>The city's owner forbids a mechanic in this city whatever the players say, or lifts that.</summary>
public sealed record SetCityRuleAction(Mechanic Mechanic, bool Forbid) : SimAction;

/// <summary>The server's own minimums: the mechanics its host forbids. Sent by the server whenever a city arrives.</summary>
public sealed record SetHostRulesAction(IReadOnlyList<Mechanic> Forbidden) : SimAction
{
    public bool Equals(SetHostRulesAction? o) => o != null && By == o.By && Forbidden.SequenceEqual(o.Forbidden);
    public override int GetHashCode() => Forbidden.Aggregate(By.GetHashCode(), (h, m) => h * 31 + (int)m);
}

/// <summary>
/// A pact: the sender offers another player a mechanic between the two of them, whatever either allows in general.
/// The other accepts with <see cref="AcceptPactAction"/>. Either can end it, or withdraw the offer, with <see cref="EndPactAction"/>.
/// </summary>
public sealed record OfferPactAction(string With, Mechanic Mechanic) : SimAction;
public sealed record AcceptPactAction(string From, Mechanic Mechanic) : SimAction;
public sealed record EndPactAction(string With, Mechanic Mechanic) : SimAction;

/// <summary>Two players and a mechanic, the names in order.</summary>
public readonly record struct Pact(string A, string B, Mechanic Mechanic)
{
    public static Pact Of(string x, string y, Mechanic m) => string.CompareOrdinal(x, y) <= 0 ? new(x, y, m) : new(y, x, m);
    public bool Has(string p) => A == p || B == p;
    public string Other(string p) => A == p ? B : A;
}

/// <summary>An offer made and not yet taken up.</summary>
public readonly record struct PactOffer(string From, string To, Mechanic Mechanic);

/// <summary>
/// One mechanic in the settings table: each owner's answer, the result for everyone, who is blocking it, its scope,
/// and whether the host or the city's rules forbid it.
/// </summary>
public sealed record ConsentRow(Mechanic Mechanic, string Name, string Note, IReadOnlyList<bool> ByOwner, bool Effective, IReadOnlyList<string> Blockers,
    ConsentScope Scope, bool HostForbids, bool CityForbids);

public sealed partial class World
{
    /// <summary>What a player allows before they say otherwise: only what the game did before consent existed.</summary>
    static readonly Mechanic[] DefaultAllowed = { Mechanic.AbsurdDisasters };

    public static readonly Mechanic[] AllMechanics = Enum.GetValues<Mechanic>();

    /// <summary>Each player's allowances, by name. Players not listed have the defaults.</summary>
    readonly SortedDictionary<string, HashSet<Mechanic>> _consent = new(StringComparer.Ordinal);
    readonly SortedSet<Mechanic> _hostForbids = new(), _cityForbids = new();
    readonly List<Pact> _pacts = new();
    readonly List<PactOffer> _offers = new();

    public IReadOnlyList<Pact> Pacts => _pacts;
    public IReadOnlyList<PactOffer> PactOffers => _offers;

    public static string MechanicName(Mechanic m) => m switch
    {
        Mechanic.Bribery => "Bribery",
        Mechanic.ExclusiveRights => "Exclusive rights",
        Mechanic.Sabotage => "Sabotage of other players",
        Mechanic.HostileEvictions => "Hostile evictions",
        Mechanic.AbsurdDisasters => "Absurd disasters",
        _ => "Crime aimed at players' lots",
    };

    static string MechanicNote(Mechanic m) => m switch
    {
        Mechanic.Bribery => "Side games may bribe a district council for a better rating, and may get caught. Between the operator and the mayor.",
        Mechanic.ExclusiveRights => "A side game may buy a year in which only its buses stop in a district. Shuts out every rival, so everyone must agree.",
        Mechanic.Sabotage => "One player may sabotage another's lots. Between the two of them.",
        Mechanic.HostileEvictions => "The mayor may end a lease in good standing, after notice and with six months' rent as compensation. Between the mayor and the operator.",
        Mechanic.AbsurdDisasters => "The Clockwork Colossus may walk in by itself. It walks over everyone's lots, so everyone must agree.",
        _ => "Not in the game yet.",
    };

    public static ConsentScope ScopeOf(Mechanic m) => m is Mechanic.ExclusiveRights or Mechanic.AbsurdDisasters ? ConsentScope.Everyone : ConsentScope.Pair;

    /// <summary>The players who own a seat here, in name order. Computer-run seats count for their owner; unowned ones don't.</summary>
    public List<string> Owners() =>
        Seats.Where(s => s.Owner.Length > 0).Select(s => s.Owner).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();

    public bool Allows(string owner, Mechanic m) => _consent.TryGetValue(owner, out var set) ? set.Contains(m) : DefaultAllowed.Contains(m);

    /// <summary>What a player has said, if anything: null for a player who has never changed a setting here.</summary>
    public IReadOnlySet<Mechanic>? ConsentOf(string owner) => _consent.TryGetValue(owner, out var set) ? set : null;

    public bool HostForbids(Mechanic m) => _hostForbids.Contains(m);
    public bool CityForbids(Mechanic m) => _cityForbids.Contains(m);

    /// <summary>On for everyone: neither the host nor the city forbids it, and every seat owner allows it.</summary>
    public bool Allowed(Mechanic m) => !HostForbids(m) && !CityForbids(m) && Owners().All(o => Allows(o, m));

    /// <summary>
    /// On between two players (either may be empty for a computer-run seat, which never objects). A mechanic that
    /// reaches everyone still needs everyone; one between two needs only them, or a pact.
    /// </summary>
    public bool AllowedBetween(Mechanic m, string a, string b)
    {
        if (HostForbids(m) || CityForbids(m)) return false;
        if (ScopeOf(m) == ConsentScope.Everyone) return Allowed(m);
        if (a.Length > 0 && b.Length > 0 && _pacts.Contains(Pact.Of(a, b, m))) return true;
        return (a.Length == 0 || Allows(a, m)) && (b.Length == 0 || Allows(b, m));
    }

    public List<string> Blockers(Mechanic m) => Owners().Where(o => !Allows(o, m)).ToList();

    /// <summary>Why a mechanic is off between two players (or for everyone, with no names), or null when it's on.</summary>
    public string? WhyOff(Mechanic m, string a = "", string b = "")
    {
        bool everyone = ScopeOf(m) == ConsentScope.Everyone || (a.Length == 0 && b.Length == 0);
        if (everyone ? Allowed(m) : AllowedBetween(m, a, b)) return null;
        string name = MechanicName(m);
        if (HostForbids(m)) return $"{name} is forbidden on this server by its host.";
        if (CityForbids(m)) return $"{name} is forbidden in this city by its rules.";
        var who = everyone ? Blockers(m) : new[] { a, b }.Where(p => p.Length > 0 && !Allows(p, m)).Distinct().ToList();
        return $"{name} is switched off: {string.Join(", ", who)} won't allow it.";
    }

    public List<ConsentRow> ConsentTable()
    {
        var owners = Owners();
        return AllMechanics.Select(m => new ConsentRow(m, MechanicName(m), MechanicNote(m), owners.Select(o => Allows(o, m)).ToList(), Allowed(m), Blockers(m),
            ScopeOf(m), HostForbids(m), CityForbids(m))).ToList();
    }

    /// <summary>
    /// Tightening applies to new actions at once; anything already under way (exclusive rights, an eviction) runs out
    /// rather than being reversed.
    /// </summary>
    void ApplySetConsent(SetConsentAction a)
    {
        if (a.Owner.Length is 0 or > 40) { Reject("A player needs a name to set what they allow."); return; }
        if (!Enum.IsDefined(a.Mechanic)) { Reject("There is no such setting."); return; }
        if (!_consent.TryGetValue(a.Owner, out var set)) _consent[a.Owner] = set = new HashSet<Mechanic>(DefaultAllowed);
        bool was = Allowed(a.Mechanic);
        if (a.Allowed) set.Add(a.Mechanic); else set.Remove(a.Mechanic);
        bool now = Allowed(a.Mechanic);
        if (was != now)
            Notices.Add(new SimNotice($"{MechanicName(a.Mechanic)} is now {(now ? "on" : "off")} in this city.", "info"));
    }

    void ApplySetCityRule(SetCityRuleAction a)
    {
        if (!Enum.IsDefined(a.Mechanic)) { Reject("There is no such setting."); return; }
        if (a.Forbid ? !_cityForbids.Add(a.Mechanic) : !_cityForbids.Remove(a.Mechanic)) return;
        Notices.Add(new SimNotice(a.Forbid ? $"{MechanicName(a.Mechanic)} is now forbidden in this city, whatever the players allow." : $"The city no longer forbids {MechanicName(a.Mechanic).ToLowerInvariant()}: it's up to the players.", "info"));
    }

    void ApplySetHostRules(SetHostRulesAction a)
    {
        _hostForbids.Clear();
        foreach (var m in a.Forbidden) if (Enum.IsDefined(m)) _hostForbids.Add(m);
    }

    void ApplyOfferPact(OfferPactAction a)
    {
        string me = a.By;
        if (me.Length == 0 || a.With.Length == 0 || a.With == me) { Reject("A pact is between you and one other player."); return; }
        if (ScopeOf(a.Mechanic) != ConsentScope.Pair) { Reject($"{MechanicName(a.Mechanic)} reaches everyone, so it can't be agreed between two players."); return; }
        if (!Owners().Contains(a.With)) { Reject($"{a.With} doesn't own a seat in this city."); return; }
        if (_pacts.Contains(Pact.Of(me, a.With, a.Mechanic))) { Reject($"You and {a.With} already have that pact."); return; }
        // An offer the other side already made is taken up instead.
        if (_offers.Remove(new PactOffer(a.With, me, a.Mechanic))) { MakePact(a.With, me, a.Mechanic); return; }
        var offer = new PactOffer(me, a.With, a.Mechanic);
        if (_offers.Contains(offer)) return;
        _offers.Add(offer);
        Notices.Add(new SimNotice($"{me} offers you a pact: {MechanicName(a.Mechanic).ToLowerInvariant()} between the two of you. See House rules.", "info", To: a.With));
        Notices.Add(new SimNotice($"Offered {a.With} a pact on {MechanicName(a.Mechanic).ToLowerInvariant()}.", "info", To: me));
    }

    void ApplyAcceptPact(AcceptPactAction a)
    {
        if (!_offers.Remove(new PactOffer(a.From, a.By, a.Mechanic))) { Reject($"{a.From} hasn't offered you that pact, or has withdrawn it."); return; }
        MakePact(a.From, a.By, a.Mechanic);
    }

    void MakePact(string from, string to, Mechanic m)
    {
        _pacts.Add(Pact.Of(from, to, m));
        _pacts.Sort((x, y) => string.CompareOrdinal($"{x.A}/{x.B}/{(int)x.Mechanic}", $"{y.A}/{y.B}/{(int)y.Mechanic}"));
        Notices.Add(new SimNotice($"{from} and {to} have a pact: {MechanicName(m).ToLowerInvariant()} between them, whatever they allow in general.", "info"));
    }

    void ApplyEndPact(EndPactAction a)
    {
        if (_offers.Remove(new PactOffer(a.By, a.With, a.Mechanic)) || _offers.Remove(new PactOffer(a.With, a.By, a.Mechanic))) return;
        if (!_pacts.Remove(Pact.Of(a.By, a.With, a.Mechanic))) { Reject($"You and {a.With} have no such pact."); return; }
        Notices.Add(new SimNotice($"{a.By} ended the pact with {a.With} on {MechanicName(a.Mechanic).ToLowerInvariant()}.", "info"));
    }

    /// <summary>Changes whenever the settings table would, for the server's change detection.</summary>
    public string ConsentSig() => string.Join(",", Owners().Select(o => $"{o}:{AllMechanics.Sum(m => Allows(o, m) ? 1 << (int)m : 0)}"))
        + $"|{string.Join(",", _hostForbids)}|{string.Join(",", _cityForbids)}|{string.Join(",", _pacts)}|{string.Join(",", _offers)}";

    void WriteConsent(BinaryWriter w)
    {
        w.Write(_consent.Count);
        foreach (var (owner, set) in _consent)
        {
            w.Write(owner);
            int bits = 0;
            foreach (var m in set) bits |= 1 << (int)m;
            w.Write(bits);
        }
        w.Write(Bits(_hostForbids));
        w.Write(Bits(_cityForbids));
        w.Write(_pacts.Count);
        foreach (var p in _pacts) { w.Write(p.A); w.Write(p.B); w.Write((byte)p.Mechanic); }
        w.Write(_offers.Count);
        foreach (var o in _offers) { w.Write(o.From); w.Write(o.To); w.Write((byte)o.Mechanic); }
    }

    void ReadConsent(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string owner = r.ReadString();
            int bits = r.ReadInt32();
            _consent[owner] = AllMechanics.Where(m => (bits & (1 << (int)m)) != 0).ToHashSet();
        }
        _hostForbids.Clear(); _cityForbids.Clear(); _pacts.Clear(); _offers.Clear();
        foreach (var m in ReadMechanics(r)) _hostForbids.Add(m);
        foreach (var m in ReadMechanics(r)) _cityForbids.Add(m);
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++) _pacts.Add(new Pact(r.ReadString(), r.ReadString(), (Mechanic)r.ReadByte()));
        int no = r.ReadInt32();
        for (int i = 0; i < no; i++) _offers.Add(new PactOffer(r.ReadString(), r.ReadString(), (Mechanic)r.ReadByte()));
    }

    static IEnumerable<Mechanic> ReadMechanics(BinaryReader r)
    {
        int bits = r.ReadInt32();
        return AllMechanics.Where(m => (bits & (1 << (int)m)) != 0).ToList();
    }

    static int Bits(IEnumerable<Mechanic> ms) => ms.Aggregate(0, (b, m) => b | 1 << (int)m);
}
