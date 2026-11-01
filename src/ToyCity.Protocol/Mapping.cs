using Google.Protobuf;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Protocol;

/// <summary>Converts between the simulation's own types and the wire and save format.</summary>
public static class Mapping
{
    public const uint ProtocolVersion = 0;

    static CellRect Rect(P.Rect? r) => r == null ? new CellRect(0, 0, 0, 0) : new CellRect(r.X0, r.Y0, r.X1, r.Y1);
    static P.Rect Rect(CellRect r) => new() { X0 = r.X0, Y0 = r.Y0, X1 = r.X1, Y1 = r.Y1 };

    /// <summary>Wire action to sim action. Returns null for an empty or unknown action.</summary>
    public static SimAction? ToSim(P.Action a) => a.KindCase switch
    {
        P.Action.KindOneofCase.Path => new PlacePathAction(a.Path.X0, a.Path.Y0, a.Path.X1, a.Path.Y1, a.Path.ProfileId, a.Path.Curved, a.Path.Cx, a.Path.Cy, a.Path.Diagonal),
        P.Action.KindOneofCase.Block => new ZoneBlockAction(a.Block.X, a.Block.Y, (Zone)(int)a.Block.Zone, (Level)(int)a.Block.Density, (Leftover)(int)a.Block.Leftover),
        P.Action.KindOneofCase.Zone => new PaintZoneAction(Rect(a.Zone.Area), (Zone)(int)a.Zone.Zone, (Level)(int)a.Zone.Density),
        P.Action.KindOneofCase.Building => new PlaceBuildingAction(a.Building.X, a.Building.Y, a.Building.TypeId, a.Building.Side - 1),
        P.Action.KindOneofCase.Upgrade => new UpgradeBuildingAction(a.Upgrade.BuildingId, a.Upgrade.ToTypeId),
        P.Action.KindOneofCase.Bulldoze => new BulldozeAction(Rect(a.Bulldoze.Area), a.Bulldoze.Underground),
        P.Action.KindOneofCase.Terraform => new TerraformAction(Rect(a.Terraform.Area), (TerraformMode)(int)a.Terraform.Mode),
        P.Action.KindOneofCase.Tax => new SetTaxAction((Zone)(int)a.Tax.Zone, (Level)(int)a.Tax.Wealth, a.Tax.PercentTenths),
        P.Action.KindOneofCase.Funding => new SetFundingAction(a.Funding.BuildingId, a.Funding.Percent),
        P.Action.KindOneofCase.Loan => new TakeLoanAction(a.Loan.Amount),
        P.Action.KindOneofCase.Utility => new PlaceUtilityAction(a.Utility.X0, a.Utility.Y0, a.Utility.X1, a.Utility.Y1, (UtilityKind)(int)a.Utility.Kind),
        P.Action.KindOneofCase.Ordinance => new SetOrdinanceAction(a.Ordinance.Id, a.Ordinance.Enact),
        P.Action.KindOneofCase.Respond => new RespondPromptAction(a.Respond.PromptId, a.Respond.Accept),
        P.Action.KindOneofCase.Disaster => new TriggerDisasterAction((DisasterKind)(int)a.Disaster.Kind, a.Disaster.X, a.Disaster.Y),
        P.Action.KindOneofCase.Disasters => new SetDisasterSettingAction((DisasterSetting)(int)a.Disasters.Setting),
        P.Action.KindOneofCase.SideGame => new SideGameAction(a.SideGame.Game, a.SideGame.Seat, a.SideGame.Payload.ToByteArray()),
        P.Action.KindOneofCase.Attach => new AttachSideGameAction(a.Attach.Game),
        P.Action.KindOneofCase.Lease => new LeaseLotAction(a.Lease.Game, a.Lease.BuildingId, a.Lease.Terms == null ? null : Terms(a.Lease.Terms)),
        P.Action.KindOneofCase.LeaseTerms => new SetLeaseTermsAction(a.LeaseTerms.LeaseId, Terms(a.LeaseTerms.Terms ?? new P.LeaseTerms())),
        P.Action.KindOneofCase.EndLease => new EndLeaseAction(a.EndLease.LeaseId, a.EndLease.ByOperator),
        P.Action.KindOneofCase.Offer => new OfferLeaseAction(a.Offer.LeaseId, a.Offer.FromOperator, (OfferKind)(int)a.Offer.Kind,
            a.Offer.Terms == null ? null : Terms(a.Offer.Terms), a.Offer.Amount, a.Offer.ToBuildingId, a.Offer.CounterOf),
        P.Action.KindOneofCase.AnswerOffer => new AnswerOfferAction(a.AnswerOffer.OfferId, a.AnswerOffer.Accept),
        P.Action.KindOneofCase.Delink => new DelinkAction(a.Delink.Player),
        P.Action.KindOneofCase.OpenTender => new OpenTenderAction(a.OpenTender.District, a.OpenTender.Months),
        P.Action.KindOneofCase.Consent => new SetConsentAction(a.Consent.Owner, (Mechanic)(int)a.Consent.Mechanic, a.Consent.Allowed),
        P.Action.KindOneofCase.Seat => new SetSeatAction(a.Seat.Seat, a.Seat.Owner, (ToyCity.Contract.OperatorLevel)(int)a.Seat.Level, (AutopilotStyle)(int)a.Seat.Style, a.Seat.SpendCap),
        P.Action.KindOneofCase.CityRule => new SetCityRuleAction((Mechanic)(int)a.CityRule.Mechanic, a.CityRule.Forbid),
        P.Action.KindOneofCase.Pact => a.Pact.Step switch
        {
            P.Pact.Types.Step.PactOffer => new OfferPactAction(a.Pact.With, (Mechanic)(int)a.Pact.Mechanic),
            P.Pact.Types.Step.PactAccept => new AcceptPactAction(a.Pact.With, (Mechanic)(int)a.Pact.Mechanic),
            _ => new EndPactAction(a.Pact.With, (Mechanic)(int)a.Pact.Mechanic),
        },
        P.Action.KindOneofCase.Deputy => new SetDeputyAction(a.Deputy.Player, (Portfolio)(byte)a.Deputy.Portfolios),
        P.Action.KindOneofCase.Council => new SetCouncilAction(a.Council.On),
        P.Action.KindOneofCase.Vote => new VoteAction(a.Vote.Proposal, a.Vote.Yes),
        P.Action.KindOneofCase.AwayLevel => new SetAwayLevelAction(a.AwayLevel.Seat, (ToyCity.Contract.OperatorLevel)(int)a.AwayLevel.Level, (AutopilotStyle)(int)a.AwayLevel.Style),
        _ => null,
    };

