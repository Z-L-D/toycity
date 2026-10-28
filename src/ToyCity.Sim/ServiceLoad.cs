namespace ToyCity.Sim;

/// <summary>
/// How many people a school or clinic serves against what it was built for. Each home sends its pupils (of the
/// school's ages) or patients to the nearest working building of the kind whose reach covers it. Worked out when
/// asked, from the homes and their residents, so it is never saved and always matches the People panel.
/// </summary>
public sealed partial class World
{
    /// <summary>Residents who need a doctor in a month, per thousand.</summary>
    public const int PatientsPerMille = 20;

    public sealed record Load(int Served, int Capacity, string Unit);

    /// <summary>The building's load, or null for types that don't count one.</summary>
    public Load? ServiceLoad(Building b)
    {
        var t = TypeOf(b);
        if (t.Serves == 0) return null;
        bool school = t.Category == Category.Education;
        string unit = school ? $"pupils (ages {t.MinAge}–{t.MaxAge})" : "patients a month";
        if (!Serving(b)) return new Load(0, t.Serves, unit);
        long served = 0;
        foreach (var home in Buildings)
        {
            if (home == null || home.Residents == 0 || !home.Operating) continue;
            if (Gap(b, home) > EffectiveRadius(b) || NearestServing(home, t) != b) continue;
            served += school ? Pupils(home, t.MinAge, t.MaxAge) : home.Residents * (long)PatientsPerMille;
        }
        if (!school) served = (served + 500) / 1000;
        return new Load((int)served, t.Serves, unit);
    }

    /// <summary>Distance from a service building to a home's cell, as coverage measures it.</summary>
    static int Gap(Building s, Building home) => DistToRect(home.X, home.Y, s.X, s.Y, s.W, s.H);

    bool Serving(Building b) => b.Operating && b.Funding > 0 && (b.Powered || TypeOf(b).PowerKw == 0);

    /// <summary>The nearest working building that serves the same people as type t and reaches the home; ties go to the lower id.</summary>
    Building? NearestServing(Building home, BuildingType t)
    {
        Building? best = null;
        int bestGap = int.MaxValue;
        foreach (var o in Buildings)
        {
            if (o == null) continue;
            var ot = TypeOf(o);
            if (ot.Serves == 0 || ot.Category != t.Category || ot.MinAge != t.MinAge || ot.MaxAge != t.MaxAge || !Serving(o)) continue;
            int gap = Gap(o, home);
            if (gap > EffectiveRadius(o) || gap >= bestGap) continue;
            best = o;
            bestGap = gap;
        }
        return best;
    }

    /// <summary>Children of a home aged min to max, counted one by one as the People panel lists them.</summary>
    int Pupils(Building home, int min, int max)
    {
        var since = SinceOf(home);
        int n = 0;
        for (int s = 0; s < home.Residents; s++)
            if (RoleOf(home, s, since[s]) == CitizenRole.Child && AgeOf(home, s, since[s]) is var age && age >= min && age <= max) n++;
        return n;
    }

    /// <summary>A line for the building's panels: "Teaching 120 of 400 pupils (ages 5–11)."</summary>
    public string LoadLine(Building b)
    {
        if (ServiceLoad(b) is not { } l) return "";
        string verb = TypeOf(b).Category == Category.Education ? "Teaching" : "Seeing";
        string over = l.Served > l.Capacity ? $" Over capacity by {l.Served - l.Capacity:N0}: another one nearby would share the load." : "";
        return $"{verb} {l.Served:N0} of {l.Capacity:N0} {l.Unit}.{over}";
    }
}
