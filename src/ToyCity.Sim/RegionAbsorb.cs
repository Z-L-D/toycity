namespace ToyCity.Sim;

/// <summary>
/// An offer to take in a neighbouring city (Beta 4, part 6): one city's mayor offers a price, and the neighbour's
/// mayor agrees or declines. A computer mayor answers at once, by its temperament's price.
/// </summary>
public sealed class TakeoverOffer
{
    public int Id;
    /// <summary>Indexes of the city taking the other in, and of the city taken in (each counted from its top-left tile).</summary>
    public int Buyer, Seller;
    public long Price;
    public string By = "";
    public long Day;
}

/// <summary>
/// Absorbing a neighbour: two cities side by side whose tiles together make a rectangle become one, by a deal. The
/// city taking the other in moves onto the bigger map as with annexing; the other's part of the map, its buildings,
/// roads and people come with it, and its treasury and debts. Its mayor, if a player, stays on as a deputy.
/// </summary>
public sealed partial class Region
{
    public readonly List<TakeoverOffer> Takeovers = new();
    public int NextTakeoverId { get; set; } = 1;
    /// <summary>Days an offer stays open before it lapses.</summary>
    public const int TakeoverDays = 30;

    /// <summary>The rectangle two cities would make together, or null when theirs aren't a rectangle side by side.</summary>
    public (int Col, int Row, int Cols, int Rows)? TakeoverRect(RegionTile buyer, RegionTile seller)
    {
        var a = Footprint(buyer);
        var b = Footprint(seller);
        int c0 = Math.Min(a.Col, b.Col), r0 = Math.Min(a.Row, b.Row);
        int c1 = Math.Max(a.Col + a.Cols, b.Col + b.Cols), r1 = Math.Max(a.Row + a.Rows, b.Row + b.Rows);
        if ((c1 - c0) * (r1 - r0) != a.Cols * a.Rows + b.Cols * b.Rows) return null;
        return (c0, r0, c1 - c0, r1 - r0);
    }

    /// <summary>
    /// Where a price for a city starts: a computer mayor's asking price (it takes no less), or for a player's city the
    /// land's worth, for its mayor to weigh.
    /// </summary>
    public long TakeoverFloor(RegionTile seller)
    {
        var (_, _, cols, rows) = Footprint(seller);
        long perTile = 25_000L * TileSize * TileSize / (128 * 128);
        return seller.City!.ComputerMayor ? seller.City.AskingPrice(cols * rows, perTile) : perTile * cols * rows;
    }

    /// <summary>Why this city can't take in that one at that price, or null.</summary>
    public string? CantTakeOver(RegionTile buyer, RegionTile seller, long price)
    {
        if (buyer.City is not { } a || seller.City is not { } b) return "There's no city there.";
        if (a == b) return "That's the same city.";
        if (price < 0 || price > 1_000_000_000) return "That price is out of range.";
        var ba = buyer.Anchor!;
        var sa = seller.Anchor!;
        if (!Enumerable.Range(0, 4).Any(side => NeighbourOn(ba, side) == sa)) return $"{b.Name} isn't next door to {a.Name}.";
        if (TakeoverRect(ba, sa) is not { } to) return $"{a.Name} and {b.Name} together wouldn't make a rectangle: a city is always one.";
        if (to.Cols > MaxCitySide || to.Rows > MaxCitySide) return $"A city here can be at most {MaxCitySide} × {MaxCitySide} tiles.";
        if (CrowdsASide(to.Col, to.Row, a, to.Cols, to.Rows, b) is { } crowd) return crowd;
        var fa = Footprint(ba);
        var fb = Footprint(sa);
        int s = TileSize;
        if (a.CantResize(to.Cols * s, to.Rows * s, (fa.Col - to.Col) * s, (fa.Row - to.Row) * s) is { } why) return why;
        if (b.CantResize(to.Cols * s, to.Rows * s, (fb.Col - to.Col) * s, (fb.Row - to.Row) * s) is { } why2) return $"{b.Name}: {why2}";
        if (a.Funds < price) return $"{a.Name} needs ${price:N0} for that, and has ${a.Funds:N0}.";
        return null;
    }