    public static P.Action ToProto(SimAction a) => a switch
    {
        PlacePathAction x => new P.Action { Path = new P.PlacePath { X0 = x.X0, Y0 = x.Y0, X1 = x.X1, Y1 = x.Y1, ProfileId = x.ProfileId, Curved = x.Curved, Cx = x.CX, Cy = x.CY, Diagonal = x.Diagonal } },
        ZoneBlockAction x => new P.Action { Block = new P.ZoneBlock { X = x.X, Y = x.Y, Zone = (P.ZoneKind)(int)x.Zone, Density = (P.Density)(int)x.Density, Leftover = (P.Leftover)(int)x.Leftover } },
        PaintZoneAction x => new P.Action { Zone = new P.PaintZone { Area = Rect(x.Area), Zone = (P.ZoneKind)(int)x.Zone, Density = (P.Density)(int)x.Density } },
        PlaceBuildingAction x => new P.Action { Building = new P.PlaceBuilding { X = x.X, Y = x.Y, TypeId = x.TypeId, Side = x.Side + 1 } },
        UpgradeBuildingAction x => new P.Action { Upgrade = new P.UpgradeBuilding { BuildingId = x.BuildingId, ToTypeId = x.ToTypeId } },
        BulldozeAction x => new P.Action { Bulldoze = new P.Bulldoze { Area = Rect(x.Area), Underground = x.Underground } },
        TerraformAction x => new P.Action { Terraform = new P.Terraform { Area = Rect(x.Area), Mode = (P.TerraformMode)(int)x.Mode } },
        SetTaxAction x => new P.Action { Tax = new P.SetTax { Zone = (P.ZoneKind)(int)x.Zone, Wealth = (P.Wealth)(int)x.Wealth, PercentTenths = x.PercentTenths } },
        SetFundingAction x => new P.Action { Funding = new P.SetFunding { BuildingId = x.BuildingId, Percent = x.Percent } },
        TakeLoanAction x => new P.Action { Loan = new P.TakeLoan { Amount = x.Amount } },
        PlaceUtilityAction x => new P.Action { Utility = new P.PlaceUtility { X0 = x.X0, Y0 = x.Y0, X1 = x.X1, Y1 = x.Y1, Kind = (P.UtilityKind)(int)x.Kind } },
        SetOrdinanceAction x => new P.Action { Ordinance = new P.SetOrdinance { Id = x.Id, Enact = x.Enact } },
        RespondPromptAction x => new P.Action { Respond = new P.RespondPrompt { PromptId = x.PromptId, Accept = x.Accept } },
        TriggerDisasterAction x => new P.Action { Disaster = new P.TriggerDisaster { Kind = (P.DisasterKind)(int)x.Kind, X = x.X, Y = x.Y } },
        SetDisasterSettingAction x => new P.Action { Disasters = new P.SetDisasters { Setting = (P.DisasterSetting)(int)x.Setting } },
        SideGameAction x => new P.Action { SideGame = new P.SideGameCommand { Game = x.GameId, Seat = x.Seat, Payload = ByteString.CopyFrom(x.Payload) } },
        AttachSideGameAction x => new P.Action { Attach = new P.AttachSideGame { Game = x.GameId } },
        LeaseLotAction x => new P.Action { Lease = new P.LeaseLot { Game = x.GameId, BuildingId = x.BuildingId, Terms = x.Terms == null ? null : Terms(x.Terms) } },
        SetLeaseTermsAction x => new P.Action { LeaseTerms = new P.SetLeaseTerms { LeaseId = x.LeaseId, Terms = Terms(x.Terms) } },
        EndLeaseAction x => new P.Action { EndLease = new P.EndLease { LeaseId = x.LeaseId, ByOperator = x.ByOperator } },
        OfferLeaseAction x => new P.Action { Offer = new P.OfferLease
        {
            LeaseId = x.LeaseId, FromOperator = x.FromOperator, Kind = (P.OfferKind)(int)x.Kind, Terms = x.Terms == null ? null : Terms(x.Terms),
            Amount = x.Amount, ToBuildingId = x.ToBuildingId, CounterOf = x.CounterOf,
        } },
        AnswerOfferAction x => new P.Action { AnswerOffer = new P.AnswerOffer { OfferId = x.OfferId, Accept = x.Accept } },
        DelinkAction x => new P.Action { Delink = new P.Delink { Player = x.Player } },
        OpenTenderAction x => new P.Action { OpenTender = new P.OpenTender { District = x.District, Months = x.Months } },
        SetConsentAction x => new P.Action { Consent = new P.SetConsent { Owner = x.Owner, Mechanic = (P.Mechanic)(int)x.Mechanic, Allowed = x.Allowed } },
        SetSeatAction x => new P.Action { Seat = new P.SetSeat { Seat = x.SeatId, Owner = x.Owner, Level = (P.SeatLevel)(int)x.Level, Style = (P.AutopilotStyle)(int)x.Style, SpendCap = x.SpendCap } },
        SetCityRuleAction x => new P.Action { CityRule = new P.SetCityRule { Mechanic = (P.Mechanic)(int)x.Mechanic, Forbid = x.Forbid } },
        OfferPactAction x => new P.Action { Pact = new P.Pact { Step = P.Pact.Types.Step.PactOffer, With = x.With, Mechanic = (P.Mechanic)(int)x.Mechanic } },
        AcceptPactAction x => new P.Action { Pact = new P.Pact { Step = P.Pact.Types.Step.PactAccept, With = x.From, Mechanic = (P.Mechanic)(int)x.Mechanic } },
        EndPactAction x => new P.Action { Pact = new P.Pact { Step = P.Pact.Types.Step.PactEnd, With = x.With, Mechanic = (P.Mechanic)(int)x.Mechanic } },
        SetDeputyAction x => new P.Action { Deputy = new P.SetDeputy { Player = x.Player, Portfolios = (uint)x.Portfolios } },
        SetCouncilAction x => new P.Action { Council = new P.SetCouncil { On = x.On } },
        VoteAction x => new P.Action { Vote = new P.Vote { Proposal = x.ProposalId, Yes = x.Yes } },
        SetAwayLevelAction x => new P.Action { AwayLevel = new P.SetAwayLevel { Seat = x.SeatId, Level = (P.SeatLevel)(int)x.Level, Style = (P.AutopilotStyle)(int)x.Style } },
        _ => throw new ArgumentException($"unknown action {a.GetType().Name}"),
    };

