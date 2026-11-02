using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>How one path cell looks in today's weather: each 0..1.</summary>
public readonly record struct RoadCondition(float Wet, float Mud, float Snow, bool Ice, float Banked)
{
    public static readonly RoadCondition Dry = new(0, 0, 0, false, 0);
    public bool IsDry => Wet == 0 && Mud == 0 && Snow == 0 && Banked == 0;

    /// <summary>
    /// A small number that changes only when the cell's look does enough to redraw it (wet, mud, snow and banks in
    /// quarters), so a client rebuilds a chunk of roads when it matters and not every tick.
    /// </summary>
    public int Key => (int)(Mud * 4 + 0.5f) | (int)(Snow * 4 + 0.5f) << 3 | (Ice ? 1 : 0) << 6 | (int)(Banked * 4 + 0.5f) << 7;
}

/// <summary>
/// Weather on the roads as a client draws it (Beta 3.2, part 4), from the city's <see cref="RoadWeather"/> stats and
/// each cell's profile and surface: wet roads shine, mud darkens dirt, snow lies on roads the crews haven't reached and
/// is pushed to the kerbs on those they have, and ice glazes them.
/// </summary>
public static class RoadConditions
{
    public static RoadCondition At(CityMirror city, int i)
    {
        var r = city.Stats.Roads;
        if (r == null || i < 0 || i >= city.Paths.Length || city.Paths[i] == 0) return RoadCondition.Dry;
        var p = city.ProfileAt(i);
        if (p == null || p.WinterTier < 0) return RoadCondition.Dry;
        var s = city.SurfaceAt(i);
        int t = Math.Clamp(p.WinterTier, 0, 2);
        float Of(Google.Protobuf.Collections.RepeatedField<int> f) => f.Count > t ? f[t] / 100f : 0;
        bool ice = r.Ice.Count > t && r.Ice[t];
        return new RoadCondition(r.Wet / 100f, s is { Muddy: true } ? r.Mud / 100f : 0, Of(r.Cover), ice && Of(r.Cover) > 0, Of(r.Banked));
    }

    /// <summary>"Snow on the streets, wet roads · crews 100%", for the traffic view's legend; "" on a dry day.</summary>
    public static string Legend(CityStats s)
    {
        var r = s.Roads;
        if (r == null || r.Text.Length == 0) return "";
        return $"{r.Text} · road crews {r.CrewsPct}%" + (r.StayHomePct > 0 ? $" · {r.StayHomePct}% stay home" : "");
    }
}
