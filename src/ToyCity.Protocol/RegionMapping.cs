using Google.Protobuf;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Protocol;

/// <summary>Regions on the wire and on disk.</summary>
public static class RegionMapping
{
    public const uint RegionFormat = 1;
    /// <summary>Pixels along a side of a tile's picture on the region map.</summary>
    public const int PictureSize = 32;

    public static byte[] Save(Region r)
    {
        var s = new P.RegionSave
        {
            FormatVersion = RegionFormat, Name = r.Name, Seed = r.Seed, Cols = r.Cols, Rows = r.Rows, TileSize = r.TileSize, Tick = r.Tick,
        };
        s.Grows = r.Grows;
        s.TileByTile = r.TileByTile;
        s.WaterStays = !r.WaterCrossesBorders;
        s.Charter = Charter(r.Charter);
        foreach (var v in r.Votes) s.Votes.Add(Vote(r, v, text: false));
        foreach (var t in r.Tiles)
            if (r.GrownGround(t.Col, t.Row) is { } g)
            {
                var gt = new P.RegionGrownTile
                {
                    Col = t.Col, Row = t.Row, Corners = Words(g.Corners), Rivers = ByteString.CopyFrom(g.Rivers),
                    Bedrock = Words(g.Bedrock), Rock = ByteString.CopyFrom(g.Rock),
                };
                gt.Downstream.AddRange(g.Downstream);
                s.Grown.Add(gt);
            }
        foreach (var t in r.Anchors)
            s.Cities.Add(new P.RegionSavedCity { Col = t.Col, Row = t.Row, Save = ByteString.CopyFrom(Mapping.Save(t.City!)) });
        foreach (var d in r.Deals.Where(d => !d.Active)) s.Offers.Add(Deal(r, d));
        foreach (var o in r.Takeovers) s.Takeovers.Add(Takeover(r, o));
        s.NextTakeover = r.NextTakeoverId;
        return s.ToByteArray();
    }

    public static Region Load(ContentDb content, byte[] data)
    {
        var s = P.RegionSave.Parser.ParseFrom(data);
        if (s.FormatVersion != RegionFormat) throw new InvalidDataException($"region format {s.FormatVersion} is not supported");
        var cities = new List<World>();
        try
        {
            var at = new Dictionary<World, (int, int)>();
            foreach (var c in s.Cities)
            {
                var w = Mapping.Load(content, c.Save.ToByteArray());
                cities.Add(w);
                at[w] = (c.Col, c.Row);
            }
            var grown = s.Grows || s.TileByTile
                ? s.Grown.Select(g => (g.Col, g.Row, new TileGround(Words(g.Corners), g.Rivers.ToByteArray(), g.Downstream.ToArray(), Words(g.Bedrock), g.Rock.ToByteArray()))).ToList()
                : null;
            var r = Region.Restore(content, s.Name, s.Seed, s.Cols, s.Rows, s.TileSize, s.Tick, cities, grown, s.TileByTile, w => at.TryGetValue(w, out var p) ? p : null);
            r.WaterCrossesBorders = !s.WaterStays;
            if (s.Charter != null) r.Charter = Charter(s.Charter);
            foreach (var v in s.Votes) r.RestoreVote(Vote(v));
            foreach (var o in s.Takeovers)
                r.Takeovers.Add(new TakeoverOffer
                {
                    Id = o.Id, Buyer = o.BuyerRow * s.Cols + o.BuyerCol, Seller = o.SellerRow * s.Cols + o.SellerCol, Price = o.Price, By = o.By, Day = o.Day,
                });
            r.NextTakeoverId = Math.Max(s.NextTakeover, r.Takeovers.Select(o => o.Id + 1).DefaultIfEmpty(1).Max());
            foreach (var o in s.Offers)
                r.RestoreOffer(new RegionDeal
                {
                    Id = o.Id, Kind = (DealKind)(int)o.Kind, Seller = o.SellerRow * s.Cols + o.SellerCol, Buyer = o.BuyerRow * s.Cols + o.BuyerCol,
                    Amount = o.Amount, Price = o.Price, OfferedBySeller = o.OfferedBySeller, Round = o.Round,
                });
            return r;
        }
        catch
        {
            foreach (var c in cities) c.Dispose();
            throw;
        }
    }

    static ByteString Words(ushort[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++) { b[2 * i] = (byte)v[i]; b[2 * i + 1] = (byte)(v[i] >> 8); }
        return ByteString.CopyFrom(b);
    }

