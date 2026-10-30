using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>A side game's standing in one district: its authority rating and what it has paid for there.</summary>
public sealed class Standing
{
    public string GameId = "";
    public int District;
    /// <summary>-1000..1000. Drifts a quarter of the way to its target each month; paid actions move it at once.</summary>
    public int Rating;
    /// <summary>Where the rating is heading this month, from service, nuisance and any statue.</summary>
    public int Target;
    public bool Statue;
    /// <summary>The tick its exclusive rights run out; the city's own buses don't stop in the district until then.</summary>
    public long ExclusiveUntil;
    /// <summary>Caught bribing: no more bribes in the district until this tick.</summary>
    public long BribeBanUntil;
    /// <summary>Incidents the side game caused in the district this month (breakdowns and the like).</summary>
    public int Incidents;
}

public sealed partial class World
{
    public readonly List<Standing> Standings = new();
    /// <summary>Paid actions answered today, handed to their side games tomorrow.</summary>
    readonly List<(string GameId, AuthorityResult Result)> _authorityResults = new();

    /// <summary>Below this the district turns down the side game's requests for more lots.</summary>
    public const int RefuseRating = -400;
    public const int AdvertiseLift = 200, StatueLift = 150, BribeLift = 400, CaughtRating = -800;
    public const int ExclusiveMonths = 12, BribeBanMonths = 6;

    /// <summary>The paid actions, OpenTTD style: name, price, and the consent setting that switches it off, if any.</summary>
    public static readonly (string Kind, string Name, long Cost, Mechanic? Gate)[] PaidActions =
    {
        ("advertise", "Advertising campaign", 2_000, null),
        ("statue", "Statue in the square", 8_000, null),
        ("exclusive-rights", "Exclusive rights for a year", 12_000, Mechanic.ExclusiveRights),
        ("bribe", "Bribe the district council", 5_000, Mechanic.Bribery),
    };

    /// <summary>A side game's authority rating in a district, -1000..1000.</summary>
    public int RatingOf(string gameId, int district) => FindStanding(gameId, district)?.Rating ?? 0;

    public Standing? FindStanding(string gameId, int district) => Standings.FirstOrDefault(s => s.GameId == gameId && s.District == district);

    Standing StandingFor(string gameId, int district)
    {
        if (FindStanding(gameId, district) is { } s) return s;
        s = new Standing { GameId = gameId, District = district };
        Standings.Add(s);
        Standings.Sort((a, b) => a.GameId != b.GameId ? string.CompareOrdinal(a.GameId, b.GameId) : a.District.CompareTo(b.District));
        return s;
    }

    /// <summary>Whether a side game other than this one holds exclusive rights in the district today.</summary>
    bool ExclusiveHeld(int district, out string gameId)
    {
        foreach (var s in Standings)
            if (s.District == district && s.ExclusiveUntil > Tick) { gameId = s.GameId; return true; }
        gameId = "";
        return false;
    }

    /// <summary>Unleased bus stops in districts where a side game holds exclusive rights: the city's own buses skip them.</summary>
    void AddExclusiveStops()
    {
        foreach (var b in Buildings)
            if (b != null && IsStopKind(TypeOf(b).LotKind) && ExclusiveHeld(DistrictOf(b.X, b.Y), out _)) _leasedStops.Add(b.Id);
    }

    void FillOffers(HostedGame hg, CityToGame input, List<(string GameId, AuthorityResult Result)> results)
    {
        foreach (var (kind, name, cost, gate) in PaidActions)
        {
            string why = gate is { } m ? WhyOff(m, SeatOf(hg.Id)?.Owner ?? "", MayorSeat.Owner) ?? "" : "";
            input.Offers.Add(new PaidActionOffer(kind, name, cost, why.Length == 0, why));
        }
        foreach (var (id, r) in results) if (id == hg.Id) input.AuthorityResults.Add(r);
    }

    /// <summary>
    /// The side game's paid actions from today's output. The city answers each; the side game pays for those that went
    /// ahead when it hears back tomorrow. Statues and exclusive rights are paid to the city.
    /// </summary>
    void TakeAuthorityActions(HostedGame hg, GameToCity output)
    {
        foreach (var a in output.AuthorityActions.Take(8))
        {
            var r = a.Kind == "bid" ? Bid(hg.Id, a.DistrictId, a.Amount) : PaidAction(hg.Id, a.Kind, a.DistrictId);
            _authorityResults.Add((hg.Id, r));
            if (!r.Ok) continue;
            if (a.Kind is "statue" or "exclusive-rights") Funds += r.Cost;
            if (r.Text.Length > 0) Notices.Add(new SimNotice(r.Text, a.Kind == "bribe" && r.Text.Contains("caught") ? "warning" : "info"));
        }
    }

