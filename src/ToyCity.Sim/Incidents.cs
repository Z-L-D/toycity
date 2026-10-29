using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// The city's own incidents beyond fires and disasters, for the emergency services side game to answer: crime
/// where policing is thin, injuries on busy roads and in burning homes. And what the city makes of the answers
/// (contract v1.1): a fire put out or spreading, an arrest or an escape, a casualty treated or lost; and incidents a
/// player is handling at the scene, which the city holds still until the scene ends.
/// </summary>
public sealed partial class World
{
    /// <summary>Most crime and injury calls the city raises in a day, so a big city doesn't flood the dispatcher.</summary>
    public const int MaxCallsPerDay = 4;
    /// <summary>How long the city remembers an incident it raised, for answers that come back late.</summary>
    const int IncidentMemoryDays = 60;

    /// <summary>The city's own incidents still open to an answer, by id.</summary>
    readonly SortedDictionary<long, (Incident Inc, long Tick)> _openIncidents = new();
    /// <summary>Per district, 0..100: escapes heat a district up and its crime calls come faster; arrests cool it.</summary>
    int[] _crimeHeat = Array.Empty<int>();

    public int CrimeHeat(int district) => district >= 0 && district < _crimeHeat.Length ? _crimeHeat[district] : 0;

    void Remember(Incident inc)
    {
        if (inc.Source != "city" || inc.Kind is not ("fire" or "crime" or "injury")) return;
        _openIncidents[inc.Id] = (inc, Tick);
    }

    /// <summary>Incidents some side game is handling at the scene right now.</summary>
    bool Held(int x, int y, string kind)
    {
        foreach (var hg in SideGames)
            foreach (long id in hg.Last.Holding)
                if (_openIncidents.TryGetValue(id, out var o) && o.Inc.Kind == kind && o.Inc.X == x && o.Inc.Y == y) return true;
        return false;
    }

    /// <summary>Daily: crime calls where policing is thin, and crashes on busy roads.</summary>
    void RaiseCityIncidents()
    {
        if (_crimeHeat.Length != DistrictCount) Array.Resize(ref _crimeHeat, DistrictCount);
        for (int d = 0; d < _crimeHeat.Length; d++) _crimeHeat[d] = Math.Max(0, _crimeHeat[d] - 1);
        // Forget what nobody answered in time.
        foreach (var id in _openIncidents.Where(kv => Tick - kv.Value.Tick > IncidentMemoryDays).Select(kv => kv.Key).ToList())
            _openIncidents.Remove(id);
        if (SideGames.Count == 0) return;
        int raised = 0;
        var figures = DistrictFigures();
        foreach (var f in figures)
        {
            if (raised >= MaxCallsPerDay) break;
            if (f.Population < 50) continue;
            // A district with no police at all reads 100: about one call every four days, sooner when it's hot.
            int chance = f.Crime * (100 + 2 * CrimeHeat(f.Id)) / 400;
            if ((int)(Hash((int)Tick, f.Id, 0x6372, 1) % 100) >= chance) continue;
            var home = PickBuildingIn(f, 0x6372);
            if (home == null) continue;
            int sev = 1 + (int)(Hash((int)Tick, f.Id, 0x6372, 2) % 3);
            RaiseIncident("crime", home.X, home.Y, "city", sev == 3 ? "an armed robbery" : sev == 2 ? "a break-in" : "a mugging", sev, "");
            raised++;
        }
        // Crashes: on the busiest road cells, a small chance each.
        for (int k = 0; k < 2 && raised < MaxCallsPerDay; k++)
        {
            int i = (int)(Hash((int)Tick, k, 0x696e, 3) % (ulong)N);
            // Walk to the nearest busy road along the row: cheap and deterministic.
            for (int step = 0; step < 64; step++, i = (i + 1) % N)
            {
                if (Path[i] == 0 || Traffic[i] < 96) continue;
                if ((int)(Hash((int)Tick, i, 0x696e, 4) % 100) < 15)
                {
                    RaiseIncident("injury", i % Width, i / Width, "city", "a crash", Traffic[i] > 160 ? 2 : 1, "");
                    raised++;
                }
                break;
            }
        }
    }

    Building? PickBuildingIn(District f, int salt)
    {
        Building? pick = null;
        ulong best = ulong.MaxValue;
        for (int y = f.Y0; y <= f.Y1 && y < HeightCells; y++)
        for (int x = f.X0; x <= f.X1 && x < Width; x++)
        {
            int at = BuildingAt[Idx(x, y)];
            if (at == 0 || Buildings[at - 1] is not { Operating: true } b || b.X != x || b.Y != y || b.Residents + b.Jobs == 0) continue;
            ulong h = Hash((int)Tick, b.Id, salt, 5);
            if (h < best) { best = h; pick = b; }
        }
        return pick;
    }

