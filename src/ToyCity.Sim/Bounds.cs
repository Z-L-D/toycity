using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// Limits every action must keep to before anything else looks at it: cells on the map, enums that exist, ids that
/// point at something, amounts and text of sane size. An action outside them is refused whole, so a hostile or broken
/// client can't push the city into a state no player could reach.
/// </summary>
public sealed partial class World
{
    public const int MaxText = 64;
    public const int MaxName = 40;
    public const int MaxPayload = 64 * 1024;
    public const long MaxMoney = 1_000_000_000;

    /// <summary>Why an action is out of bounds, or null when it's within them.</summary>
    public string? OutOfBounds(SimAction a)
    {
        const string offMap = "That's off the map.";
        // Drags may run past the edge, and the city clips them, so a cell may be up to a map's width off it; a
        // rectangle must still touch the map.
        bool Cell(int x, int y) => x >= -Width && x < 2 * Width && y >= -HeightCells && y < 2 * HeightCells;
        bool Rect(CellRect r) => Cell(r.X0, r.Y0) && Cell(r.X1, r.Y1) && r.MaxX >= 0 && r.MinX < Width && r.MaxY >= 0 && r.MinY < HeightCells;
        static bool Text(string? s, int max = MaxText) => s != null && s.Length <= max;
        static bool Def<T>(T v) where T : struct, Enum => Enum.IsDefined(v);
        bool Building(int id) => id >= 0 && id < Buildings.Count && Buildings[id] != null;
        if (a.By.Length > MaxName) return "That player name is too long.";
        return a switch
        {
            PlacePathAction p => !Cell(p.X0, p.Y0) || !Cell(p.X1, p.Y1) || p.Curved && !Cell(p.CX, p.CY) ? offMap
                : !Text(p.ProfileId) ? "Unknown road type." : null,
            PaintZoneAction z => !Rect(z.Area) ? offMap : !Def(z.Zone) || !Def(z.Density) ? "Unknown zone." : null,
            ZoneBlockAction z => !InBounds(z.X, z.Y) ? offMap : !Def(z.Zone) || !Def(z.Density) || !Def(z.Leftover) ? "Unknown zone." : null,
            PlaceBuildingAction b => !InBounds(b.X, b.Y) ? offMap : !Text(b.TypeId) || b.Side is < -1 or > 3 ? "Unknown building." : null,
            UpgradeBuildingAction u => !Building(u.BuildingId) || !Text(u.ToTypeId) ? "There's no such building." : null,
            BulldozeAction d => !Rect(d.Area) ? offMap : null,
            TerraformAction t => !Rect(t.Area) ? offMap : !Def(t.Mode) ? "Unknown terraforming." : null,
            SetTaxAction t => !Def(t.Zone) || !Def(t.Wealth) || t.PercentTenths is < 0 or > 200 ? "Taxes run from 0% to 20%." : null,
            SetFundingAction f => !Building(f.BuildingId) ? "There's no such building." : f.Percent is < 0 or > 200 ? "Funding runs from 0% to 200%." : null,
            TakeLoanAction l => l.Amount is <= 0 or > MaxMoney ? "That isn't a loan the bank would make." : null,
            PlaceUtilityAction u => !Cell(u.X0, u.Y0) || !Cell(u.X1, u.Y1) ? offMap : !Def(u.Kind) ? "Unknown utility." : null,
            TriggerDisasterAction d => !InBounds(d.X, d.Y) ? offMap : !Def(d.Kind) ? "Unknown disaster." : null,
            SetDisasterSettingAction d => !Def(d.Setting) ? "Unknown setting." : null,
            RespondPromptAction => null,
            SetOrdinanceAction o => !Text(o.Id) ? "Unknown ordinance." : null,
            SetSeatAction s => !Text(s.SeatId) || !Text(s.Owner, MaxName) || !Def(s.Level) || !Def(s.Style) || s.SpendCap is < 0 or > MaxMoney ? "That isn't a seat setting." : null,
            SetAwayLevelAction s => !Text(s.SeatId) || !Def(s.Level) || !Def(s.Style) ? "That isn't a seat setting." : null,
            SeatAwayAction s => !Text(s.SeatId) ? "That isn't a seat." : null,
            SetConsentAction c => !Text(c.Owner, MaxName) || !Def(c.Mechanic) ? "Unknown mechanic." : null,
            SetCityRuleAction c => !Def(c.Mechanic) ? "Unknown mechanic." : null,
            SetHostRulesAction h => h.Forbidden.Count > 64 || !h.Forbidden.All(Def) ? "Unknown mechanic." : null,
            OfferPactAction p => !Text(p.With, MaxName) || !Def(p.Mechanic) ? "Unknown mechanic." : null,
            AcceptPactAction p => !Text(p.From, MaxName) || !Def(p.Mechanic) ? "Unknown mechanic." : null,
            EndPactAction p => !Text(p.With, MaxName) || !Def(p.Mechanic) ? "Unknown mechanic." : null,
            SetDeputyAction d => !Text(d.Player, MaxName) || (int)d.Portfolios > 15 ? "Unknown portfolio." : null,
            AttachSideGameAction g => !Text(g.GameId) ? "Unknown side game." : null,
            SideGameAction g => !Text(g.GameId) || !Text(g.Seat) ? "Unknown side game." : g.Payload.Length > MaxPayload ? "That command is too large." : null,
            SideGameMissedAction g => !Text(g.GameId) ? "Unknown side game." : null,
            LeaseLotAction l => !Text(l.GameId) || !Building(l.BuildingId) ? "There's no such lot." : l.Terms is { } t ? TermsOutOfBounds(t) : null,
            SetLeaseTermsAction l => TermsOutOfBounds(l.Terms),
            DelinkAction d => !Text(d.Player, MaxName) ? "That player name is too long." : null,
            ForkAction f => !Text(f.Player, MaxName) || !Text(f.HomeName, 200) ? "That name is too long." : null,
            OfferLeaseAction o => !Def(o.Kind) ? "That isn't an offer." : o.Amount is < 0 or > MaxMoney ? "That amount is out of range."
                : o.Kind == OfferKind.Relocate && !Building(o.ToBuildingId) ? "There's no such lot." : o.Terms is { } ot ? TermsOutOfBounds(ot) : null,
            RenameOwnerAction r => !Text(r.From, MaxName) || !Text(r.To, MaxName) ? "That player name is too long." : null,
            _ => null,
        };
    }

    static string? TermsOutOfBounds(LeaseTerms t) =>
        !Enum.IsDefined(t.Mode) || t.RentPerMonth is < 0 or > MaxMoney || t.SubsidyPerMonth is < 0 or > MaxMoney || t.TaxTenths is < 0 or > 1000
        || t.FareCapCents is < 0 or > 100_000 || t.NoticeMonths is < 0 or > 24
            ? "Those lease terms are out of range." : null;
}
