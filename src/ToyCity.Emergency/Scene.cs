using ToyCity.Contract;
using W = ToyCity.Emergency.Wire;

namespace ToyCity.Emergency;

/// <summary>
/// One incident played at the scene: a small patch of the city in tiles (four to a cell), the fire on it, the people
/// there and the units that come. It runs in steps of a quarter second, only when the player's client asks, so it
/// is as deterministic as the city: every step and every order is a logged command, and all chance comes from the
/// scene's own seeded generator.
/// </summary>
sealed class Scene
{
    public const int CellsAcross = 9, TilesPerCell = 4, Size = CellsAcross * TilesPerCell;
    public const int MaxSteps = 4 * 60 * 10;  // ten minutes: the crews finish what's left
    const byte Burnt = 5;

    public sealed class Person
    {
        public int Id;
        public W.Role Role;
        public int X, Y, Health = 100;
        public W.TaskKind Task;
        public int TaskX, TaskY, Target;
        public int Unit;
        public bool Armed, Warned, Surrendered, Cuffed, Escaped, Stable;
        /// <summary>A suspect hiding: not shown to the player until an officer sees them or searches near them.</summary>
        public bool Hidden;
        public int StunnedUntil, ShotAt = -1000, LastShot = -1000;
        public List<int> Path = new();
        public bool Dead => Health <= 0;
        public bool Down(int step) => Dead || Cuffed || Escaped || StunnedUntil > step;
    }

    public sealed class Vehicle
    {
        public int Unit;
        public W.UnitKind Kind;
        public int X, Y;
        public List<int> Path = new();
        public bool Drawn, HitSomeone;
    }

    public int Id, CallId;
    public long IncidentId;
    public string Kind = "";
    public int Severity, OriginX, OriginY, Minutes, Step;
    /// <summary>The city tick the player last ran or ordered the scene: one left alone too long is handed to the crews.</summary>
    public long Touched;
    public string Where = "";
    public byte[] Tiles = new byte[Size * Size];
    public byte[] Fire = new byte[Size * Size];
    readonly byte[] _peak = new byte[Size * Size];
    public readonly List<Person> People = new();
    public readonly List<Vehicle> Vehicles = new();
    /// <summary>Units on the way: the step each one reaches the edge of the scene.</summary>
    public readonly List<(int Unit, W.UnitKind Kind, int Crew, int AtStep)> Arriving = new();
    public readonly List<string> Events = new();
    public int Entry;
    public int BuildingTiles, Harmed, FriendlyFire, Unauthorized;
    /// <summary>Part 11: a hostage scene, hostages the suspect hurt, breaches made, and each unit's equipment bits.</summary>
    public bool Hostages;
    public int HostagesHurt, Breaches;
    public readonly SortedDictionary<int, int> Gear = new();
    /// <summary>The last shots fired, for the lines of fire the clients draw.</summary>
    public readonly List<(int Step, int FromX, int FromY, int ToX, int ToY, bool Hit, bool Taser, bool Police)> Shots = new();
    public const int BreathingGear = 1, BodyArmour = 2, Defibrillator = 4;
    public readonly List<string> Notes = new();
    ulong _rng;
    /// <summary>People's ids start here, so they never clash with the unit ids vehicles go by.</summary>
    public const int PersonBase = 1_000_000;
    int _nextPerson = PersonBase;

    // -----------------------------------------------------------------
    // Setting up
    // -----------------------------------------------------------------

    public static Scene Create(int id, int callId, long incidentId, string kind, int severity, int x, int y, string where, int minutes, CellKind[] area, int entryX, int entryY, ulong seed)
    {
        var s = new Scene
        {
            Id = id, CallId = callId, IncidentId = incidentId, Kind = kind, Severity = Math.Clamp(severity, 1, 3), Minutes = minutes, Where = where,
            OriginX = x - CellsAcross / 2, OriginY = y - CellsAcross / 2, _rng = seed | 1,
        };
        for (int ty = 0; ty < Size; ty++)
        for (int tx = 0; tx < Size; tx++)
        {
            var k = area[(ty / TilesPerCell) * CellsAcross + tx / TilesPerCell];
            s.Tiles[ty * Size + tx] = (byte)k;
        }
        s.BuildingTiles = s.Tiles.Count(t => t == (byte)CellKind.Building);
        int c = CellsAcross / 2 * TilesPerCell;
        if (kind == "fire")
        {
            // The building at the call is alight, worse the more serious the call.
            for (int ty = c; ty < c + TilesPerCell; ty++)
            for (int tx = c; tx < c + TilesPerCell; tx++)
                if (s.Tiles[ty * Size + tx] == (byte)CellKind.Building) s.Fire[ty * Size + tx] = (byte)(25 + 20 * s.Severity);
            if (s.Fire.All(f => f == 0)) s.Fire[(c + 1) * Size + c + 1] = 50;
            if (s.Severity >= 2) s.Add(W.Role.Casualty, c + 1, c + 1, health: 70 - 15 * s.Severity);
        }
        else if (kind == "crime" && s.Severity >= 3 && s.InsideAt(c, c) is { } inside && s.Chance(60))
        {
            // A hostage situation: an armed suspect holed up in the building at the call, holding people inside.
            s.Hostages = true;
            var (ix, iy) = inside;
            s.Add(W.Role.Suspect, ix, iy).Armed = true;
            foreach (var (hx, hy) in s.InsideNear(ix, iy).Take(2)) s.Add(W.Role.Hostage, hx, hy);
        }
        else if (kind == "crime")
        {
            var (sx, sy) = s.Open(c + 1, c + 1);
            var suspect = s.Add(W.Role.Suspect, sx, sy);
            suspect.Armed = s.Severity == 3;
            // A suspect at a lesser crime may have gone to ground nearby.
            if (s.Severity == 2 && s.Chance(50))
            {
                var (hx, hy) = s.Open(c + 1 + (int)(s.Next() % 9) - 4, c + 1 + (int)(s.Next() % 9) - 4);
                (suspect.X, suspect.Y, suspect.Hidden) = (hx, hy, true);
            }
            if (s.Severity >= 2) { var (vx, vy) = s.Open(c + 2, c); s.Add(W.Role.Casualty, vx, vy, health: 80 - 10 * s.Severity); }
        }
        else
        {
            var (vx, vy) = s.Open(c + 1, c + 1);
            s.Add(W.Role.Casualty, vx, vy, health: 75 - 15 * s.Severity);
        }
        // Onlookers.
        int onlookers = 4 + (int)(s.Next() % 5);
        for (int i = 0; i < onlookers; i++)
        {
            var (bx, by) = s.Open(c - 4 + (int)(s.Next() % 12), c - 4 + (int)(s.Next() % 12));
            s.Add(W.Role.Bystander, bx, by);
        }
        s.Entry = s.EdgeTile(entryX, entryY);
        return s;
    }

