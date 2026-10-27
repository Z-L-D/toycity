namespace ToyCity.Sim;

/// <summary>A rectangle of cells, inclusive on both ends, in any corner order.</summary>
public readonly record struct CellRect(int X0, int Y0, int X1, int Y1)
{
    public int MinX => Math.Min(X0, X1);
    public int MaxX => Math.Max(X0, X1);
    public int MinY => Math.Min(Y0, Y1);
    public int MaxY => Math.Max(Y0, Y1);
}

public enum TerraformMode : byte { Raise = 0, Lower = 1, Level = 2 }

public enum UtilityKind : byte { PowerLine = 0, WaterPipe = 1 }

/// <summary>Everything a player can do to the city. Actions are queued and applied at the start of the next tick.</summary>
public abstract record SimAction;

/// <summary>A straight or L-shaped run, horizontal first.</summary>
public sealed record PlacePathAction(int X0, int Y0, int X1, int Y1, string ProfileId) : SimAction;
public sealed record PaintZoneAction(CellRect Area, Zone Zone, Level Density) : SimAction;
public sealed record PlaceBuildingAction(int X, int Y, string TypeId) : SimAction;
/// <summary>Clears buildings, paths and power lines in the area, or only water pipes when <c>Underground</c> is set.</summary>
public sealed record BulldozeAction(CellRect Area, bool Underground = false) : SimAction;
public sealed record TerraformAction(CellRect Area, TerraformMode Mode) : SimAction;
public sealed record SetTaxAction(Zone Zone, Level Wealth, int PercentTenths) : SimAction;
public sealed record SetFundingAction(int BuildingId, int Percent) : SimAction;
public sealed record TakeLoanAction(long Amount) : SimAction;
/// <summary>A straight or L-shaped run of power line or water pipe, horizontal first.</summary>
public sealed record PlaceUtilityAction(int X0, int Y0, int X1, int Y1, UtilityKind Kind) : SimAction;

public readonly record struct LoggedAction(long Tick, SimAction Action);

/// <summary>A message for the player, optionally pinned to a cell.</summary>
public readonly record struct SimNotice(string Text, string Severity, int X = -1, int Y = -1);
