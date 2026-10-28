using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>How a building looks at this moment: how much of it stands, how worn it is, and what's happening to it.</summary>
public readonly record struct Life(float Standing, float Wear, float Graffiti, LifeFlags Flags);

[Flags]
public enum LifeFlags { None = 0, Burning = 1, Abandoned = 2, Crumbling = 4 }

/// <summary>
/// The animation side of building life, kept apart from drawing so it can be tested. A building under
/// construction rises toward the share of its build days done, smoothly between days; a building that goes
/// (demolished, burnt out, flattened by the Colossus) crumbles from the top down over a moment and a half.
/// Wear, graffiti, fire and abandonment come straight from the server's state.
/// </summary>
public sealed class BuildingLife
{
    public const double CrumbleSeconds = 1.5;
    const float RisePerSecond = 4f;  // how quickly the drawn height catches up with the build's progress
    readonly Dictionary<int, int> _totalDays = new();
    readonly Dictionary<int, float> _standing = new();
    readonly List<(BuildingState B, double Start)> _crumbling = new();
    double _now;

    /// <summary>True while something is moving, so the renderer must refresh every frame.</summary>
    public bool Animating { get; private set; }

    public IEnumerable<(BuildingState B, float Left)> Crumbling =>
        _crumbling.Select(c => (c.B, (float)Math.Clamp(1 - (_now - c.Start) / CrumbleSeconds, 0, 1)));

    /// <summary>Days from groundbreaking to opening: as the sim counts them, or the most seen left if more.</summary>
    int TotalDays(BuildingState b, BuildingTypeInfo? t)
    {
        int formula = t == null ? 1 : t.Category == "zoned" ? Math.Max(1, t.BuildDays) + (b.Width * b.Height - 1) * 2 : Math.Max(1, t.BuildDays);
        int seen = _totalDays.TryGetValue(b.Id, out var d) ? d : 0;
        int total = Math.Max(Math.Max(formula, seen), b.ConstructionDaysLeft);
        _totalDays[b.Id] = total;
        return total;
    }

    /// <summary>Moves time on, and starts the crumbling of the buildings just removed.</summary>
    public void Advance(CityMirror city, double dt, IEnumerable<BuildingState> removed)
    {
        // They went during the frame just past, so they start crumbling at its beginning.
        foreach (var b in removed)
        {
            _crumbling.Add((b, _now));
            _standing.Remove(b.Id);
            _totalDays.Remove(b.Id);
        }
        _now += dt;
        _crumbling.RemoveAll(c => _now - c.Start >= CrumbleSeconds);
        bool moving = _crumbling.Count > 0;
        foreach (var b in city.Buildings.Values)
        {
            float target = Target(b, city.TypeById.GetValueOrDefault(b.TypeId));
            if (!_standing.TryGetValue(b.Id, out var shown)) { _standing[b.Id] = target; continue; }
            if (Math.Abs(shown - target) < 0.001f) continue;
            // Rising catches up smoothly; a building can't un-build, except when the server says it started over.
            shown = target < shown ? target : shown + (target - shown) * Math.Min(1f, (float)dt * RisePerSecond);
            if (Math.Abs(shown - target) < 0.002f) shown = target;
            _standing[b.Id] = shown;
            moving = true;
        }
        Animating = moving;
    }

    float Target(BuildingState b, BuildingTypeInfo? t)
    {
        if (b.ConstructionDaysLeft <= 0) { _totalDays.Remove(b.Id); return 1; }
        int total = TotalDays(b, t);
        // Ground is broken on the first day, so a new site shows its first layer.
        return Math.Clamp(1f - (float)b.ConstructionDaysLeft / total, 0.03f, 1f);
    }

    public Life Of(BuildingState b)
    {
        float standing = _standing.TryGetValue(b.Id, out var s) ? s : b.ConstructionDaysLeft > 0 ? 0.03f : 1f;
        var flags = LifeFlags.None;
        if (b.BurningDays > 0 || b.Look == BuildingLook.LookBurning) flags |= LifeFlags.Burning;
        if (b.Abandoned || b.Look is BuildingLook.LookAbandoned or BuildingLook.LookDerelict) flags |= LifeFlags.Abandoned;
        float wear = Math.Clamp(1f - b.Condition / 100f, 0, 1);
        if (b.Look == BuildingLook.LookDerelict) wear = Math.Max(wear, 0.9f);
        return new Life(standing, wear, Math.Clamp(b.Graffiti / 3f, 0, 1), flags);
    }

    public Life OfCrumbling(BuildingState b, float left) => Of(b) with { Standing = left, Flags = Of(b).Flags | LifeFlags.Crumbling };

    /// <summary>Forgets everything, as after a whole new city arrives.</summary>
    public void Reset() { _standing.Clear(); _totalDays.Clear(); _crumbling.Clear(); }
}