    /// <summary>A mayor offers to take in the city next door. A computer mayor answers now: the offer comes back null, with why.</summary>
    public TakeoverOffer? OfferTakeover(RegionTile buyer, RegionTile seller, long price, string by, out string? answer)
    {
        answer = null;
        if (CantTakeOver(buyer, seller, price) is { } why) throw new InvalidOperationException(why);
        int bi = Index(buyer), si = Index(seller);
        if (Takeovers.Any(o => o.Buyer == bi && o.Seller == si)) throw new InvalidOperationException("That offer is open already.");
        var o = new TakeoverOffer { Id = NextTakeoverId++, Buyer = bi, Seller = si, Price = price, By = by, Day = Tick };
        Takeovers.Add(o);
        if (seller.City!.ComputerMayor)
        {
            long floor = TakeoverFloor(seller);
            Takeovers.Remove(o);
            answer = price >= floor ? null : $"{seller.Mayor} won't sell {seller.Name} for less than ${floor:N0}.";
            return answer == null ? o : null;
        }
        return o;
    }

    /// <summary>The offer's cities as tiles: the one taking in, and the one taken.</summary>
    public (RegionTile Buyer, RegionTile Seller) Parties(TakeoverOffer o) => (Tiles[o.Buyer], Tiles[o.Seller]);

    /// <summary>Drops offers that lapsed or whose cities changed under them.</summary>
    public List<TakeoverOffer> LapseTakeovers()
    {
        var gone = Takeovers.Where(o => Tick - o.Day > TakeoverDays || !Tiles[o.Buyer].IsAnchor || !Tiles[o.Seller].IsAnchor).ToList();
        foreach (var o in gone) Takeovers.Remove(o);
        return gone;
    }

    /// <summary>
    /// The city takes in its neighbour, as agreed: both move onto the map of the two, the neighbour's part laid into
    /// the buyer's, and the buyer pays the price. Deals the neighbour had end. Returns the city as it is now; both
    /// old worlds are spent.
    /// </summary>
    public World TakeOver(RegionTile buyer, RegionTile seller, long price)
    {
        if (CantTakeOver(buyer, seller, price) is { } why) throw new InvalidOperationException(why);
        var ba = buyer.Anchor!;
        var sa = seller.Anchor!;
        var a = ba.City!;
        var b = sa.City!;
        var to = TakeoverRect(ba, sa)!.Value;
        var fa = Footprint(ba);
        var fb = Footprint(sa);
        int s = TileSize, bi = Index(ba), si = Index(sa);
        string them = b.Name, theirMayor = b.MayorSeat.Owner;
        // The neighbour's deals end first, in both cities, so nobody is left paying for what can't come.
        foreach (var d in Deals.Where(d => d.Seller == si || d.Buyer == si).ToList())
        {
            if (d.Active) { End(d, $"{a.Name} took in {them}"); ApplyNow(Tiles[d.Seller].City!); ApplyNow(Tiles[d.Buyer].City!); }
            else Deals.Remove(d);
        }
        Takeovers.RemoveAll(o => o.Seller == si || o.Buyer == si || o.Buyer == bi);
        var bigA = a.Resized(to.Cols * s, to.Rows * s, (fa.Col - to.Col) * s, (fa.Row - to.Row) * s);
        var bigB = b.Resized(to.Cols * s, to.Rows * s, (fb.Col - to.Col) * s, (fb.Row - to.Row) * s);
        var w = bigA.Merged(bigB, (fb.Col - to.Col) * s, (fb.Row - to.Row) * s, fb.Cols * s, fb.Rows * s);
        bigA.Dispose();
        bigB.Dispose();
        w.PayForCity(price, them, theirMayor.Length > 0 ? theirMayor : "nobody", to.Cols, to.Rows);
        // A player who was its mayor stays on, as a deputy for everything.
        if (theirMayor.Length > 0 && !World.IsComputer(theirMayor) && theirMayor != w.MayorSeat.Owner)
            w.Enqueue(new SetDeputyAction(theirMayor, Portfolio.Planning | Portfolio.Budget | Portfolio.Services | Portfolio.Transit) { By = w.MayorSeat.Owner });
        w.ApplyPending();
        w.Settle();
        var anchor = TileAt(to.Col, to.Row)!;
        int ni = Index(anchor);
        for (int r = to.Row; r < to.Row + to.Rows; r++)
        for (int c = to.Col; c < to.Col + to.Cols; c++)
        {
            var t = TileAt(c, r)!;
            t.City = w;
            t.Anchor = anchor;
        }
        foreach (var d in Deals)
        {
            if (d.Seller == bi) d.Seller = ni;
            if (d.Buyer == bi) d.Buyer = ni;
        }
        a.Dispose();
        b.Dispose();
        GroundChanged();
        return w;
    }
}
