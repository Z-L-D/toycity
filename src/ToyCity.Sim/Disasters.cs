namespace ToyCity.Sim;

public enum DisasterKind : byte { Fire = 0, Tornado = 1, Earthquake = 2, Robot = 3 }

/// <summary>How often disasters happen by themselves. The sandbox menu works whatever this is set to.</summary>
public enum DisasterSetting : byte { Off = 0, Rare = 1, Normal = 2 }

/// <summary>
/// A disaster in progress. Positions are in 1/256ths of a cell so movers can travel at fractional speeds with
/// integer math. Fires are tracked here for their location and toll; the flames themselves live on buildings.
/// </summary>
public sealed class Disaster
{
    public int Id;
    public DisasterKind Kind;
    public int X, Y;     // 1/256 cell
    public int Dx, Dy;   // 1/256 cell per day
    public int DaysLeft;
    public long Started;
    public int Destroyed;
    public int Damaged;

    public int CellX => X >> 8;
    public int CellY => Y >> 8;
}

public sealed record TriggerDisasterAction(DisasterKind Kind, int X, int Y) : SimAction;
public sealed record SetDisasterSettingAction(DisasterSetting Setting) : SimAction;

public sealed partial class World
{
    public const int BurnDaysToDestroy = 6;
    public const int QuakeRadius = 14;
    public const int RobotDays = 60;

    public readonly List<Disaster> Disasters = new();
    public DisasterSetting DisasterSetting = DisasterSetting.Rare;
    int _nextDisasterId = 1;
    static readonly int[] Sin16 = { 0, 383, 707, 924, 1000, 924, 707, 383, 0, -383, -707, -924, -1000, -924, -707, -383 };

    static string DisasterName(DisasterKind k) => k switch
    {
        DisasterKind.Fire => "fire", DisasterKind.Tornado => "tornado", DisasterKind.Earthquake => "earthquake", _ => "robot",
    };

