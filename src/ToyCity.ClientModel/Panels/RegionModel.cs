using ToyCity.Protocol.V0;
using static ToyCity.ClientModel.Panels.Bb;

namespace ToyCity.ClientModel.Panels;

/// <summary>
/// The region map's screen, with no engine types: the latest <see cref="RegionState"/>, the tile picked, what the
/// player is waiting on, and what each link does. While <see cref="Active"/> it replaces the city's whole interface.
/// Links: rg-open, rg-close, rg-pick:COL:ROW, rg-go, rg-found, rg-starter, rg-window; deals: rg-kind:K, rg-sell, rg-amt:±,
/// rg-price:±, rg-offer, rg-accept:ID, rg-counter:ID, rg-decline:ID, rg-end:ID.
/// </summary>
public sealed class RegionModel
{
    /// <summary>Pixels along a side of a tile's picture (the server's RegionMapping.PictureSize).</summary>
    public const int PictureSize = 32;

    readonly Func<ClientMessage, uint> _send;
    readonly System.Action _changed;

    public RegionModel(Func<ClientMessage, uint> send, System.Action changed)
    {
        _send = send;
        _changed = changed;
    }

    public Func<string> Me { get; set; } = () => "player";

    public RegionState? State { get; private set; }
    public bool Active { get; private set; }
    public (int Col, int Row)? Picked { get; private set; }
    /// <summary>What the player asked for and is waiting on ("Opening Westbrook…"), shown from the first frame.</summary>
    public string Pending { get; private set; } = "";
    /// <summary>The name typed for a new city.</summary>
    public string NewName { get; set; } = "New City";
    public bool StarterTown { get; private set; } = true;
    /// <summary>The deal being drawn up for the picked neighbour.</summary>
    public DealKind OfferKind { get; private set; } = DealKind.DealPower;
    public bool OfferSelling { get; private set; } = true;
    public int OfferAmount { get; private set; } = 500;
    public int OfferPrice { get; private set; } = 100;
    /// <summary>The offer being answered with other terms, or 0 for a new offer.</summary>
    public int CounterId { get; private set; }
    /// <summary>Goes up when the pictures or the grid change, so the view rebuilds its tiles.</summary>
    public int MapVersion { get; private set; }

    /// <summary>Open a founded city in a window of its own: its column and row.</summary>
    public event Action<int, int>? OwnWindow;
    /// <summary>Asks the view to bring up the city screen again after a move.</summary>
    public event System.Action? Closed;

    public RegionTileState? TileAt(int col, int row) => State?.Tiles.FirstOrDefault(t => t.Col == col && t.Row == row);
    public RegionTileState? PickedTile => Picked is { } p ? TileAt(p.Col, p.Row) : null;
    public RegionTileState? Here => State is { } s ? TileAt(s.HereCol, s.HereRow) : null;

    /// <summary>True when this player is mayor of any city here: mayors may slow the region's one clock.</summary>
    public bool IsMayor => State?.Tiles.Any(t => t.Founded && t.Mayor == Me()) == true;

    /// <summary>Takes a message from the server, after the city mirror has applied it.</summary>
    public void Handle(ServerMessage m)
    {
        switch (m.BodyCase)
        {
            case ServerMessage.BodyOneofCase.Region:
                State = m.Region;
                Picked ??= (m.Region.HereCol, m.Region.HereRow);
                if (Pending.StartsWith("Founding") && PickedTile?.Founded == true) Pending = "";
                if (Pending.StartsWith("Deal:")) Pending = "";
                MapVersion++;
                _changed();
                break;
            case ServerMessage.BodyOneofCase.Full when Pending.StartsWith("Opening"):
                // The other city has arrived on this connection: the move is done.
                Pending = "";
                SetActive(false);
                break;
            case ServerMessage.BodyOneofCase.Error when Pending.Length > 0:
                Pending = "";
                _changed();
                break;
        }
    }

    public void SetActive(bool on)
    {
        if (Active == on) return;
        Active = on;
        if (on)
        {
            _send(new ClientMessage { Region = new RegionRequest() });
            if (State != null) Picked = (State.HereCol, State.HereRow);
        }
        else Closed?.Invoke();
        _changed();
    }

