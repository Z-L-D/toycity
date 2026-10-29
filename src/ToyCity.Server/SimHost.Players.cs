using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>Who is here: presence, the seats of players who've gone, and who the mayor is on a dedicated server.</summary>
public sealed partial class SimHost
{
    /// <summary>When each seat owner's last connection dropped, by name, until the computer takes their seats.</summary>
    readonly Dictionary<string, long> _leftAt = new(StringComparer.Ordinal);
    string _presenceSig = "";
    Timer? _awayTimer;

    void StartAwayTimer()
    {
        if (_host.Dedicated && _host.AwayAfterSeconds > 0)
            _awayTimer = new Timer(_ => CheckAway(), null, 1000, 1000);
    }

    bool Online(string player) => _clients.Any(c => c.Player == player);

    /// <summary>A player's connection closed. Caller holds the gate.</summary>
    void Left(Client c)
    {
        if (!c.SignedIn || Online(c.Player)) return;
        if (_world.Seats.Any(s => s.Owner == c.Player && s.Level == ToyCity.Contract.OperatorLevel.Manual)) _leftAt[c.Player] = _clock.ElapsedMilliseconds;
    }

    /// <summary>Hands the seats of players gone longer than the host allows to the computer, at each seat's away level.</summary>
    void CheckAway()
    {
        List<(Client, P.ServerMessage)>? outbox = null;
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            foreach (var (player, at) in _leftAt.ToList())
            {
                if (Online(player)) { _leftAt.Remove(player); continue; }
                if (now - at < _host.AwayAfterSeconds * 1000L) continue;
                _leftAt.Remove(player);
                foreach (var s in _world.Seats.Where(s => s.Owner == player && s.Level == ToyCity.Contract.OperatorLevel.Manual).ToList())
                    _world.Enqueue(new SeatAwayAction(s.Id, true) { By = player });
                _log.LogInformation("{Player} has been away {Seconds} s: the computer has their seats", player, _host.AwayAfterSeconds);
            }
            if (_world.PendingCount > 0)
            {
                _world.ApplyPending();
                outbox = BuildDeltas(forceAlerts: true);
            }
        }
        if (outbox != null) foreach (var (c, m) in outbox) _ = SendAsync(c, m);
    }

    /// <summary>A player is back: seats the computer took when they left are theirs again. Caller holds the gate.</summary>
    void Returned(string player)
    {
        _leftAt.Remove(player);
        foreach (var s in _world.Seats.Where(s => s.Owner == player && s.AutoAway).ToList())
            _world.Enqueue(new SeatAwayAction(s.Id, false) { By = player });
        if (_world.PendingCount > 0) _world.ApplyPending();
    }

    /// <summary>
    /// On a dedicated server the mayor must be a real player. A city that arrives with the solo "player" (or anyone
    /// without an account here) as mayor goes to the given admin, or the first admin with an account. Caller holds the gate.
    /// </summary>
    void EnsureMayor(string? admin = null)
    {
        if (!_host.Dedicated || _accounts == null) return;
        string owner = _world.MayorSeat.Owner;
        if (_accounts.Exists(owner)) return;
        string? to = admin ?? _host.Admins.Select(a => _accounts.Canonical(a)).FirstOrDefault(a => a != null);
        if (to != null) _world.RenameOwner(owner, to);
    }

    P.Presence Presence()
    {
        var p = new P.Presence();
        var names = _clients.Where(c => c.SignedIn).Select(c => c.Player)
            .Concat(_world.Seats.Select(s => s.Owner).Where(o => o.Length > 0))
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var player = new P.Presence.Types.Player { Name = n, Online = Online(n), Admin = _host.Dedicated ? _host.IsAdmin(n) : n == LocalPlayer };
            player.Seats.AddRange(_world.Seats.Where(s => s.Owner == n).Select(s => s.Id));
            player.Spectator = player.Online && player.Seats.Count == 0;
            p.Players.Add(player);
        }
        return p;
    }

    /// <summary>The presence message for everyone, when it has changed. Caller holds the gate.</summary>
    IEnumerable<(Client, P.ServerMessage)> PresenceIfChanged()
    {
        var p = Presence();
        string sig = p.ToString();
        if (sig == _presenceSig) yield break;
        _presenceSig = sig;
        foreach (var c in _clients) yield return (c, new P.ServerMessage { Presence = p });
    }
}
