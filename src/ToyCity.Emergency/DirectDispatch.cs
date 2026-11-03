using Google.Protobuf;
using ToyCity.Contract;
using W = ToyCity.Emergency.Wire;

namespace ToyCity.Emergency;

/// <summary>
/// Direct dispatch (Beta 4, Emergency and SWAT 2 style): every unit is somewhere on the city's streets. It drives each
/// leg by the city's fastest route, leaving at a minute of the day clock and taking the route's minutes, so clients
/// ease it along by the clock. The player orders units about on the map: to a place (with more places queued after
/// it), to a call, to hold where they are, back to their station, or to follow another unit. Auto-dispatch still runs
/// on top, as Zac chose: a unit posted somewhere by hand stays ready there and answers calls from where it stands,
/// which is the point of posting it, unless it's held.
/// </summary>
public sealed partial class EmergencyGame
{
    /// <summary>The city, for routes, during a tick or a command.</summary>
    ICityQueries? _q;

    /// <summary>A unit's station door, or -1.</summary>
    int Home(Unit u) => Lot(u.LeaseId)?.AccessCell ?? -1;

    bool Moving(Unit u) => u.Leg.Count > 0 && _clock < u.LegStart + u.LegMinutes;

    /// <summary>Where a unit is now: along its leg by the clock, where it stands, or its station's door.</summary>
    int Where(Unit u)
    {
        if (Moving(u))
        {
            long done = Math.Max(0, _clock - u.LegStart);
            int k = (int)Math.Min(u.Leg.Count - 1, done * (u.Leg.Count - 1) / Math.Max(1, u.LegMinutes));
            return u.Leg[k];
        }
        return u.Cell >= 0 ? u.Cell : Home(u);
    }

    /// <summary>Sets a unit driving from where it is to a road cell. False when there's no way there.</summary>
    bool DriveTo(Unit u, int target)
    {
        int from = Where(u);
        if (_q == null || from < 0 || target < 0) return false;
        if (from == target) { u.Leg.Clear(); u.Cell = target; return true; }
        var route = _q.Route(from, target);
        if (route == null || route.Cells.Count == 0) return false;
        u.Leg.Clear();
        u.Leg.AddRange(route.Cells);
        u.LegStart = _clock;
        u.LegMinutes = Math.Max(1, route.Minutes);
        u.Cell = target;
        return true;
    }

    /// <summary>Back to its station: the leg home, after which it stands at the door as "home" (-1).</summary>
    void GoHome(Unit u)
    {
        u.Order = ""; u.FollowId = 0; u.Waypoints.Clear();
        int home = Home(u);
        if (home < 0 || Where(u) == home) { u.Leg.Clear(); u.Cell = -1; return; }
        if (!DriveTo(u, home)) { u.Leg.Clear(); u.Cell = -1; }
    }

    /// <summary>Every step: legs end, queued places are driven to in turn, and followers keep up.</summary>
    void Drive()
    {
        foreach (var u in _units)
        {
            if (u.Leg.Count > 0 && !Moving(u))
            {
                u.Leg.Clear();
                if (u.Cell == Home(u) && u.Order == "") u.Cell = -1;
            }
            if (u.Leg.Count == 0 && u.Waypoints.Count > 0)
            {
                int next = u.Waypoints[0];
                u.Waypoints.RemoveAt(0);
                DriveTo(u, next);
            }
            if (u.Order == "following")
            {
                if (Find(u.FollowId) is not { } lead || lead.State == "suspended") { u.Order = "posted"; u.FollowId = 0; continue; }
                int there = Where(lead), here = Moving(u) ? u.Cell : Where(u);
                if (there >= 0 && _q != null && Gap(here, there) > 2) DriveTo(u, there);
            }
        }
    }

    int Gap(int a, int b) => _q == null || a < 0 || b < 0 ? int.MaxValue : Math.Abs(a % _q.Width - b % _q.Width) + Math.Abs(a / _q.Width - b / _q.Width);

    /// <summary>Whether a unit can take a direct order: on the road and not busy with a call.</summary>
    static bool Free(Unit u) => u.State is "ready" or "proposed";

