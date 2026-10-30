namespace ToyCity.Sim;

/// <summary>A deal on the region's books: offered and waiting for an answer, or agreed and running in both cities.</summary>
public sealed class RegionDeal
{
    public int Id;
    public DealKind Kind;
    /// <summary>Tile indexes of the city that sells and the city that buys.</summary>
    public int Seller, Buyer;
    public int Amount, Price;
    /// <summary>False while offered; true once the other mayor agreed.</summary>
    public bool Active;
    /// <summary>Which of the two made the offer: the other one answers it.</summary>
    public bool OfferedBySeller;

    public int OfferedBy => OfferedBySeller ? Seller : Buyer;
    public int AnsweredBy => OfferedBySeller ? Buyer : Seller;
}

/// <summary>
/// Neighbour deals: one mayor offers power, water, garbage collection or a shuttle across a border, to sell or to buy,
/// for a monthly price; the neighbour's mayor agrees or declines. An agreed deal runs in both cities from the same day.
/// Either mayor may end it, and the region ends it when the connection breaks, the buyer can't pay, or the seller has
/// nothing to spare.
/// </summary>
public sealed partial class Region
{
    public readonly List<RegionDeal> Deals = new();
    public int NextDealId { get; set; } = 1;

    /// <summary>Most of one kind of deal a border carries at once, either way.</summary>
    public const int MaxDealsPerKind = 1;

    int Index(RegionTile t) => t.Row * Cols + t.Col;

    /// <summary>The side of the seller's city its buyer is on.</summary>
    public int DealSide(RegionDeal d)
    {
        var s = Tiles[d.Seller];
        for (int side = 0; side < 4; side++) if (Across(s, side) == Tiles[d.Buyer]) return side;
        return -1;
    }

    /// <summary>Why this city can't offer that deal across that side, or null.</summary>
    public string? CantOffer(RegionTile from, int side, DealKind kind, int amount, int price)
    {
        if (from.City is not { } w) return "There's no city here.";
        if (side is < 0 or > 3 || Across(from, side)?.City is not { } n) return "There's no neighbouring city on that side.";
        if (!Enum.IsDefined(kind)) return "That isn't a kind of deal.";
        if (amount is <= 0 or > 1_000_000) return "Say how much.";
        if (price is < 0 or > 1_000_000) return "That price is out of range.";
        int other = Index(Across(from, side)!), me = Index(from);
        if (Deals.Count(d => d.Kind == kind && ((d.Seller == me && d.Buyer == other) || (d.Seller == other && d.Buyer == me))) >= MaxDealsPerKind)
            return $"There's already a {World.DealNoun(kind)} deal with {n.Name}, offered or running.";
        if (w.DealCrossing(kind, side) < 0)
            return kind switch
            {
                DealKind.Power => $"Run a power line to the border to meet one of {n.Name}'s first.",
                DealKind.Water => $"Run a pipe to the border to meet one of {n.Name}'s first.",
                _ => $"Build a road to the border to meet one of {n.Name}'s first.",
            };
        return null;
    }

    /// <summary>
    /// Offers a deal to the neighbour across <paramref name="side"/>: to sell it (<paramref name="selling"/>) or buy from
    /// it. It waits for that city's mayor to answer.
    /// </summary>
    public RegionDeal Offer(RegionTile from, int side, DealKind kind, int amount, int price, bool selling)
    {
        if (CantOffer(from, side, kind, amount, price) is { } why) throw new InvalidOperationException(why);
        int me = Index(from), other = Index(Across(from, side)!);
        var d = new RegionDeal
        {
            Id = NextDealId++, Kind = kind, Amount = amount, Price = price, OfferedBySeller = selling,
            Seller = selling ? me : other, Buyer = selling ? other : me,
        };
        Deals.Add(d);
        return d;
    }

    /// <summary>The neighbour's mayor answers an offer. On yes the deal starts in both cities today. Returns why not, or null.</summary>
    public string? Answer(RegionTile by, int id, bool accept)
    {
        var d = Deals.FirstOrDefault(x => x.Id == id);
        if (d == null || d.Active) return "That offer isn't open any more.";
        if (Index(by) != d.AnsweredBy) return "Only the city it was offered to can answer.";
        if (!accept) { Deals.Remove(d); return null; }
        int side = DealSide(d);
        var seller = Tiles[d.Seller].City!;
        var buyer = Tiles[d.Buyer].City!;
        if (seller.DealCrossing(d.Kind, side) < 0 || buyer.DealCrossing(d.Kind, World.Opposite(side)) < 0)
            return "The two cities aren't connected for that at the border yet.";
        d.Active = true;
        seller.Enqueue(new CityDealAction(d.Id, d.Kind, true, side, buyer.Name, d.Amount, d.Price));
        buyer.Enqueue(new CityDealAction(d.Id, d.Kind, false, World.Opposite(side), seller.Name, d.Amount, d.Price));
        seller.ApplyPending();
        buyer.ApplyPending();
        return null;
    }

