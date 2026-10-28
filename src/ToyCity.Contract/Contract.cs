namespace ToyCity.Contract;

// The side-game contract, version 1: everything a side game and the city say to each other. A side game references
// this assembly and nothing else of the city's. The city is authoritative outside a leased lot; the side game is
// authoritative inside it. In solo play both run in one process on one tick (see ISideGame); the same messages are
// written out in protocol/contract.proto for side games hosted elsewhere.

public static class ContractInfo
{
    /// <summary>Bumped only by a breaking change. Additive fields keep the version.</summary>
    public const int Version = 1;
}

/// <summary>What a side game tells the city when it registers.</summary>
public sealed record Manifest(string Id, string Name, int ContractVersion, IReadOnlyList<string> LotKinds, string Description);

/// <summary>How a seat is played: by its owner, or by the computer at a level the owner picks.</summary>
public enum OperatorLevel { Manual = 0, Stasis = 1, Caretaker = 2, Autopilot = 3 }

/// <summary>Business lease: the operator pays rent and tax and keeps the profit. Public service: the city pays a subsidy and caps fees.</summary>
public enum LeaseMode { Business = 0, PublicService = 1 }

/// <summary>
/// A lease's terms. Business: rent a month plus a tax on fare revenue, in tenths of a percent. Public service: a
/// subsidy a month, and fares capped (0 = free to ride). Changes take effect after the notice period.
/// </summary>
public sealed record LeaseTerms(LeaseMode Mode, long RentPerMonth, int TaxTenths, long SubsidyPerMonth, int FareCapCents, int NoticeMonths);

/// <summary>A lot the city has leased to this side game.</summary>
public sealed record LeasedLot(
    int LeaseId, int BuildingId, string Kind, int X, int Y, int W, int H, int AccessCell, int DistrictId,
    LeaseTerms Terms, LeaseTerms? PendingTerms, int PendingMonths, bool BadStanding);

/// <summary>A lot nobody leases yet that this side game could ask for (in <see cref="GameToCity.LeaseRequests"/>), and the city's standard terms for it.</summary>
public sealed record OpenLot(int BuildingId, string Kind, int X, int Y, int W, int H, int AccessCell, int DistrictId, LeaseTerms Terms);

/// <summary>A district of the city: how many live and work there and how it feels, plus this game's rating there.</summary>
public sealed record District(
    int Id, string Name, int X0, int Y0, int X1, int Y1, int Population, int Jobs, int LandValue, int Crime, int Health,
    int Rating);

/// <summary>Commuter trips a day from one district to another, whatever way they travel now.</summary>
public sealed record TripDemand(int FromDistrict, int ToDistrict, int Trips);

/// <summary>Yesterday's riders on one of this game's lines, and how many got on at each stop.</summary>
public sealed record LineRidership(int LineId, int Riders, IReadOnlyList<int> BoardingsByStop);

/// <summary>
/// Something that happened, routed through the city so any side game can react: a fire, a breakdown, an injury.
/// Severity 1 (minor) to 3 (serious). Source is "city" or the side game's id.
/// </summary>
public sealed record Incident(long Id, string Kind, int X, int Y, string Source, string Cause, int Severity, string Text);

/// <summary>Money between the city and the side game. Positive: the side game pays the city; negative: the city pays it.</summary>
public sealed record Bill(int LeaseId, string Label, long Amount);

/// <summary>
/// A paid action the city offers a side game for raising its standing in a district: "advertise", "statue",
/// "exclusive-rights" or "bribe". Allowed false, with the reason, when the players' consent settings switch it off.
/// </summary>
public sealed record PaidActionOffer(string Kind, string Name, long Cost, bool Allowed, string WhyNot);

/// <summary>A side game buys a paid action in a district.</summary>
public sealed record AuthorityAction(string Kind, int DistrictId);

/// <summary>What became of a paid action, the day after. When Ok the side game pays Cost; otherwise nothing is owed. Rating: the side game's rating in the district now.</summary>
public sealed record AuthorityResult(string Kind, int DistrictId, bool Ok, long Cost, string Text, int Rating);

