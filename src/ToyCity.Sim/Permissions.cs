using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>Chooses how the computer plays a seat while its owner is away (disconnected from a dedicated server).</summary>
public sealed record SetAwayLevelAction(string SeatId, OperatorLevel Level, AutopilotStyle Style = AutopilotStyle.Growth) : SimAction;

/// <summary>
/// The owner left (Away) or came back. Leaving hands a seat they play by hand to the computer at their away level;
/// coming back takes back a seat the computer took that way, with the "while you were away" report. The server sends
/// it; it is logged like any other action so a replay sees the same hand-offs.
/// </summary>
public sealed record SeatAwayAction(string SeatId, bool Away) : SimAction;

/// <summary>
/// The server gives one player's seats and settings to another: on a dedicated server, the solo "player" of a save
/// or a new city becomes the admin who loaded or founded it. Logged, so a replay arrives the same way.
/// </summary>
public sealed record RenameOwnerAction(string From, string To) : SimAction;

public sealed partial class World
{
    /// <summary>The player whose action is being applied, so a refusal goes to them alone. Empty outside an action.</summary>
    string _actor = "";

    /// <summary>
    /// Why the player who sent an action may not do it, or null if they may. An action from nobody in particular (tests,
    /// tools, saves from before Beta 2) is allowed, as before. The city's own actions belong to the mayor's owner;
    /// a side game's commands to its seat's owner; seats and consent to whoever they're about.
    /// </summary>
    public string? Forbidden(SimAction a)
    {
        string by = a.By;
        if (by.Length == 0) return null;
        // A player who de-linked from the mayor can't take a seat or a portfolio here until the cooldown is over.
        if (a is SetSeatAction { Owner.Length: > 0 } ss && Cooldown(ss.Owner) is { } cs) return cs;
        if (a is SetDeputyAction { Portfolios: not Portfolio.None } sd && Cooldown(sd.Player) is { } cd) return cd;
        switch (a)
        {
            case SetSeatAction s:
            {
                var seat = SeatOf(s.SeatId);
                if (seat == null || seat.Owner == by) return null;
                if (seat.Owner.Length == 0) return s.Owner == by ? null : "Claim a free seat for yourself, not for someone else.";
                return $"{seat.Owner} owns that seat. Ask them to hand it over.";
            }
            case SetAwayLevelAction or SeatAwayAction:
            {
                string id = a is SetAwayLevelAction l ? l.SeatId : ((SeatAwayAction)a).SeatId;
                var seat = SeatOf(id);
                return seat == null || seat.Owner == by ? null : "Only a seat's owner decides how it's played while they're away.";
            }
            case SetConsentAction c:
                return c.Owner == by ? null : "You can only change your own consent settings.";
            case OfferPactAction or AcceptPactAction or EndPactAction:
                return null;  // always about the sender and one other player
            case SetHostRulesAction:
                return "Only the server sets its host's rules.";
            case RenameOwnerAction:
                return "Only the server hands a player's seats to another.";
            case ForkAction:
                return "Only a de-link makes a copy of the city.";
            case DelinkAction d:
                return ForbiddenDelink(d, by);
            case SideGameMissedAction:
                return "Only the city records a side game's missed deadline.";
            case SideGameAction g:
            {
                var seat = SeatOf(g.GameId);
                if (seat == null || seat.Owner == by) return null;
                string name = SideGame(g.GameId)?.Game.Manifest.Name ?? g.GameId;
                return seat.Owner.Length == 0 ? $"Nobody runs {name}. Claim it to give it orders." : $"{seat.Owner} runs {name}, not you.";
            }
            case LeaseLotAction { Terms: null } l when by.Length > 0 && SeatOf(l.GameId)?.Owner == by:
                return null;  // whoever runs a side game leases free lots for it on the standard terms, as the computer would
            case EndLeaseAction { ByOperator: true } e:
            {
                var lease = LeaseById(e.LeaseId);
                if (lease == null || SeatOf(lease.GameId)?.Owner == by) return null;
                return $"Only whoever runs {GameName(lease.GameId)} can give up its lease.";
            }
            case VoteAction:
                return null;  // membership is checked when the vote is counted
            case OfferLeaseAction or AnswerOfferAction:
                return ForbiddenOffer(a, by);
            default:
                return CityRole(a, by);
        }
    }

    void ApplySetAwayLevel(SetAwayLevelAction a)
    {
        var seat = SeatOf(a.SeatId);
        if (seat == null) { Reject($"There is no seat called '{a.SeatId}'."); return; }
        if (a.Level == OperatorLevel.Manual) { Reject("Pick how the computer plays it: stasis, caretaker or autopilot."); return; }
        if (a.Level == OperatorLevel.Stasis && seat.Id != Seat.Mayor) { Reject("Stasis is for the city seat."); return; }
        seat.AwayLevel = a.Level;
        seat.AwayStyle = a.Style;
        Notices.Add(new SimNotice($"While you're away the computer will play it on {LevelName(a.Level)}.", "info", To: _actor));
    }

    void ApplySeatAway(SeatAwayAction a)
    {
        var seat = SeatOf(a.SeatId);
        if (seat == null || seat.Owner.Length == 0) return;
        if (a.Away)
        {
            if (seat.Level != OperatorLevel.Manual) return;
            ApplySetSeat(new SetSeatAction(seat.Id, seat.Owner, seat.AwayLevel, seat.AwayStyle, seat.SpendCap));
            seat.AutoAway = true;
        }
        else
        {
            if (!seat.AutoAway) return;
            seat.AutoAway = false;
            if (seat.Level != OperatorLevel.Manual) ApplySetSeat(new SetSeatAction(seat.Id, seat.Owner, OperatorLevel.Manual, seat.Style, seat.SpendCap));
        }
    }

    /// <summary>
    /// Gives every seat and consent setting of one player to another: on a dedicated server, the solo "player" of a
    /// save or a new city becomes the admin who loaded or founded it. The server does it with <see cref="RenameOwnerAction"/>.
    /// </summary>
    public void RenameOwner(string from, string to)
    {
        if (from == to) return;
        foreach (var s in Seats) if (s.Owner == from) s.Owner = to;
        if (_consent.Remove(from, out var set)) _consent[to] = set;
        string R(string n) => n == from ? to : n;
        if (_deputies.Remove(from, out var portfolio)) _deputies[to] = portfolio;
        foreach (var p in Proposals)
        {
            if (p.Proposer == from) { p.Proposer = to; p.Action = p.Action with { By = to }; }
            if (p.Votes.Remove(from, out var v)) p.Votes[to] = v;
        }
        for (int i = 0; i < _pacts.Count; i++) _pacts[i] = Pact.Of(R(_pacts[i].A), R(_pacts[i].B), _pacts[i].Mechanic);
        for (int i = 0; i < _offers.Count; i++) _offers[i] = new PactOffer(R(_offers[i].From), R(_offers[i].To), _offers[i].Mechanic);
    }
}