    static ushort[] Words(ByteString b)
    {
        var s = b.Span;
        var v = new ushort[s.Length / 2];
        for (int i = 0; i < v.Length; i++) v[i] = (ushort)(s[2 * i] | s[2 * i + 1] << 8);
        return v;
    }

    /// <summary>True when the bytes are a region save rather than a city's.</summary>
    public static bool IsRegion(byte[] data)
    {
        try { var s = P.RegionSave.Parser.ParseFrom(data); return s.FormatVersion == RegionFormat && s.Cols > 0; }
        catch (InvalidProtocolBufferException) { return false; }
    }

    /// <summary>The region map. Pictures are the costly part: pass the ones already made to reuse them.</summary>
    public static P.RegionState State(Region r, int hereCol, int hereRow, int speed, Func<RegionTile, IEnumerable<string>> playersIn,
        IDictionary<int, ByteString>? pictures = null)
    {
        var s = new P.RegionState
        {
            Name = r.Name, Cols = r.Cols, Rows = r.Rows, TileSize = r.TileSize, Tick = r.Tick, HereCol = hereCol, HereRow = hereRow, Speed = speed,
            Grows = r.Grows, WaterStays = !r.WaterCrossesBorders, TileByTile = r.TileByTile, Charter = Charter(r.Charter), MaxCitySide = r.MaxCitySide,
            ServerMaxCitySide = Math.Max(1, Region.MaxCityCells / r.TileSize),
        };
        s.Voters.AddRange(r.Voters());
        foreach (var v in r.Votes) s.Votes.Add(Vote(r, v, text: true));
        // A growing region's frame is 255 tiles a side: only the tiles in it, and the wild ring around them where it
        // can grow, go on the wire.
        int c0 = r.BoxCol - 1, r0 = r.BoxRow - 1, c1 = r.BoxCol + r.BoxCols, r1 = r.BoxRow + r.BoxRows;
        // Pictures not made yet are made side by side: a huge region has a thousand of them.
        if (pictures != null)
        {
            var missing = Enumerable.Range(0, r.Tiles.Length).Where(i => !r.Tiles[i].Wild && !pictures.ContainsKey(i)).ToList();
            if (missing.Count > 4)
            {
                r.Glimpse(0, 0, 1);  // the shore, worked out once before the threads share it
                var made = new byte[missing.Count][];
                Parallel.For(0, missing.Count, k => made[k] = Picture(r, r.Tiles[missing[k]]));
                for (int k = 0; k < missing.Count; k++) pictures[missing[k]] = ByteString.CopyFrom(made[k]);
            }
        }
        for (int i = 0; i < r.Tiles.Length; i++)
        {
            var t = r.Tiles[i];
            if (t.Wild)
            {
                if (t.Col < c0 || t.Col > c1 || t.Row < r0 || t.Row > r1) continue;
                s.Tiles.Add(new P.RegionTileState { Col = t.Col, Row = t.Row, Name = "Wild land", Wild = true });
                continue;
            }
            var pic = pictures != null && pictures.TryGetValue(i, out var p) ? p : ByteString.CopyFrom(Picture(r, t));
            if (pictures != null) pictures[i] = pic;
            var ts = new P.RegionTileState { Col = t.Col, Row = t.Row, Founded = t.Founded, Name = t.Name, Mayor = t.Mayor, Picture = pic, Unsurveyed = Unsurveyed(r, t) };
            if (t.Founded)
            {
                var (fc, fr, fcs, frs) = r.Footprint(t);
                (ts.CityCol, ts.CityRow, ts.CityCols, ts.CityRows) = (fc, fr, fcs, frs);
            }
            if (t.City is { } w)
            {
                ts.Population = w.Population;
                ts.Funds = w.Funds;
                if (w.ComputerMayor) ts.Computer = World.Temperament(w.MayorSeat.Style);
            }
            if ((r.Tiles.Length > 1 || r.PinnedWeather != null) && r.WeatherAt(t, r.Tick) is var (wk, ws) && wk != WeatherKind.Clear) ts.Weather = $"{World.WeatherIcon(wk)} {World.WeatherName(wk, ws)}";
            ts.Players.AddRange(playersIn(t));
            s.Tiles.Add(ts);
        }
        // Across each side of this city: what the neighbour shows there, for markers on the map's edge.
        if (r.TileAt(hereCol, hereRow) is { City: not null } here)
        {
            for (int side = 0; side < 4; side++)
                if (r.NeighbourOn(here, side)?.City is { } n)
                    s.Borders.Add(Mapping.Edge(n.EdgeOut(World.Opposite(side))));
            // What annexing each open tile around it would take.
            var (hc, hr, hcs, hrs) = r.Footprint(here);
            for (int row = hr - 1; row <= hr + hrs; row++)
            for (int col = hc - 1; col <= hc + hcs; col++)
            {
                if (r.TileAt(col, row) is not { Founded: false, Wild: false }) continue;
                var to = r.AnnexRect(here, col, row);
                s.Annex.Add(new P.AnnexOption
                {
                    Col = col, Row = row, ToCol = to.Col, ToRow = to.Row, ToCols = to.Cols, ToRows = to.Rows,
                    Price = r.AnnexPrice(here, to), Why = r.CantAnnex(here, col, row) ?? "",
                });
            }
            // The cities next door it could take in, by a deal.
            var seen = new HashSet<RegionTile>();
            for (int side = 0; side < 4; side++)
            {
                if (r.NeighbourOn(here, side) is not { } n || !seen.Add(n)) continue;
                var to = r.TakeoverRect(here, n);
                s.TakeoverOptions.Add(new P.TakeoverOption
                {
                    Col = n.Col, Row = n.Row, ToCol = to?.Col ?? 0, ToRow = to?.Row ?? 0, ToCols = to?.Cols ?? 0, ToRows = to?.Rows ?? 0,
                    Floor = r.TakeoverFloor(n), Why = r.CantTakeOver(here, n, 0) ?? "", Computer = n.City!.ComputerMayor,
                });
            }
            int mine = r.Footprint(here) is var f ? f.Row * r.Cols + f.Col : -1;
            foreach (var o in r.Takeovers.Where(o => o.Buyer == mine || o.Seller == mine)) s.Takeovers.Add(Takeover(r, o));
        }
        if (r.TileAt(hereCol, hereRow) is { } at)
        {
            int me = at.Row * r.Cols + at.Col;
            foreach (var d in r.Deals.Where(d => d.Seller == me || d.Buyer == me)) s.Deals.Add(Deal(r, d));
        }
        return s;
    }

