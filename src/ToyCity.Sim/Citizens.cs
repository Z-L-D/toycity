using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace ToyCity.Sim;

public enum CitizenRole : byte { Worker = 0, JobSeeker = 1, Child = 2, Retiree = 3 }

/// <summary>
/// One resident. Homes keep their people as counts; a citizen is the same household seen one person at a time:
/// slots below the home's labour force are of working age, the rest are children or retirees, and the employed
/// slots map onto the home's job assignments in order. So citizens always add up to the city's totals.
/// </summary>
public sealed record Citizen(
    long Id, int HomeId, int Slot, string Name, int Age, CitizenRole Role, int SiteId, bool ByTransit, int CommuteMinutes, int SinceMonth);

public sealed partial class World
{
    /// <summary>Chance in a thousand that a resident moves away in a given month, besides ageing out.</summary>
    public const int MovePerMille = 8;

    public int MonthIndex => (int)(Tick / DaysPerMonth);

    /// <summary>
    /// A stable hash of the world seed and some numbers. Life events and their timing come from this, not from
    /// <see cref="Rng"/>, so they never shift the rest of the simulation's random draws.
    /// </summary>
    internal ulong Hash(int a, int b, int c, int d)
    {
        Span<int> v = stackalloc int[4] { a, b, c, d };
        return XxHash64.HashToUInt64(MemoryMarshal.AsBytes(v), (long)Seed);
    }

    /// <summary>A citizen's id: home, slot and the month they moved in, which together name one person.</summary>
    public static long CitizenId(int home, int slot, int since) => ((long)home << 32) | ((long)(slot & 0xFFFF) << 16) | (uint)(since & 0xFFFF);

    /// <summary>
    /// The day this month a home looks for work again, spread by hash rather than by id so households don't all
    /// move in lockstep.
    /// </summary>
    public int RematchDay(Building home) => (int)(Hash(home.Id, MonthIndex, 17, 0) % DaysPerMonth);

    /// <summary>When each resident moved in (month index). Set when the building is added; a query never changes it.</summary>
    static int[] SinceOf(Building home) =>
        home.Since is { } s && s.Length == home.Residents ? s : new int[home.Residents];

    CitizenRole RoleOf(Building home, int slot, int since) =>
        slot < LaborOf(home) ? CitizenRole.Worker : Hash(home.Id, slot, since, 1) % 2 == 0 ? CitizenRole.Child : CitizenRole.Retiree;

    int AgeOf(Building home, int slot, int since)
    {
        var role = RoleOf(home, slot, since);
        ulong h = Hash(home.Id, slot, since, 2);
        int start = role switch { CitizenRole.Child => (int)(h % 6), CitizenRole.Retiree => 65 + (int)(h % 15), _ => 18 + (int)(h % 40) };
        return start + Math.Max(0, MonthIndex - since) / MonthsPerYear;
    }

    /// <summary>Monthly: residents age; those who age out of their slot, or draw a move, leave and someone new moves in.</summary>
    void RunLifeEvents()
    {
        int month = MonthIndex;
        foreach (var home in Buildings)
        {
            if (home == null || !home.Operating || home.Residents == 0) continue;
            if (home.Since == null || home.Since.Length != home.Residents) { home.Since = new int[home.Residents]; Array.Fill(home.Since, month); }
            var since = home.Since;
            for (int s = 0; s < since.Length; s++)
            {
                var role = RoleOf(home, s, since[s]);
                int age = AgeOf(home, s, since[s]);
                bool agedOut = role switch { CitizenRole.Child => age >= 18, CitizenRole.Retiree => age >= 95, _ => age >= 65 };
                if (agedOut || Hash(home.Id, s, since[s], month) % 1000 < MovePerMille) since[s] = month;
            }
        }
    }

    /// <summary>The people of a home, in slot order.</summary>
    public List<Citizen> CitizensOf(Building home)
    {
        var list = new List<Citizen>();
        if (home.Residents == 0 || !home.Operating) return list;
        var since = SinceOf(home);
        int labor = LaborOf(home);
        // Employed slots take the home's assignments in order.
        var jobs = new List<Assignment>();
        foreach (var a in home.Assignments) for (int k = 0; k < a.Count; k++) jobs.Add(a);
        for (int s = 0; s < home.Residents; s++)
        {
            var role = RoleOf(home, s, since[s]);
            Assignment? job = s < labor && s < jobs.Count ? jobs[s] : null;
            if (role == CitizenRole.Worker && job == null) role = CitizenRole.JobSeeker;
            long id = CitizenId(home.Id, s, since[s]);
            list.Add(new Citizen(id, home.Id, s, Content.Voice.Name((ulong)id ^ Seed), AgeOf(home, s, since[s]), role,
                job?.SiteId ?? -1, job?.ByTransit ?? false, job?.Minutes ?? 0, since[s]));
        }
        return list;
    }
}
