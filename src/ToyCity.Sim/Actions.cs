namespace ToyCity.Sim;

/// <summary>A rectangle of cells, inclusive on both ends, in any corner order.</summary>
public readonly record struct CellRect(int X0, int Y0, int X1, int Y1)
{
    public int MinX => Math.Min(X0, X1);
    public int MaxX => Math.Max(X0, X1);
    public int MinY => Math.Min(Y0, Y1);
    public int MaxY => Math.Max(Y0, Y1);
}

/// <summary>Smooth evens each cell toward its neighbours' average, wearing steps down into slopes.</summary>
public enum TerraformMode : byte { Raise = 0, Lower = 1, Level = 2, Smooth = 3 }

public enum UtilityKind : byte { PowerLine = 0, WaterPipe = 1 }

/// <summary>
/// Everything a player can do to the city. Actions are queued and applied at the start of the next tick. By is the
/// player who sent it, set by the server from the connection (never from the message), and logged with it.
/// </summary>
public abstract record SimAction
{
    public string By { get; init; } = "";
}

/// <summary>
/// A straight or L-shaped run, horizontal first; or, when <c>Curved</c>, an arc from (X0,Y0) to (X1,Y1) bent toward
/// the control cell (CX,CY), snapped to the grid; or, when <c>Diagonal</c>, a straight line at any angle, snapped to
/// the grid. Arcs and diagonals are remembered as the line drawn (<see cref="RoadStrip"/>), so clients draw them smooth.
/// </summary>
public sealed record PlacePathAction(int X0, int Y0, int X1, int Y1, string ProfileId, bool Curved = false, int CX = 0, int CY = 0, bool Diagonal = false) : SimAction;
public sealed record PaintZoneAction(CellRect Area, Zone Zone, Level Density) : SimAction;

/// <summary>What block zoning does with cells too deep in the block for any lot to reach a road.</summary>
public enum Leftover : byte { Leave = 0, Parks = 1, Zone = 2 }

/// <summary>Zones the whole block enclosed by paths around (X,Y), lot-deep from the road; <c>Leftover</c> decides the rest.</summary>
public sealed record ZoneBlockAction(int X, int Y, Zone Zone, Level Density, Leftover Leftover) : SimAction;
/// <summary>
/// Places a civic building with its top-left at (X,Y). A roadside one (a bus stop) goes on a road cell, on the kerb
/// given by Side (0 north, 1 east, 2 south, 3 west; -1 picks one); clicked on the land beside a road, it goes on that
/// road's kerb facing the click.
/// </summary>
public sealed record PlaceBuildingAction(int X, int Y, string TypeId, int Side = -1) : SimAction;

/// <summary>Upgrades a civic building in place to one of the types its own type lists.</summary>
public sealed record UpgradeBuildingAction(int BuildingId, string ToTypeId) : SimAction;
/// <summary>Clears buildings, paths and power lines in the area, or only water pipes when <c>Underground</c> is set.</summary>
public sealed record BulldozeAction(CellRect Area, bool Underground = false) : SimAction;
public sealed record TerraformAction(CellRect Area, TerraformMode Mode) : SimAction;
public sealed record SetTaxAction(Zone Zone, Level Wealth, int PercentTenths) : SimAction;
public sealed record SetFundingAction(int BuildingId, int Percent) : SimAction;
public sealed record TakeLoanAction(long Amount) : SimAction;
/// <summary>A straight or L-shaped run of power line or water pipe, horizontal first.</summary>
public sealed record PlaceUtilityAction(int X0, int Y0, int X1, int Y1, UtilityKind Kind) : SimAction;

public readonly record struct LoggedAction(long Tick, SimAction Action);

/// <summary>A message for the players, optionally pinned to a cell; To names the one player it's for, or is empty for all.</summary>
public readonly record struct SimNotice(string Text, string Severity, int X = -1, int Y = -1, string To = "");