    static ToyCity.Contract.LeaseTerms Terms(P.LeaseTerms? t) => t == null ? World.StandardTerms("bus-stop")
        : new((ToyCity.Contract.LeaseMode)(int)t.Mode, t.RentPerMonth, t.TaxTenths, t.SubsidyPerMonth, t.FareCapCents, t.NoticeMonths);

    static P.LeaseTerms Terms(ToyCity.Contract.LeaseTerms t) => new()
    {
        Mode = (P.LeaseMode)(int)t.Mode, RentPerMonth = t.RentPerMonth, TaxTenths = t.TaxTenths, SubsidyPerMonth = t.SubsidyPerMonth,
        FareCapCents = t.FareCapCents, NoticeMonths = t.NoticeMonths,
    };

    static string CategoryName(Category c) => c.ToString().ToLowerInvariant();

    public static P.Catalog Catalog(ContentDb c)
    {
        var cat = new P.Catalog();
        foreach (var p in c.Profiles)
            cat.Profiles.Add(new P.PathProfileInfo
            {
                Id = p.Id, Name = p.Name, Kind = "path", SpeedKmh = p.SpeedKmh, Cars = p.Cars, Bikes = p.Bikes,
                Pedestrians = p.Pedestrians, OneWay = p.OneWay, Frontage = p.Frontage, CostPerCell = p.CostPerCell, Color = p.Color, WidthM = (int)Math.Round(p.WidthM),
                Tram = p.Tram, Rail = p.Rail,
            });
        foreach (var t in c.Types)
            cat.BuildingTypes.Add(new P.BuildingTypeInfo
            {
                Id = t.Id, Name = t.Name, Category = CategoryName(t.Category), Zone = (P.ZoneKind)(int)t.Zone,
                Density = (P.Density)(int)t.Density, Wealth = (P.Wealth)(int)t.Wealth, Width = t.W, Height = t.H,
                Cost = t.Cost, Placeable = t.Placeable, Color = t.Color, UnlockText = t.Unlock != null ? t.UnlockText : "",
                Unique = t.Unique, Income = t.Income, Attraction = t.Attraction,
                Transit = t.Transit == TransitKind.None ? "" : t.Transit.ToString().ToLowerInvariant(), Capacity = t.Capacity, Catchment = t.Catchment,
                Roadside = t.Roadside, LotKind = t.LotKind, Maintenance = t.Maintenance, LeaseRent = t.LeaseRent, BuildDays = t.BuildDays,
                Upgrades =
                {
                    t.Upgrades.Select(u => new P.UpgradeInfo
                    {
                        ToTypeId = u.To, Cost = u.Cost, Riders = u.Riders, Population = u.Population, Text = u.Text,
                        NearZone = u.NearZone switch { "R" => P.ZoneKind.ZoneResidential, "C" => P.ZoneKind.ZoneCommercial, "I" => P.ZoneKind.ZoneIndustrial, _ => P.ZoneKind.ZoneNone },
                    }),
                },
            });
        foreach (var sizes in c.LotSizes) cat.LotDepth.Add(sizes.Max(z => Math.Max(z.W, z.D)));
        cat.StreetNames.AddRange(c.Voice.StreetNames);
        cat.StreetKinds.AddRange(c.Voice.StreetKinds);
        return cat;
    }

    public static P.BuildingState Building(World w, Building b)
    {
        var t = w.TypeOf(b);
        return new P.BuildingState
        {
            Id = b.Id, TypeId = t.Id, X = b.X, Y = b.Y, Width = b.W, Height = b.H, Zone = (P.ZoneKind)(int)t.Zone,
            Density = (P.Density)(int)t.Density, Wealth = (P.Wealth)(int)t.Wealth, Name = t.Name,
            Residents = b.Operating ? b.Residents : 0, Jobs = b.Operating ? b.Jobs : 0,
            Workers = b.Residents > 0 ? b.Employed : b.Workers, Powered = b.Powered, Watered = b.Watered,
            Abandoned = b.Abandoned, ConstructionDaysLeft = b.ConstructionLeft, FundingPercent = b.Funding, Condition = b.Condition,
            Graffiti = b.Graffiti, BurningDays = b.BurningDays, Look = (P.BuildingLook)(int)w.LookOf(b), Riders = b.Riders,
            Side = b.Side,
        };
    }