    /// <summary>A building tile at or near a spot, or null when there is none close.</summary>
    (int, int)? InsideAt(int x, int y)
    {
        for (int r = 0; r < TilesPerCell * 2; r++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && TileAt(x + dx, y + dy) == (byte)CellKind.Building) return (x + dx, y + dy);
        return null;
    }

    /// <summary>Building tiles joined to this one, nearest first, not counting it.</summary>
    IEnumerable<(int X, int Y)> InsideNear(int x, int y) => Building(x, y).Where(t => t != (x, y)).OrderBy(t => Dist(t.X, t.Y, x, y)).ThenBy(t => t.Y).ThenBy(t => t.X);

    /// <summary>The building a tile is part of: its tiles joined edge to edge.</summary>
    List<(int X, int Y)> Building(int x, int y)
    {
        var list = new List<(int, int)>();
        if (TileAt(x, y) != (byte)CellKind.Building) return list;
        var seen = new HashSet<(int, int)> { (x, y) };
        list.Add((x, y));
        for (int q = 0; q < list.Count && list.Count < 256; q++)
        {
            var (cx, cy) = list[q];
            foreach (var n in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                if (TileAt(n.Item1, n.Item2) == (byte)CellKind.Building && seen.Add(n)) list.Add(n);
        }
        return list;
    }

    Person Add(W.Role role, int x, int y, int health = 100)
    {
        var p = new Person { Id = _nextPerson++, Role = role, X = Math.Clamp(x, 0, Size - 1), Y = Math.Clamp(y, 0, Size - 1), Health = health };
        People.Add(p);
        return p;
    }

    ulong Next()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return _rng;
    }

    bool Chance(int percent) => (int)(Next() % 100) < percent;