    public void Click(string meta)
    {
        var a = meta.Split(':');
        switch (a[0])
        {
            case "rg-open": SetActive(true); return;
            case "rg-close": SetActive(false); return;
            case "rg-pick" when a.Length == 3 && int.TryParse(a[1], out int c) && int.TryParse(a[2], out int r):
                Picked = (c, r);
                CounterId = 0;
                _changed();
                return;
            case "rg-go" when PickedTile is { Founded: true } t:
                if (State is { } s && t.Col == s.HereCol && t.Row == s.HereRow) { SetActive(false); return; }
                Pending = $"Opening {t.Name}…";
                _send(new ClientMessage { GoTo = new GoToCity { Col = t.Col, Row = t.Row } });
                _changed();
                return;
            case "rg-found" when PickedTile is { Founded: false } t:
            {
                string name = string.IsNullOrWhiteSpace(NewName) ? "New City" : NewName.Trim();
                Pending = $"Founding {name}…";
                _send(new ClientMessage { FoundCity = new FoundCity { Col = t.Col, Row = t.Row, Name = name, StarterTown = StarterTown } });
                _changed();
                return;
            }
            case "rg-starter": StarterTown = !StarterTown; _changed(); return;
            case "rg-window" when PickedTile is { Founded: true } t: OwnWindow?.Invoke(t.Col, t.Row); return;
            case "rg-kind" when a.Length == 2 && int.TryParse(a[1], out int k) && Enum.IsDefined(typeof(DealKind), k):
                OfferKind = (DealKind)k;
                OfferAmount = OfferKind == DealKind.DealTransit ? 200 : OfferKind == DealKind.DealGarbage ? 300 : 500;
                _changed();
                return;
            case "rg-sell": OfferSelling = !OfferSelling; _changed(); return;
            case "rg-amt" when a.Length == 2: OfferAmount = Math.Clamp(OfferAmount + (a[1] == "-" ? -100 : 100), 100, 100_000); _changed(); return;
            case "rg-price" when a.Length == 2: OfferPrice = Math.Clamp(OfferPrice + (a[1] == "-" ? -25 : 25), 0, 100_000); _changed(); return;
            case "rg-counter" when a.Length == 2 && int.TryParse(a[1], out int cid) && State?.Deals.FirstOrDefault(x => x.Id == cid) is { } cd:
            {
                // The form takes the offer's terms, from this side, to change and send back.
                CounterId = cid;
                OfferKind = cd.Kind;
                OfferAmount = cd.Amount;
                OfferPrice = cd.Price;
                bool sellerHere = cd.SellerCol == State.HereCol && cd.SellerRow == State.HereRow;
                OfferSelling = sellerHere;
                Picked = sellerHere ? (cd.BuyerCol, cd.BuyerRow) : (cd.SellerCol, cd.SellerRow);
                _changed();
                return;
            }
            case "rg-offer" when CounterId > 0:
                Pending = "Deal: sending your terms…";
                _send(new ClientMessage { Deal = new DealRequest { Op = DealRequest.Types.Op.Counter, Id = CounterId, Amount = OfferAmount, Price = OfferPrice } });
                CounterId = 0;
                _changed();
                return;
            case "rg-offer" when PickedTile is { Founded: true } t && SideOf(t) is int side and >= 0:
                Pending = $"Deal: offering {t.Name} {KindNoun(OfferKind)}…";
                _send(new ClientMessage { Deal = new DealRequest { Op = DealRequest.Types.Op.Offer, Side = side, Kind = OfferKind, Amount = OfferAmount, Price = OfferPrice, Selling = OfferSelling } });
                _changed();
                return;
            case "rg-accept" or "rg-decline" or "rg-end" when a.Length == 2 && int.TryParse(a[1], out int id):
                Pending = a[0] == "rg-accept" ? "Deal: agreeing…" : a[0] == "rg-decline" ? "Deal: declining…" : "Deal: ending…";
                var op = a[0] == "rg-accept" ? DealRequest.Types.Op.Accept : a[0] == "rg-decline" ? DealRequest.Types.Op.Decline : DealRequest.Types.Op.End;
                _send(new ClientMessage { Deal = new DealRequest { Op = op, Id = id } });
                _changed();
                return;
        }
    }

