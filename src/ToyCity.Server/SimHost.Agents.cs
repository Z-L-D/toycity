using ToyCity.Protocol;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>
/// The followed commuters: tiers come from this machine's benchmark (measured once in the background and kept in
/// the save folder), the view is stepped with the city and its trips are sent when they change.
/// </summary>
public sealed partial class SimHost
{
    /// <summary>Most trips sent to a client; the rest are still followed, just not drawn.</summary>
    public const int MaxDrawn = 3000;

    AgentProfile? _profile;
    AgentView? _agents;
    string _rosterSig = "";

    string ProfilePath => Path.Combine(_saveDir, "agent-tiers.json");

    void LoadAgentTiers()
    {
        _profile = AgentTiers.Load(ProfilePath);
        if (_profile != null) { _log.LogInformation("agent tiers: full {Full}, light {Light} (measured {When:u})", _profile.Full, _profile.Light, _profile.MeasuredUtc); return; }
        _log.LogInformation("no agent tiers for this machine yet; measuring in the background");
        _ = Task.Run(() =>
        {
            try
            {
                var p = AgentBenchmark.Run(_content, quick: true, m => _log.LogInformation("benchmark: {Message}", m));
                AgentBenchmark.Save(p, ProfilePath);
                lock (_gate)
                {
                    _profile = p;
                    if (_agents != null) { _agents.Tiers = p.Tiers; _agents.Select(); }
                }
            }
            catch (Exception e) { _log.LogWarning(e, "agent benchmark failed; keeping the default tiers"); }
        });
    }

    /// <summary>The view for the current city, made again when the city is replaced. Caller holds the gate.</summary>
    AgentView Agents()
    {
        if (_agents?.World != _world)
        {
            _agents = new AgentView(_world, _profile?.Tiers ?? AgentTiers.Default);
            _agents.Select();
            _rosterSig = "";
        }
        return _agents;
    }

    /// <summary>The roster when it changed since it was last sent (new trips, a new month), else null. Caller holds the gate.</summary>
    P.AgentRoster? RosterIfChanged()
    {
        var a = Agents();
        string sig = $"{a.Version}/{_world.MonthIndex}/{a.Tiers}";
        if (sig == _rosterSig) return null;
        _rosterSig = sig;
        return Roster(a);
    }

    P.AgentRoster Roster(AgentView a)
    {
        var tiers = a.Tiers;
        var r = new P.AgentRoster
        {
            FullTier = tiers.Full, LightTier = tiers.Light, Full = a.Trips.Count, Light = a.LightCount, Citizens = a.Citizens,
            Machine = _profile is { } p ? $"{p.Cores} cores, {p.Os}; measured {p.MeasuredUtc:yyyy-MM-dd}" : "not measured yet (default tiers)",
        };
        foreach (var t in a.Trips.Take(MaxDrawn))
        {
            var m = new P.AgentTripInfo { Id = t.Id, DepartMinute = t.DepartMinute, Minutes = a.MinutesOf(t), ByTransit = t.ByTransit };
            m.Cells.AddRange(t.Cells);
            r.Trips.Add(m);
        }
        return r;
    }

    /// <summary>Most residents listed in one inspect reply.</summary>
    public const int MaxResidentsListed = 100;

    /// <summary>The inspect reply plus the residents of the building there, one by one. Caller holds the gate.</summary>
    P.InspectResult InspectWithResidents(int x, int y)
    {
        var r = Mapping.Inspect(_world, x, y);
        if (!_world.InBounds(x, y) || _world.BuildingAtCell(_world.Idx(x, y)) is not { Residents: > 0 } home) return r;
        var a = Agents();
        foreach (var c in _world.CitizensOf(home).Take(MaxResidentsListed))
        {
            var info = new P.CitizenInfo
            {
                Id = c.Id, Name = c.Name, Age = c.Age, SinceMonth = c.SinceMonth, ByTransit = c.ByTransit, CommuteMinutes = c.CommuteMinutes,
                Role = c.Role switch { CitizenRole.Worker => "worker", CitizenRole.JobSeeker => "job seeker", CitizenRole.Child => "child", _ => "retiree" },
                Tier = a.TierOf(c.Id) switch { AgentTier.Full => "full", AgentTier.Light => "light", _ => "household" },
            };
            if (c.SiteId >= 0 && c.SiteId < _world.Buildings.Count && _world.Buildings[c.SiteId] is { } site)
            {
                info.Job = _world.TypeOf(site).Name;
                info.JobX = site.X;
                info.JobY = site.Y;
            }
            if (a.StatsOf(c.Id) is { Days: > 0 } s)
            {
                info.DaysTracked = s.Days;
                info.DaysLate = s.Late;
                info.AvgMinutes = (int)(s.Minutes / s.Days);
            }
            r.Residents.Add(info);
        }
        return r;
    }
}
