using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>A tornado or the Colossus that walked off this city's map into a neighbouring city, kept for a day.</summary>
public sealed record Departure(DisasterKind Kind, int Side, int K, int Dx, int Dy, int DaysLeft, long Tick);

/// <summary>
/// Disasters across borders. Fire at a building on the border can jump to the neighbour's building across it, and a
/// tornado or the Clockwork Colossus that walks off the map carries on into the city next door, if there is one. Both
/// go through the border exchange, so each city's log replays them alone. Whatever crosses obeys the stricter of the
/// two cities' rules: nothing crosses into a city with disasters off, and the Colossus stops at the border of a city
/// that forbids absurd disasters.
/// </summary>
public sealed partial class World
{
    /// <summary>Movers that left over a border yesterday, for the region to hand on. Saved.</summary>
    public readonly List<Departure> Departures = new();

    /// <summary>A mover leaving the map: kept for the neighbour if there's a city across that side.</summary>
    void Depart(Disaster d)
    {
        if (d.Kind is not (DisasterKind.Tornado or DisasterKind.Robot) || d.DaysLeft <= 0) return;
        int side = d.CellY < 0 ? 0 : d.CellX >= Width ? 1 : d.CellY >= HeightCells ? 2 : d.CellX < 0 ? 3 : -1;
        if (side < 0 || NextDoor[side] is not { } n) return;
        int k = side is 0 or 2 ? Math.Clamp(d.CellX, 0, Width - 1) : Math.Clamp(d.CellY, 0, HeightCells - 1);
        Departures.Add(new Departure(d.Kind, side, k, d.Dx, d.Dy, d.DaysLeft, Tick));
        Notices.Add(new SimNotice($"The {DisasterName(d.Kind)} crossed the border into {n.Name}.", "info", Math.Clamp(d.CellX, 0, Width - 1), Math.Clamp(d.CellY, 0, HeightCells - 1)));
    }

    /// <summary>Why nothing of this kind may cross into this city, or null when it may.</summary>
    string? BarsArrival(DisasterKind kind)
    {
        if (DisasterSetting == DisasterSetting.Off) return $"{Name} has disasters turned off";
        if (kind == DisasterKind.Robot && !Allowed(Mechanic.AbsurdDisasters)) return $"{Name}'s rules forbid absurd disasters";
        return null;
    }

    /// <summary>A mover walks in over a side from the neighbour there, keeping its heading and the days it has left.</summary>
    void Arrive(int side, string from, DisasterKind kind, int k, int dx, int dy, int daysLeft)
    {
        if (!Enum.IsDefined(kind) || kind is not (DisasterKind.Tornado or DisasterKind.Robot) || daysLeft <= 0) return;
        k = Math.Clamp(k, 0, SideLength(side) - 1);
        int i = SideCell(side, k);
        int x = i % Width, y = i / Width;
        if (BarsArrival(kind) is { } why)
        {
            Notices.Add(new SimNotice($"The {DisasterName(kind)} from {from} stopped at the border: {why}.", "info", x, y));
            return;
        }
        var d = new Disaster
        {
            Id = _nextDisasterId++, Kind = kind, X = (x << 8) + 128, Y = (y << 8) + 128, Dx = dx, Dy = dy,
            DaysLeft = Math.Min(daysLeft, RobotDays), Started = Tick,
        };
        Disasters.Add(d);
        string text = $"The {DisasterName(kind)} from {from} has crossed into {Name} near {StreetNear(x, y)}!";
        Notices.Add(new SimNotice(text, "warning", x, y));
        AddHeadline(text, "warning", x, y);
        RaiseIncident(DisasterName(kind), x, y, "city", "disaster", 3, text);
    }

    /// <summary>Daily, before the flames spread: fire at the neighbour's border buildings may catch this city's across it.</summary>
    void CatchFireAcrossBorders()
    {
        Departures.RemoveAll(d => d.Tick < Tick);
        if (DisasterSetting == DisasterSetting.Off) return;
        for (int side = 0; side < 4; side++)
        {
            if (NextDoor[side] is not { } n || n.Fire.Length == 0) continue;
            int inward = side switch { 0 => Width, 1 => -1, 2 => -Width, _ => 1 };
            bool told = false;
            for (int k = 0; k < n.Fire.Length; k++)
            {
                if (!n.Fire[k]) continue;
                int i = SideCell(side, k);
                var b = BuildingAtCell(i) ?? BuildingAtCell(i + inward);
                if (b is not { BurningDays: 0 } || Held(b.X, b.Y, "fire")) continue;
                if (Rng.Next(100) >= 20 * WeatherFirePct / 100) continue;
                Ignite(b);
                if (told) continue;
                told = true;
                string text = $"Fire spread across the border from {n.Name}!";
                Notices.Add(new SimNotice(text, "warning", b.X, b.Y));
                AddHeadline(text, "warning", b.X, b.Y);
            }
        }
    }

    void WriteRegionState(BinaryWriter w)
    {
        w.Write((byte)Weather); w.Write(WeatherStrength);
        w.Write(Departures.Count);
        foreach (var d in Departures)
        {
            w.Write((byte)d.Kind); w.Write(d.Side); w.Write(d.K); w.Write(d.Dx); w.Write(d.Dy); w.Write(d.DaysLeft); w.Write(d.Tick);
        }
    }

    void ReadRegionState(BinaryReader r)
    {
        Weather = (WeatherKind)r.ReadByte(); WeatherStrength = r.ReadInt32();
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++)
            Departures.Add(new Departure((DisasterKind)r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt64()));
    }
}
