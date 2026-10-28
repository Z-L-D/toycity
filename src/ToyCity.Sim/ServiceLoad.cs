namespace ToyCity.Sim;

/// <summary>
/// How much a public building is doing against what it was built for, shown as its headline gauge: pupils for a
/// school, patients for a clinic, residents for a police or fire station, visitors for a park, the share of the
/// city's demand for a plant, pump or landfill, riders for a stop. Each home goes to the nearest working building of
/// the kind that reaches it, so nobody is counted twice. Worked out when asked, so it is never saved.
/// </summary>
public sealed partial class World
{
    /// <summary>Residents who need a doctor in a month, per thousand.</summary>
    public const int PatientsPerMille = 20;
    /// <summary>Residents in a park's reach who visit it on a given day, per thousand.</summary>
    public const int VisitorsPerMille = 250;

    public sealed record Load(int Served, int Capacity, string Unit);

    /// <summary>A gauge for a building's panel. Fun ones are the city's own quirky statistics, rotating monthly.</summary>
    public sealed record Gauge(string Label, long Value, long Max, string Unit, bool Fun);

    /// <summary>The building's load, or null for types that don't have one.</summary>
    public Load? ServiceLoad(Building b)
    {
        var t = TypeOf(b);
        switch (t.Category)
        {
            case Category.Power:
            {
                long cap = (long)t.PowerOutKw * FundingFactor(b.Funding) / 100;
                return new Load((int)(b.Operating && PowerCapacity > 0 ? Math.Min(cap, PowerDemand * cap / PowerCapacity) : 0), (int)cap, "kW");
            }
            case Category.Water:
            {
                long cap = PumpOutput(b);
                return new Load((int)(b.Operating && b.Powered && WaterCapacity > 0 ? Math.Min(cap, WaterDemand * cap / WaterCapacity) : 0), (int)cap, "units a day");
            }
            case Category.Garbage:
            {
                long cap = (long)t.GarbageCap * FundingFactor(b.Funding) / 100;
                return new Load((int)(b.Operating && GarbageCapacity > 0 ? Math.Min(cap, GarbageProduced * cap / GarbageCapacity) : 0), (int)cap, "units a day");
            }
            case Category.Transit when t.Capacity > 0:
                return new Load(b.Riders, t.Capacity, "riders a day");
        }
        if (t.Serves == 0) return null;
        string unit = t.Category switch
        {
            Category.Education => $"pupils (ages {t.MinAge}–{t.MaxAge})",
            Category.Health => "patients a month",
            Category.Park => "visitors a day",
            Category.Fire => "residents protected",
            _ => "residents patrolled",
        };
        if (!Serving(b)) return new Load(0, t.Serves, unit);
        long served = 0;
        foreach (var home in Buildings)
        {
            if (home == null || home.Residents == 0 || !home.Operating) continue;
            if (Gap(b, home) > EffectiveRadius(b) || NearestServing(home, t) != b) continue;
            served += t.Category switch
            {
                Category.Education => Pupils(home, t.MinAge, t.MaxAge) * 1000L,
                Category.Health => home.Residents * (long)PatientsPerMille,
                Category.Park => home.Residents * (long)VisitorsPerMille,
                _ => home.Residents * 1000L,
            };
        }
        return new Load((int)((served + 500) / 1000), t.Serves, unit);
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

    /// <summary>
    /// The building's gauges: what it serves against its capacity, then one of the city's quirky statistics for its
    /// kind (bears spotted in a park, pencils dropped at a school), a different one each month, scaled by how busy it is.
    /// </summary>
    public List<Gauge> Gauges(Building b)
    {
        var list = new List<Gauge>();
        if (ServiceLoad(b) is not { } l) return list;
        var t = TypeOf(b);
        string label = t.Category switch
        {
            Category.Education => "Pupils", Category.Health => "Patients", Category.Park => "Visitors", Category.Police => "Patrolled",
            Category.Fire => "Protected", Category.Power => "Power used", Category.Water => "Water used", Category.Garbage => "Garbage taken",
            _ => "Riders",
        };
        list.Add(new Gauge(label, l.Served, l.Capacity, l.Unit, false));
        var quirks = Content.Voice.Quirks(t.Category.ToString().ToLowerInvariant());
        if (quirks.Count > 0)
        {
            int month = MonthIndex;
            var q = quirks[(month + b.Id) % quirks.Count];
            long max = Math.Max(1, (long)Math.Round(Math.Max(l.Served, 1) * q.Per));
            long value = max * (long)(30 + Hash(b.Id, month, 31, 0) % 66) / 100;
            list.Add(new Gauge(q.Label, value, max, q.Unit, true));
        }
        return list;
    }

    /// <summary>A line for the building's panels: "Teaching 120 of 400 pupils (ages 5–11)."</summary>
    public string LoadLine(Building b)
    {
        if (ServiceLoad(b) is not { } l || TypeOf(b).Serves == 0) return "";
        string verb = TypeOf(b).Category switch { Category.Education => "Teaching", Category.Health => "Seeing", Category.Park => "Welcoming", Category.Fire => "Protecting", _ => "Patrolling for" };
        string over = l.Served > l.Capacity ? $" Over capacity by {l.Served - l.Capacity:N0}: another one nearby would share the load." : "";
        return $"{verb} {l.Served:N0} of {l.Capacity:N0} {l.Unit}.{over}";
    }
}