    static bool In(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;
    byte TileAt(int x, int y) => In(x, y) ? Tiles[y * Size + x] : (byte)CellKind.Water;
    bool Walkable(int x, int y, bool crew) => In(x, y) && TileAt(x, y) switch
    {
        (byte)CellKind.Water => false,
        (byte)CellKind.Building => crew,
        _ => true,
    };
    bool Drivable(int x, int y) => In(x, y) && TileAt(x, y) is (byte)CellKind.Road or (byte)CellKind.Ground or (byte)CellKind.Park or Burnt;

    /// <summary>The nearest open tile to a spot, for placing people.</summary>
    (int, int) Open(int x, int y)
    {
        for (int r = 0; r < Size; r++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                if (Walkable(x + dx, y + dy, crew: false)) return (x + dx, y + dy);
            }
        return (x, y);
    }

    /// <summary>Where units come in: the road on the scene's edge nearest the way they come from.</summary>
    int EdgeTile(int towardX, int towardY)
    {
        int best = -1, bestD = int.MaxValue;
        for (int i = 0; i < Size; i++)
            foreach (var (x, y) in new[] { (i, 0), (i, Size - 1), (0, i), (Size - 1, i) })
            {
                int rank = TileAt(x, y) == (byte)CellKind.Road ? 0 : Drivable(x, y) ? 1000 : -1;
                if (rank < 0) continue;
                int d = rank + Math.Abs(x - towardX) + Math.Abs(y - towardY);
                if (d < bestD) { bestD = d; best = y * Size + x; }
            }
        return best < 0 ? 0 : best;
    }

    // -----------------------------------------------------------------
    // Running
    // -----------------------------------------------------------------

    void Event(string text)
    {
        Events.Add(text);
        if (Events.Count > 12) Events.RemoveAt(0);
    }

    public bool Done =>
        Step > 0 && Arriving.Count == 0 && Vehicles.Count > 0 && Fire.All(f => f == 0)
        && People.Where(p => p.Role == W.Role.Casualty).All(p => p.Stable || p.Dead)
        && People.Where(p => p.Role == W.Role.Suspect).All(p => p.Cuffed || p.Escaped || p.Dead)
        && People.Where(p => p.Role == W.Role.Hostage).All(p => p.Stable || p.Dead);

    public void Run(int steps, Settings settings, Func<int, string> unitName)
    {
        for (int i = 0; i < steps && Step < MaxSteps && !Done; i++) OneStep(settings, unitName);
    }

    void OneStep(Settings settings, Func<int, string> unitName)
    {
        Step++;
        foreach (var a in Arriving.Where(a => a.AtStep <= Step).ToList())
        {
            Arriving.Remove(a);
            int ex = Entry % Size, ey = Entry / Size;
            Vehicles.Add(new Vehicle { Unit = a.Unit, Kind = a.Kind, X = ex, Y = ey });
            var role = a.Kind switch { W.UnitKind.Engine => W.Role.Firefighter, W.UnitKind.Ambulance => W.Role.Paramedic, _ => W.Role.Officer };
            for (int k = 0; k < a.Crew; k++)
            {
                var (px, py) = Open(ex + (k % 2 == 0 ? 1 : -1), ey + (k / 2));
                Add(role, px, py).Unit = a.Unit;
            }
            Event($"{unitName(a.Unit)} is on scene.");
        }
        if (Step % 2 == 0) MoveVehicles(settings, unitName);
        if (Step % 2 == 0) MovePeople(crew: true);
        if (Step % 3 == 0) MovePeople(crew: false);
        foreach (var p in People.Where(p => p.Role is W.Role.Firefighter or W.Role.Paramedic or W.Role.Officer && !p.Down(Step)).ToList())
        {
            if (p.Task == W.TaskKind.None && settings.CrewsUseHeads) Think(p);
            Work(p, unitName);
        }
        Spot();
        HoldHostages(unitName);
        Suspects();
        Bystanders();
        if (Step % 4 == 0) BurnAndSpread();
        Hurt();
    }

    static int Dist(int x0, int y0, int x1, int y1) => Math.Max(Math.Abs(x0 - x1), Math.Abs(y0 - y1));

    /// <summary>Breadth-first over open tiles: the way a person or vehicle finds by itself.</summary>
    List<int> FindPath(int fx, int fy, int tx, int ty, Func<int, int, bool> open)
    {
        var prev = new int[Size * Size];
        Array.Fill(prev, -2);
        var q = new Queue<int>();
        int start = fy * Size + fx;
        prev[start] = -1;
        q.Enqueue(start);
        int goal = -1, bestD = int.MaxValue, best = start;
        while (q.Count > 0)
        {
            int c = q.Dequeue(), x = c % Size, y = c / Size;
            int d = Dist(x, y, tx, ty);
            if (d < bestD) { bestD = d; best = c; }
            if (x == tx && y == ty) { goal = c; break; }
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (!In(nx, ny) || prev[ny * Size + nx] != -2 || !open(nx, ny)) continue;
                prev[ny * Size + nx] = c;
                q.Enqueue(ny * Size + nx);
            }
        }
        if (goal < 0) goal = best;
        var path = new List<int>();
        for (int c = goal; c != start && c >= 0; c = prev[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    public void Move(int id, int tx, int ty)
    {
        tx = Math.Clamp(tx, 0, Size - 1); ty = Math.Clamp(ty, 0, Size - 1);
        if (People.FirstOrDefault(p => p.Id == id) is { } p)
        {
            if (!IsCrew(p)) return;
            p.Path = FindPath(p.X, p.Y, tx, ty, (x, y) => Walkable(x, y, crew: true));
            p.Task = W.TaskKind.None;
        }
        else if (Vehicles.FirstOrDefault(v => v.Unit == id) is { } v)
        {
            v.Path = FindPath(v.X, v.Y, tx, ty, Drivable);
            v.Drawn = false;
        }
    }

    /// <summary>A drawn path is followed exactly, wherever it goes: only steps between neighbouring tiles count.</summary>
    public void FollowPath(int id, IEnumerable<int> tiles)
    {
        var list = tiles.Where(t => t >= 0 && t < Size * Size).Take(400).ToList();
        if (People.FirstOrDefault(p => p.Id == id) is { } p) { if (IsCrew(p)) { p.Path = Contiguous(p.X, p.Y, list, (x, y) => Walkable(x, y, true)); p.Task = W.TaskKind.None; } }
        else if (Vehicles.FirstOrDefault(v => v.Unit == id) is { } v) { v.Path = Contiguous(v.X, v.Y, list, Drivable); v.Drawn = true; }
    }

    static List<int> Contiguous(int x, int y, List<int> tiles, Func<int, int, bool> open)
    {
        var path = new List<int>();
        foreach (int t in tiles)
        {
            int tx = t % Size, ty = t / Size;
            if (tx == x && ty == y) continue;
            if (Math.Abs(tx - x) + Math.Abs(ty - y) != 1 || !open(tx, ty)) break;
            path.Add(t);
            x = tx; y = ty;
        }
        return path;
    }

    static bool IsCrew(Person p) => p.Role is W.Role.Firefighter or W.Role.Paramedic or W.Role.Officer;

    public void Order(int id, W.TaskKind task, int x, int y, int target)
    {
        if (People.FirstOrDefault(p => p.Id == id) is not { } p || !IsCrew(p) || p.Down(Step)) return;
        bool fits = task switch
        {
            W.TaskKind.Hose => p.Role == W.Role.Firefighter,
            W.TaskKind.Treat => p.Role == W.Role.Paramedic,
            W.TaskKind.Arrest or W.TaskKind.Warn or W.TaskKind.Shoot or W.TaskKind.Taser or W.TaskKind.Cordon or W.TaskKind.Search => p.Role == W.Role.Officer,
            W.TaskKind.Breach => p.Role == W.Role.Officer && VehicleKind(p.Unit) == W.UnitKind.Swat,
            _ => true,
        };
        if (!fits) { Event(task == W.TaskKind.Breach && p.Role == W.Role.Officer ? "Only a SWAT team breaches." : $"A {p.Role.ToString().ToLowerInvariant()} can't do that."); return; }
        p.Task = task;
        p.TaskX = Math.Clamp(x, 0, Size - 1);
        p.TaskY = Math.Clamp(y, 0, Size - 1);
        p.Target = target;
        p.Path.Clear();
    }

    W.UnitKind? VehicleKind(int unit) =>
        Vehicles.FirstOrDefault(v => v.Unit == unit)?.Kind ?? (Arriving.Any(a => a.Unit == unit) ? Arriving.First(a => a.Unit == unit).Kind : null);

    bool Has(int unit, int bit) => Gear.TryGetValue(unit, out var g) && (g & bit) != 0;

    /// <summary>Crews left to themselves do the obvious job nearest them.</summary>
    void Think(Person p)
    {
        switch (p.Role)
        {
            case W.Role.Firefighter:
            {
                int best = -1, bestD = 12;
                for (int i = 0; i < Fire.Length; i++)
                    if (Fire[i] > 0 && Dist(p.X, p.Y, i % Size, i / Size) < bestD) { bestD = Dist(p.X, p.Y, i % Size, i / Size); best = i; }
                if (best >= 0) { p.Task = W.TaskKind.Hose; p.TaskX = best % Size; p.TaskY = best / Size; }
                break;
            }
            case W.Role.Paramedic:
                if (People.Where(c => c.Role == W.Role.Casualty && !c.Stable && !c.Dead).OrderBy(c => Dist(p.X, p.Y, c.X, c.Y)).ThenBy(c => c.Id).FirstOrDefault() is { } hurt)
                { p.Task = W.TaskKind.Treat; p.Target = hurt.Id; }
                break;
            case W.Role.Officer:
                // Nobody rushes a hostage-taker by themselves: that waits for the player's order.
                if (People.Where(c => c.Role == W.Role.Suspect && !c.Down(Step) && !c.Hidden && !(Hostages && c.Armed && !c.Surrendered))
                        .OrderBy(c => Dist(p.X, p.Y, c.X, c.Y)).ThenBy(c => c.Id).FirstOrDefault() is { } s)
                { p.Task = s.Armed && !s.Warned ? W.TaskKind.Warn : W.TaskKind.Arrest; p.Target = s.Id; }
                break;
        }
    }

    void Approach(Person p, int x, int y)
    {
        if (p.Path.Count > 0 && p.Path[^1] == y * Size + x) return;
        p.Path = FindPath(p.X, p.Y, x, y, (tx, ty) => Walkable(tx, ty, crew: true));
    }

    void Work(Person p, Func<int, string> unitName)
    {
        var target = People.FirstOrDefault(t => t.Id == p.Target);
        switch (p.Task)
        {
            case W.TaskKind.Hose:
                if (Fire[p.TaskY * Size + p.TaskX] == 0 && !AnyFireNear(p.TaskX, p.TaskY)) { p.Task = W.TaskKind.None; break; }
                if (Dist(p.X, p.Y, p.TaskX, p.TaskY) > 3) { Approach(p, p.TaskX, p.TaskY); break; }
                p.Path.Clear();
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (In(p.TaskX + dx, p.TaskY + dy))
                    {
                        // Breathing gear lets a crew work closer in, and harder.
                        int i = (p.TaskY + dy) * Size + p.TaskX + dx, k = Has(p.Unit, BreathingGear) ? 2 : 0;
                        Fire[i] = (byte)Math.Max(0, Fire[i] - (dx == 0 && dy == 0 ? 5 + k : 3 + k / 2));
                    }
                break;
            case W.TaskKind.Treat:
                if (target == null || target.Dead || target.Stable) { p.Task = W.TaskKind.None; break; }
                if (Dist(p.X, p.Y, target.X, target.Y) > 1) { Approach(p, target.X, target.Y); break; }
                p.Path.Clear();
                target.Health = Math.Min(100, target.Health + (Has(p.Unit, Defibrillator) ? 4 : 2));
                if (target.Health >= 70) { target.Stable = true; p.Task = W.TaskKind.None; Event($"{unitName(p.Unit)}'s paramedic has a casualty stable."); }
                break;
            case W.TaskKind.Warn:
                if (target == null || target.Down(Step)) { p.Task = W.TaskKind.None; break; }
                if (Dist(p.X, p.Y, target.X, target.Y) > 8 || !Clear(p.X, p.Y, target.X, target.Y)) { Approach(p, target.X, target.Y); break; }
                p.Path.Clear();
                target.Warned = true;
                if (!target.Surrendered && Chance(target.Armed ? 3 : 8)) { target.Surrendered = true; Event("The suspect puts their hands up."); }
                if (target.Surrendered) p.Task = W.TaskKind.Arrest;
                break;
            case W.TaskKind.Arrest:
                if (target == null || target.Cuffed || target.Dead || target.Escaped) { p.Task = W.TaskKind.None; break; }
                if (Dist(p.X, p.Y, target.X, target.Y) > 1) { Approach(p, target.X, target.Y); break; }
                p.Path.Clear();
                if (!target.Armed || target.Surrendered || target.Down(Step) || Chance(20))
                {
                    target.Cuffed = true;
                    p.Task = W.TaskKind.None;
                    Event($"{unitName(p.Unit)} made an arrest.");
                }
                break;
            case W.TaskKind.Breach:
                Breach(p, unitName);
                break;
            case W.TaskKind.Cordon:
                // Hold the spot: the cordon works by standing there.
                if (p.X != p.TaskX || p.Y != p.TaskY) Approach(p, p.TaskX, p.TaskY);
                break;
            case W.TaskKind.Search:
                if (Dist(p.X, p.Y, p.TaskX, p.TaskY) > 1) { Approach(p, p.TaskX, p.TaskY); break; }
                p.Path.Clear();
                int found = 0;
                foreach (var h in People.Where(h => h.Hidden && Dist(h.X, h.Y, p.X, p.Y) <= 8)) { h.Hidden = false; found++; }
                Event(found > 0 ? $"{unitName(p.Unit)} found someone hiding." : $"{unitName(p.Unit)} searched and found nobody.");
                p.Task = W.TaskKind.None;
                break;
            case W.TaskKind.Shoot or W.TaskKind.Taser:
                if (Step - p.LastShot < 6) break;
                p.LastShot = Step;
                Shoot(p, p.TaskX, p.TaskY, taser: p.Task == W.TaskKind.Taser, unitName);
                p.Task = W.TaskKind.None;
                break;
        }
    }

    bool AnyFireNear(int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (In(x + dx, y + dy) && Fire[(y + dy) * Size + x + dx] > 0) return true;
        return false;
    }

    /// <summary>The tiles on the line from one tile toward another, carried on to the given range.</summary>
    static IEnumerable<(int X, int Y)> Line(int x0, int y0, int x1, int y1, int range)
    {
        int dx = x1 - x0, dy = y1 - y0, n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (n == 0) yield break;
        for (int i = 1; i <= range; i++)
        {
            int x = x0 + (int)Math.Round((double)dx * i / n), y = y0 + (int)Math.Round((double)dy * i / n);
            if (!In(x, y)) yield break;
            yield return (x, y);
        }
    }

    /// <summary>Nothing solid between two tiles: buildings block a line of sight and a line of fire.</summary>
    bool Clear(int x0, int y0, int x1, int y1)
    {
        foreach (var (x, y) in Line(x0, y0, x1, y1, Dist(x0, y0, x1, y1)))
        {
            if (x == x1 && y == y1) return true;
            if (TileAt(x, y) == (byte)CellKind.Building) return false;
        }
        return true;
    }

    /// <summary>
    /// A shot goes along the line of fire and hits the first person on it, whoever they are: nothing aims for the
    /// player. Then it is judged against the rules of engagement.
    /// </summary>
    void Shoot(Person shooter, int tx, int ty, bool taser, Func<int, string> unitName)
    {
        int range = taser ? 5 : 14;
        Person? hit = null;
        int ex = shooter.X, ey = shooter.Y;
        // Inside a building, a shot carries only within it; outside, walls stop it.
        bool inside = TileAt(shooter.X, shooter.Y) == (byte)CellKind.Building;
        foreach (var (x, y) in Line(shooter.X, shooter.Y, tx, ty, range))
        {
            if ((TileAt(x, y) == (byte)CellKind.Building) != inside) break;
            (ex, ey) = (x, y);
            hit = People.Where(p => p != shooter && p.X == x && p.Y == y && !p.Dead && !p.Escaped && !p.Hidden).OrderBy(p => p.Id).FirstOrDefault();
            if (hit != null) break;
        }
        bool police = shooter.Role == W.Role.Officer;
        Shots.Add((Step, shooter.X, shooter.Y, ex, ey, hit != null, taser, police));
        if (Shots.Count > 8) Shots.RemoveAt(0);
        string who = police ? $"An officer from {unitName(shooter.Unit)}" : "The suspect";
        if (hit == null) { Event($"{who} fired and missed."); return; }
        if (taser) hit.StunnedUntil = Step + 40;
        else hit.Health -= IsCrew(hit) && (Has(hit.Unit, BodyArmour) || VehicleKind(hit.Unit) == W.UnitKind.Swat) ? 25 : 60;
        if (hit.Role == W.Role.Hostage && !police) { HostagesHurt++; Notes.Add("The suspect shot a hostage."); }
        string what = hit.Role switch
        {
            W.Role.Suspect => "the suspect",
            W.Role.Bystander => "a bystander",
            W.Role.Hostage => "a hostage",
            W.Role.Casualty => "a casualty",
            _ => $"one of {unitName(hit.Unit)}'s own crew",
        };
        Event($"{who} {(taser ? "tased" : "shot")} {what}.");
        if (shooter.Role != W.Role.Officer) { shooter.ShotAt = Step; return; }
        if (hit.Role == W.Role.Suspect)
        {
            // Force is justified against an armed suspect who was warned or who has been shooting.
            bool justified = hit.Armed && (hit.Warned || Step - hit.ShotAt < 40);
            if (!justified && !taser) { Unauthorized++; Notes.Add($"Unauthorised force: {unitName(shooter.Unit)} shot a suspect who was {(hit.Armed ? "never warned" : "unarmed")}."); }
            if (!justified && taser && !hit.Warned) { Unauthorized++; Notes.Add($"Unauthorised force: {unitName(shooter.Unit)} tased a suspect without a warning."); }
        }
        else if (IsCrew(hit)) { FriendlyFire++; Notes.Add($"Friendly fire: {unitName(shooter.Unit)} hit one of {unitName(hit.Unit)}'s crew."); }
        else
        {
            Harmed++;
            if (hit.Role == W.Role.Bystander) hit.Role = W.Role.Casualty;
            Notes.Add($"{unitName(shooter.Unit)} {(taser ? "tased" : "shot")} {what}.");
        }
    }

    void MoveVehicles(Settings settings, Func<int, string> unitName)
    {
        foreach (var v in Vehicles)
        {
            if (v.Path.Count == 0) continue;
            int next = v.Path[0], nx = next % Size, ny = next / Size;
            var inWay = People.Where(p => p.X == nx && p.Y == ny && !p.Dead && !p.Escaped).OrderBy(p => p.Id).ToList();
            if (inWay.Count > 0)
            {
                // The computer's own drivers, and anyone's with the switch on, stop for people. Otherwise they don't.
                if (settings.DriversWatchOut) continue;
                foreach (var p in inWay)
                {
                    p.Health -= 50;
                    if (p.Role == W.Role.Bystander) p.Role = W.Role.Casualty;
                    p.Stable = false;
                    Harmed++;
                    v.HitSomeone = true;
                    Event($"{unitName(v.Unit)} hit {(IsCrew(p) ? "one of its own crew" : p.Role == W.Role.Casualty && p.Health > -50 ? "someone" : "a person")}.");
                    Notes.Add($"{unitName(v.Unit)} drove into {(IsCrew(p) ? "a crew member" : "a person")}{(v.Drawn ? " along the path it was given" : "")}.");
                }
            }
            v.Path.RemoveAt(0);
            v.X = nx; v.Y = ny;
        }
    }

    void MovePeople(bool crew)
    {
        foreach (var p in People)
        {
            if (IsCrew(p) != crew || p.Path.Count == 0 || p.Down(Step)) continue;
            int next = p.Path[0];
            p.Path.RemoveAt(0);
            p.X = next % Size; p.Y = next / Size;
        }
    }

    void Suspects()
    {
        foreach (var s in People.Where(p => p.Role == W.Role.Suspect && !p.Down(Step) && !p.Surrendered && !p.Hidden))
        {
            var cop = People.Where(p => p.Role == W.Role.Officer && !p.Down(Step)).OrderBy(p => Dist(p.X, p.Y, s.X, s.Y)).ThenBy(p => p.Id).FirstOrDefault();
            if (cop == null) continue;
            int d = Dist(cop.X, cop.Y, s.X, s.Y);
            if (s.Armed && d <= 7 && Step - s.LastShot >= 12 && Clear(s.X, s.Y, cop.X, cop.Y) && Chance(50))
            {
                s.LastShot = Step;
                Shoot(s, cop.X, cop.Y, taser: false, _ => "");
                continue;
            }
            // A hostage-taker stays put with the hostages.
            if (Hostages || d > 10 || Step % 3 != 0) continue;
            // Run: the open neighbouring tile furthest from the officer.
            var (bx, by) = (s.X, s.Y);
            foreach (var (nx, ny) in new[] { (s.X + 1, s.Y), (s.X - 1, s.Y), (s.X, s.Y + 1), (s.X, s.Y - 1) })
                if (Walkable(nx, ny, false) && Dist(nx, ny, cop.X, cop.Y) > Dist(bx, by, cop.X, cop.Y)) (bx, by) = (nx, ny);
            s.X = bx; s.Y = by;
            if (People.Any(o => o.Task == W.TaskKind.Cordon && o.Role == W.Role.Officer && !o.Down(Step) && Dist(o.X, o.Y, s.X, s.Y) <= 2))
            {
                s.Cuffed = true;
                Event("The suspect ran into the cordon and was caught.");
            }
            else if (s.X == 0 || s.Y == 0 || s.X == Size - 1 || s.Y == Size - 1) { s.Escaped = true; Event("The suspect got away."); }
        }
    }

    /// <summary>An officer who comes within three tiles of someone hiding, with nothing in the way, sees them.</summary>
    void Spot()
    {
        foreach (var h in People.Where(h => h.Hidden))
            if (People.Any(o => o.Role == W.Role.Officer && !o.Down(Step) && Dist(o.X, o.Y, h.X, h.Y) <= 3 && Clear(o.X, o.Y, h.X, h.Y)))
            {
                h.Hidden = false;
                Event("An officer spotted the suspect hiding.");
            }
    }

    /// <summary>
    /// A hostage-taker with police close around and nobody going in grows desperate: now and then they hurt a hostage.
    /// Hostages go free once the suspect is down.
    /// </summary>
    void HoldHostages(Func<int, string> unitName)
    {
        if (!Hostages) return;
        var taker = People.FirstOrDefault(p => p.Role == W.Role.Suspect && p.Armed);
        bool held = taker != null && !taker.Down(Step) && !taker.Surrendered;
        foreach (var h in People.Where(h => h.Role == W.Role.Hostage && !h.Dead && !h.Stable))
        {
            if (!held) { h.Stable = true; Event("A hostage is free."); continue; }
            if (Step % 20 != 0) continue;
            int near = People.Count(o => o.Role == W.Role.Officer && !o.Down(Step) && Dist(o.X, o.Y, taker!.X, taker.Y) <= 6 && o.Task != W.TaskKind.Breach);
            if (near > 0 && Chance(6 + 2 * near))
            {
                h.Health -= 60;
                HostagesHurt++;
                Event(h.Dead ? "A hostage has been killed." : "The suspect hurt a hostage.");
                Notes.Add("The suspect hurt a hostage while officers closed in without going in.");
            }
        }
    }

    /// <summary>
    /// A SWAT officer goes in: up to the building, then into it. Two or more going in together at the same step
    /// stun everyone inside for a moment; one alone gives the suspect a chance to fire first.
    /// </summary>
    void Breach(Person p, Func<int, string> unitName)
    {
        if (TileAt(p.TaskX, p.TaskY) != (byte)CellKind.Building) { p.Task = W.TaskKind.None; return; }
        var building = Building(p.TaskX, p.TaskY).Select(c => (X: c.Item1, Y: c.Item2)).ToHashSet();
        // A block of buildings can run on a long way: the walls that count are the ones near the target.
        var walls = building.Where(c => Dist(c.X, c.Y, p.TaskX, p.TaskY) <= 3).ToList();
        // The team stacks up beside the building's walls: anywhere next to it will do for a door.
        bool AtWall(Person o) => walls.Any(c => Dist(o.X, o.Y, c.X, c.Y) <= 1);
        if (!AtWall(p))
        {
            if (p.Path.Count > 0 && walls.Any(c => Dist(p.Path[^1] % Size, p.Path[^1] / Size, c.X, c.Y) <= 1)) return;
            var door = walls.SelectMany(c => Enumerable.Range(0, 9).Select(k => (X: c.X + k % 3 - 1, Y: c.Y + k / 3 - 1)))
                .Where(t => In(t.X, t.Y) && Walkable(t.X, t.Y, crew: true))
                .Distinct().OrderBy(t => Dist(p.X, p.Y, t.X, t.Y)).ThenBy(t => t.Y).ThenBy(t => t.X).FirstOrDefault((X: -1, Y: -1));
            if (door.X < 0) { p.Task = W.TaskKind.None; return; }
            p.Path = FindPath(p.X, p.Y, door.X, door.Y, (tx, ty) => Walkable(tx, ty, crew: true));
            return;
        }
        p.Path.Clear();
        var team = People.Where(o => o.Task == W.TaskKind.Breach && !o.Down(Step) && o.TaskX == p.TaskX && o.TaskY == p.TaskY && AtWall(o)).ToList();
        // The team goes in on its first member's step; the rest wait for them.
        if (team.Count == 0 || team.Min(o => o.Id) != p.Id) return;
        Breaches++;
        foreach (var o in team) { o.X = p.TaskX; o.Y = p.TaskY; o.Task = W.TaskKind.None; o.Path.Clear(); }
        var inside = People.Where(o => o.Role == W.Role.Suspect && !o.Down(Step) && building.Contains((X: o.X, Y: o.Y))).ToList();
        if (team.Count >= 2)
        {
            foreach (var s in inside) { s.StunnedUntil = Step + 30; s.Hidden = false; }
            Event($"{unitName(p.Unit)} breached: flashbangs, and everyone inside is stunned.");
            Notes.Add($"{unitName(p.Unit)} breached with {team.Count} officers.");
            foreach (var o in team) if (inside.FirstOrDefault() is { } s) { o.Task = W.TaskKind.Arrest; o.Target = s.Id; }
        }
        else
        {
            Event($"{unitName(p.Unit)} went in alone.");
            Notes.Add($"{unitName(p.Unit)} went in alone.");
            foreach (var s in inside.Where(s => s.Armed && Chance(50))) { s.LastShot = Step; Shoot(s, p.X, p.Y, taser: false, _ => ""); }
        }
    }

    void Bystanders()
    {
        if (Step % 6 != 0) return;
        foreach (var b in People.Where(p => p.Role == W.Role.Bystander && !p.Down(Step)))
        {
            bool fireNear = false;
            for (int dy = -2; dy <= 2 && !fireNear; dy++)
            for (int dx = -2; dx <= 2 && !fireNear; dx++)
                fireNear = In(b.X + dx, b.Y + dy) && Fire[(b.Y + dy) * Size + b.X + dx] > 0;
            int dir = (int)(Next() % 8);
            // A cordon moves onlookers back from it.
            var cordon = People.Where(o => o.Task == W.TaskKind.Cordon && !o.Down(Step) && Dist(o.X, o.Y, b.X, b.Y) <= 6).OrderBy(o => o.Id).FirstOrDefault();
            if (cordon != null)
            {
                int ax = Math.Sign(b.X - cordon.X), ay = Math.Sign(b.Y - cordon.Y);
                if (ax == 0 && ay == 0) ax = 1;
                if (Walkable(b.X + ax, b.Y, false) && ax != 0) { b.X += ax; continue; }
                if (Walkable(b.X, b.Y + ay, false) && ay != 0) { b.Y += ay; continue; }
            }
            if (!fireNear && dir >= 4) continue;
            var (nx, ny) = (dir % 4) switch { 0 => (b.X + 1, b.Y), 1 => (b.X - 1, b.Y), 2 => (b.X, b.Y + 1), _ => (b.X, b.Y - 1) };
            if (Walkable(nx, ny, false)) { b.X = nx; b.Y = ny; }
        }
    }

    void BurnAndSpread()
    {
        var lit = new List<int>();
        for (int i = 0; i < Fire.Length; i++)
        {
            if (Fire[i] == 0) continue;
            Fire[i] = (byte)Math.Min(100, Fire[i] + 2);
            _peak[i] = Math.Max(_peak[i], Fire[i]);
            if (Fire[i] >= 100 && Chance(5)) { Fire[i] = 0; Tiles[i] = Burnt; continue; }
            if (Fire[i] < 50) continue;
            int x = i % Size, y = i / Size;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                if (In(nx, ny) && Tiles[ny * Size + nx] == (byte)CellKind.Building && Fire[ny * Size + nx] == 0 && Chance(Fire[i] / 10)) lit.Add(ny * Size + nx);
        }
        foreach (int i in lit) Fire[i] = Math.Max(Fire[i], (byte)20);
    }

    void Hurt()
    {
        foreach (var p in People)
        {
            if (p.Dead || p.Escaped) continue;
            if (Fire[p.Y * Size + p.X] > 30 && (IsCrew(p) ? Step % 2 == 0 && !Has(p.Unit, BreathingGear) : true)) p.Health--;
            if (p.Role == W.Role.Casualty && !p.Stable && Step % (12 - 3 * Severity) == 0) p.Health--;
            if (p.Dead && p.Role is W.Role.Casualty or W.Role.Bystander or W.Role.Hostage) Event("Someone at the scene has died.");
        }
    }

    // -----------------------------------------------------------------
    // Finishing
    // -----------------------------------------------------------------

    /// <summary>The crews finish what's left the way the computer would: the units that came decide it.</summary>
    public void FinishByCrews()
    {
        bool engines = Vehicles.Any(v => v.Kind == W.UnitKind.Engine) || Arriving.Any(a => a.Kind == W.UnitKind.Engine);
        bool medics = Vehicles.Any(v => v.Kind == W.UnitKind.Ambulance) || Arriving.Any(a => a.Kind == W.UnitKind.Ambulance);
        bool police = Vehicles.Any(v => v.Kind is W.UnitKind.Patrol or W.UnitKind.Swat) || Arriving.Any(a => a.Kind is W.UnitKind.Patrol or W.UnitKind.Swat);
        bool swat = Vehicles.Any(v => v.Kind == W.UnitKind.Swat) || Arriving.Any(a => a.Kind == W.UnitKind.Swat);
        for (int i = 0; i < Fire.Length; i++)
            if (Fire[i] > 0)
            {
                if (!engines || Chance(10)) Tiles[i] = Burnt;
                Fire[i] = 0;
            }
        foreach (var p in People.Where(p => p.Role == W.Role.Casualty && !p.Stable && !p.Dead))
            if (medics && Chance(85)) p.Stable = true; else p.Health = 0;
        foreach (var p in People.Where(p => p.Role == W.Role.Suspect && !p.Cuffed && !p.Escaped && !p.Dead))
            if (police && Chance(Hostages ? (swat ? 75 : 35) : 55)) p.Cuffed = true; else p.Escaped = true;
        // The crews' own way with a hostage-taker: SWAT usually gets everyone out; without them it goes worse.
        foreach (var h in People.Where(h => h.Role == W.Role.Hostage && !h.Stable && !h.Dead))
            if (Chance(swat ? 85 : 55)) h.Stable = true; else { h.Health = 0; HostagesHurt++; }
        Arriving.Clear();
    }

    public int Burned => Tiles.Count(t => t == Burnt);
    public int PropertyLostPercent => BuildingTiles == 0 ? 0 : Math.Min(100, Burned * 100 / BuildingTiles);
    public int Saved => People.Count(p => p.Role is W.Role.Casualty or W.Role.Hostage && p.Stable && !p.Dead);
    public int Lost => People.Count(p => p.Role is W.Role.Casualty or W.Role.Bystander or W.Role.Hostage && p.Dead);
    public int HostagesFreed => People.Count(p => p.Role == W.Role.Hostage && p.Stable && !p.Dead);
    public int HostagesLost => People.Count(p => p.Role == W.Role.Hostage && p.Dead);
    /// <summary>How well force kept to the rules, 0..100: unauthorised force and friendly fire count most, then harm to anyone else.</summary>
    public int RulesOfEngagement => Math.Clamp(100 - 25 * Unauthorized - 30 * FriendlyFire - 20 * Harmed, 0, 100);

    /// <summary>How the call ended, in the city's terms (contract v1.1).</summary>
    public string Outcome => Kind switch
    {
        "fire" => PropertyLostPercent > 40 || Burned > 2 * TilesPerCell * TilesPerCell ? "spread" : "out",
        "crime" => People.Where(p => p.Role == W.Role.Suspect).All(p => p.Cuffed || p.Dead) && HostagesLost == 0 ? "arrest"
            : People.Where(p => p.Role == W.Role.Suspect).All(p => p.Cuffed || p.Dead) ? "died" : "escaped",
        _ => Lost > 0 ? "died" : "treated",
    };

    /// <summary>
    /// The scene's report card: lives saved count for, and harm the crews caused costs more than a slow response.
    /// </summary>
    public W.ReportCard Card()
    {
        int score = 60 + 15 * Saved - 25 * Lost - 40 * Harmed - 30 * FriendlyFire - 30 * Unauthorized - PropertyLostPercent / 2 - 3 * Math.Max(0, Minutes - 8);
        if (Outcome is "out" or "arrest" or "treated") score += 20;
        var c = new W.ReportCard
        {
            SceneId = Id, Kind = Kind, Where = Where, ResponseMinutes = Minutes, Saved = Saved, Lost = Lost, HarmedByCrews = Harmed,
            FriendlyFire = FriendlyFire, UnauthorizedForce = Unauthorized, PropertyLostPercent = PropertyLostPercent,
            Score = Math.Clamp(score, -100, 100), Outcome = Outcome, RulesOfEngagement = RulesOfEngagement,
            HostagesFreed = HostagesFreed, HostagesLost = HostagesLost,
        };
        c.Notes.AddRange(Notes.Take(12));
        return c;
    }

    // -----------------------------------------------------------------
    // The view and saving
    // -----------------------------------------------------------------

    public W.SceneView ToView()
    {
        var v = new W.SceneView
        {
            Id = Id, CallId = CallId, Kind = Kind, OriginX = OriginX, OriginY = OriginY, Width = Size, Height = Size,
            Tiles = Google.Protobuf.ByteString.CopyFrom(Tiles), Fire = Google.Protobuf.ByteString.CopyFrom(Fire), Step = Step,
            Burning = Fire.Count(f => f > 0),
        };
        foreach (var p in People)
        {
            if (p.Hidden) continue;
            var pv = new W.PersonView
            {
                Id = p.Id, Role = p.Role, X = p.X * 16 + 8, Y = p.Y * 16 + 8, Health = Math.Max(0, p.Health), Task = p.Task, UnitId = p.Unit, Armed = p.Armed,
                Down = p.Down(Step),
                State = p.Dead ? "dead" : p.Escaped ? "escaped" : p.Cuffed ? "cuffed" : p.StunnedUntil > Step ? "stunned"
                    : p.Stable ? (p.Role == W.Role.Hostage ? "freed" : "stable") : p.Surrendered ? "surrendered" : "ok",
            };
            pv.Path.AddRange(p.Path.Take(60));
            v.People.Add(pv);
        }
        foreach (var veh in Vehicles)
        {
            var vv = new W.VehicleView { Id = veh.Unit, Kind = veh.Kind, X = veh.X * 16 + 8, Y = veh.Y * 16 + 8 };
            vv.Path.AddRange(veh.Path.Take(60));
            v.Vehicles.Add(vv);
        }
        foreach (var a in Arriving) v.Arriving.Add(new W.Arrival { UnitId = a.Unit, AtStep = a.AtStep });
        v.Events.AddRange(Events);
        foreach (var sh in Shots.Where(sh => Step - sh.Step <= 12))
            v.Shots.Add(new W.ShotView { FromX = sh.FromX * 16 + 8, FromY = sh.FromY * 16 + 8, ToX = sh.ToX * 16 + 8, ToY = sh.ToY * 16 + 8, Step = sh.Step, Hit = sh.Hit, Taser = sh.Taser, Police = sh.Police });
        return v;
    }

    public void Save(BinaryWriter w)
    {
        w.Write(Id); w.Write(CallId); w.Write(IncidentId); w.Write(Kind); w.Write(Severity); w.Write(OriginX); w.Write(OriginY);
        w.Write(Minutes); w.Write(Step); w.Write(Where); w.Write(Entry); w.Write(BuildingTiles); w.Write(Harmed); w.Write(FriendlyFire);
        w.Write(Unauthorized); w.Write(_rng); w.Write(_nextPerson); w.Write(Touched);
        w.Write(Tiles); w.Write(Fire); w.Write(_peak);
        w.Write(People.Count);
        foreach (var p in People)
        {
            w.Write(p.Id); w.Write((int)p.Role); w.Write(p.X); w.Write(p.Y); w.Write(p.Health); w.Write((int)p.Task); w.Write(p.TaskX); w.Write(p.TaskY);
            w.Write(p.Target); w.Write(p.Unit); w.Write(p.Armed); w.Write(p.Warned); w.Write(p.Surrendered); w.Write(p.Cuffed); w.Write(p.Escaped);
            w.Write(p.Stable); w.Write(p.StunnedUntil); w.Write(p.ShotAt); w.Write(p.LastShot);
            w.Write(p.Path.Count); foreach (var t in p.Path) w.Write(t);
        }
        w.Write(Vehicles.Count);
        foreach (var v in Vehicles)
        {
            w.Write(v.Unit); w.Write((int)v.Kind); w.Write(v.X); w.Write(v.Y); w.Write(v.Drawn); w.Write(v.HitSomeone);
            w.Write(v.Path.Count); foreach (var t in v.Path) w.Write(t);
        }
        w.Write(Arriving.Count);
        foreach (var a in Arriving) { w.Write(a.Unit); w.Write((int)a.Kind); w.Write(a.Crew); w.Write(a.AtStep); }
        w.Write(Events.Count); foreach (var e in Events) w.Write(e);
        w.Write(Notes.Count); foreach (var n in Notes) w.Write(n);
    }

    /// <summary>What part 11 added, saved after the rest of the service so older saves still load.</summary>
    public void SaveMore(BinaryWriter w)
    {
        w.Write(Hostages); w.Write(HostagesHurt); w.Write(Breaches);
        w.Write(Gear.Count); foreach (var (u, g) in Gear) { w.Write(u); w.Write(g); }
        foreach (var p in People) w.Write(p.Hidden);
        w.Write(Shots.Count);
        foreach (var sh in Shots) { w.Write(sh.Step); w.Write(sh.FromX); w.Write(sh.FromY); w.Write(sh.ToX); w.Write(sh.ToY); w.Write(sh.Hit); w.Write(sh.Taser); w.Write(sh.Police); }
    }

    public void LoadMore(BinaryReader r)
    {
        Hostages = r.ReadBoolean(); HostagesHurt = r.ReadInt32(); Breaches = r.ReadInt32();
        int ng = r.ReadInt32(); for (int i = 0; i < ng; i++) Gear[r.ReadInt32()] = r.ReadInt32();
        foreach (var p in People) p.Hidden = r.ReadBoolean();
        int ns = r.ReadInt32();
        for (int i = 0; i < ns; i++) Shots.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean()));
    }

    public static Scene Load(BinaryReader r)
    {
        var s = new Scene
        {
            Id = r.ReadInt32(), CallId = r.ReadInt32(), IncidentId = r.ReadInt64(), Kind = r.ReadString(), Severity = r.ReadInt32(),
            OriginX = r.ReadInt32(), OriginY = r.ReadInt32(), Minutes = r.ReadInt32(), Step = r.ReadInt32(), Where = r.ReadString(),
            Entry = r.ReadInt32(), BuildingTiles = r.ReadInt32(), Harmed = r.ReadInt32(), FriendlyFire = r.ReadInt32(), Unauthorized = r.ReadInt32(),
            _rng = r.ReadUInt64(), _nextPerson = r.ReadInt32(), Touched = r.ReadInt64(),
        };
        s.Tiles = r.ReadBytes(Size * Size); s.Fire = r.ReadBytes(Size * Size);
        r.ReadBytes(Size * Size).CopyTo(s._peak, 0);
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++)
        {
            var p = new Person
            {
                Id = r.ReadInt32(), Role = (W.Role)r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(), Health = r.ReadInt32(), Task = (W.TaskKind)r.ReadInt32(),
                TaskX = r.ReadInt32(), TaskY = r.ReadInt32(), Target = r.ReadInt32(), Unit = r.ReadInt32(), Armed = r.ReadBoolean(), Warned = r.ReadBoolean(),
                Surrendered = r.ReadBoolean(), Cuffed = r.ReadBoolean(), Escaped = r.ReadBoolean(), Stable = r.ReadBoolean(), StunnedUntil = r.ReadInt32(),
                ShotAt = r.ReadInt32(), LastShot = r.ReadInt32(),
            };
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++) p.Path.Add(r.ReadInt32());
            s.People.Add(p);
        }
        int nv = r.ReadInt32();
        for (int i = 0; i < nv; i++)
        {
            var v = new Vehicle { Unit = r.ReadInt32(), Kind = (W.UnitKind)r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(), Drawn = r.ReadBoolean(), HitSomeone = r.ReadBoolean() };
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++) v.Path.Add(r.ReadInt32());
            s.Vehicles.Add(v);
        }
        int na = r.ReadInt32();
        for (int i = 0; i < na; i++) s.Arriving.Add((r.ReadInt32(), (W.UnitKind)r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        int ne = r.ReadInt32();
        for (int i = 0; i < ne; i++) s.Events.Add(r.ReadString());
        int nn = r.ReadInt32();
        for (int i = 0; i < nn; i++) s.Notes.Add(r.ReadString());
        return s;
    }
}

/// <summary>The player's automation switches.</summary>
sealed class Settings
{
    public W.CommandStyle Style = W.CommandStyle.SceneCommand;
    public bool PickResponders, CrewsUseHeads = true, DriversWatchOut = true;
}
