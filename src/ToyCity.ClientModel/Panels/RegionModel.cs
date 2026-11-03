using ToyCity.Protocol.V0;
using static ToyCity.ClientModel.Panels.Bb;

namespace ToyCity.ClientModel.Panels;

/// <summary>
/// The region map's screen, with no engine types: the latest <see cref="RegionState"/>, the tile picked, what the
/// player is waiting on, and what each link does. While <see cref="Active"/> it replaces the city's whole interface.
/// Links: rg-open, rg-close, rg-pick:COL:ROW, rg-go, rg-found, rg-grow, rg-annex, rg-bid:±, rg-takeover-offer, rg-to-accept|decline|withdraw:ID, rg-starter, rg-founder[:T], rg-window, rg-takeover, rg-hand:T; deals: rg-kind:K, rg-sell, rg-amt:±,
/// rg-price:±, rg-offer, rg-accept:ID, rg-counter:ID, rg-decline:ID, rg-end:ID; the charter (Beta 4): rg-charter, rg-ch-grow:R,
/// rg-ch-annex:R, rg-ch-max:N, rg-ch-send, rg-ch-cancel, rg-vote:ID:yes|no.
/// </summary>
public sealed class RegionModel
{
    /// <summary>Pixels along a side of a tile's picture (the server's RegionMapping.PictureSize).</summary>
    public const int PictureSize = 32;

    /// <summary>The sizes a new region can be, tiles a side (the server's Region.Sizes).</summary>
    public static readonly (string Name, int Side)[] Sizes = { ("Small", 3), ("Medium", 8), ("Large", 16), ("Huge", 32) };

    /// <summary>True when the server makes a region this big a tile at a time (its Region.MaxEagerCells, 1024 cells a side).</summary>
    public static bool MadeTileByTile(int side, int tileSize) => side * tileSize > 1024;

    readonly Func<ClientMessage, uint> _send;
    readonly System.Action _changed;

    public RegionModel(Func<ClientMessage, uint> send, System.Action changed)
    {
        _send = send;
        _changed = changed;
    }

    public Func<string> Me { get; set; } = () => "player";
    /// <summary>True when this player is one of the server's admins: their charter changes take effect at once.</summary>
    public Func<bool> Admin { get; set; } = () => false;

    /// <summary>A new charter being drawn up, or null.</summary>
    public RegionCharter? CharterDraft { get; private set; }

    public RegionState? State { get; private set; }
    public bool Active { get; private set; }
    public (int Col, int Row)? Picked { get; private set; }
    /// <summary>What the player asked for and is waiting on ("Opening Westbrook…"), shown from the first frame.</summary>
    public string Pending { get; private set; } = "";
    /// <summary>The name typed for a new city.</summary>
    public string NewName { get; set; } = "New City";
    public bool StarterTown { get; private set; } = true;
    /// <summary>Who will be mayor of a city founded now: "" for this player, else a computer's temperament.</summary>
    public string FoundComputer { get; private set; } = "";
    /// <summary>The deal being drawn up for the picked neighbour.</summary>
    public DealKind OfferKind { get; private set; } = DealKind.DealPower;
    public bool OfferSelling { get; private set; } = true;
    public int OfferAmount { get; private set; } = 500;
    public int OfferPrice { get; private set; } = 100;
    /// <summary>The price on the table for taking in a neighbour, and whose it is (its top-left tile).</summary>
    public (int Col, int Row, long Price) Bid { get; private set; } = (-1, -1, 0);
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

    /// <summary>A growing region's wild tile that can be added: one on a side of a tile already in the region (G4).</summary>
    public bool CanGrow(RegionTileState t) =>
        t.Wild && new[] { (0, -1), (1, 0), (0, 1), (-1, 0) }.Any(d => TileAt(t.Col + d.Item1, t.Row + d.Item2) is { Wild: false });

