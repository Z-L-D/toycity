using Google.Protobuf;
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

    /// <summary>Adds the side games this server hosts to a city that doesn't have them yet. Caller holds the gate.</summary>
    void AttachSideGames(World w)
    {
        bool any = false;
        foreach (var id in _sideGameIds)
            if (w.SideGame(id) == null && SideGameCatalog.Has(id)) { w.Enqueue(new AttachSideGameAction(id)); any = true; }
        if (any) w.ApplyPending();
    }

    /// <summary>Views that changed since they were last sent, one message each. Caller holds the gate.</summary>
    List<P.ServerMessage> ChangedViews(bool all)
    {
        var list = new List<P.ServerMessage>();
        foreach (var hg in _world.SideGames)
        {
            const string seat = "operator";
            var bytes = hg.Game.View(seat);
            string key = hg.Id + "/" + seat;
            if (!all && _sentViews.TryGetValue(key, out var old) && old.AsSpan().SequenceEqual(bytes)) continue;
            _sentViews[key] = bytes;
            list.Add(new P.ServerMessage
            {
                SideGame = new P.SideGameView { Game = hg.Id, Name = hg.Game.Manifest.Name, Seat = seat, Payload = ByteString.CopyFrom(bytes), Status = hg.Last.Status },
            });
        }
        return list;
    }
}
