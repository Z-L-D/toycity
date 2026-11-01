namespace ToyCity.Contract;

// The side-game contract, version 1: everything a side game and the city say to each other. A side game references
// this assembly and nothing else of the city's. The city is authoritative outside a leased lot; the side game is
// authoritative inside it. In solo play both run in one process on one tick (see ISideGame); the same messages are
// written out in protocol/contract.proto for side games hosted elsewhere.

public static class ContractInfo
{
    /// <summary>Bumped only by a breaking change. Additive fields keep the version. v1 was frozen at the Alpha 3 gate.</summary>
    public const int Version = 1;
    /// <summary>
    /// Additions since v1 froze, each optional for a side game to use: 1 added incident responses and holds
    /// (<see cref="GameToCity.Responses"/>, <see cref="GameToCity.Holding"/>) and <see cref="ICityQueries.Area"/>; 2 added
    /// tram and rail lines (<see cref="TransitLine.Mode"/>) and <see cref="ICityQueries.RouteFor"/>; 3 added franchise
    /// tenders (<see cref="CityToGame.Tenders"/>, <see cref="CityToGame.Franchises"/>, bids as <see cref="AuthorityAction"/>
    /// with an amount) and freight (<see cref="CityToGame.Freight"/>, <see cref="GameToCity.FreightPicked"/>,
    /// <see cref="GameToCity.FreightDropped"/>, and routes to the map edge); 4 added lines that run on into a neighbouring
    /// city (<see cref="TransitLine.ThroughSide"/>, <see cref="CityToGame.Crossings"/>); 5 added the day clock beside the
    /// calendar (<see cref="CityToGame.ClockMinute"/>, <see cref="CityToGame.NewTurn"/> and the rest).
    /// </summary>
    public const int Minor = 5;
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

/// <summary>
/// What a side game did about one of the city's incidents (v1.1), and how it ended: for a fire "out" (put out before
/// it spread) or "spread"; for a crime "arrest" or "escaped"; for an injury "treated" or "died". Text, if any, goes
/// in the city's news.
/// </summary>
public sealed record IncidentResponse(long IncidentId, string Outcome, string Text);

/// <summary>What is on a cell, as a side game may see it for laying out a scene (v1.1).</summary>
public enum CellKind : byte { Ground = 0, Road = 1, Building = 2, Water = 3, Park = 4 }

/// <summary>Money between the city and the side game. Positive: the side game pays the city; negative: the city pays it.</summary>
public sealed record Bill(int LeaseId, string Label, long Amount);

/// <summary>
/// A paid action the city offers a side game for raising its standing in a district: "advertise", "statue",
/// "exclusive-rights" or "bribe". Allowed false, with the reason, when the players' consent settings switch it off.
/// </summary>
public sealed record PaidActionOffer(string Kind, string Name, long Cost, bool Allowed, string WhyNot);

/// <summary>A side game buys a paid action in a district. v1.3: kind "bid" offers Amount for the district's open franchise tender.</summary>
public sealed record AuthorityAction(string Kind, int DistrictId, long Amount = 0);

/// <summary>
/// v1.3: the city has put a district's transit franchise out to tender. Bids close in DaysLeft days; the highest wins
/// and pays its bid. Exclusive: the winner also gets exclusive rights there (only when every player allows them);
/// otherwise the franchise means only the winner may lease new transit lots in the district. YourBid: this game's bid.
/// </summary>
public sealed record Tender(int DistrictId, int Months, int DaysLeft, bool Exclusive, long MinBid, long YourBid);

/// <summary>v1.3: who holds a district's transit franchise or exclusive rights, and for how much longer.</summary>
public sealed record Franchise(int DistrictId, string Holder, bool Exclusive, int DaysLeft);

/// <summary>
/// v1.3: goods at a leased freight depot today, in tons: ready to ship from the industry in its reach, and wanted by
/// the shops in its reach.
/// </summary>
public sealed record FreightLot(int LeaseId, int TonsReady, int TonsWanted);

/// <summary>What became of a paid action, the day after. When Ok the side game pays Cost; otherwise nothing is owed. Rating: the side game's rating in the district now.</summary>
public sealed record AuthorityResult(string Kind, int DistrictId, bool Ok, long Cost, string Text, int Rating);

/// <summary>Everything the city tells a side game each day.</summary>
public sealed class CityToGame
{
    public long Tick;
    public int Day, Month, Year;
    /// <summary>True on the first day of a month: districts, demand and bills are refreshed then.</summary>
    public bool NewMonth;
    /// <summary>
    /// v1.5: the day clock, in minutes since the city's clock started (Monday, 08:00 is 480). The calendar (tick, day,
    /// month, year, and so seasons) moves on by a day each tick; the day clock moves by <see cref="ClockMinutesPerTick"/>,
    /// at a pace the mayor sets. Life-paced play (calls waiting, crews on the road, a scene left alone) counts in it.
    /// </summary>
    public long ClockMinute;
    /// <summary>v1.5: how far the day clock moves each tick: 48 minutes at a month a clock day, 2 at two years a day.</summary>
    public int ClockMinutesPerTick = 48;
    /// <summary>
    /// v1.5: true when a turn of the day clock (48 minutes) began this tick. The city raises its own calls and moves
    /// fires and storms on turns; a side game that counts life-paced things in turns stays in step with it. At the
    /// slowest calendar every tick is a turn; at two years a clock day one tick in 24 is.
    /// </summary>
    public bool NewTurn = true;
    /// <summary>v1.5: turns of the day clock since it started (<see cref="ClockMinute"/> / 48).</summary>
    public long Turn;
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
    /// <summary>v1.3: franchise tenders open today.</summary>
    public readonly List<Tender> Tenders = new();
    /// <summary>v1.3: franchises and exclusive rights in force today, any game's.</summary>
    public readonly List<Franchise> Franchises = new();
    /// <summary>v1.3: goods at this game's leased freight depots.</summary>
    public readonly List<FreightLot> Freight = new();
    /// <summary>v1.4: where the city's roads cross into neighbouring cities, one a side.</summary>
    public readonly List<BorderCrossing> Crossings = new();
}

/// <summary>
/// What a line's vehicles run on: buses on streets, trams on tram strips (shared with traffic or on their own lane),
/// trains on rail lines. Each has its own kind of stop: "bus-stop", "tram-stop" or "rail-station".
/// </summary>
public enum TransitMode : byte { Bus = 0, Tram = 1, Rail = 2 }

public static class TransitModes
{
    public static string StopKind(TransitMode m) => m switch { TransitMode.Tram => "tram-stop", TransitMode.Rail => "rail-station", _ => "bus-stop" };
    public static string DepotKind(TransitMode m) => m switch { TransitMode.Tram => "tram-depot", TransitMode.Rail => "rail-depot", _ => "bus-depot" };
}

/// <summary>
/// A transit line a side game runs: its stops in order (it runs both ways), how often, and the fare. v1.2: the mode
/// its vehicles run on; its stops are leased lots of that mode's stop kind.
/// </summary>
public sealed record TransitLine(int LineId, string Name, string Color, IReadOnlyList<int> StopLeaseIds, int HeadwayMinutes, int FareCents, int SeatsPerHour,
    TransitMode Mode = TransitMode.Bus, int ThroughSide = -1);

/// <summary>
/// v1.4: a road crossing into a neighbouring city on one side (0 north, 1 east, 2 south, 3 west), and how many
/// commuters cross that side a day, both ways. A line with <see cref="TransitLine.ThroughSide"/> set runs on past its
/// last stop over that border, and carries a share of them.
/// </summary>
public sealed record BorderCrossing(int Side, string Neighbour, int Cell, int CommutersADay);

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
    /// <summary>v1.1: how incidents the side game answered turned out. The city applies each once.</summary>
    public readonly List<IncidentResponse> Responses = new();
    /// <summary>
    /// v1.1: incidents a player is handling at the scene right now. The city holds each still (a fire neither spreads
    /// nor goes out by itself) until a response for it comes back.
    /// </summary>
    public readonly List<long> Holding = new();
    /// <summary>v1.3: tons of goods collected today at each leased freight depot (at most its tons ready).</summary>
    public readonly Dictionary<int, int> FreightPicked = new();
    /// <summary>v1.3: tons delivered today to each leased freight depot's shops, or to the map edge under lease id 0 (exports).</summary>
    public readonly Dictionary<int, int> FreightDropped = new();
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
    /// <summary>v1.1: what is on each cell of a rectangle, row by row; cells off the map read as water.</summary>
    CellKind[] Area(int x0, int y0, int w, int h) => new CellKind[Math.Max(0, w) * Math.Max(0, h)];
    /// <summary>
    /// v1.2: the fastest route a vehicle of this mode can take between two stops (by building id), along the paths it
    /// runs on, at today's travel times; null when there is none. Buses go stop to stop by street, as <see cref="Route"/>.
    /// v1.3: toBuildingId -1 asks for the way to the nearest map edge, where goods leave the city.
    /// </summary>
    StreetRoute? RouteFor(TransitMode mode, int fromBuildingId, int toBuildingId) => null;
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

/// <summary>
/// Thrown by a side game hosted in another process when it doesn't answer a tick or a command by the city's deadline.
/// The call never happened on either side: the host puts the game back as it was. The city logs the miss, so replays
/// skip the same call.
/// </summary>
public sealed class SideGameMissedException(string message) : Exception(message);
