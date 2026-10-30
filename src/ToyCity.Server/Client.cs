using System.Net.WebSockets;
using ToyCity.Protocol;

namespace ToyCity.Server;

/// <summary>One connection: who it plays as, and which of the region's cities it is in.</summary>
public sealed class Client
{
    public required WebSocket Socket;
    public readonly DeltaTracker Tracker = new();
    public readonly SemaphoreSlim SendLock = new(1, 1);
    /// <summary>Who this connection plays as; empty until signed in.</summary>
    public string Player = "";
    public bool Admin;
    public int SignInFailures;
    public int Malformed;
    public readonly RateLimiter Limiter = new(burst: 200, perSecond: 60);
    /// <summary>Set while a forecast runs for this client: one at a time.</summary>
    public int Forecasting;
    /// <summary>The city this connection is in; null before it joins one.</summary>
    public SimHost? Host;
    /// <summary>
    /// A player on a tile host, as the region host sees them: their region-wide messages arrive over the link and its
    /// answers go back the same way. Null for a connection of this server's own.
    /// </summary>
    public Func<ToyCity.Protocol.V0.ServerMessage, Task>? Relay;
    /// <summary>The tile a relayed player is in (a tile host's city); for this server's own, their host's tile.</summary>
    public ToyCity.Sim.RegionTile? RemoteTile;
    public ToyCity.Sim.RegionTile? Tile => Host?.Tile ?? RemoteTile;
    /// <summary>A tile host's number for this connection, so the region host's answers find it.</summary>
    public int Conn;
    public bool SignedIn => Player.Length > 0;
}