    /// <summary>
    /// The city an offer was made to answers with other terms: the offer is replaced by theirs, for the first city to
    /// answer. Returns why not, or null.
    /// </summary>
    public string? Counter(RegionTile by, int id, int amount, int price)
    {
        var d = Deals.FirstOrDefault(x => x.Id == id);
        if (d == null || d.Active) return "That offer isn't open any more.";
        if (Index(by) != d.AnsweredBy) return "Only the city it was offered to can answer.";
        if (amount is <= 0 or > 1_000_000) return "Say how much.";
        if (price is < 0 or > 1_000_000) return "That price is out of range.";
        Deals.Remove(d);
        Deals.Add(new RegionDeal
        {
            Id = NextDealId++, Kind = d.Kind, Seller = d.Seller, Buyer = d.Buyer, Amount = amount, Price = price, OfferedBySeller = !d.OfferedBySeller,
        });
        return null;
    }

    /// <summary>Either mayor withdraws an offer or ends a running deal. Returns why not, or null.</summary>
    public string? EndDeal(RegionTile by, int id)
    {
        var d = Deals.FirstOrDefault(x => x.Id == id);
        if (d == null) return "There's no such deal.";
        int me = Index(by);
        if (me != d.Seller && me != d.Buyer) return "That deal isn't yours to end.";
        if (!d.Active) { Deals.Remove(d); return null; }
        End(d, $"{by.Name} ended it");
        foreach (var t in new[] { Tiles[d.Seller], Tiles[d.Buyer] }) t.City!.ApplyPending();
        return null;
    }

    void End(RegionDeal d, string why)
    {
        Deals.Remove(d);
        int side = DealSide(d);
        var seller = Tiles[d.Seller].City!;
        var buyer = Tiles[d.Buyer].City!;
        seller.Enqueue(new CityDealAction(d.Id, d.Kind, true, side, buyer.Name, d.Amount, d.Price, End: true, Why: why));
        buyer.Enqueue(new CityDealAction(d.Id, d.Kind, false, World.Opposite(side), seller.Name, d.Amount, d.Price, End: true, Why: why));
    }

    /// <summary>
    /// Before each day: ends running deals that can't go on, in both cities on the same day. The connection broke, the
    /// buyer's treasury can't cover a month, or the seller has nothing left to send.
    /// </summary>
    void CheckDeals()
    {
        foreach (var d in Deals.Where(x => x.Active).ToList())
        {
            int side = DealSide(d);
            var seller = Tiles[d.Seller].City!;
            var buyer = Tiles[d.Buyer].City!;
            string? why = null;
            if (seller.DealCrossing(d.Kind, side) < 0 || buyer.DealCrossing(d.Kind, World.Opposite(side)) < 0)
                why = d.Kind switch
                {
                    DealKind.Power => "the power lines no longer meet at the border",
                    DealKind.Water => "the pipes no longer meet at the border",
                    _ => "the roads no longer meet at the border",
                };
            else if (buyer.Funds < d.Price) why = $"{buyer.Name} can't pay";
            else if (seller.Deals.FirstOrDefault(x => x.Id == d.Id) is { } sd && seller.DealShort(sd) is { } s) why = s;
            if (why != null) End(d, why);
        }
    }

    /// <summary>Puts back running deals from the cities after a load; offers come from the save.</summary>
    void RestoreDeals()
    {
        foreach (var t in Tiles)
        {
            if (t.City is not { } w) continue;
            foreach (var cd in w.Deals.Where(x => x.Selling))
            {
                if (Across(t, cd.Side) is not { City: not null } other || Deals.Any(x => x.Id == cd.Id)) continue;
                Deals.Add(new RegionDeal { Id = cd.Id, Kind = cd.Kind, Seller = Index(t), Buyer = Index(other), Amount = cd.Amount, Price = cd.Price, Active = true, OfferedBySeller = true });
            }
            foreach (var cd in w.Deals) NextDealId = Math.Max(NextDealId, cd.Id + 1);
        }
    }

    /// <summary>Adds an offer from a save.</summary>
    public void RestoreOffer(RegionDeal d)
    {
        if (d.Seller < 0 || d.Buyer < 0 || d.Seller >= Tiles.Length || d.Buyer >= Tiles.Length || Tiles[d.Seller].City == null || Tiles[d.Buyer].City == null) return;
        if (Deals.Any(x => x.Id == d.Id)) return;
        Deals.Add(d);
        NextDealId = Math.Max(NextDealId, d.Id + 1);
    }
}