    public static P.CityStats Stats(World w, int speed)
    {
        var s = new P.CityStats
        {
            Tick = w.Tick, Year = w.Year, Month = w.Month, Day = w.Day, Funds = w.Funds, Population = w.Population,
            Jobs = w.Jobs, Workers = w.Workers, LastMonthIncome = w.LastIncome, LastMonthExpenses = w.LastExpenses,
            Debt = w.Debt, Speed = speed, PowerCapacityKw = w.PowerCapacity, PowerDemandKw = w.PowerDemand,
            WaterCapacity = w.WaterCapacity, WaterDemand = w.WaterDemand, GarbageCapacity = w.GarbageCapacity,
            GarbageProduced = w.GarbageProduced, CityName = w.Name,
        };
        s.Demand.AddRange(w.Demand);
        s.DemandRaw.AddRange(w.DemandRaw);
        s.TaxTenths.AddRange(w.TaxTenths);
        s.DisasterSetting = (P.DisasterSetting)(int)w.DisasterSetting;
        s.TransitBoardings = w.TransitBoardings;
        if (w.Weather != WeatherKind.Clear) s.Weather = $"{World.WeatherIcon(w.Weather)} {World.WeatherName(w.Weather, w.WeatherStrength)}";
        s.FloodStage = w.FloodStage;
        foreach (var d in w.Disasters)
            s.Disasters.Add(new P.Disaster { Id = d.Id, Kind = (P.DisasterKind)(int)d.Kind, X = d.X, Y = d.Y, DaysLeft = d.DaysLeft, Destroyed = d.Destroyed, Damaged = d.Damaged });
        foreach (var (label, amount) in w.LastBudget) s.Budget.Add(new P.BudgetLine { Label = label, Amount = amount });
        return s;
    }

    static byte[] CornerBytes(World w)
    {
        var bytes = new byte[w.Corner.Length * 2];
        for (int i = 0; i < w.Corner.Length; i++) { bytes[2 * i] = (byte)w.Corner[i]; bytes[2 * i + 1] = (byte)(w.Corner[i] >> 8); }
        return bytes;
    }

    static byte[] PathBytes(World w)
    {
        var bytes = new byte[w.N * 2];
        for (int i = 0; i < w.N; i++) { bytes[2 * i] = (byte)w.Path[i]; bytes[2 * i + 1] = (byte)(w.Path[i] >> 8); }
        return bytes;
    }

    public static P.FullState FullState(World w, int speed)
    {
        var f = new P.FullState
        {
            Width = w.Width, Height = w.HeightCells, WaterLevel = w.WaterLevel,
            Heights = ByteString.CopyFrom(w.Height), Corners = ByteString.CopyFrom(CornerBytes(w)), Rivers = ByteString.CopyFrom(w.River), Zones = ByteString.CopyFrom(w.ZoneCode), Paths = ByteString.CopyFrom(PathBytes(w)),
            PowerLines = ByteString.CopyFrom(w.PowerLine), WaterPipes = ByteString.CopyFrom(w.WaterPipe),
            Stats = Stats(w, speed), Catalog = Catalog(w.Content), Seed = w.Seed,
        };
        foreach (var b in w.Buildings) if (b != null) f.Buildings.Add(Building(w, b));
        f.Strips.AddRange(Strips(w));
        return f;
    }

    /// <summary>The city's arcs and diagonals, as clients draw them.</summary>
    public static IEnumerable<P.RoadStrip> Strips(World w) => w.Strips.Select(s => new P.RoadStrip
    {
        Path = s.Code, Curved = s.Curved, X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1, Cx = s.CX, Cy = s.CY, Cells = { s.Cells },
    });

    public static P.Notice Notice(SimNotice n) => new() { Text = n.Text, Severity = n.Severity, X = n.X, Y = n.Y };

    public static P.WhyResult Why(World w, WhyReport r)
    {
        var p = new P.WhyResult { X = r.X, Y = r.Y, Subject = r.Subject, Summary = r.Summary };
        if (w.InBounds(r.X, r.Y) && w.BuildingOrStopAt(w.Idx(r.X, r.Y)) is { } b)
            foreach (var g in w.Gauges(b)) p.Gauges.Add(new P.Gauge { Label = g.Label, Value = g.Value, Max = g.Max, Unit = g.Unit, Fun = g.Fun });
        foreach (var q in r.Requirements)
            p.Requirements.Add(new P.Requirement { Key = q.Key, Label = q.Label, Met = q.Met, Detail = q.Detail, Topic = TopicOf(q.Key) });
        foreach (var f in r.Factors) p.Factors.Add(new P.Factor { Label = f.Label, Value = f.Value });
        return p;
    }

    /// <summary>The encyclopedia article that explains a why-panel requirement.</summary>
    static string TopicOf(string key) => key switch
    {
        "road" => "lots", "power" or "grid" => "power", "water" or "pipe" => "water", "desirability" => "desirability",
        "demand" => "demand", "jobs" or "workers" => "traffic", "condition" => "wear", _ => "",
    };

    static P.DemandLine Line(DemandLine d)
    {
        var p = new P.DemandLine { Key = d.Key, Label = d.Label, Detail = d.Detail };
        p.Points.AddRange(d.Points);
        return p;
    }

    public static P.DemandReport Demand(List<ZoneDemandReport> zones)
    {
        var r = new P.DemandReport();
        foreach (var z in zones)
        {
            var p = new P.ZoneDemand { Zone = (P.ZoneKind)(int)z.Zone, BindingKey = z.BindingKey, Binding = z.Binding, X = z.X, Y = z.Y, Topic = z.Topic };
            p.Raw.AddRange(z.Raw);
            p.Final.AddRange(z.Final);
            foreach (var d in z.Sources) p.Sources.Add(Line(d));
            foreach (var d in z.Adjustments) p.Adjustments.Add(Line(d));
            foreach (var d in z.Limits) p.Limits.Add(Line(d));
            r.Zones.Add(p);
        }
        return r;
    }


