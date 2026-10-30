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
    public bool SignedIn => Player.Length > 0;
}
