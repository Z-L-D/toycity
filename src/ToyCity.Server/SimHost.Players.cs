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

    /// <summary>Online anywhere in the region: a mayor looking at a neighbour's city hasn't left their own.</summary>
    bool Online(string player) => _region.Online(player);

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
        if (_accounts.Exists(owner) || World.IsComputer(owner)) return;
        string? to = admin ?? _host.Admins.Select(a => _accounts.Canonical(a)).FirstOrDefault(a => a != null);
        if (to == null) return;
        _world.Enqueue(new RenameOwnerAction(owner, to));
        _world.ApplyPending();
    }

    P.Presence Presence()
    {
        var p = new P.Presence();
        var names = _clients.Where(c => c.SignedIn).Select(c => c.Player)
            .Concat(_world.Seats.Select(s => s.Owner).Where(o => o.Length > 0)).Concat(_world.Deputies.Keys)
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var player = new P.Presence.Types.Player { Name = n, Online = Online(n), Admin = _host.Dedicated ? _host.IsAdmin(n) : n == LocalPlayer };
            player.Seats.AddRange(_world.Seats.Where(s => s.Owner == n).Select(s => s.Id));
            if (_world.Deputies.TryGetValue(n, out var dp))
                player.DeputyFor.AddRange(Enum.GetValues<Portfolio>().Where(x => x != Portfolio.None && (dp & x) != 0).Select(x => x.ToString().ToLowerInvariant()));
            player.Spectator = player.Online && player.Seats.Count == 0 && player.DeputyFor.Count == 0;
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

public sealed partial class SimHost
{
    /// <summary>
    /// A city has just been loaded or founded by an admin: it gets a real mayor, the host's minimums, and the consent
    /// settings of everyone here. Caller holds the gate.
    /// </summary>
    internal void Arrived(string admin)
    {
        EnsureMayor(admin);
        ApplyHostRules();
        if (_host.Dedicated) foreach (var p in _clients.Select(c => c.Player).Distinct().ToList()) BringConsent(p);
    }

    /// <summary>The host's minimums, for the city that has just arrived (logged, so replays agree). Caller holds the gate.</summary>
    void ApplyHostRules()
    {
        var forbid = _host.Forbid.Select(n => Enum.TryParse<Mechanic>(n, ignoreCase: true, out var m) ? m : (Mechanic?)null)
            .Where(m => m != null).Select(m => m!.Value).Distinct().OrderBy(m => m).ToList();
        if (World.AllMechanics.All(m => _world.HostForbids(m) == forbid.Contains(m))) return;
        _world.Enqueue(new SetHostRulesAction(forbid));
        _world.ApplyPending();
    }

    /// <summary>
    /// A player's consent settings follow them between cities on this server: one who hasn't set anything in this city
    /// brings what they last set anywhere here. Caller holds the gate.
    /// </summary>
    void BringConsent(string player)
    {
        if (_accounts?.ConsentOf(player) is not { } saved || _world.ConsentOf(player) != null) return;
        foreach (var m in World.AllMechanics)
        {
            bool allow = saved.Contains(m.ToString());
            if (allow != _world.Allows(player, m)) _world.Enqueue(new SetConsentAction(player, m, allow) { By = player });
        }
        if (_world.PendingCount > 0) _world.ApplyPending();
    }

    /// <summary>Remembers what a player allows, after they change it, for the next city they join. Caller holds the gate.</summary>
    void KeepConsent(string player)
    {
        if (_accounts != null && _world.ConsentOf(player) is { } set) _accounts.SetConsent(player, set.Select(m => m.ToString()));
    }
}