    public static P.AlertList Alerts(World w, List<Alert> alerts)
    {
        var r = new P.AlertList();
        foreach (var a in alerts)
        {
            var (fix, area) = w.AlertHelp(a);
            r.Alerts.Add(new P.Alert { Key = a.Key, Severity = a.Severity, Text = a.Text, X = a.X, Y = a.Y, Count = a.Count, Topic = a.Topic, FixType = fix, Area = Rect(area) });
        }
        return r;
    }

    public static P.ForecastResult Forecast(ForecastReport f, bool withPlan)
    {
        var r = new P.ForecastResult { Months = f.Months, WithPlan = withPlan };
        foreach (var p in f.Points)
        {
            var q = new P.ForecastPoint
            {
                Year = p.Year, Month = p.Month, Population = p.Population, Jobs = p.Jobs, Workers = p.Workers, Funds = p.Funds,
                PowerPercent = p.PowerPercent, WaterPercent = p.WaterPercent, Abandoned = p.Abandoned,
            };
            q.Demand.AddRange(p.Demand);
            r.Points.Add(q);
        }
        foreach (var e in f.Events) r.Events.Add(new P.ForecastEvent { Year = e.Year, Month = e.Month, Severity = e.Severity, Text = e.Text, X = e.X, Y = e.Y });
        r.Summary.AddRange(f.Summary);
        return r;
    }

    public static P.Encyclopedia Encyclopedia(List<Article> articles)
    {
        var r = new P.Encyclopedia();
        foreach (var a in articles)
        {
            var p = new P.Article { Id = a.Id, Title = a.Title, Category = a.Category };
            p.Paragraphs.AddRange(a.Paragraphs);
            foreach (var (k, v) in a.Facts) p.Facts.Add(new P.KeyValue { Key = k, Value = v });
            p.SeeAlso.AddRange(a.SeeAlso);
            r.Articles.Add(p);
        }
        return r;
    }

    public static P.OrdinanceList Ordinances(List<OrdinanceView> views)
    {
        var r = new P.OrdinanceList();
        foreach (var v in views)
        {
            var o = new P.Ordinance
            {
                Id = v.Def.Id, Name = v.Def.Name, Category = v.Def.Category, Description = v.Def.Description,
                Availability = v.Def.AvailabilityText, Status = (P.OrdinanceStatus)(int)v.Status, MonthsLeft = v.MonthsLeft,
                MonthlyNet = v.MonthlyNet,
            };
            o.Effects.AddRange(v.Effects);
            foreach (var (k, val) in v.Trace) o.Steps.Add(new P.KeyValue { Key = k, Value = val });
            r.Ordinances.Add(o);
        }
        return r;
    }

