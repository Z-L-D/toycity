namespace ToyCity.Sim;

/// <summary>What one city sells another across a border.</summary>
public enum DealKind
{
    /// <summary>Kilowatts, over a power line that meets the neighbour's.</summary>
    Power,
    /// <summary>Water, over a pipe that meets the neighbour's.</summary>
    Water,
    /// <summary>Garbage the seller's dumps and incinerators take in, trucked over a road that meets.</summary>
    Garbage,
    /// <summary>A shuttle the seller runs across a road that meets: extra vehicles an hour each way.</summary>
    Transit,
}

/// <summary>A deal between two neighbouring cities, as one of them keeps it.</summary>
public sealed class CityDeal
{
    /// <summary>The region's number for the deal; the same in both cities.</summary>
    public int Id;
    public DealKind Kind;
    /// <summary>True in the city that sells (and is paid); false in the one that buys.</summary>
    public bool Selling;
    /// <summary>The side the other city is on.</summary>
    public int Side;
    public string Other = "";
    /// <summary>kW for power, water units for water, garbage units for garbage, vehicles an hour for transit.</summary>
    public int Amount;
    /// <summary>Paid monthly by the buyer to the seller.</summary>
    public int Price;
    public long Since;
    /// <summary>Price-days owed or due and not yet booked: thirtieths of a month's price. Booked at the budget.</summary>
    public long Accrued;
}

/// <summary>
/// The region starts or ends a deal in a city. Sent to both cities on the same day, so both pay or are paid for the
/// same days. Logged, so a city's log replays it without its neighbour.
/// </summary>
public sealed record CityDealAction(int Id, DealKind Kind, bool Selling, int Side, string Other, int Amount, int Price, bool End = false, string Why = "") : SimAction;

public sealed partial class World
{
    /// <summary>Deals with neighbouring cities, in the order they started. Saved.</summary>
    public readonly List<CityDeal> Deals = new();

    public static string DealNoun(DealKind k) => k switch
    {
        DealKind.Power => "power",
        DealKind.Water => "water",
        DealKind.Garbage => "garbage collection",
        _ => "a shuttle",
    };

    public static string DealUnit(DealKind k, int amount) => k switch
    {
        DealKind.Power => $"{amount:N0} kW",
        DealKind.Water => $"{amount:N0} water",
        DealKind.Garbage => $"{amount:N0} garbage",
        _ => $"{amount:N0} vehicles an hour",
    };

    /// <summary>How the budget names a deal: "Power sold to Eastfield", "Shuttle from Westbrook".</summary>
    public static string DealLabel(CityDeal d) => (d.Kind, d.Selling) switch
    {
        (DealKind.Power, true) => $"Power sold to {d.Other}",
        (DealKind.Power, false) => $"Power bought from {d.Other}",
        (DealKind.Water, true) => $"Water sold to {d.Other}",
        (DealKind.Water, false) => $"Water bought from {d.Other}",
        (DealKind.Garbage, true) => $"Garbage taken from {d.Other}",
        (DealKind.Garbage, false) => $"Garbage sent to {d.Other}",
        (_, true) => $"Shuttle run for {d.Other}",
        _ => $"Shuttle from {d.Other}",
    };

    /// <summary>
    /// The border cell where a deal of this kind crosses that side: this city's line, pipe or road meeting the
    /// neighbour's. -1 when there's none, and the deal carries nothing.
    /// </summary>
    public int DealCrossing(DealKind kind, int side)
    {
        if (NextDoor[side] is not { } n) return -1;
        for (int k = 1; k < SideLength(side) - 1; k++)
        {
            int i = SideCell(side, k);
            bool meets = kind switch
            {
                DealKind.Power => PowerLine[i] != 0 && n.Power[k],
                DealKind.Water => WaterPipe[i] != 0 && n.Pipe[k],
                _ => Path[i] != 0 && n.Path[k] != 0,
            };
            if (meets) return i;
        }
        return -1;
    }