/// <summary>Everything the city tells a side game each day.</summary>
public sealed class CityToGame
{
    public long Tick;
    public int Day, Month, Year;
    /// <summary>True on the first day of a month: districts, demand and bills are refreshed then.</summary>
    public bool NewMonth;
    /// <summary>How the side game's seat is played today.</summary>
    public OperatorLevel Operator;
    public string? OwnerName;
    public readonly List<LeasedLot> Lots = new();
    /// <summary>Lots of the kinds this game leases that nobody leases yet.</summary>
    public readonly List<OpenLot> OpenLots = new();
    public readonly List<District> Districts = new();
    public readonly List<TripDemand> Demand = new();
    public readonly List<LineRidership> Ridership = new();
    public readonly List<Incident> Incidents = new();
    /// <summary>Bills due this month. The side game settles them in its output.</summary>
    public readonly List<Bill> Bills = new();
    /// <summary>The paid actions on offer today, at the city's prices.</summary>
    public readonly List<PaidActionOffer> Offers = new();
    /// <summary>What became of yesterday's paid actions.</summary>
    public readonly List<AuthorityResult> AuthorityResults = new();
}

/// <summary>A transit line a side game runs: its stops in order (it runs both ways), how often, and the fare.</summary>
public sealed record TransitLine(int LineId, string Name, string Color, IReadOnlyList<int> StopLeaseIds, int HeadwayMinutes, int FareCents, int SeatsPerHour);

/// <summary>Everything a side game tells the city each day.</summary>
public sealed class GameToCity
{
    /// <summary>The lines running today. The city's commuters ride them tomorrow.</summary>
    public readonly List<TransitLine> Lines = new();
    /// <summary>People employed on each leased lot (drivers at a depot, say).</summary>
    public readonly Dictionary<int, int> JobsByLease = new();
    public readonly List<Incident> Incidents = new();
    /// <summary>What was paid against this month's bills, by lease. Unpaid bills put a lease in arrears.</summary>
    public readonly Dictionary<int, long> Paid = new();
    /// <summary>Fares taken today at each leased lot, in dollars. Business leases pay their fare tax on the month's total.</summary>
    public readonly Dictionary<int, long> RevenueByLease = new();
    /// <summary>Lots (by building id) the side game would like to lease on the city's standard terms.</summary>
    public readonly List<int> LeaseRequests = new();
    /// <summary>Leases the side game gives up. They end today; anything owed is written off.</summary>
    public readonly List<int> LeaseReleases = new();
    /// <summary>Paid actions bought today. The city answers in tomorrow's <see cref="CityToGame.AuthorityResults"/>.</summary>
    public readonly List<AuthorityAction> AuthorityActions = new();
    /// <summary>0..100: how well the side game serves the city (on time, not crowded). Feeds its authority rating.</summary>
    public int ServiceQuality = 50;
    /// <summary>One line for the city's why-traces and the news ("Buses running on 3 lines").</summary>
    public string Status = "";
}

/// <summary>A route along the city's streets between two cells, at today's travel times.</summary>
public sealed record StreetRoute(IReadOnlyList<int> Cells, int Minutes);

/// <summary>Read-only questions a side game may ask the city during its tick or a command.</summary>
public interface ICityQueries
{
    int Width { get; }
    int Height { get; }
    /// <summary>The fastest street route between two cells, or null when there is none.</summary>
    StreetRoute? Route(int fromCell, int toCell);
    /// <summary>The district a cell belongs to.</summary>
    int DistrictOf(int cell);
    /// <summary>A stable hash of the city's seed and some numbers, for the side game's own deterministic randomness.</summary>
    ulong Hash(int a, int b, int c, int d);
}

/// <summary>What became of a player's command.</summary>
public sealed record CommandResult(bool Ok, string Message);

/// <summary>
/// A side game hosted in the city's process. Every method runs under the city's lock, on the city's thread, in tick
/// order, and must be deterministic: no clocks, no shared randomness, no unordered iteration. Its state must round
/// trip through Save and Load exactly, since forecasts, saves and replays all copy it that way.
/// </summary>
public interface ISideGame
{
    Manifest Manifest { get; }

    /// <summary>Sets up a fresh game for a city with this seed.</summary>
    void NewGame(ulong seed);

    /// <summary>One day, after the city's own day.</summary>
    void Tick(CityToGame input, ICityQueries city, GameToCity output);

    /// <summary>
    /// A command from the seat's owner (or the city's automation on their behalf), as the side game's own message bytes.
    /// It is logged with the city's actions and applied at the start of a tick, so replays match.
    /// </summary>
    CommandResult Command(string seat, byte[] payload, ICityQueries city);

    /// <summary>The state a front end shows, as the side game's own message bytes. A query: it changes nothing.</summary>
    byte[] View(string seat);

    void Save(BinaryWriter w);
    void Load(BinaryReader r);
}
