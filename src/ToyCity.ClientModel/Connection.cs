using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// The WebSocket connection to a ToyCity server, reconnecting with back-off. Messages arrive on a background task
/// and wait in a queue; the game loop drains them with <see cref="Poll"/> so all state changes happen on its thread.
/// </summary>
public sealed class Connection : IDisposable
{
    readonly Uri _uri;
    readonly string _clientName;
    readonly ConcurrentQueue<ServerMessage> _inbox = new();
    readonly CancellationTokenSource _stop = new();
    ClientWebSocket? _socket;
    uint _seq = 1;

    public bool Connected => _socket?.State == WebSocketState.Open;
    /// <summary>Raised on the polling thread when the connection opens or drops.</summary>
    public event Action<bool>? StatusChanged;
    bool _reportedOpen;

    public Connection(Uri uri, string clientName = "toycity-godot")
    {
        _uri = uri;
        _clientName = clientName;
        _ = Task.Run(RunAsync);
    }

    async Task RunAsync()
    {
        int retry = 500;
        while (!_stop.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(_uri, _stop.Token);
                _socket = ws;
                retry = 500;
                Send(new ClientMessage { Hello = new Hello { ClientName = _clientName, ProtocolVersion = 0 } });
                var buffer = new byte[1 << 16];
                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, _stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    _inbox.Enqueue(ServerMessage.Parser.ParseFrom(ms.GetBuffer(), 0, (int)ms.Length));
                    ms.SetLength(0);
                }
            }
            catch (Exception) when (!_stop.IsCancellationRequested) { }
            catch (OperationCanceledException) { }
            finally
            {
                _socket = null;
                ws.Dispose();
            }
            if (_stop.IsCancellationRequested) break;
            try { await Task.Delay(retry, _stop.Token); } catch (OperationCanceledException) { break; }
            retry = Math.Min(retry * 2, 8000);
        }
    }

    readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Sends a message, stamping its sequence number, which it returns. Dropped while disconnected.</summary>
    public uint Send(ClientMessage m)
    {
        m.Seq = Interlocked.Increment(ref _seq) - 1;
        var ws = _socket;
        if (ws?.State != WebSocketState.Open) return m.Seq;
        var bytes = m.ToByteArray();
        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync();
            try { await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, _stop.Token); }
            catch (Exception) { }
            finally { _sendLock.Release(); }
        });
        return m.Seq;
    }

    public uint Act(Protocol.V0.Action a) => Send(new ClientMessage { Action = a });

    /// <summary>Hands queued messages to the handler, on the caller's thread.</summary>
    public void Poll(Action<ServerMessage> handle)
    {
        bool open = Connected;
        if (open != _reportedOpen) { _reportedOpen = open; StatusChanged?.Invoke(open); }
        while (_inbox.TryDequeue(out var m)) handle(m);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket?.Abort();
    }
}