    /// <summary>Why a deal this city sells can't go on: its supply at the crossing ran dry. Null while it can.</summary>
    public string? DealShort(CityDeal d)
    {
        if (!d.Selling) return null;
        int c = DealCrossing(d.Kind, d.Side);
        if (c < 0) return null;
        return d.Kind switch
        {
            DealKind.Power when Powered[c] == 0 => $"{Name} has no power to spare at the border",
            DealKind.Water when Watered[c] == 0 => $"{Name} has no water to spare at the border",
            _ => null,
        };
    }

    void ApplyCityDeal(CityDealAction a)
    {
        var d = Deals.FirstOrDefault(x => x.Id == a.Id);
        if (a.End)
        {
            if (d == null) return;
            Deals.Remove(d);
            // What's owed for the last days is settled now.
            BookDeal(d, null);
            Notices.Add(new SimNotice($"The {DealNoun(d.Kind)} deal with {d.Other} has ended{(a.Why.Length > 0 ? ": " + a.Why : "")}.", "info"));
            return;
        }
        if (d != null) return;
        Deals.Add(new CityDeal { Id = a.Id, Kind = a.Kind, Selling = a.Selling, Side = a.Side, Other = a.Other, Amount = a.Amount, Price = a.Price, Since = Tick });
        _edgeDirty = true;
        string what = a.Selling ? $"{Name} sells {a.Other} {DealUnit(a.Kind, a.Amount)}" : $"{a.Other} sells {Name} {DealUnit(a.Kind, a.Amount)}";
        if (a.Kind == DealKind.Garbage) what = a.Selling ? $"{Name} takes {DealUnit(a.Kind, a.Amount)} from {a.Other}" : $"{a.Other} takes {DealUnit(a.Kind, a.Amount)} from {Name}";
        if (a.Kind == DealKind.Transit) what = a.Selling ? $"{Name} runs a shuttle to {a.Other}" : $"{a.Other} runs a shuttle to {Name}";
        Notices.Add(new SimNotice($"Deal made: {what} for ${a.Price:N0} a month.", "good"));
    }

    /// <summary>Daily: each deal runs up a day's price, booked at the next budget.</summary>
    void AccrueDeals()
    {
        foreach (var d in Deals) d.Accrued += d.Price;
    }

    /// <summary>Books what's accrued on a deal: into the budget's lines at the month, or straight to the treasury when it ends.</summary>
    long BookDeal(CityDeal d, Action<string, long>? line)
    {
        long months = d.Accrued / DaysPerMonth;
        d.Accrued -= months * DaysPerMonth;
        long amount = d.Selling ? months : -months;
        if (line != null) line(DealLabel(d), amount);
        else Funds += amount;
        return amount;
    }

    /// <summary>The grid or pipe network a deal at this crossing feeds or draws from, for RunPower and RunWater.</summary>
    void DealCapacity(DealKind kind, int[] comp, long[] capacity)
    {
        foreach (var d in Deals)
        {
            if (d.Kind != kind) continue;
            int c = DealCrossing(kind, d.Side);
            if (c < 0 || comp[c] < 0) continue;
            int g = comp[c];
            capacity[g] = d.Selling ? Math.Max(0, capacity[g] - d.Amount) : capacity[g] + d.Amount;
        }
    }

    void WriteDeals(BinaryWriter w)
    {
        w.Write(Deals.Count);
        foreach (var d in Deals)
        {
            w.Write(d.Id); w.Write((int)d.Kind); w.Write(d.Selling); w.Write(d.Side); w.Write(d.Other);
            w.Write(d.Amount); w.Write(d.Price); w.Write(d.Since); w.Write(d.Accrued);
        }
    }

    void ReadDeals(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++)
            Deals.Add(new CityDeal
            {
                Id = r.ReadInt32(), Kind = (DealKind)r.ReadInt32(), Selling = r.ReadBoolean(), Side = r.ReadInt32(), Other = r.ReadString(),
                Amount = r.ReadInt32(), Price = r.ReadInt32(), Since = r.ReadInt64(), Accrued = r.ReadInt64(),
            });
    }
}
