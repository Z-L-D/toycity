using ToyCity.Contract;

namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>Ends every computer mayor's name. Account names can't hold brackets, so no player is ever taken for one.</summary>
    public const string ComputerTag = " (computer)";

    public static bool IsComputer(string owner) => owner.EndsWith(ComputerTag, StringComparison.Ordinal);

    static readonly string[][] ComputerNames =
    {
        new[] { "Gus", "Greta", "Gil", "Gwen" },     // growth
        new[] { "Fern", "Ash", "Rowan", "Hazel" },   // green
        new[] { "Penny", "Prue", "Silas", "Mona" },  // careful (profit)
        new[] { "Rex", "Dizzy", "Loki", "Pip" },     // chaos
    };

    /// <summary>A computer mayor's name for a temperament; the salt (a tile's) keeps neighbours from sharing one.</summary>
    public static string ComputerName(AutopilotStyle s, ulong salt)
    {
        var names = ComputerNames[Math.Clamp((int)s, 0, 3)];
        return names[(int)(salt % (ulong)names.Length)] + ComputerTag;
    }

    /// <summary>How the region map describes a computer mayor.</summary>
    public static string Temperament(AutopilotStyle s) => s switch
    {
        AutopilotStyle.Growth => "growth-hungry",
        AutopilotStyle.Green => "green",
        AutopilotStyle.Profit => "careful",
        _ => "unpredictable",
    };

    /// <summary>True while the computer is this city's mayor: it plays on autopilot and answers deals itself.</summary>
    public bool ComputerMayor => IsComputer(MayorSeat.Owner) && MayorSeat.Level == OperatorLevel.Autopilot;

    /// <summary>What a city has to spare of a deal's kind (negative when short). Transit has no measure: 0.</summary>
    public long Spare(DealKind kind) => kind switch
    {
        DealKind.Power => PowerCapacity - PowerDemand,
        DealKind.Water => WaterCapacity - WaterDemand,
        DealKind.Garbage => GarbageCapacity - GarbageProduced,
        _ => 0,
    };

    /// <summary>A fair monthly price, the one computer mayors start from: $1 for 5 kW or 5 water, 3 garbage, 2 vehicles an hour.</summary>
    public static int FairPrice(DealKind kind, int amount) => kind switch
    {
        DealKind.Power or DealKind.Water => amount / 5,
        DealKind.Garbage => amount / 3,
        _ => amount / 2,
    };
}

/// <summary>
/// Computer mayors: a tile founded with one, or a city handed to one, is played by Beta 2's autopilot in the mayor's
/// seat with a temperament (growth-hungry, green, careful). Each answers the deals offered to it by rules, and offers
/// to buy what it's short of from neighbours it's joined to. A player can take one's city over.
/// </summary>
public sealed partial class Region
{
    /// <summary>
    /// Hands a city to a computer mayor of this temperament, by logged actions: the seat changes owner, then goes on
    /// autopilot. Returns why not, or null.
    /// </summary>
    public string? HandToComputer(RegionTile t, AutopilotStyle style)
    {
        if (t.City is not { } w) return "There's no city there.";
        if (w.ComputerMayor && w.MayorSeat.Style == style) return $"{t.Name} already has a {World.Temperament(style)} computer mayor.";
        string name = World.ComputerName(style, t.Seed);
        if (w.MayorSeat.Owner != name) w.Enqueue(new RenameOwnerAction(w.MayorSeat.Owner, name));
        w.Enqueue(new SetSeatAction(Seat.Mayor, name, OperatorLevel.Autopilot, style) { By = name });
        w.ApplyPending();
        return null;
    }

    /// <summary>A player takes over a computer mayor's city and plays it by hand. Returns why not, or null.</summary>
    public string? TakeOver(RegionTile t, string player)
    {
        if (t.City is not { } w) return "There's no city there.";
        if (!World.IsComputer(w.MayorSeat.Owner)) return $"{t.Mayor} is {t.Name}'s mayor. Only a computer mayor's city can be taken over.";
        if (player.Length == 0 || World.IsComputer(player)) return "Sign in to take over a city.";
        w.Enqueue(new RenameOwnerAction(w.MayorSeat.Owner, player));
        w.Enqueue(new SetSeatAction(Seat.Mayor, player, OperatorLevel.Manual) { By = player });
        w.ApplyPending();
        return null;
    }

    /// <summary>How much more (or less) than the fair price a temperament asks when it sells, and will pay when it buys.</summary>
    static (int SellPct, int BuyPct) Terms(AutopilotStyle s) => s switch
    {
        AutopilotStyle.Profit => (125, 100),
        AutopilotStyle.Green => (90, 120),
        AutopilotStyle.Growth => (100, 150),
        _ => (100, 100),
    };