    /// <summary>
    /// The tiles to show: the whole grid for a fixed region; for a growing one, the tiles in it and a ring of wild
    /// land around them, where it can grow.
    /// </summary>
    public (int C0, int R0, int C1, int R1) Shown
    {
        get
        {
            if (State is not { } s) return (0, 0, -1, -1);
            if (!s.Grows) return (0, 0, s.Cols - 1, s.Rows - 1);
            var tiles = s.Tiles.Where(t => !t.Wild).ToList();
            if (tiles.Count == 0) return (0, 0, s.Cols - 1, s.Rows - 1);
            return (Math.Max(0, tiles.Min(t => t.Col) - 1), Math.Max(0, tiles.Min(t => t.Row) - 1),
                Math.Min(s.Cols - 1, tiles.Max(t => t.Col) + 1), Math.Min(s.Rows - 1, tiles.Max(t => t.Row) + 1));
        }
    }

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
                if (Pending.StartsWith("Deal:") || Pending.StartsWith("Taking over") || Pending.StartsWith("Handing")) Pending = "";
                if (Pending.StartsWith("Adding") && (PickedTile is { Wild: false } || m.Region.Votes.Any(v => v.Kind == VoteKind.VoteGrow && v.Col == Picked?.Col && v.Row == Picked?.Row))) Pending = "";
                if (Pending.StartsWith("Charter") || Pending.StartsWith("Voting") || Pending.StartsWith("Takeover")) Pending = "";
                // A tile a city has annexed: the city is picked whole, from its top-left tile.
                var pt = PickedTile;
                if (pt is { Founded: true } && pt.CityCols * pt.CityRows > 1) Picked = (pt.CityCol, pt.CityRow);
                if (Pending.StartsWith("Anne") && (pt?.Founded == true || m.Region.Votes.Any(v => v.Kind == VoteKind.VoteAnnex && v.ToCol == pt?.Col && v.ToRow == pt?.Row))) Pending = "";
                MapVersion++;
                _changed();
                break;
            case ServerMessage.BodyOneofCase.Full when Pending.StartsWith("Opening"):
                // The other city has arrived on this connection: the move is done.
                Pending = "";
                SetActive(false);
                break;
            case ServerMessage.BodyOneofCase.Redirect:
                // That city is on another host: the client reconnects there, so this map is done with.
                Pending = $"Moving to {m.Redirect.Address}…";
                _changed();
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
                // A city of several tiles is picked as a whole, by its top-left tile.
                Picked = TileAt(c, r) is { Founded: true, CityCols: > 0 } pt && pt.CityCols * pt.CityRows > 1 ? (pt.CityCol, pt.CityRow) : (c, r);
                CounterId = 0;
                _changed();
                return;
            case "rg-go" when PickedTile is { Founded: true } t:
                if (State is { } s && t.Col == s.HereCol && t.Row == s.HereRow) { SetActive(false); return; }
                Pending = $"Opening {t.Name}…";
                _send(new ClientMessage { GoTo = new GoToCity { Col = t.Col, Row = t.Row } });
                _changed();
                return;
            case "rg-grow" when PickedTile is { } t && CanGrow(t):
                Pending = "Adding a tile to the region: making its ground…";
                _send(new ClientMessage { Grow = new GrowRegion { Col = t.Col, Row = t.Row } });
                _changed();
                return;
            case "rg-annex" when PickedTile is { Founded: false, Wild: false } t && AnnexAt(t) is { Why.Length: 0 }:
                Pending = AnnexAsks ? "Annex: asking the mayors…" : "Annexing the land: moving the city onto its bigger map…";
                _send(new ClientMessage { Annex = new AnnexRequest { Col = t.Col, Row = t.Row } });
                _changed();
                return;
            case "rg-found" when PickedTile is { Founded: false, Wild: false } t:
            {
                string name = string.IsNullOrWhiteSpace(NewName) ? "New City" : NewName.Trim();
                Pending = $"Founding {name}…";
                _send(new ClientMessage { FoundCity = new FoundCity { Col = t.Col, Row = t.Row, Name = name, StarterTown = StarterTown, Computer = FoundComputer } });
                _changed();
                return;
            }
            case "rg-starter": StarterTown = !StarterTown; _changed(); return;
            case "rg-charter" when State is { } cs: CharterDraft = (cs.Charter ?? new RegionCharter()).Clone(); _changed(); return;
            case "rg-ch-grow" when CharterDraft != null && a.Length == 2 && Enum.TryParse<CharterRule>(a[1], out var gr): CharterDraft.Grow = gr; _changed(); return;
            case "rg-ch-annex" when CharterDraft != null && a.Length == 2 && Enum.TryParse<CharterRule>(a[1], out var ar): CharterDraft.Annex = ar; _changed(); return;
            case "rg-ch-max" when CharterDraft != null && a.Length == 2 && int.TryParse(a[1], out int mx): CharterDraft.MaxCitySide = Math.Max(0, mx); _changed(); return;
            case "rg-ch-cancel": CharterDraft = null; _changed(); return;
            case "rg-ch-send" when CharterDraft != null:
                Pending = Admin() ? "Charter: setting it…" : "Charter: asking the mayors…";
                _send(new ClientMessage { Charter = new SetCharter { Charter = CharterDraft } });
                CharterDraft = null;
                _changed();
                return;
            case "rg-vote" when a.Length == 3 && int.TryParse(a[1], out int vid):
                Pending = "Voting…";
                _send(new ClientMessage { Vote = new CastVote { Id = vid, Yes = a[2] == "yes" } });
                _changed();
                return;
            case "rg-water" when State is { } ws:
                _send(new ClientMessage { RegionWater = new SetRegionWater { Stays = !ws.WaterStays } });
                return;
            case "rg-founder": FoundComputer = a.Length == 2 ? a[1] : ""; _changed(); return;
            case "rg-takeover" when PickedTile is { Founded: true } t:
                Pending = $"Taking over {t.Name}…";
                _send(new ClientMessage { Mayor = new MayorRequest { Col = t.Col, Row = t.Row, TakeOver = true } });
                _changed();
                return;
            case "rg-hand" when a.Length == 2 && PickedTile is { Founded: true } t:
                Pending = $"Handing {t.Name} to the computer…";
                _send(new ClientMessage { Mayor = new MayorRequest { Col = t.Col, Row = t.Row, Computer = a[1] } });
                _changed();
                return;
            case "rg-window" when PickedTile is { Founded: true } t: OwnWindow?.Invoke(t.Col, t.Row); return;
            case "rg-kind" when a.Length == 2 && int.TryParse(a[1], out int k) && Enum.IsDefined(typeof(DealKind), k):
                OfferKind = (DealKind)k;
                OfferAmount = OfferKind == DealKind.DealTransit ? 200 : OfferKind == DealKind.DealGarbage ? 300 : 500;
                _changed();
                return;
            case "rg-bid" when a.Length == 2 && Bid.Col >= 0: Bid = (Bid.Col, Bid.Row, Math.Max(0, Bid.Price + (a[1] == "-" ? -5000 : 5000))); _changed(); return;
            case "rg-takeover-offer" when PickedTile is { Founded: true } t && TakeoverAt(t) is { Why.Length: 0 } && Bid.Col == t.Col && Bid.Row == t.Row:
                Pending = $"Takeover: offering {Money(Bid.Price)}…";
                _send(new ClientMessage { Takeover = new TakeoverRequest { Op = TakeoverRequest.Types.Op.Offer, Col = t.Col, Row = t.Row, Price = Bid.Price } });
                _changed();
                return;
            case "rg-to-accept" or "rg-to-decline" or "rg-to-withdraw" when a.Length == 2 && int.TryParse(a[1], out int toId):
            {
                var top = a[0] == "rg-to-accept" ? TakeoverRequest.Types.Op.Accept : a[0] == "rg-to-decline" ? TakeoverRequest.Types.Op.Decline : TakeoverRequest.Types.Op.Withdraw;
                Pending = top == TakeoverRequest.Types.Op.Accept ? "Takeover: agreeing: the two cities become one…" : top == TakeoverRequest.Types.Op.Decline ? "Takeover: declining…" : "Takeover: withdrawing…";
                _send(new ClientMessage { Takeover = new TakeoverRequest { Op = top, Id = toId } });
                _changed();
                return;
            }
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
        if (Here is not { } home) return -1;
        // Cities of several tiles: the sides meet where their rectangles touch.
        var (ac, ar, aw, ah) = Footprint(home);
        var (bc, br, bw, bh) = Footprint(t);
        bool cols = bc < ac + aw && bc + bw > ac, rows = br < ar + ah && br + bh > ar;
        return cols && br + bh == ar ? 0 : rows && bc == ac + aw ? 1 : cols && br == ar + ah ? 2 : rows && bc + bw == ac ? 3 : -1;
    }

    /// <summary>The tiles a city covers, from its top-left tile; one tile for land with no city.</summary>
    public static (int Col, int Row, int Cols, int Rows) Footprint(RegionTileState t) =>
        t.Founded && t.CityCols * t.CityRows > 1 ? (t.CityCol, t.CityRow, t.CityCols, t.CityRows) : (t.Col, t.Row, 1, 1);

    /// <summary>The city this player is in could annex this open land: the rectangle it would make and the price.</summary>
    public AnnexOption? AnnexAt(RegionTileState t) => State?.Annex.FirstOrDefault(x => x.Col == t.Col && x.Row == t.Row);

    /// <summary>Annexing goes to a vote of the mayors, as the charter has it, for this player.</summary>
    bool AnnexAsks => State is { } s && (s.Charter?.Annex ?? CharterRule.CharterVote) == CharterRule.CharterVote && !Admin() && s.Voters.Count > 1;

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
        int inRegion = s.Tiles.Count(t => !t.Wild);
        string size = s.Grows ? $"{Plural(inRegion, "tile")}, growing" : $"{s.Cols} × {s.Rows} tiles";
        return $"{s.Name}   {Months[(int)(s.Tick / 30 % 12)]} {2000 + s.Tick / 360} · {size}{held}";
    }

    /// <summary>The side of the screen: the region's totals and the picked tile, with what can be done there.</summary>
    public string Render()
    {
        if (State is not { } s) return Dim("Loading the region…");
        var sb = new System.Text.StringBuilder();
        // A city of several tiles counts once, by its top-left tile.
        var towns = s.Tiles.Where(t => t.Founded && Footprint(t) is var f && f.Col == t.Col && f.Row == t.Row).ToList();
        int cities = towns.Count;
        long pop = towns.Sum(t => (long)t.Population);
        sb.Append(Dim($"{Plural(cities, "city").Replace("citys", "cities")} · {Num(pop)} people in the region")).Append('\n');
        // Beta 4, part 3: the region's option for foul water crossing borders down its rivers.
        if (s.Cols * s.Rows > 1)
            sb.Append(Link("rg-water", (s.WaterStays ? "☐" : "☑") + " Rivers carry foul water on into the next city")).Append('\n');
        sb.Append('\n');
        if (PickedTile is { } p)
        {
            bool here = p.Col == s.HereCol && p.Row == s.HereRow;
            if (p.Founded)
            {
                sb.Append(H3(Esc(p.Name)));
                sb.Append(Table(new[]
                {
                    ("Mayor", Esc(p.Mayor.Length > 0 ? p.Mayor : "nobody") + (p.Computer.Length > 0 ? Dim($" · {Esc(p.Computer)}") : "")),
                    ("Size", p.CityCols * p.CityRows > 1 ? $"{p.CityCols} × {p.CityRows} tiles" : "one tile"),
                    ("Population", Num(p.Population)),
                    ("Treasury", Money(p.Funds)),
                    ("Here now", p.Players.Count > 0 ? Esc(string.Join(", ", p.Players)) : Dim("nobody")),
                }));
                sb.Append('\n');
                sb.Append(here ? Dim("You are here. ") + Link("rg-close", "Back to the city") : Link("rg-go", $"Open {Esc(p.Name)}"));
                sb.Append("   ").Append(Link("rg-window", "⧉ Own window"));
                if (p.Computer.Length > 0) sb.Append("\n").Append(Link("rg-takeover", "Take over this city")).Append(' ').Append(Dim("and play it yourself"));
                else if (p.Mayor == Me())
                    sb.Append("\n").Append(Dim("Hand it to a computer mayor: ")).Append(Link("rg-hand:growth", "growth-hungry")).Append(" · ")
                      .Append(Link("rg-hand:green", "green")).Append(" · ").Append(Link("rg-hand:careful", "careful"));
                if (here && s.Annex.Any(x => x.Why.Length == 0)) sb.Append("\n\n").Append(Dim("Room to grow: pick the open land beside it, marked ⊕, to annex it."));
            }
            else if (p.Wild)
            {
                sb.Append(H3("Wild land"));
                if (CanGrow(p))
                {
                    sb.Append(Dim($"{Where(s, p)} isn't part of the region yet. Add it and its ground, rivers and rock are made now, "
                        + "carrying on from the tiles beside it. It joins as open land: you or any other player can found a city on it.")).Append("\n\n");
                    var rule = s.Charter?.Grow ?? CharterRule.CharterAnyone;
                    sb.Append(Dim(rule switch
                    {
                        CharterRule.CharterAdmins => "The region's charter lets only the server's admins add land.",
                        CharterRule.CharterVote => "By the region's charter, adding land goes to a vote of the mayors: more than half must agree.",
                        _ => "By the region's charter, any mayor here can add land.",
                    })).Append("\n\n");
                    var asked = s.Votes.FirstOrDefault(v => v.Kind == VoteKind.VoteGrow && v.Col == p.Col && v.Row == p.Row);
                    if (asked != null) sb.Append(Dim("The mayors are voting on it now (below)."));
                    else sb.Append(Link("rg-grow", rule == CharterRule.CharterVote && !Admin() && s.Voters.Count > 1 ? "Ask the mayors to add this tile" : "Add this tile to the region"));
                }
                else sb.Append(Dim("Not part of the region yet. Land joins on a side of a tile already in it."));
            }
            else
            {
                sb.Append(H3("Open land"));
                sb.Append(Dim($"{Where(s, p)}. Nobody has built here yet. Found a city and you'll be its mayor."
                    + (p.Unsurveyed ? " The picture is a rough look: its ground, rivers and rock are made when a city is founded." : ""))).Append("\n\n");
                sb.Append(Link("rg-starter", (StarterTown ? "☑" : "☐") + " Start with a small working town")).Append("\n\n");
                sb.Append(Dim("Mayor: "));
                foreach (var (key, label) in new[] { ("", "you"), ("growth", "growth-hungry computer"), ("green", "green computer"), ("careful", "careful computer") })
                    sb.Append(FoundComputer == key ? $"[b]{label}[/b]" : Link(key.Length > 0 ? $"rg-founder:{key}" : "rg-founder", label)).Append("  ");
                sb.Append("\n").Append(Dim("A computer mayor plays its city on autopilot and answers deals by its temperament. Admins only.")).Append("\n\n");
                sb.Append(Link("rg-found", "Found a city here"));
                RenderAnnex(sb, s, p);
            }
        }
        RenderDeals(sb, s);
        RenderTakeovers(sb, s, PickedTile is { Founded: true } pk && !(pk.Col == s.HereCol && pk.Row == s.HereRow) ? pk : null);
        RenderCharter(sb, s);
        if (Pending.Length > 0) sb.Append("\n\n").Append(Col(Warn, "⏳ " + Esc(Pending.StartsWith("Deal: ") ? Pending[6..] : Pending)));
        return sb.ToString();
    }

    /// <summary>Annexing a picked tile of open land for the city this player is in: the rectangle, the price, the rule.</summary>
    void RenderAnnex(System.Text.StringBuilder sb, RegionState s, RegionTileState p)
    {
        if (AnnexAt(p) is not { } a || Here is not { Founded: true } home) return;
        var (_, _, hw, hh) = Footprint(home);
        int takes = a.ToCols * a.ToRows - hw * hh;
        var rule = s.Charter?.Annex ?? CharterRule.CharterVote;
        sb.Append("\n\n").Append(H3($"Annex for {Esc(home.Name)}"));
        sb.Append(Dim($"{Esc(home.Name)} would take {(takes == 1 ? "this tile" : $"{takes} tiles (a city is always a rectangle)")} and be ")).Append($"[b]{a.ToCols} × {a.ToRows} tiles[/b]")
          .Append(Dim(". Everything in it moves onto the bigger map as it stands; the new land comes with its ground, rivers and rock.")).Append('\n');
        sb.Append(Table(new[] { ("Price", Money(a.Price)), ("Treasury", Money(home.Funds)) })).Append('\n');
        sb.Append(Dim(rule switch
        {
            CharterRule.CharterAdmins => "By the region's charter only the server's admins annex land.",
            CharterRule.CharterVote => "By the region's charter, annexing goes to a vote of the mayors: more than half must agree.",
            _ => $"By the region's charter, {Esc(home.Name)}'s mayor can annex land beside it.",
        })).Append("\n\n");
        bool may = Admin() || (home.Mayor == Me() && rule != CharterRule.CharterAdmins);
        if (a.Why.Length > 0) sb.Append(Col(Warn, Esc(a.Why)));
        else if (s.Votes.Any(v => v.Kind == VoteKind.VoteAnnex && v.ToCol == p.Col && v.ToRow == p.Row)) sb.Append(Dim("The mayors are voting on it now (below)."));
        else if (may) sb.Append(Link("rg-annex", AnnexAsks ? "Ask the mayors to let it annex" : $"Annex for {Money(a.Price)}"));
    }

    /// <summary>
    /// Where a tile is, in words: by column and row for a fixed region; for a growing one, from the tile it began with
    /// (its frame is so wide the numbers would mean nothing).
    /// </summary>
    public static string Where(RegionState s, RegionTileState t)
    {
        if (!s.Grows) return $"Tile {t.Col + 1}, {t.Row + 1}";
        int c = (s.Cols - 1) / 2, dx = t.Col - c, dy = t.Row - c;
        if (dx == 0 && dy == 0) return "The tile the region began with";
        var parts = new List<string>();
        if (dy != 0) parts.Add($"{Math.Abs(dy)} {(dy < 0 ? "north" : "south")}");
        if (dx != 0) parts.Add($"{Math.Abs(dx)} {(dx < 0 ? "west" : "east")}");
        return $"The tile {string.Join(" and ", parts)} of where the region began";
    }

    public static string RuleName(CharterRule r) => r switch
    {
        CharterRule.CharterAdmins => "admins only",
        CharterRule.CharterVote => "a vote of the mayors",
        _ => "any mayor",
    };

    static string SideText(int side) => side == 1 ? "one tile" : side > 0 ? $"{side} × {side} tiles" : "as big as the server allows";

    /// <summary>
    /// The region's charter (Beta 4, part 6): who grows it, who annexes, how big a city may be; the votes open; and,
    /// for a mayor or an admin, a new charter to set or ask for.
    /// </summary>
    void RenderCharter(System.Text.StringBuilder sb, RegionState s)
    {
        var ch = s.Charter ?? new RegionCharter();
        sb.Append("\n\n").Append(H3("Region charter"));
        sb.Append(Table(new[]
        {
            ("Adding land", s.Grows ? RuleName(ch.Grow) : Dim("this map was made all at once")),
            ("Annexing", RuleName(ch.Annex)),
            ("Cities up to", $"{s.MaxCitySide} × {s.MaxCitySide} tiles" + (ch.MaxCitySide == 0 ? Dim(" (the server's limit)") : "")),
        }));
        foreach (var v in s.Votes)
        {
            sb.Append('\n').Append($"🗳 {Esc(v.By)} asks for {Esc(v.Text)}. ");
            sb.Append(Dim($"{v.Yes.Count} yes, {v.No.Count} no, of {s.Voters.Count} mayors; open until day {v.Day + 30}. "));
            if (s.Voters.Contains(Me()))
            {
                bool yes = v.Yes.Contains(Me()), no = v.No.Contains(Me());
                sb.Append(yes ? "[b]yes[/b]" : Link($"rg-vote:{v.Id}:yes", "Yes")).Append(" · ").Append(no ? "[b]no[/b]" : Link($"rg-vote:{v.Id}:no", "No"));
            }
        }
        bool mayor = s.Voters.Contains(Me());
        if (!mayor && !Admin()) return;
        if (CharterDraft is not { } d)
        {
            sb.Append("\n").Append(Link("rg-charter", Admin() || s.Voters.Count <= 1 ? "Change the charter" : "Ask the mayors for a new charter"));
            return;
        }
        sb.Append("\n\n").Append(H4("A new charter"));
        void Rules(string key, string label, CharterRule now)
        {
            sb.Append(Dim(label + ": "));
            foreach (var r in new[] { CharterRule.CharterAdmins, CharterRule.CharterVote, CharterRule.CharterAnyone })
                sb.Append(r == now ? $"[b]{RuleName(r)}[/b]" : Link($"{key}:{r}", RuleName(r))).Append("  ");
            sb.Append('\n');
        }
        if (s.Grows) Rules("rg-ch-grow", "Adding land", d.Grow);
        Rules("rg-ch-annex", "Annexing", d.Annex);
        sb.Append(Dim("Cities up to: "));
        foreach (int side in new[] { 1, 2, 3, 4, 0 }.Where(x => x <= Math.Max(1, s.ServerMaxCitySide)).Distinct())
            sb.Append(side == d.MaxCitySide ? $"[b]{SideText(side)}[/b]" : Link($"rg-ch-max:{side}", SideText(side))).Append("  ");
        sb.Append("\n").Append(Link("rg-ch-cancel", "Cancel")).Append("   ")
          .Append($"[b]{Link("rg-ch-send", Admin() || s.Voters.Count <= 1 ? "Set it" : "Put it to a vote")}[/b]");
    }

    /// <summary>
    /// This city's deals with its neighbours, with what its mayor can do about each; and with a neighbour picked, the
    /// offer being drawn up for it.
    /// </summary>
    /// <summary>The city next door this player's city could take in: the rectangle the two would make and a fair start.</summary>
    public TakeoverOption? TakeoverAt(RegionTileState t) => State?.TakeoverOptions.FirstOrDefault(x => x.Col == t.Col && x.Row == t.Row);

    /// <summary>Taking in a neighbour by a deal: the offers open now, both ways, and for a picked neighbour an offer to send.</summary>
    void RenderTakeovers(System.Text.StringBuilder sb, RegionState s, RegionTileState? p)
    {
        var home = Here;
        if (s.Takeovers.Count > 0)
        {
            sb.Append("\n\n").Append(H3("Taking in a neighbour"));
            foreach (var o in s.Takeovers)
            {
                bool theirs = o.SellerCol == s.HereCol && o.SellerRow == s.HereRow;
                sb.Append($"[b]{Esc(o.Buyer)}[/b] would take in [b]{Esc(o.Seller)}[/b] for {Money(o.Price)} ").Append(Dim($"({o.ToCols} × {o.ToRows} tiles; offered by {Esc(o.By)})")).Append('\n');
                if (theirs && home?.Mayor == Me()) sb.Append(Link($"rg-to-accept:{o.Id}", $"Agree: {Esc(o.Seller)} joins {Esc(o.Buyer)}")).Append("   ").Append(Link($"rg-to-decline:{o.Id}", "Decline")).Append('\n');
                else if (!theirs && (home?.Mayor == Me() || o.By == Me())) sb.Append(Dim($"Waiting for {Esc(o.Seller)}'s mayor. ")).Append(Link($"rg-to-withdraw:{o.Id}", "Withdraw")).Append('\n');
            }
        }
        if (p == null || home == null || TakeoverAt(p) is not { } opt) return;
        if (Bid.Col != opt.Col || Bid.Row != opt.Row) Bid = (opt.Col, opt.Row, opt.Floor);
        // A computer mayor takes no less than its price, which moves with its city.
        if (opt.Computer && Bid.Price < opt.Floor) Bid = (Bid.Col, Bid.Row, opt.Floor);
        sb.Append("\n\n").Append(H3($"Take in {Esc(p.Name)}"));
        sb.Append(Dim($"{Esc(home.Name)} and {Esc(p.Name)} would be one city of ")).Append($"[b]{opt.ToCols} × {opt.ToRows} tiles[/b]")
          .Append(Dim($", named {Esc(home.Name)}. {Esc(p.Name)}'s buildings, roads and people come with it, and its treasury and debts; its bus lines and dispatch close. "
            + (opt.Computer ? $"{Esc(p.Mayor)} sells for {Money(opt.Floor)} or more, by its temperament." : $"Its mayor, {Esc(p.Mayor)}, has to agree, and stays on as a deputy."))).Append("\n\n");
        if (opt.Why.Length > 0) sb.Append(Col(Warn, Esc(opt.Why)));
        else if (home.Mayor == Me() || Admin())
            sb.Append(Dim("Price ")).Append(Link("rg-bid:-", "−")).Append($" [b]{Money(Bid.Price)}[/b] ").Append(Link("rg-bid:+", "＋")).Append("   ")
              .Append(Link("rg-takeover-offer", opt.Computer ? "Buy it" : "Offer it"));
    }

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