    AuthorityResult PaidAction(string gameId, string kind, int district)
    {
        AuthorityResult No(string why) => new(kind, district, false, 0, why, RatingOf(gameId, district));
        int i = Array.FindIndex(PaidActions, p => p.Kind == kind);
        if (i < 0) return No($"The city doesn't sell '{kind}'.");
        var (_, _, cost, gate) = PaidActions[i];
        if (district < 0 || district >= DistrictCount) return No("There is no such district.");
        if (gate is { } m && WhyOff(m, SeatOf(gameId)?.Owner ?? "", MayorSeat.Owner) is { } off) return No(off);
        if (InStasis) return No("The city is in stasis: the councils aren't sitting.");
        string game = GameName(gameId), where = DistrictName(district);
        var s = StandingFor(gameId, district);
        switch (kind)
        {
            case "advertise":
                s.Rating = Math.Min(1000, s.Rating + AdvertiseLift);
                return new(kind, district, true, cost, $"{game} is advertising all over {where}.", s.Rating);
            case "statue":
                if (s.Statue) return No($"{game} already has a statue in {where}.");
                s.Statue = true;
                s.Target = Math.Min(1000, s.Target + StatueLift);
                return new(kind, district, true, cost, $"A statue of {game}'s founder goes up in {where}.", s.Rating);
            case "exclusive-rights":
                if (ExclusiveHeld(district, out string holder)) return No($"{GameName(holder)} holds exclusive rights in {where} already.");
                if (FranchiseIn(district) is { } f && f.Winner != gameId) return No($"{GameName(f.Winner)} holds the transit franchise in {where}.");
                s.ExclusiveUntil = Tick + ExclusiveMonths * 30;
                AddHeadline($"{game} buys exclusive rights in {where}: city buses stop running there for a year.", "info");
                return new(kind, district, true, cost, $"{game} has exclusive rights in {where} for {ExclusiveMonths} months.", s.Rating);
            default:
                if (s.BribeBanUntil > Tick) return No($"Nobody in {where} will take {game}'s money after the last scandal.");
                if (Hash((int)Tick, district, (int)XxHashOf(gameId), 0xb1) % 3 == 0)
                {
                    s.Rating = CaughtRating;
                    s.BribeBanUntil = Tick + BribeBanMonths * 30;
                    AddHeadline($"Scandal: {game} caught bribing the {where} council.", "warning");
                    return new(kind, district, true, cost, $"{game} was caught bribing the {where} council. Its rating there is in ruins.", s.Rating);
                }
                s.Rating = Math.Min(1000, s.Rating + BribeLift);
                return new(kind, district, true, cost, "", s.Rating);
        }
    }

    /// <summary>
    /// First of the month: each side game's rating in each district moves a quarter of the way to its target. Good
    /// service, riders and jobs raise the target; leases in bad standing and incidents the game caused lower it.
    /// </summary>
    void UpdateRatings(HostedGame hg)
    {
        var mine = Leases.Where(l => l.GameId == hg.Id && Buildings[l.BuildingId] != null).ToList();
        foreach (int d in mine.Select(l => DistrictOf(Buildings[l.BuildingId]!.X, Buildings[l.BuildingId]!.Y)).Distinct().ToList())
            StandingFor(hg.Id, d);
        foreach (var s in Standings.Where(s => s.GameId == hg.Id))
        {
            var here = mine.Where(l => DistrictOf(Buildings[l.BuildingId]!.X, Buildings[l.BuildingId]!.Y) == s.District).ToList();
            int target = 0;
            if (here.Count > 0)
            {
                int riders = here.Sum(l => Buildings[l.BuildingId]!.Riders), jobs = here.Sum(l => l.Jobs);
                target = (Math.Clamp(hg.Last.ServiceQuality, 0, 100) - 50) * 4
                    + Math.Min(400, riders) + Math.Min(100, jobs * 2)
                    - 250 * here.Count(l => l.BadStanding || l.Arrears > 0)
                    - Math.Min(400, 60 * s.Incidents);
            }
            if (s.Statue) target += StatueLift;
            s.Target = Math.Clamp(target, -1000, 1000);
            s.Rating = Math.Clamp(s.Rating + (s.Target - s.Rating) / 4, -1000, 1000);
            s.Incidents = 0;
        }
    }

    /// <summary>Counts an incident a side game caused against its standing in the district.</summary>
    void NoteGameIncident(string gameId, int x, int y)
    {
        if (InBounds(x, y)) StandingFor(gameId, DistrictOf(x, y)).Incidents++;
    }

    void WriteAuthority(BinaryWriter w)
    {
        w.Write(Standings.Count);
        foreach (var s in Standings)
        {
            w.Write(s.GameId); w.Write(s.District); w.Write(s.Rating); w.Write(s.Target); w.Write(s.Statue);
            w.Write(s.ExclusiveUntil); w.Write(s.BribeBanUntil); w.Write(s.Incidents);
        }
        w.Write(_authorityResults.Count);
        foreach (var (id, r) in _authorityResults)
        {
            w.Write(id); w.Write(r.Kind); w.Write(r.DistrictId); w.Write(r.Ok); w.Write(r.Cost); w.Write(r.Text); w.Write(r.Rating);
        }
        WriteConsent(w);
        WriteTenders(w);
    }

    void ReadAuthority(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
            Standings.Add(new Standing
            {
                GameId = r.ReadString(), District = r.ReadInt32(), Rating = r.ReadInt32(), Target = r.ReadInt32(), Statue = r.ReadBoolean(),
                ExclusiveUntil = r.ReadInt64(), BribeBanUntil = r.ReadInt64(), Incidents = r.ReadInt32(),
            });
        int nr = r.ReadInt32();
        for (int i = 0; i < nr; i++)
            _authorityResults.Add((r.ReadString(), new AuthorityResult(r.ReadString(), r.ReadInt32(), r.ReadBoolean(), r.ReadInt64(), r.ReadString(), r.ReadInt32())));
        ReadConsent(r);
        ReadTenders(r);
    }
}