    /// <summary>Starts a disaster at a cell: from the sandbox menu, or from the monthly roll.</summary>
    public void StartDisaster(DisasterKind kind, int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, HeightCells - 1);
        var d = new Disaster { Id = _nextDisasterId++, Kind = kind, X = (x << 8) + 128, Y = (y << 8) + 128, Started = Tick };
        switch (kind)
        {
            case DisasterKind.Fire:
            {
                // Light the building at the spot, or the nearest one within a few cells.
                Building? target = null;
                int best = int.MaxValue;
                for (int yy = Math.Max(0, y - 4); yy <= Math.Min(HeightCells - 1, y + 4); yy++)
                for (int xx = Math.Max(0, x - 4); xx <= Math.Min(Width - 1, x + 4); xx++)
                {
                    var b = BuildingAtCell(Idx(xx, yy));
                    int dist = Math.Abs(xx - x) + Math.Abs(yy - y);
                    if (b != null && b.BurningDays == 0 && dist < best) { best = dist; target = b; }
                }
                if (target == null) { Reject("Nothing to burn there.", x, y); return; }
                Ignite(target);
                d.X = (target.X << 8) + 128;
                d.Y = (target.Y << 8) + 128;
                d.DaysLeft = 1;
                break;
            }
            case DisasterKind.Tornado:
            {
                // A random heading from 16 compass points, in integers so every machine agrees.
                int h = Rng.Next(16);
                d.Dx = Sin16[(h + 4) % 16] * 400 / 1000;
                d.Dy = Sin16[h] * 400 / 1000;
                d.DaysLeft = 10 + Rng.Next(8);
                break;
            }
            case DisasterKind.Earthquake:
                Quake(d);
                d.DaysLeft = 3;
                break;
            case DisasterKind.Robot:
            {
                // It walks in from the nearest map edge, through the spot, and on until its spring runs down.
                int toLeft = x, toRight = Width - 1 - x, toTop = y, toBottom = HeightCells - 1 - y;
                int m = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
                if (m == toLeft) { d.X = 128; d.Dx = 256; }
                else if (m == toRight) { d.X = ((Width - 2) << 8) + 128; d.Dx = -256; }
                else if (m == toTop) { d.Y = 128; d.Dy = 256; }
                else { d.Y = ((HeightCells - 2) << 8) + 128; d.Dy = -256; }
                d.DaysLeft = RobotDays;
                break;
            }
        }
        Disasters.Add(d);
        var facts = new Dictionary<string, string> { ["street"] = StreetNear(d.CellX, d.CellY) };
        string text = Say($"disaster.{DisasterName(kind)}.start", d.Id, 0, facts);
        Notices.Add(new SimNotice(text, "warning", d.CellX, d.CellY));
        AddHeadline(text, "warning", d.CellX, d.CellY);
        RaiseIncident(DisasterName(kind), d.CellX, d.CellY, "city", "disaster", 3, text);
    }

    internal void Ignite(Building b)
    {
        if (b.BurningDays > 0) return;
        b.BurningDays = 1;
        RaiseIncident("fire", b.X, b.Y, "city", TypeOf(b).Name, 2, "");
        FireCasualty(b);
    }

    /// <summary>Damages a building. Returns true when it was destroyed.</summary>
    bool Damage(Building b, int amount, Disaster d)
    {
        b.Condition -= amount;
        d.Damaged++;
        if (b.Condition > 0) return false;
        RemoveBuilding(b);
        d.Destroyed++;
        return true;
    }

    void Quake(Disaster d)
    {
        int cx = d.CellX, cy = d.CellY;
        foreach (var b in Buildings.ToArray())
        {
            if (b == null) continue;
            int dist = DistToRect(cx, cy, b.X, b.Y, b.W, b.H);
            if (dist > QuakeRadius) continue;
            int amount = 90 * (QuakeRadius + 1 - dist) / (QuakeRadius + 1);
            if (!Damage(b, amount, d) && amount > 40 && Rng.Next(10) == 0) Ignite(b);
        }
    }

    /// <summary>Daily: flames spread or go out, movers move, finished disasters report their toll.</summary>
    void RunDisasters()
    {
        RunFires();
        foreach (var d in Disasters)
        {
            if (d.DaysLeft <= 0) continue;
            switch (d.Kind)
            {
                case DisasterKind.Fire:
                    // A fire lasts while any building it set alight still burns.
                    var burning = Buildings.FirstOrDefault(b => b is { BurningDays: > 0 });
                    if (burning == null) d.DaysLeft = 0;
                    else { d.X = (burning.X << 8) + 128; d.Y = (burning.Y << 8) + 128; }
                    break;
                case DisasterKind.Tornado:
                    d.Dx += Rng.Next(161) - 80;
                    d.Dy += Rng.Next(161) - 80;
                    Move(d);
                    StrikeAround(d, 1, 55);
                    d.DaysLeft--;
                    break;
                case DisasterKind.Earthquake:
                    d.DaysLeft--;
                    break;
                case DisasterKind.Robot:
                    Move(d);
                    StrikeAround(d, 0, 200, size: 2);
                    d.DaysLeft--;
                    break;
            }
            if (d.CellX < 0 || d.CellY < 0 || d.CellX >= Width || d.CellY >= HeightCells) d.DaysLeft = 0;
        }
        foreach (var d in Disasters.Where(d => d.DaysLeft <= 0).ToList())
        {
            Disasters.Remove(d);
            int x = Math.Clamp(d.CellX, 0, Width - 1), y = Math.Clamp(d.CellY, 0, HeightCells - 1);
            var facts = new Dictionary<string, string>
            {
                ["street"] = StreetNear(x, y), ["count"] = d.Destroyed.ToString("N0"), ["damaged"] = d.Damaged.ToString("N0"),
            };
            string text = Say($"disaster.{DisasterName(d.Kind)}.end", d.Id, 1, facts);
            Notices.Add(new SimNotice(text, "info", x, y));
            AddHeadline(text, "info", x, y);
        }
    }

    void Move(Disaster d)
    {
        // Keep movers at a steady speed: rescale the step to its kind's speed.
        int speed = d.Kind == DisasterKind.Robot ? 256 : 400;
        int len = Math.Max(1, ISqrt(d.Dx * d.Dx + d.Dy * d.Dy));
        d.Dx = d.Dx * speed / len;
        d.Dy = d.Dy * speed / len;
        d.X += d.Dx;
        d.Y += d.Dy;
    }

    void StrikeAround(Disaster d, int radius, int amount, int size = 1)
    {
        var hit = new SortedSet<int>();
        for (int y = d.CellY - radius; y < d.CellY + size + radius; y++)
        for (int x = d.CellX - radius; x < d.CellX + size + radius; x++)
        {
            if (!InBounds(x, y)) continue;
            int i = Idx(x, y);
            if (BuildingAt[i] != 0) hit.Add(BuildingAt[i] - 1);
            if (d.Kind == DisasterKind.Tornado) PowerLine[i] = 0;
        }
        foreach (int id in hit)
            if (Buildings[id] is { } b && !Damage(b, amount, d) && d.Kind == DisasterKind.Tornado && Rng.Next(5) == 0) Ignite(b);
    }

    void RunFires()
    {
        var burning = Buildings.Where(b => b is { BurningDays: > 0 }).Select(b => b!).ToList();
        if (burning.Count == 0) return;
        var fire = Disasters.FirstOrDefault(d => d.Kind == DisasterKind.Fire && d.DaysLeft > 0);
        if (fire == null)
        {
            // Fires lit by a tornado or quake get their own entry so they show up and report their toll.
            fire = new Disaster { Id = _nextDisasterId++, Kind = DisasterKind.Fire, X = (burning[0].X << 8) + 128, Y = (burning[0].Y << 8) + 128, Started = Tick, DaysLeft = 1 };
            Disasters.Add(fire);
        }
        foreach (var b in burning)
        {
            if (Buildings[b.Id] != b) continue;
            // A fire a player is fighting at the scene holds still until the scene ends.
            if (Held(b.X, b.Y, "fire")) continue;
            int cov = Coverage[(int)Service.Fire][Idx(b.X, b.Y)] * 100 / 255;
            if (Rng.Next(100) < 5 + cov / 3)
            {
                b.BurningDays = 0;
                b.Condition = Math.Max(1, b.Condition - 30);
                fire.Damaged++;
                continue;
            }
            // Spread to neighbours touching this footprint.
            int spread = (int)(Math.Max(2, 14 - cov / 8) * Math.Max(0, 100 + Mod(ModKey.FireSpread)) / 100);
            var near = new SortedSet<int>();
            for (int y = b.Y - 1; y <= b.Y + b.H; y++)
            for (int x = b.X - 1; x <= b.X + b.W; x++)
                if (InBounds(x, y) && BuildingAt[Idx(x, y)] is var at && at != 0 && at - 1 != b.Id) near.Add(at - 1);
            foreach (int id in near)
                if (Buildings[id] is { BurningDays: 0 } n && Rng.Next(100) < spread) Ignite(n);
            if (++b.BurningDays > BurnDaysToDestroy)
            {
                RemoveBuilding(b);
                fire.Destroyed++;
            }
        }
    }

    /// <summary>Monthly: a chance of a disaster by itself, by setting. Fires are the most common.</summary>
    void RollDisaster()
    {
        // A young town gets a grace period: nothing happens by itself in the first three years or under 1,000 people.
        if (DisasterSetting == DisasterSetting.Off || Population < 1000 || Tick < 3 * MonthsPerYear * DaysPerMonth) return;
        int oneIn = DisasterSetting == DisasterSetting.Rare ? 120 : 36;
        if (Rng.Next(oneIn) != 0) return;
        var built = Buildings.Where(b => b is { ConstructionLeft: 0 }).Select(b => b!).ToList();
        if (built.Count == 0) return;
        var at = built[Rng.Next(built.Count)];
        int roll = Rng.Next(100);
        var kind = roll < 60 ? DisasterKind.Fire : roll < 80 ? DisasterKind.Tornado : roll < 92 ? DisasterKind.Earthquake : DisasterKind.Robot;
        // The Colossus only bothers with cities worth stomping.
        if (kind == DisasterKind.Robot && (Population < 5000 || !Allowed(Mechanic.AbsurdDisasters))) kind = DisasterKind.Fire;
        StartDisaster(kind, at.X, at.Y);
    }

    void WriteDisasters(BinaryWriter w)
    {
        w.Write((byte)DisasterSetting); w.Write(_nextDisasterId);
        w.Write(Disasters.Count);
        foreach (var d in Disasters)
        {
            w.Write(d.Id); w.Write((byte)d.Kind); w.Write(d.X); w.Write(d.Y); w.Write(d.Dx); w.Write(d.Dy);
            w.Write(d.DaysLeft); w.Write(d.Started); w.Write(d.Destroyed); w.Write(d.Damaged);
        }
    }

    void ReadDisasters(BinaryReader r)
    {
        DisasterSetting = (DisasterSetting)r.ReadByte(); _nextDisasterId = r.ReadInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
            Disasters.Add(new Disaster
            {
                Id = r.ReadInt32(), Kind = (DisasterKind)r.ReadByte(), X = r.ReadInt32(), Y = r.ReadInt32(), Dx = r.ReadInt32(), Dy = r.ReadInt32(),
                DaysLeft = r.ReadInt32(), Started = r.ReadInt64(), Destroyed = r.ReadInt32(), Damaged = r.ReadInt32(),
            });
    }
}
