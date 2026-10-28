namespace ToyCity.Sim;

/// <summary>Mechanics each player opts into. A mechanic is on only while every player who owns a seat allows it.</summary>
public enum Mechanic : byte { Bribery = 0, ExclusiveRights = 1, Sabotage = 2, HostileEvictions = 3, AbsurdDisasters = 4, TargetedCrime = 5 }

/// <summary>A player switches a mechanic on or off for themselves.</summary>
public sealed record SetConsentAction(string Owner, Mechanic Mechanic, bool Allowed) : SimAction;

/// <summary>One mechanic in the settings table: each owner's answer, the result, and who is blocking it.</summary>
public sealed record ConsentRow(Mechanic Mechanic, string Name, string Note, IReadOnlyList<bool> ByOwner, bool Effective, IReadOnlyList<string> Blockers);

public sealed partial class World
{
    /// <summary>What a player allows before they say otherwise: only what the game did before consent existed.</summary>
    static readonly Mechanic[] DefaultAllowed = { Mechanic.AbsurdDisasters };

    public static readonly Mechanic[] AllMechanics = Enum.GetValues<Mechanic>();

    /// <summary>Each player's allowances, by name. Players not listed have the defaults.</summary>
    readonly SortedDictionary<string, HashSet<Mechanic>> _consent = new(StringComparer.Ordinal);

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
        Mechanic.Bribery => "Side games may bribe a district council for a better rating, and may get caught.",
        Mechanic.ExclusiveRights => "A side game may buy a year in which only its buses stop in a district.",
        Mechanic.HostileEvictions => "The mayor may end a lease in good standing, after notice and with six months' rent as compensation.",
        Mechanic.AbsurdDisasters => "The Clockwork Colossus may walk in by itself.",
        _ => "Not in the game yet.",
    };

    /// <summary>The players who own a seat here, in name order. Computer-run seats count for their owner; unowned ones don't.</summary>
    public List<string> Owners() =>
        Seats.Where(s => s.Owner.Length > 0).Select(s => s.Owner).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();

    public bool Allows(string owner, Mechanic m) => _consent.TryGetValue(owner, out var set) ? set.Contains(m) : DefaultAllowed.Contains(m);

    /// <summary>Most restrictive owner wins.</summary>
    public bool Allowed(Mechanic m) => Owners().All(o => Allows(o, m));

    public List<string> Blockers(Mechanic m) => Owners().Where(o => !Allows(o, m)).ToList();

    public List<ConsentRow> ConsentTable()
    {
        var owners = Owners();
        return AllMechanics.Select(m => new ConsentRow(m, MechanicName(m), MechanicNote(m), owners.Select(o => Allows(o, m)).ToList(), Allowed(m), Blockers(m))).ToList();
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

    /// <summary>Changes whenever the settings table would, for the server's change detection.</summary>
    public string ConsentSig() => string.Join(",", Owners().Select(o => $"{o}:{AllMechanics.Sum(m => Allows(o, m) ? 1 << (int)m : 0)}"));

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
    }
}