    CommandResult OrderUnits(W.UnitOrder o, ICityQueries city)
    {
        var units = o.UnitIds.Distinct().Take(32).Select(Find).ToList();
        if (units.Count == 0 || units.Any(u => u == null)) return No("Pick at least one of the service's units.");
        var picked = units!.Cast<Unit>().ToList();
        switch (o.Kind)
        {
            case "scene":
                return Command("operator", new W.Command { Dispatch = new W.Dispatch { CallId = o.CallId, UnitIds = { picked.Select(u => u.Id) } } }.ToByteArray(), city);
            case "go":
            {
                if (picked.Any(u => !Free(u))) return No("One of those units is busy with a call.");
                if (o.X < 0 || o.Y < 0 || o.X >= city.Width || o.Y >= city.Height) return No("That's off the map.");
                int road = NearestRoad(o.X, o.Y, city);
                if (road < 0) return No("There's no road near there to drive to.");
                int stuck = 0;
                foreach (var u in picked)
                {
                    ReleaseProposed(u);
                    u.FollowId = 0;
                    u.Order = "posted";
                    if (o.Queue && (Moving(u) || u.Waypoints.Count > 0)) { u.Waypoints.Add(road); continue; }
                    u.Waypoints.Clear();
                    if (!DriveTo(u, road)) stuck++;
                }
                if (stuck == picked.Count) return No("They can't get there by road.");
                if (picked.Count == 1) Log($"{UnitName(picked[0].Id)} {(o.Queue ? "will go on" : "is going")} to {o.X},{o.Y}.");
                else Log($"{picked.Count} units {(o.Queue ? "will go on" : "are going")} to {o.X},{o.Y}.");
                return Yes;
            }
            case "hold":
                if (picked.Any(u => !Free(u))) return No("One of those units is busy with a call.");
                foreach (var u in picked)
                {
                    ReleaseProposed(u);
                    u.Cell = Where(u);
                    u.Leg.Clear(); u.Waypoints.Clear(); u.FollowId = 0;
                    u.Order = "held";
                }
                Log(picked.Count == 1 ? $"{UnitName(picked[0].Id)} is holding where it is." : $"{picked.Count} units are holding where they are.");
                return Yes;
            case "return":
                if (picked.Any(u => !Free(u))) return No("One of those units is busy with a call.");
                foreach (var u in picked) { ReleaseProposed(u); GoHome(u); }
                Log(picked.Count == 1 ? $"{UnitName(picked[0].Id)} is going back to its station." : $"{picked.Count} units are going back to their stations.");
                return Yes;
            case "follow":
            {
                if (Find(o.FollowId) is not { } lead) return No("There's no such unit to follow.");
                if (picked.Any(u => !Free(u))) return No("One of those units is busy with a call.");
                foreach (var u in picked.Where(u => u.Id != lead.Id))
                {
                    ReleaseProposed(u);
                    u.Waypoints.Clear();
                    u.Order = "following"; u.FollowId = lead.Id;
                    DriveTo(u, Where(lead));
                }
                Log($"{string.Join(", ", picked.Where(u => u.Id != lead.Id).Select(u => UnitName(u.Id)))} following {UnitName(lead.Id)}.");
                return Yes;
            }
            default:
                return No("The crews don't know that order.");
        }
    }

    /// <summary>A unit the computer proposed for a call is taken off it when the player orders it elsewhere.</summary>
    void ReleaseProposed(Unit u)
    {
        if (u.State != "proposed") return;
        if (_calls.FirstOrDefault(c => c.Id == u.CallId) is { } c)
        {
            c.Units.Remove(u.Id);
            if (c.Units.Count == 0) c.Status = "waiting";
        }
        u.State = "ready"; u.CallId = 0;
    }

    void FillPosition(Unit u, W.UnitView v)
    {
        v.Cell = Where(u);
        v.Home = Home(u);
        v.Order = u.Order;
        v.FollowId = u.FollowId;
        if (Moving(u)) { v.Leg.AddRange(u.Leg); v.LegStart = u.LegStart; v.LegMinutes = u.LegMinutes; v.Cell = u.Cell; }
        v.Waypoints.AddRange(u.Waypoints);
    }

    const int DirectMark = 0x42340001;

    void SaveDirect(BinaryWriter w)
    {
        w.Write(DirectMark);
        foreach (var u in _units)
        {
            w.Write(u.Cell); w.Write(u.LegStart); w.Write(u.LegMinutes); w.Write(u.FollowId); w.Write(u.Order);
            w.Write(u.Leg.Count); foreach (int c in u.Leg) w.Write(c);
            w.Write(u.Waypoints.Count); foreach (int c in u.Waypoints) w.Write(c);
        }
    }

    void LoadDirect(BinaryReader r)
    {
        if (r.BaseStream.Position >= r.BaseStream.Length || r.ReadInt32() != DirectMark) return;
        foreach (var u in _units)
        {
            u.Cell = r.ReadInt32(); u.LegStart = r.ReadInt64(); u.LegMinutes = r.ReadInt32(); u.FollowId = r.ReadInt32(); u.Order = r.ReadString();
            int n = r.ReadInt32(); for (int k = 0; k < n; k++) u.Leg.Add(r.ReadInt32());
            n = r.ReadInt32(); for (int k = 0; k < n; k++) u.Waypoints.Add(r.ReadInt32());
        }
    }
}