    public static P.CivicState Civic(World w)
    {
        var r = new P.CivicState { Approval = w.Approval };
        foreach (var p in w.Prompts)
        {
            var ev = w.Content.Events[p.Event];
            var (fix, area) = w.PromptHelp(p);
            r.Prompts.Add(new P.Prompt
            {
                Id = p.Id, EventId = ev.Id, Kind = p.Kind == EventKind.Deal ? "deal" : "petition", X = p.X, Y = p.Y,
                Petitioner = p.Petitioner, Text = p.Text, Goal = ev.GoalText, Status = (P.PromptStatus)(int)p.Status,
                MonthsLeft = p.Status == PromptStatus.Open ? (int)Math.Max(0, (p.Deadline - w.Tick + World.DaysPerMonth - 1) / World.DaysPerMonth) : 0,
                ResultText = p.ResultText, FixType = fix, Area = Rect(area), Progress = w.PromptProgress(p),
            });
        }
        for (int t = 0; t < w.TypeUnlocked.Length; t++)
            if (w.TypeUnlocked[t] && w.Content.Types[t].Placeable) r.UnlockedTypes.Add(w.Content.Types[t].Id);
        foreach (var h in w.Headlines)
            r.Headlines.Add(new P.Headline { Seq = h.Seq, Tick = h.Tick, Text = h.Text, Severity = h.Severity, X = h.X, Y = h.Y });
        foreach (var s in w.Seats)
        {
            var seat = new P.SeatState
            {
                Id = s.Id, Name = s.Id == Seat.Mayor ? "City" : w.SideGame(s.Id)?.Game.Manifest.Name ?? s.Id, Owner = s.Owner,
                Level = (P.SeatLevel)(int)s.Level, Style = (P.AutopilotStyle)(int)s.Style, SpendCap = s.SpendCap,
                AwaySinceTick = s.Away?.Tick ?? -1,
                AwayLevel = (P.SeatLevel)(int)s.AwayLevel, AwayStyle = (P.AutopilotStyle)(int)s.AwayStyle, AutoAway = s.AutoAway,
            };
            if (s.Report is { } rep)
            {
                var pr = new P.AwayReport { FromTick = rep.FromTick, ToTick = rep.ToTick, Level = (P.SeatLevel)(int)rep.Level };
                pr.Changes.AddRange(rep.Changes);
                pr.News.AddRange(rep.News);
                foreach (var n in rep.Notes) pr.Notes.Add(new P.AwayNote { Tick = n.Tick, Text = n.Text, X = n.X, Y = n.Y });
                seat.Report = pr;
            }
            r.Seats.Add(seat);
        }
        foreach (var hg in w.SideGames)
        {
            var info = new P.SideGameInfo { Id = hg.Id, Name = hg.Game.Manifest.Name, Description = hg.Game.Manifest.Description };
            info.LotKinds.AddRange(hg.Game.Manifest.LotKinds);
            foreach (var s in w.Standings.Where(s => s.GameId == hg.Id))
                info.Standings.Add(new P.StandingState
                {
                    District = s.District, DistrictName = w.DistrictName(s.District), Rating = s.Rating, Target = s.Target, Statue = s.Statue,
                    ExclusiveDays = (int)Math.Max(0, s.ExclusiveUntil - w.Tick), BribeBanDays = (int)Math.Max(0, s.BribeBanUntil - w.Tick),
                });
            r.SideGames.Add(info);
        }
        var table = new P.ConsentTable();
        table.Owners.AddRange(w.Owners());
        foreach (var row in w.ConsentTable())
        {
            var pr = new P.ConsentRow
            {
                Mechanic = (P.Mechanic)(int)row.Mechanic, Name = row.Name, Note = row.Note, Effective = row.Effective,
                Pairwise = row.Scope == ConsentScope.Pair, HostForbids = row.HostForbids, CityForbids = row.CityForbids,
            };
            pr.ByOwner.AddRange(row.ByOwner);
            pr.Blockers.AddRange(row.Blockers);
            table.Rows.Add(pr);
        }
        foreach (var p in w.Pacts) table.Pacts.Add(new P.PactState { A = p.A, B = p.B, Mechanic = (P.Mechanic)(int)p.Mechanic });
        foreach (var o in w.PactOffers) table.Pacts.Add(new P.PactState { A = o.From, B = o.To, Mechanic = (P.Mechanic)(int)o.Mechanic, Offer = true });
        r.Consent = table;
        foreach (var (player, p) in w.Deputies) r.Deputies.Add(new P.Deputy { Player = player, Portfolios = (uint)p });
        r.CouncilOn = w.CouncilOn;
        r.Council.AddRange(w.CouncilMembers());
        foreach (var d in Enumerable.Reverse(w.Delinks))
        {
            var ds = new P.DelinkState
            {
                Day = (int)d.Tick, Player = d.Player, Mayor = d.Mayor, By = d.By, LeasesEnded = d.LeasesEnded, WrittenOff = d.WrittenOff, Paid = d.Paid,
                CooldownDays = (int)Math.Max(0, d.CooldownUntil - w.Tick),
            };
            ds.Games.AddRange(d.Games);
            r.Delinks.Add(ds);
        }
        for (int d = 0; d < w.DistrictCount; d++)
        {
            var f = new P.DistrictFranchise { District = d, Name = w.DistrictName(d), Status = "none" };
            if (w.FranchiseIn(d) is { } held)
            {
                f.Status = "held"; f.Holder = held.Winner; f.HolderName = w.GameName(held.Winner); f.Amount = held.Amount; f.Exclusive = held.Exclusive;
                f.Months = held.Months; f.DaysLeft = (int)(held.Until - w.Tick);
            }
            else if (w.OpenTenderIn(d) is { } open)
            {
                f.Status = "open"; f.Months = open.Months; f.DaysLeft = (int)Math.Max(0, open.Closes - w.Tick); f.Bids = open.Bids.Count;
            }
            if (f.Status != "held" && w.Tenders.Where(t => t.District == d && t.Status == TenderStatus.NoBids).OrderByDescending(t => t.Id).FirstOrDefault() is { } none)
                f.Last = $"Nobody bid when it was last out to tender.";
            r.Franchises.Add(f);
        }
        foreach (var (id, name, about) in SideGameCatalog.Invitable())
            if (w.SideGame(id) == null) r.Invitable.Add(new P.SideGameOffer { Id = id, Name = name, Description = about });
        r.FreightExports = w.FreightExportDaily;
        r.FreightDelivered = w.FreightDeliveredDaily;
        foreach (var (player, why) in w.DelinkOptions()) r.DelinkOptions.Add(new P.DelinkOption { Player = player, Open = why == null, Rule = why ?? w.DelinkRule(player) });
        foreach (var p in w.Proposals.OrderBy(p => p.Status != ProposalStatus.Open).ThenByDescending(p => p.Id).Take(12))
        {
            var pp = new P.Proposal
            {
                Id = p.Id, Text = w.Describe(p.Action), Proposer = p.Proposer, DaysLeft = (int)Math.Max(0, p.Deadline - w.Tick),
                Status = p.Status switch { ProposalStatus.Open => "open", ProposalStatus.Passed => "passed", _ => "failed" },
            };
            pp.Yes.AddRange(p.Votes.Where(v => v.Value).Select(v => v.Key));
            pp.No.AddRange(p.Votes.Where(v => !v.Value).Select(v => v.Key));
            r.Proposals.Add(pp);
        }
        foreach (var l in w.Leases)
        {
            var b = w.Buildings[l.BuildingId];
            var ls = new P.LeaseState
            {
                Id = l.Id, Game = l.GameId, GameName = w.SideGame(l.GameId)?.Game.Manifest.Name ?? l.GameId, BuildingId = l.BuildingId, Kind = l.Kind,
                X = b?.X ?? -1, Y = b?.Y ?? -1, Terms = Terms(l.Terms), PendingTerms = l.PendingTerms == null ? null : Terms(l.PendingTerms),
                PendingMonths = l.PendingMonths, Arrears = l.Arrears, BadStanding = l.BadStanding, CureMonthsLeft = l.CureMonthsLeft,
                Terminable = l.Terminable, Jobs = l.Jobs, LastMonthFares = l.LastRevenue, EvictMonths = l.EvictMonths,
                Rating = b == null ? 0 : w.RatingOf(l.GameId, w.DistrictOf(b.X, b.Y)),
            };
            if (b != null) ls.Targets.AddRange(w.LeaseTargets(l));
            foreach (var t in w.MoveTargets(l)) ls.MoveTo.Add(new P.MoveTarget { BuildingId = t.Id, X = t.X, Y = t.Y, Name = w.LotName(t) });
            if (b != null) foreach (var step in w.DisputeLadder(l)) ls.Ladder.Add(new P.LadderStep { Step = step.Step, Open = step.Open, Rule = step.Rule });
            foreach (var o in w.OffersOn(l.Id).OrderBy(o => o.Status != OfferStatus.Open).ThenByDescending(o => o.Id))
            {
                var to = o.ToBuildingId >= 0 && o.ToBuildingId < w.Buildings.Count ? w.Buildings[o.ToBuildingId] : null;
                ls.Offers.Add(new P.OfferState
                {
                    Id = o.Id, Kind = (P.OfferKind)(int)o.Kind, FromOperator = o.FromOperator, By = o.By, Terms = o.Terms == null ? null : Terms(o.Terms),
                    Amount = o.Amount, ToBuildingId = o.ToBuildingId, ToX = to?.X ?? -1, ToY = to?.Y ?? -1, CounterOf = o.CounterOf,
                    Status = o.Status.ToString().ToLowerInvariant(), DaysLeft = (int)Math.Max(0, o.Expires - w.Tick),
                    Text = b == null ? "" : w.DescribeOffer(o, l), Note = o.Note,
                });
            }
            r.Leases.Add(ls);
        }
        return r;
    }

