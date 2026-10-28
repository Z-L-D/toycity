namespace ToyCity.Sim;

/// <summary>An encyclopedia article. Written from the content files and the live city, so it never goes stale.</summary>
public sealed record Article(string Id, string Title, string Category, List<string> Paragraphs, List<(string Key, string Value)> Facts, List<string> SeeAlso);

public sealed partial class World
{
    /// <summary>
    /// The in-game encyclopedia. Rules are described from the same constants the sim uses, and every article
    /// ends with what that thing is doing in this city right now.
    /// </summary>
    public List<Article> Encyclopedia()
    {
        var list = new List<Article>();
        void Add(string id, string title, string cat, List<string> paras, List<(string, string)> facts, params string[] see) =>
            list.Add(new Article(id, title, cat, paras, facts, see.ToList()));

        string Money(long v) => v < 0 ? $"-${-v:N0}" : $"${v:N0}";
        string Pct(long a, long b) => b > 0 ? $"{a * 100 / b}%" : "n/a";

        // ---- How the city works ----
        Add("time", "Time", "Rules", new()
        {
            $"One tick is one day. A month is {DaysPerMonth} days and a year {MonthsPerYear} months. Most slow systems (pollution, land value, traffic, the budget) update on the first day of each month.",
            "The simulation is deterministic: the same city and the same actions always give the same result. That is why the forecast can show exactly what happens if you do nothing.",
        }, new() { ("Today", $"{Day}/{Month}/{Year}"), ("Days since founding", Tick.ToString("N0")) }, "forecast", "budget");

        Add("demand", "Demand", "Rules", new()
        {
            "Demand runs from -100 to 100 for each zone and wealth level. Above zero, empty lots of that kind grow buildings; far below zero, existing ones struggle.",
            "Each zone's demand comes from three sources. Local demand is the town itself: jobs pull in residents, residents want shops, workers attract industry. Regional demand comes from the surrounding region and fades as the city grows, slower when roads lead out to the map edge. External demand comes from further away: tourists drawn by parks and landmarks, and exports.",
            "Taxes and education then adjust each wealth level, and capacity limits can hold demand down: below 25% spare power, demand is capped so the grid isn't swamped. The demand panel names the one thing holding each zone back.",
        }, new()
        {
            ("Residential (low / mid / high)", $"{Demand[0]} / {Demand[1]} / {Demand[2]}"),
            ("Commercial", $"{Demand[3]} / {Demand[4]} / {Demand[5]}"),
            ("Industrial", $"{Demand[6]} / {Demand[7]} / {Demand[8]}"),
        }, "neighbors", "taxes", "zones", "power");

        Add("neighbors", "Neighbour connections", "Rules", new()
        {
            "A path that runs off the edge of the map and is joined to your city is a neighbour connection. The region sends people, shoppers and freight over it.",
            "Without one, people still trickle in by back roads, but regional pull halves at 2,000 people. Each connection's capacity (vehicles an hour) pushes that out, and adds regional shoppers and export jobs.",
        }, new()
        {
            ("Connection capacity", EdgeCapacity > 0 ? $"{EdgeCapacity:N0} vehicles/hour" : "none"),
            ("Regional pull halves at", $"{RegionalScale:N0} people"),
        }, "demand", "road", "highway-2");

        string lotRows = string.Join("; ", new[] { Level.Low, Level.Medium, Level.High }
            .Select(d => $"{d.ToString().ToLowerInvariant()}: {string.Join(", ", Content.LotSizes[(int)d].Select(s => $"{s.W}×{s.D}"))}"));
        Add("lots", "Lots and road access", "Rules", new()
        {
            "Zoned buildings grow on lots of several sizes, as in SimCity 4. A lot fits when every cell is empty land in the same zone and density, and one side touches a path with frontage. Highways have no frontage.",
            $"Lot sizes (frontage × depth, either way round): {lotRows}. A bigger lot holds more people or jobs and takes a little longer to build.",
            "Zoned cells that no allowed lot can reach are greyed and crossed out on the map.",
        }, new(), "zones", "growth");

        Add("zones", "Zones", "Rules", new()
        {
            $"Zoning costs ${Content.ZoneCostPerCell} a cell. Residential holds homes, commercial holds shops and offices, industrial holds factories. Density sets how big buildings may get: low needs only power, medium and high also need water and more desirability.",
            "Land value decides wealth: residential and commercial need land value 60 for $$ and 110 for $$$. Industry chooses its wealth from demand alone.",
        }, ZoneCellCounts(), "residential", "commercial", "industrial", "lots", "growth");

        Add("growth", "Growth", "Rules", new()
        {
            "An empty zoned lot grows when it has road access, power, water (medium and high density), enough desirability and positive demand. These are exactly the checks the Why panel lists.",
            $"Desirability thresholds: low {DesirabilityThreshold(Level.Low)}, medium {DesirabilityThreshold(Level.Medium)}, high {DesirabilityThreshold(Level.High)}. Each lot is checked once a month, and grows with a chance of 5% plus a quarter of demand, at most 30%.",
            "Existing buildings redevelop when the zone is upgraded, or when land value and demand support a richer building.",
        }, new() { ("Buildings under construction", Buildings.Count(b => b is { ConstructionLeft: > 0 }).ToString("N0")) }, "demand", "desirability", "lots", "decline");

        Add("decline", "Decline and abandonment", "Rules", new()
        {
            $"Every month each zoned building is checked against the growth needs, with {30} points of slack on desirability and demand allowed down to -60. Homes also need a job within a 60 minute commute when jobs are free, and workplaces need workers when people are looking.",
            $"After {MonthsToAbandon} failing months in a row a building is abandoned. {MonthsToRecover} good months and it is fixed up; after {MonthsToCrumble} months abandoned it crumbles and the lot can grow again.",
        }, new()
        {
            ("Struggling buildings", Buildings.Count(b => b is { Abandoned: false, UnmetMonths: > 0 }).ToString("N0")),
            ("Abandoned buildings", Buildings.Count(b => b is { Abandoned: true }).ToString("N0")),
        }, "growth", "traffic");

        Add("power", "Power", "Rules", new()
        {
            "Power flows through power lines, buildings, zoned land and paths with frontage, but not across highways or open land. Each connected grid adds up its plants' output.",
            "When a grid needs more than it makes, it browns out: buildings are served in the order they were built until power runs out. Unpowered civic buildings do nothing. Spare capacity also matters for growth: under 25% spare, demand is held down.",
        }, new()
        {
            ("Capacity", $"{PowerCapacity:N0} kW"), ("Demand", $"{PowerDemand:N0} kW"), ("Used", Pct(PowerDemand, PowerCapacity)),
        }, "coal-plant", "gas-plant", "wind-turbine", "solar-farm", "demand");

        Add("water", "Water", "Rules", new()
        {
            $"Pumps need power and a pipe touching them. Every cell within {PipeReach} cells of a pipe in that network is served. Medium and high density need water; low density does not.",
            "Water pollution at a pump cuts its output, so keep pumps away from industry.",
        }, new()
        {
            ("Capacity", $"{WaterCapacity:N0} a day"), ("Demand", $"{WaterDemand:N0} a day"), ("Used", Pct(WaterDemand, WaterCapacity)),
        }, "water-pump", "pollution");

        Add("garbage", "Garbage", "Rules", new()
        {
            "Every building makes garbage. Landfills with a road beside them take it away. Uncollected garbage lowers desirability for homes and shops by one point per 100 units, up to 30.",
        }, new()
        {
            ("Produced", $"{GarbageProduced:N0} a day"), ("Capacity", $"{GarbageCapacity:N0} a day"), ("Desirability penalty", GarbagePenalty.ToString()),
        }, "landfill", "desirability");

        int busiest = 0;
        for (int i = 0; i < N; i++) busiest = Math.Max(busiest, Trips[i]);
        Add("traffic", "Commutes and traffic", "Rules", new()
        {
            "Half of each home's residents go to work. Once a month each home searches the paths from its driveway for the fastest route to free jobs, up to 60 minutes away.",
            "Each path's speed sets its travel time; busy paths slow down along the BPR curve (time × (1 + 0.15 (volume/capacity)^4), at most 5×). Traffic adds noise and pollution.",
        }, new()
        {
            ("Workers with a job", $"{Workers:N0} of {LaborForce:N0}"), ("Busiest path cell", $"{busiest:N0} trips a day"),
        }, "road", "noise", "pollution");

        Add("land-value", "Land value", "Rules", new()
        {
            "Land value starts at 40 and rises with elevation, nearby water, parks and services, and falls with pollution and noise. It decides how wealthy new buildings can be.",
        }, new() { ("Average on zoned land", AverageOnZoned(LandValue).ToString()) }, "desirability", "zones");

        Add("desirability", "Desirability", "Rules", new()
        {
            "Each zone weighs land differently. Homes like land value, parks and services and dislike pollution, noise, traffic, garbage and long commutes. Shops like land value, passing traffic and customers. Industry likes cheap land and fire cover.",
            "Query any lot with the Why tool to see every factor and its points.",
        }, new(), "growth", "land-value");

        Add("pollution", "Pollution", "Rules", new()
        {
            "Industry, power plants, landfills and busy roads pollute the ground nearby, falling off with distance. Some also pollute the water, which cuts pump output.",
        }, new() { ("Worst ground pollution", Pollution.Max().ToString()) }, "land-value", "water");

        Add("noise", "Noise", "Rules", new()
        {
            "Roads and some buildings are loud. Only the loudest nearby road counts, so a second road beside the first adds little. Homes dislike noise.",
        }, new() { ("Loudest cell", Noise.Max().ToString()) }, "traffic", "desirability");

        Add("services", "Services", "Rules", new()
        {
            "Police, fire, health, education and parks cover a radius around them that grows with the square root of funding. Coverage raises land value and desirability; education also shifts demand toward richer homes and high-tech industry.",
        }, new() { ("Education at homes", $"{EducationIndex}%") }, "police-station", "fire-station", "clinic", "elementary-school", "small-park");

        Add("taxes", "Taxes", "Rules", new()
        {
            "Each zone and wealth level has its own rate. Homes pay per resident and businesses per filled job, every month. Each point above 9% lowers that group's demand by 6; each point below raises it by 6.",
        }, new()
        {
            ("Residential", string.Join(" / ", TaxTenths.Take(3).Select(t => $"{t / 10.0:0.0}%"))),
            ("Commercial", string.Join(" / ", TaxTenths.Skip(3).Take(3).Select(t => $"{t / 10.0:0.0}%"))),
            ("Industrial", string.Join(" / ", TaxTenths.Skip(6).Select(t => $"{t / 10.0:0.0}%"))),
        }, "budget", "demand");

        Add("budget", "Budget and loans", "Rules", new()
        {
            "The budget closes on the first of each month: taxes in, upkeep out. Paths cost upkeep per cell by type, power lines and pipes 10 cents a cell, and civic buildings their monthly cost times their funding.",
            $"Loans up to ${MaxDebt:N0} in total are repaid over about ten years at 6% a year. The city can go into the red and the game goes on.",
        }, new()
        {
            ("Treasury", Money(Funds)), ("Last month in", Money(LastIncome)), ("Last month out", Money(-LastExpenses)), ("Debt", Money(Debt)),
        }, "taxes");

        Add("forecast", "Forecast", "Rules", new()
        {
            "The forecast runs a copy of your city ahead and reports what happens: population, money, power and water, and any problems that start. Because the simulation is deterministic, it is exactly what will happen if you change nothing.",
        }, new(), "time");

        // ---- Zones ----
        foreach (var zone in new[] { Zone.Residential, Zone.Commercial, Zone.Industrial })
        {
            var facts = new List<(string, string)>();
            foreach (var d in new[] { Level.Low, Level.Medium, Level.High })
            foreach (var w in new[] { Level.Low, Level.Medium, Level.High })
            {
                var t = Content.Zoned(zone, d, w);
                int n = Buildings.Count(b => b != null && b.Type == t.Index && !b.Abandoned);
                string per = zone == Zone.Residential ? $"{t.Residents} residents" : $"{t.Jobs} jobs";
                facts.Add(($"{t.Name} ({d.ToString().ToLowerInvariant()}, {WealthTag(zone, w)})", $"{per} per cell; {n} in town"));
            }
            string blurb = zone switch
            {
                Zone.Residential => "Homes. Residents work, shop and pay tax per head. They want jobs within reach, clean quiet streets, parks and services.",
                Zone.Commercial => "Shops and offices. They hire workers and pay tax per filled job. They want customers: residents, passing traffic and tourists.",
                _ => "Factories, from dirty industry to high-tech. They hire workers and pay tax per filled job, and they pollute. High-tech needs an educated town.",
            };
            Add(ZoneName(zone), char.ToUpperInvariant(ZoneName(zone)[0]) + ZoneName(zone)[1..] + " zones", "Zones",
                new() { blurb, "Each template's figures are per cell of lot; a 2×2 lot holds four times as much." }, facts, "zones", "demand", "lots");
        }

        // ---- Civic buildings ----
        foreach (var t in Content.Types.Where(t => t.Placeable))
        {
            var built = Buildings.Where(b => b != null && b.Type == t.Index).ToList();
            int working = built.Count(b => b!.Operating && (b.Powered || t.PowerKw == 0));
            var facts = new List<(string, string)>
            {
                ("Size", $"{t.W} × {t.H}"), ("Cost", Money(t.Cost)), ("Monthly upkeep", Money(t.Maintenance) + " at 100% funding"),
            };
            var paras = new List<string>();
            if (t.PowerOutKw > 0) { facts.Add(("Output", $"{t.PowerOutKw:N0} kW")); paras.Add($"Makes {t.PowerOutKw:N0} kW for the grid it is joined to."); }
            if (t.WaterOut > 0) { facts.Add(("Output", $"{t.WaterOut:N0} water a day")); paras.Add("Pumps water into the pipe network it touches. Needs power."); }
            if (t.GarbageCap > 0) { facts.Add(("Takes", $"{t.GarbageCap:N0} garbage a day")); paras.Add("Takes the city's garbage. Needs a road beside it for the trucks."); }
            if (t.Radius > 0) { facts.Add(("Radius", $"{t.Radius} cells at 100% funding")); paras.Add($"Covers {t.Radius} cells around it; more funding reaches further."); }
            if (t.Jobs > 0) facts.Add(("Jobs", t.Jobs.ToString()));
            if (t.PowerKw > 0) facts.Add(("Power use", $"{t.PowerKw} kW"));
            if (t.Pollution > 0) facts.Add(("Pollution", t.Pollution.ToString()));
            if (t.Noise > 0) facts.Add(("Noise", t.Noise.ToString()));
            if (t.Attraction > 0) { facts.Add(("Attraction", t.Attraction.ToString())); paras.Add("Draws visitors, who bring tourist shopping and a few new residents."); }
            facts.Add(("In your city", built.Count == 0 ? "none built" : $"{built.Count} built, {working} working"));
            string cat = t.Category.ToString();
            string rule = t.Category switch
            {
                Category.Power => "power", Category.Water => "water", Category.Garbage => "garbage", _ => "services",
            };
            Add(t.Id, t.Name, cat, paras, facts, rule);
        }

        // ---- Paths ----
        var cells = new int[Content.Profiles.Count];
        var trips = new int[Content.Profiles.Count];
        for (int i = 0; i < N; i++)
            if (Path[i] != 0) { cells[Path[i] - 1]++; trips[Path[i] - 1] = Math.Max(trips[Path[i] - 1], Trips[i]); }
        for (int k = 0; k < Content.Profiles.Count; k++)
        {
            var p = Content.Profiles[k];
            var modes = new List<string>();
            if (p.Cars) modes.Add("cars");
            if (p.Bikes) modes.Add("bikes");
            if (p.Pedestrians) modes.Add("people on foot");
            var paras = new List<string>
            {
                $"Carries {string.Join(", ", modes)} at up to {p.SpeedKmh} km/h." + (p.Frontage ? " Lots beside it get road access." : " No frontage: lots beside it get no access."),
            };
            Add(p.Id, p.Name, "Paths", paras, new()
            {
                ("Speed", $"{p.SpeedKmh} km/h"), ("Capacity", $"{p.CapacityPerHour:N0} an hour"), ("Cost", $"{Money(p.CostPerCell)} a cell"),
                ("Upkeep", $"${p.MaintenancePerCell:0.00} a cell a month"), ("Noise", $"{p.NoiseDb} dB"), ("Width", $"{p.WidthM} m"),
                ("In your city", cells[k] == 0 ? "none built" : $"{cells[k]:N0} cells, busiest {trips[k]:N0} trips a day"),
            }, "traffic", "lots");
        }
        return list;
    }

    List<(string, string)> ZoneCellCounts()
    {
        var count = new int[3];
        for (int i = 0; i < N; i++) if (ZoneCode[i] != 0) count[(int)ZoneOf(ZoneCode[i]) - 1]++;
        return new() { ("Residential cells", count[0].ToString("N0")), ("Commercial cells", count[1].ToString("N0")), ("Industrial cells", count[2].ToString("N0")) };
    }

    int AverageOnZoned(byte[] layer)
    {
        long sum = 0;
        int n = 0;
        for (int i = 0; i < N; i++) if (ZoneCode[i] != 0) { sum += layer[i]; n++; }
        return n > 0 ? (int)(sum / n) : 0;
    }
}