    public static P.TakeoverOffer Takeover(Region r, TakeoverOffer o)
    {
        var (b, sl) = r.Parties(o);
        var to = b.City != null && sl.City != null ? r.TakeoverRect(b, sl) : null;
        return new P.TakeoverOffer
        {
            Id = o.Id, BuyerCol = b.Col, BuyerRow = b.Row, SellerCol = sl.Col, SellerRow = sl.Row, Buyer = b.Name, Seller = sl.Name,
            Price = o.Price, By = o.By, Day = o.Day, ToCols = to?.Cols ?? 0, ToRows = to?.Rows ?? 0,
        };
    }

    static bool Unsurveyed(Region r, RegionTile t) => r.TileByTile && t.City == null && r.GrownGround(t.Col, t.Row) == null;

    public static P.RegionCharter Charter(RegionCharter c) => new() { Grow = (P.CharterRule)(int)c.Grow, Annex = (P.CharterRule)(int)c.Annex, MaxCitySide = c.MaxCitySide };

    public static RegionCharter Charter(P.RegionCharter c) => new((CharterRule)(int)c.Grow, (CharterRule)(int)c.Annex, c.MaxCitySide);

    public static P.RegionVote Vote(Region r, RegionVote v, bool text)
    {
        var p = new P.RegionVote { Id = v.Id, Kind = (P.VoteKind)(int)v.Kind, Col = v.Col, Row = v.Row, ToCol = v.ToCol, ToRow = v.ToRow, By = v.By, Day = v.Day };
        if (v.Charter != null) p.Charter = Charter(v.Charter);
        p.Yes.AddRange(v.Yes);
        p.No.AddRange(v.No);
        if (text) p.Text = r.Describe(v);
        return p;
    }

    static RegionVote Vote(P.RegionVote p)
    {
        var v = new RegionVote
        {
            Id = p.Id, Kind = (VoteKind)(int)p.Kind, Col = p.Col, Row = p.Row, ToCol = p.ToCol, ToRow = p.ToRow, By = p.By, Day = p.Day,
            Charter = p.Charter != null ? Charter(p.Charter) : null,
        };
        foreach (var y in p.Yes) v.Yes.Add(y);
        foreach (var n in p.No) v.No.Add(n);
        return v;
    }