    public static P.AdvisorList Advisors(World w)
    {
        var r = new P.AdvisorList { Approval = w.Approval };
        foreach (var a in w.Advisors())
        {
            var p = new P.Advisor { Role = a.Role, Name = a.Name, Title = a.Title, Mood = a.Mood };
            p.Messages.AddRange(Alerts(w, a.Messages).Alerts);
            r.Advisors.Add(p);
        }
        var factors = new List<Factor>();
        w.ApprovalOf(factors);
        foreach (var f in factors) r.ApprovalFactors.Add(new P.Factor { Label = f.Label, Value = f.Value });
        return r;
    }

    public static P.InspectResult Inspect(World w, int x, int y)
    {
        var p = new P.InspectResult { X = x, Y = y };
        foreach (var (k, v) in w.Inspect(x, y)) p.Fields.Add(new P.KeyValue { Key = k, Value = v });
        if (w.InBounds(x, y) && w.BuildingOrStopAt(w.Idx(x, y)) is { } b)
        {
            p.Building = Building(w, b);
            foreach (var u in w.UpgradeOptions(b))
                p.Upgrades.Add(new P.UpgradeChoice { ToTypeId = u.To.Id, Name = u.To.Name, Cost = u.Def.Cost, Available = u.Available, WhyNot = u.WhyNot, Text = u.Def.Text });
        }
        return p;
    }

    public static P.OverlayData Overlay(OverlayResult o) => new()
    {
        Kind = (P.OverlayKind)(int)o.Kind, Values = ByteString.CopyFrom(o.Values), LegendLow = o.LegendLow, LegendHigh = o.LegendHigh,
    };

    // ------------------------------------------------------------------
    // Saves
    // ------------------------------------------------------------------

    public static byte[] Save(World w)
    {
        var s = new P.SaveGame
        {
            FormatVersion = World.FormatVersion, CityName = w.Name, Seed = w.Seed, Width = w.Width, Height = w.HeightCells,
            Tick = w.Tick, World = ByteString.CopyFrom(w.Snapshot()),
        };
        foreach (var l in w.Log) s.Log.Add(Record(l));
        return s.ToByteArray();
    }

    /// <summary>A logged action as a save or the federated link carries it.</summary>
    public static P.ActionRecord Record(LoggedAction l) => l.Action switch
            {
                SeatAwayAction sa => new P.ActionRecord { Tick = l.Tick, By = sa.By, Away = new P.SeatAway { Seat = sa.SeatId, Away = sa.Away } },
                SideGameMissedAction ms => new P.ActionRecord { Tick = l.Tick, By = ms.By, Missed = new P.SideGameMissed { Game = ms.GameId, What = ms.What } },
                RenameOwnerAction rn => new P.ActionRecord { Tick = l.Tick, By = rn.By, Rename = new P.RenameOwner { From = rn.From, To = rn.To } },
                ForkAction fk => new P.ActionRecord { Tick = l.Tick, By = fk.By, Fork = new P.Fork { Player = fk.Player, HomeName = fk.HomeName } },
                NeighbourEdgeAction ne => new P.ActionRecord { Tick = l.Tick, By = ne.By, Neighbour = Edge(ne) },
                WeatherAction wa => new P.ActionRecord { Tick = l.Tick, By = wa.By, Weather = new P.RegionWeather { Kind = (P.WeatherKind)(int)wa.Kind, Strength = wa.Strength } },
                FloodAction fa => new P.ActionRecord { Tick = l.Tick, By = fa.By, Flood = new P.RegionFlood { Stage = fa.Stage } },
                CityDealAction cd => new P.ActionRecord
                {
                    Tick = l.Tick, By = cd.By,
                    Deal = new P.CityDeal { Id = cd.Id, Kind = (P.DealKind)(int)cd.Kind, Selling = cd.Selling, Side = cd.Side, Other = cd.Other, Amount = cd.Amount, Price = cd.Price, End = cd.End, Why = cd.Why },
                },
                SetHostRulesAction hr => new P.ActionRecord { Tick = l.Tick, By = hr.By, HostRules = new P.HostRules { Forbidden = { hr.Forbidden.Select(m => (P.Mechanic)(int)m) } } },
                _ => new P.ActionRecord { Tick = l.Tick, By = l.Action.By, Action = ToProto(l.Action) },
            };