    /// <summary>The side of this city a tile lies on (0 north, 1 east, 2 south, 3 west), or -1 when it isn't next door.</summary>
    public int SideOf(RegionTileState t)
    {
        if (State is not { } s) return -1;
        return (t.Col - s.HereCol, t.Row - s.HereRow) switch { (0, -1) => 0, (1, 0) => 1, (0, 1) => 2, (-1, 0) => 3, _ => -1 };
    }

    public static string KindNoun(DealKind k) => k switch
    {
        DealKind.DealPower => "power",
        DealKind.DealWater => "water",
        DealKind.DealGarbage => "garbage collection",
        _ => "a shuttle",
    };

    public static string Unit(DealKind k, int amount) => k switch
    {
        DealKind.DealPower => $"{amount:N0} kW",
        DealKind.DealWater => $"{amount:N0} water",
        DealKind.DealGarbage => $"{amount:N0} garbage",
        _ => $"{amount:N0} vehicles/h",
    };

    /// <summary>One deal in a sentence: "Westbrook sells Eastfield 500 kW for $100 a month".</summary>
    public static string Describe(Deal d) => d.Kind switch
    {
        DealKind.DealGarbage => $"{d.Seller} takes {Unit(d.Kind, d.Amount)} from {d.Buyer} for {Money(d.Price)} a month",
        DealKind.DealTransit => $"{d.Seller} runs a shuttle ({Unit(d.Kind, d.Amount)}) for {d.Buyer} at {Money(d.Price)} a month",
        _ => $"{d.Seller} sells {d.Buyer} {Unit(d.Kind, d.Amount)} for {Money(d.Price)} a month",
    };

    static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    static readonly string[] SpeedNames = { "paused", "slow", "normal", "fast" };

    /// <summary>The line across the top of the screen: the region's name, date and size, and who holds the clock.</summary>
    public string Title()
    {
        if (State is not { } s) return "Region";
        string held = s.SpeedSetBy.Length > 0 ? $" · held at {SpeedNames[Math.Clamp(s.Speed, 0, 3)]} by {s.SpeedSetBy}" : "";
        return $"{s.Name}   {Months[(int)(s.Tick / 30 % 12)]} {2000 + s.Tick / 360} · {s.Cols} × {s.Rows} tiles{held}";
    }

    /// <summary>The side of the screen: the region's totals and the picked tile, with what can be done there.</summary>
    public string Render()
    {
        if (State is not { } s) return Dim("Loading the region…");
        var sb = new System.Text.StringBuilder();
        int cities = s.Tiles.Count(t => t.Founded);
        long pop = s.Tiles.Sum(t => (long)t.Population);
        sb.Append(Dim($"{Plural(cities, "city").Replace("citys", "cities")} · {Num(pop)} people in the region")).Append("\n\n");
        if (PickedTile is { } p)
        {
            bool here = p.Col == s.HereCol && p.Row == s.HereRow;
            if (p.Founded)
            {
                sb.Append(H3(Esc(p.Name)));
                sb.Append(Table(new[]
                {
                    ("Mayor", Esc(p.Mayor.Length > 0 ? p.Mayor : "nobody")),
                    ("Population", Num(p.Population)),
                    ("Treasury", Money(p.Funds)),
                    ("Here now", p.Players.Count > 0 ? Esc(string.Join(", ", p.Players)) : Dim("nobody")),
                }));
                sb.Append('\n');
                sb.Append(here ? Dim("You are here. ") + Link("rg-close", "Back to the city") : Link("rg-go", $"Open {Esc(p.Name)}"));
                sb.Append("   ").Append(Link("rg-window", "⧉ Own window"));
            }
            else
            {
                sb.Append(H3("Open land"));
                sb.Append(Dim($"Tile {p.Col + 1}, {p.Row + 1}. Nobody has built here yet. Found a city and you'll be its mayor.")).Append("\n\n");
                sb.Append(Link("rg-starter", (StarterTown ? "☑" : "☐") + " Start with a small working town")).Append("\n\n");
                sb.Append(Link("rg-found", "Found a city here"));
            }
        }
        RenderDeals(sb, s);
        if (Pending.Length > 0) sb.Append("\n\n").Append(Col(Warn, "⏳ " + Esc(Pending.StartsWith("Deal: ") ? Pending[6..] : Pending)));
        return sb.ToString();
    }