    /// <summary>A temperament as the wire names it ("growth", "green", "careful", "chaos"), or null.</summary>
    public static AutopilotStyle? Style(string s) => s switch
    {
        "growth" or "growth-hungry" => AutopilotStyle.Growth,
        "green" => AutopilotStyle.Green,
        "careful" or "profit" => AutopilotStyle.Profit,
        "chaos" or "unpredictable" => AutopilotStyle.Chaos,
        _ => null,
    };

    public static P.Deal Deal(Region r, RegionDeal d)
    {
        var seller = r.Tiles[d.Seller];
        var buyer = r.Tiles[d.Buyer];
        return new P.Deal
        {
            Id = d.Id, Kind = (P.DealKind)(int)d.Kind, SellerCol = seller.Col, SellerRow = seller.Row, BuyerCol = buyer.Col, BuyerRow = buyer.Row,
            Seller = seller.Name, Buyer = buyer.Name, Amount = d.Amount, Price = d.Price, Active = d.Active, OfferedBySeller = d.OfferedBySeller, Round = d.Round,
        };
    }

    /// <summary>
    /// A tile seen from above at <see cref="PictureSize"/> pixels a side: water, ground by height, and on a city its
    /// roads, zones and buildings.
    /// </summary>
    public static byte[] Picture(Region r, RegionTile t)
    {
        int n = PictureSize, s = r.TileSize, step = s / n;
        var w = t.City;
        if (Unsurveyed(r, t))
        {
            // A huge region's open land not yet looked at: a rough look, not its ground (making that takes seconds a tile).
            var g = r.Glimpse(t.Col, t.Row, n);
            var rough = new byte[n * n * 3];
            for (int i = 0; i < n * n; i++)
            {
                var (cr, cg, cb) = Colour(null, g, 8, i, 0);
                rough[i * 3] = cr; rough[i * 3 + 1] = cg; rough[i * 3 + 2] = cb;
            }
            return rough;
        }
        var heights = w?.Height ?? r.TileHeights(t.Col, t.Row);
        int water = w?.WaterLevel ?? 8;
        // A city of several tiles: this tile's part of its map.
        int wide = w?.Width ?? s, ox = 0, oy = 0;
        if (w != null && t.Anchor is { } a) (ox, oy) = ((t.Col - a.Col) * s, (t.Row - a.Row) * s);
        var px = new byte[n * n * 3];
        for (int py = 0; py < n; py++)
        for (int pxl = 0; pxl < n; pxl++)
        {
            // The most telling cell in the block: a building, then a road, then a zone, then the ground.
            int best = -1, rank = -1;
            for (int y = py * step; y < (py + 1) * step; y++)
            for (int x = pxl * step; x < (pxl + 1) * step; x++)
            {
                int i = (y + oy) * wide + x + ox;
                int k = w == null ? 0 : w.BuildingAt[i] != 0 ? 3 : w.Path[i] != 0 ? 2 : w.ZoneCode[i] != 0 ? 1 : 0;
                if (k > rank) { rank = k; best = i; }
            }
            var (cr, cg, cb) = Colour(w, heights, water, best, rank);
            int o = (py * n + pxl) * 3;
            px[o] = cr; px[o + 1] = cg; px[o + 2] = cb;
        }
        return px;
    }

    static (byte, byte, byte) Colour(World? w, byte[] heights, int water, int i, int rank)
    {
        int h = heights[i];
        if (h < water) return ((byte)(40 + h * 6), (byte)(90 + h * 8), (byte)(150 + h * 8));
        if (w != null && rank == 3)
        {
            var b = w.BuildingAtCell(i)!;
            return w.TypeOf(b).Zone switch
            {
                Zone.Residential => (120, 200, 110),
                Zone.Commercial => (110, 150, 230),
                Zone.Industrial => (230, 200, 90),
                _ => (215, 215, 215),
            };
        }
        if (rank == 2) return (95, 95, 100);
        if (w != null && rank == 1)
            return World.ZoneOf(w.ZoneCode[i]) switch
            {
                Zone.Residential => (80, 140, 75),
                Zone.Commercial => (75, 100, 150),
                _ => (150, 130, 70),
            };
        int g = Math.Clamp(h - water, 0, 24);
        return ((byte)(96 + g * 4), (byte)(140 + g * 2), (byte)(70 + g * 3));
    }
}
