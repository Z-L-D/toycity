using ToyCity.Contract;

namespace ToyCity.Sim;

public sealed partial class World
{
    /// <summary>How a side game's seat is played today. Until seats can be claimed, the computer keeps things running.</summary>
    void FillSeat(HostedGame hg, CityToGame input) => input.Operator = OperatorLevel.Caretaker;
}