    /// <summary>
    /// This city's deals with its neighbours, with what its mayor can do about each; and with a neighbour picked, the
    /// offer being drawn up for it.
    /// </summary>
    void RenderDeals(System.Text.StringBuilder sb, RegionState s)
    {
        var here = Here;
        bool mayor = here?.Mayor == Me();
        if (s.Deals.Count > 0)
        {
            sb.Append("\n\n").Append(H3("Deals with neighbours"));
            foreach (var d in s.Deals)
            {
                bool mine = (d.SellerCol == s.HereCol && d.SellerRow == s.HereRow) == d.OfferedBySeller;
                sb.Append(Esc(Describe(d))).Append('\n');
                if (d.Active) sb.Append(Col(Pos, "running")).Append(mayor ? "   " + Link($"rg-end:{d.Id}", "End it") : "");
                else if (mine) sb.Append(Dim("offered, waiting for an answer")).Append(mayor ? "   " + Link($"rg-end:{d.Id}", "Withdraw") : "");
                else sb.Append(Col(Warn, "offered to you")).Append(mayor ? "   " + Link($"rg-accept:{d.Id}", "Agree") + "   " + Link($"rg-counter:{d.Id}", "Counter") + "   " + Link($"rg-decline:{d.Id}", "Decline") : "");
                sb.Append("\n\n");
            }
        }
        if (PickedTile is not { Founded: true } p || SideOf(p) < 0 || !mayor) return;
        sb.Append("\n").Append(H3(CounterId > 0 ? $"Your terms for {Esc(p.Name)}" : $"A deal with {Esc(p.Name)}"));
        for (int k = 0; k < 4 && CounterId == 0; k++)
        {
            var kind = (DealKind)k;
            string label = kind switch { DealKind.DealPower => "Power", DealKind.DealWater => "Water", DealKind.DealGarbage => "Garbage", _ => "Shuttle" };
            sb.Append(kind == OfferKind ? $"[b]{label}[/b]" : Link($"rg-kind:{k}", label)).Append("  ");
        }
        sb.Append('\n');
        string verb = OfferKind switch
        {
            DealKind.DealGarbage => OfferSelling ? $"Take their garbage" : "Send them our garbage",
            DealKind.DealTransit => OfferSelling ? "Run a shuttle for them" : "Have them run a shuttle for us",
            _ => OfferSelling ? $"Sell them {KindNoun(OfferKind)}" : $"Buy {KindNoun(OfferKind)} from them",
        };
        sb.Append(CounterId > 0 ? "" : Link("rg-sell", "⇄") + " ").Append(verb).Append('\n');
        sb.Append(Link("rg-amt:-", "−")).Append(' ').Append($"[b]{Unit(OfferKind, OfferAmount)}[/b]").Append(' ').Append(Link("rg-amt:+", "+")).Append("   ");
        sb.Append(Link("rg-price:-", "−")).Append(' ').Append($"[b]{Money(OfferPrice)} a month[/b]").Append(' ').Append(Link("rg-price:+", "+")).Append('\n');
        sb.Append(Dim(OfferKind switch
        {
            DealKind.DealPower => "Needs a power line of each city to meet at the border.",
            DealKind.DealWater => "Needs a pipe of each city to meet at the border.",
            _ => "Needs a road of each city to meet at the border.",
        })).Append('\n');
        sb.Append(Link("rg-offer", CounterId > 0 ? "Send these terms back" : $"Offer it to {Esc(p.Name)}"));
    }
}
