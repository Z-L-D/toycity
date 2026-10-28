using ToyCity.Contract;

namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>The lots leased to a side game.</summary>
    void FillLeases(HostedGame hg, CityToGame input) { }

    /// <summary>First of the month: rent, tax and subsidy for each lease.</summary>
    void BillLeases(HostedGame hg, CityToGame input) { }

    /// <summary>First of the month: what the side game paid against its bills.</summary>
    void SettleLeases(HostedGame hg, GameToCity output) { }
}
