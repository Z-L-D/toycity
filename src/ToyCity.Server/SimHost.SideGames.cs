using Google.Protobuf;
using Microsoft.Extensions.Logging;
using ToyCity.Protocol;
using ToyCity.Sim;
using ToyCity.Transport;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Server;

/// <summary>Side games hosted in this process: attached to every city, and their views pushed to clients when they change.</summary>
public sealed partial class SimHost
{
    /// <summary>The side games every city on this server runs, by catalogue id.</summary>
    readonly List<string> _sideGameIds = new() { TransportGame.Id };

    static SimHost() => SideGameCatalog.Register(TransportGame.Id, () => new TransportGame());
    readonly Dictionary<string, byte[]> _sentViews = new();
    string _viewOwners = "";

    /// <summary>
    /// Side games the host config puts on another process: registered in place of the in-process ones, so every city
    /// here (and every forecast copy) opens its own game on that host.
    /// </summary>
    void UseRemoteSideGames()
    {
        foreach (var r in _host.SideGames)
        {
            int colon = r.Address.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(r.Address[(colon + 1)..], out int port)) { _log.LogWarning("side game {Id}: address {Address} isn't host:port", r.Id, r.Address); continue; }
            string host = r.Address[..colon], id = r.Id;
            var deadline = TimeSpan.FromMilliseconds(Math.Clamp(r.DeadlineMs, 50, 60_000));
            SideGameCatalog.Register(id, () => new RemoteSideGame(host, port, id, deadline));
            if (!_sideGameIds.Contains(id)) _sideGameIds.Add(id);
            _log.LogInformation("side game {Id} runs on {Address}, deadline {Ms} ms", id, r.Address, deadline.TotalMilliseconds);
        }
    }

    /// <summary>Adds the side games this server hosts to a city that doesn't have them yet. Caller holds the gate.</summary>
    void AttachSideGames(World w)
    {
        bool any = false;
        foreach (var id in _sideGameIds)
            if (w.SideGame(id) == null && SideGameCatalog.Has(id)) { w.Enqueue(new AttachSideGameAction(id)); any = true; }
        if (any) w.ApplyPending();
    }

    /// <summary>
    /// Which view of a side game a player gets: the operator's own when they hold its seat (or nobody does), and the
    /// public one otherwise, so a rival sees the routes on the map but not the books, the standing or the bribes.
    /// </summary>
    string ViewSeatFor(string gameId, string player)
    {
        string owner = _world.SeatOf(gameId)?.Owner ?? "";
        return owner.Length == 0 || owner == player ? "operator" : "public";
    }

    /// <summary>
    /// Views that changed since they were last sent, each with the seat it is for. With all, every view, without
    /// marking them sent (for a newcomer). Caller holds the gate.
    /// </summary>
    List<(string Seat, P.ServerMessage View)> ChangedViews(bool all)
    {
        var list = new List<(string, P.ServerMessage)>();
        // When a seat changes hands its new owner needs the operator's view, and the old one the public view.
        string owners = string.Join(",", _world.Seats.Select(s => s.Owner));
        if (owners != _viewOwners) { _viewOwners = owners; _sentViews.Clear(); }
        foreach (var hg in _world.SideGames)
            foreach (var seat in new[] { "operator", "public" })
            {
                var bytes = hg.Game.View(seat);
                string key = hg.Id + "/" + seat;
                if (!all)
                {
                    if (_sentViews.TryGetValue(key, out var old) && old.AsSpan().SequenceEqual(bytes)) continue;
                    _sentViews[key] = bytes;
                }
                list.Add((seat, new P.ServerMessage
                {
                    SideGame = new P.SideGameView { Game = hg.Id, Name = hg.Game.Manifest.Name, Seat = seat, Payload = ByteString.CopyFrom(bytes), Status = hg.Last.Status },
                }));
            }
        return list;
    }

    /// <summary>The views among these that this player should get.</summary>
    IEnumerable<P.ServerMessage> ViewsFor(List<(string Seat, P.ServerMessage View)> views, string player) =>
        views.Where(v => v.Seat == ViewSeatFor(v.View.SideGame.Game, player)).Select(v => v.View);

    /// <summary>
    /// The civic state as this player may see it. Standing with the city is the operator's business: a rival sees
    /// none of it, and the mayor sees the ratings but not who has been caught bribing. Caller holds the gate.
    /// </summary>
    P.CivicState CivicFor(P.CivicState civic, string player)
    {
        P.CivicState? mine = null;
        for (int i = 0; i < civic.SideGames.Count; i++)
        {
            var g = civic.SideGames[i];
            string owner = _world.SeatOf(g.Id)?.Owner ?? "";
            if (owner.Length == 0 || owner == player || g.Standings.Count == 0) continue;
            mine ??= civic.Clone();
            var sg = mine.SideGames[i];
            if (player == _world.MayorSeat.Owner)
                foreach (var st in sg.Standings) st.BribeBanDays = 0;
            else
                sg.Standings.Clear();
        }
        return mine ?? civic;
    }
}