    public static P.NeighbourEdge Edge(NeighbourEdgeAction a) => new()
    {
        Side = a.Side, Name = a.Name, Mayor = a.Mayor, Paths = { a.Paths }, Power = { a.Power }, Pipes = { a.Pipes }, Stats = { a.Stats ?? Array.Empty<int>() },
        Fire = { a.Fire ?? Array.Empty<int>() }, Movers = { a.Movers ?? Array.Empty<int>() },
    };

    public static NeighbourEdgeAction Edge(P.NeighbourEdge e) =>
        new(e.Side, e.Name, e.Mayor, e.Paths.ToArray(), e.Power.ToArray(), e.Pipes.ToArray(), e.Stats.ToArray(), e.Fire.ToArray(), e.Movers.ToArray());

    /// <summary>The action a record holds, with who sent it, or null for one this build doesn't know.</summary>
    public static SimAction? Action(P.ActionRecord r)
    {
        SimAction? a = r.Away != null ? new SeatAwayAction(r.Away.Seat, r.Away.Away)
                : r.Missed != null ? new SideGameMissedAction(r.Missed.Game, r.Missed.What)
                : r.Rename != null ? new RenameOwnerAction(r.Rename.From, r.Rename.To)
                : r.Fork != null ? new ForkAction(r.Fork.Player, r.Fork.HomeName)
                : r.Neighbour != null ? Edge(r.Neighbour)
                : r.Weather is { } wr ? new WeatherAction((WeatherKind)(int)wr.Kind, wr.Strength)
                : r.Flood is { } fr ? new FloodAction(fr.Stage)
                : r.Deal is { } d ? new CityDealAction(d.Id, (DealKind)(int)d.Kind, d.Selling, d.Side, d.Other, d.Amount, d.Price, d.End, d.Why)
                : r.HostRules != null ? new SetHostRulesAction(r.HostRules.Forbidden.Select(m => (Mechanic)(int)m).ToList())
                : r.Action == null ? null : ToSim(r.Action);
        return a == null ? null : a with { By = r.By };
    }

    public static World Load(ContentDb content, byte[] data)
    {
        var s = P.SaveGame.Parser.ParseFrom(data);
        if (s.FormatVersion is < 5 or > World.FormatVersion) throw new InvalidDataException($"save format {s.FormatVersion} is not supported");
        var w = World.FromSnapshot(content, s.World.ToByteArray());
        foreach (var r in s.Log)
            if (Action(r) is { } a) w.Log.Add(new LoggedAction(r.Tick, a));
        return w;
    }
}

/// <summary>Remembers what a client has seen and builds the next <see cref="P.TickDelta"/>.</summary>
public sealed class DeltaTracker
{
    byte[] _heights = Array.Empty<byte>(), _zones = Array.Empty<byte>(), _lines = Array.Empty<byte>(), _pipes = Array.Empty<byte>();
    ushort[] _paths = Array.Empty<ushort>(), _corners = Array.Empty<ushort>();
    int _strips;
    readonly Dictionary<int, P.BuildingState> _buildings = new();

    /// <summary>Records the state just sent in a FullState.</summary>
    public void Reset(World w)
    {
        _heights = (byte[])w.Height.Clone();
        _zones = (byte[])w.ZoneCode.Clone();
        _lines = (byte[])w.PowerLine.Clone();
        _pipes = (byte[])w.WaterPipe.Clone();
        _paths = (ushort[])w.Path.Clone();
        _corners = (ushort[])w.Corner.Clone();
        _strips = w.StripsVersion;
        _buildings.Clear();
        foreach (var b in w.Buildings) if (b != null) _buildings[b.Id] = Mapping.Building(w, b);
    }

    public P.TickDelta Next(World w, int speed, IEnumerable<SimNotice> notices)
    {
        var d = new P.TickDelta { Stats = Mapping.Stats(w, speed) };
        if (_heights.Length != w.N) Reset(w);
        if (_strips != w.StripsVersion)
        {
            _strips = w.StripsVersion;
            d.StripsChanged = true;
            d.Strips.AddRange(Mapping.Strips(w));
        }
        for (int i = 0; i < w.N; i++)
        {
            if (_heights[i] == w.Height[i] && _zones[i] == w.ZoneCode[i] && _paths[i] == w.Path[i] && _lines[i] == w.PowerLine[i] && _pipes[i] == w.WaterPipe[i]) continue;
            _heights[i] = w.Height[i]; _zones[i] = w.ZoneCode[i]; _paths[i] = w.Path[i]; _lines[i] = w.PowerLine[i]; _pipes[i] = w.WaterPipe[i];
            d.Cells.Add(new P.CellUpdate { Index = i, Height = w.Height[i], Zone = w.ZoneCode[i], Path = w.Path[i], PowerLine = w.PowerLine[i] != 0, WaterPipe = w.WaterPipe[i] != 0 });
        }
        for (int i = 0; i < w.Corner.Length; i++)
        {
            if (_corners[i] == w.Corner[i]) continue;
            _corners[i] = w.Corner[i];
            d.Corners.Add(new P.CornerUpdate { Index = i, Dm = w.Corner[i] });
        }
        var alive = new HashSet<int>();
        foreach (var b in w.Buildings)
        {
            if (b == null) continue;
            alive.Add(b.Id);
            var s = Mapping.Building(w, b);
            if (_buildings.TryGetValue(b.Id, out var old) && old.Equals(s)) continue;
            _buildings[b.Id] = s;
            d.Buildings.Add(s);
        }
        foreach (var id in _buildings.Keys.Where(k => !alive.Contains(k)).OrderBy(k => k).ToList())
        {
            _buildings.Remove(id);
            d.RemovedBuildings.Add(id);
        }
        foreach (var n in notices) d.Notices.Add(Mapping.Notice(n));
        return d;
    }
}