    /// <summary>
    /// Before each day: computer mayors answer what was offered them, and on the first of the month offer to buy what
    /// they're short of. Their answers reach the other mayor as a notice.
    /// </summary>
    void ComputerDeals()
    {
        foreach (var d in Deals.Where(x => !x.Active).ToList())
        {
            var t = Tiles[d.AnsweredBy];
            if (t.City is not { ComputerMayor: true } w) continue;
            AnswerByRules(d, t, w);
        }
        if (Tick % World.DaysPerMonth != 0) return;
        foreach (var t in Tiles)
        {
            if (t.City is not { ComputerMayor: true } w) continue;
            foreach (var kind in new[] { DealKind.Power, DealKind.Water, DealKind.Garbage })
            {
                long need = -w.Spare(kind);
                if (need <= 0) continue;
                int amount = (int)Math.Min(100_000, (need * 12 / 10 + 99) / 100 * 100);
                int price = World.FairPrice(kind, amount) * Terms(w.MayorSeat.Style).BuyPct / 100;
                if (w.Funds < price * 6L) continue;
                for (int side = 0; side < 4; side++)
                {
                    if (CantOffer(t, side, kind, amount, price) != null) continue;
                    var n = Across(t, side)!.City!;
                    // Only from a neighbour with that much to spare, as far as anyone can see.
                    if (n.Spare(kind) < amount) continue;
                    var offer = Offer(t, side, kind, amount, price, selling: false);
                    n.Notices.Add(new SimNotice($"{w.MayorSeat.Owner}, {w.Name}'s mayor, offers to buy {World.DealUnit(kind, amount)} for ${price:N0} a month. Answer on the region map.", "info"));
                    break;
                }
            }
        }
    }

    void AnswerByRules(RegionDeal d, RegionTile t, World w)
    {
        bool selling = Index(t) == d.Seller;
        var (sellPct, buyPct) = Terms(w.MayorSeat.Style);
        int fair = World.FairPrice(d.Kind, d.Amount);
        var other = Tiles[d.OfferedBy].City!;
        string who = $"{w.MayorSeat.Owner}, {w.Name}'s mayor,";
        string? Say(string text) { other.Notices.Add(new SimNotice($"{who} {text}", "info")); return null; }

        if (w.MayorSeat.Style == AutopilotStyle.Chaos)
        {
            bool yes = d.Id % 2 == 0;
            if (yes && Answer(t, d.Id, true) == null) Say("agreed on a whim.");
            else { Answer(t, d.Id, false); Say("turned it down on a whim."); }
            return;
        }
        if (selling)
        {
            long spare = w.Spare(d.Kind);
            long margin = Math.Max(50, (d.Kind == DealKind.Power ? w.PowerDemand : d.Kind == DealKind.Water ? w.WaterDemand : w.GarbageProduced) / 10);
            if (d.Kind != DealKind.Transit && spare < d.Amount + margin)
            {
                Answer(t, d.Id, false);
                Say($"turned down your offer: {w.Name} hasn't {World.DealUnit(d.Kind, d.Amount)} to spare.");
                return;
            }
            int want = fair * sellPct / 100;
            if (d.Price >= want && Answer(t, d.Id, true) == null) { Say("agreed."); return; }
            if (d.Price < want && d.Round == 0 && Counter(t, d.Id, d.Amount, want) == null) { Say($"asks ${want:N0} a month instead."); return; }
        }
        else
        {
            bool needs = d.Kind == DealKind.Transit ? w.MayorSeat.Style == AutopilotStyle.Growth : w.Spare(d.Kind) < 0;
            if (!needs)
            {
                Answer(t, d.Id, false);
                Say($"turned down your offer: {w.Name} doesn't need {World.DealNoun(d.Kind)}.");
                return;
            }
            int most = fair * buyPct / 100;
            if (w.Funds < d.Price * 6L) { Answer(t, d.Id, false); Say("turned down your offer: it can't afford it."); return; }
            if (d.Price <= most && Answer(t, d.Id, true) == null) { Say("agreed."); return; }
            if (d.Price > most && d.Round == 0 && Counter(t, d.Id, d.Amount, most) == null) { Say($"offers ${most:N0} a month instead."); return; }
        }
        // Terms it won't meet after a counter, or agreeing failed (not joined at the border yet): decline rather than
        // leave it hanging.
        if (Deals.Any(x => x.Id == d.Id && !x.Active)) { Answer(t, d.Id, false); Say("turned down your offer."); }
    }
}