    /// <summary>An injury when people are inside a building that catches fire.</summary>
    void FireCasualty(Building b)
    {
        if (b.Residents + b.Jobs == 0 || Hash((int)Tick, b.Id, 0x6675, 6) % 2 == 0) return;
        RaiseIncident("injury", b.X, b.Y, "city", $"smoke at {TypeOf(b).Name.ToLowerInvariant()}", 2, "");
    }

    /// <summary>What the side game says became of incidents it answered. Each is applied once.</summary>
    void TakeResponses(HostedGame hg, GameToCity output)
    {
        foreach (var r in output.Responses.Take(64))
        {
            if (!_openIncidents.Remove(r.IncidentId, out var open)) continue;
            var inc = open.Inc;
            int d = DistrictOf(inc.X, inc.Y);
            switch (inc.Kind, r.Outcome)
            {
                case ("fire", "out"):
                    if (BurningAt(inc.X, inc.Y) is { } b) { b.BurningDays = 0; b.Condition = Math.Max(1, b.Condition - 10); }
                    break;
                case ("fire", "spread"):
                    if (BurningAt(inc.X, inc.Y) is { } burning)
                        foreach (var n in Neighbours(burning).Take(1)) Ignite(n);
                    break;
                case ("crime", "arrest"):
                    if (d < _crimeHeat.Length) _crimeHeat[d] = Math.Max(0, _crimeHeat[d] - 10);
                    break;
                case ("crime", "escaped"):
                    if (d < _crimeHeat.Length) _crimeHeat[d] = Math.Min(100, _crimeHeat[d] + 15);
                    break;
            }
            if (r.Text.Length > 0)
            {
                string sev = r.Outcome is "spread" or "escaped" or "died" ? "warning" : "info";
                Notices.Add(new SimNotice(r.Text.Length > 200 ? r.Text[..200] : r.Text, sev, inc.X, inc.Y));
                if (sev == "warning") AddHeadline(r.Text.Length > 200 ? r.Text[..200] : r.Text, sev, inc.X, inc.Y);
            }
        }
    }

    /// <summary>For tests: the city takes answers as if a side game had sent them.</summary>
    internal void TakeResponsesForTest(GameToCity output) => TakeResponses(null!, output);

    Building? BurningAt(int x, int y) =>
        InBounds(x, y) && BuildingAt[Idx(x, y)] is var at && at != 0 && Buildings[at - 1] is { BurningDays: > 0 } b ? b : null;

    IEnumerable<Building> Neighbours(Building b)
    {
        var near = new SortedSet<int>();
        for (int y = b.Y - 1; y <= b.Y + b.H; y++)
        for (int x = b.X - 1; x <= b.X + b.W; x++)
            if (InBounds(x, y) && BuildingAt[Idx(x, y)] is var at && at != 0 && at - 1 != b.Id) near.Add(at - 1);
        foreach (int id in near)
            if (Buildings[id] is { BurningDays: 0 } n) yield return n;
    }

    /// <summary>The cells of a rectangle as a side game sees them (contract v1.1).</summary>
    internal CellKind[] AreaKinds(int x0, int y0, int w, int h)
    {
        w = Math.Clamp(w, 0, 256);
        h = Math.Clamp(h, 0, 256);
        var cells = new CellKind[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int cx = x0 + x, cy = y0 + y;
            if (!InBounds(cx, cy)) { cells[y * w + x] = CellKind.Water; continue; }
            int i = Idx(cx, cy);
            cells[y * w + x] = IsWater(i) ? CellKind.Water
                : Path[i] != 0 ? CellKind.Road
                : BuildingAt[i] != 0 && Buildings[BuildingAt[i] - 1] is { } b ? (TypeOf(b).Category == Category.Park ? CellKind.Park : CellKind.Building)
                : CellKind.Ground;
        }
        return cells;
    }

    void WriteIncidents(BinaryWriter w)
    {
        w.Write(_openIncidents.Count);
        foreach (var (inc, tick) in _openIncidents.Values) { WriteIncident(w, inc); w.Write(tick); }
        w.Write(_crimeHeat.Length);
        foreach (var h in _crimeHeat) w.Write(h);
    }

    void ReadIncidents(BinaryReader r)
    {
        _openIncidents.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var inc = ReadIncident(r);
            _openIncidents[inc.Id] = (inc, r.ReadInt64());
        }
        _crimeHeat = new int[r.ReadInt32()];
        for (int i = 0; i < _crimeHeat.Length; i++) _crimeHeat[i] = r.ReadInt32();
    }
}
