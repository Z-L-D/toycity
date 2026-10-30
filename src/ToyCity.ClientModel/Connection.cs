using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// The WebSocket connection to a ToyCity server, reconnecting with back-off. Messages arrive on a background task
/// and wait in a queue; the game loop drains them with <see cref="Poll"/> so all state changes happen on its thread.
/// A server that wants players to sign in says so in its Welcome. The connection then signs in with
/// <see cref="Credentials"/> if it has them, and after a sign-in keeps the token, so a reconnect signs in again
/// by itself. Without credentials (or when they are refused) <see cref="SignInNeeded"/> is set for the UI to ask.
/// </summary>
public sealed class Connection : IDisposable
{
    readonly Uri _uri;
    /// <summary>The server this connects to.</summary>
    public Uri Uri => _uri;
    readonly string _clientName;
    readonly ConcurrentQueue<ServerMessage> _inbox = new();
    readonly CancellationTokenSource _stop = new();
    ClientWebSocket? _socket;
    uint _seq = 1;

    public bool Connected => _socket?.State == WebSocketState.Open;

    /// <summary>What to sign in with when a server asks. After a sign-in it holds the token the server gave.</summary>
    public SignIn? Credentials { get; set; }
    /// <summary>Who this connection plays as, once the server has said (at once in solo play, after SignedIn otherwise).</summary>
    public string Player { get; private set; } = "";
    public bool Admin { get; private set; }
    /// <summary>The server asked for a sign-in this connection can't make by itself.</summary>
    public bool SignInNeeded { get; private set; }
    /// <summary>The server's last Welcome.</summary>
    public Welcome? Welcome { get; private set; }
    /// <summary>Why the last sign-in failed, until the next one.</summary>
    public string SignInError { get; private set; } = "";
    uint _signInSeq;
    /// <summary>Raised on the polling thread when the connection opens or drops.</summary>
    public event Action<bool>? StatusChanged;
    bool _reportedOpen;

    public Connection(Uri uri, string clientName = "toycity-godot", SignIn? credentials = null)
    {
        _uri = uri;
        _clientName = clientName;
        Credentials = credentials;
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
                    var m = ServerMessage.Parser.ParseFrom(ms.GetBuffer(), 0, (int)ms.Length);
                    ms.SetLength(0);
                    Track(m);
                    _inbox.Enqueue(m);
                }
            }
            catch (Exception) when (!_stop.IsCancellationRequested) { }
            catch (OperationCanceledException) { }
            finally
            {
                _socket = null;
                Player = "";
                ws.Dispose();
            }
            if (_stop.IsCancellationRequested) break;
            try { await Task.Delay(retry, _stop.Token); } catch (OperationCanceledException) { break; }
            retry = Math.Min(retry * 2, 8000);
        }
    }

    /// <summary>Follows the sign-in on the receiving task, so a reconnect signs in before the UI's next frame.</summary>
    void Track(ServerMessage m)
    {
        switch (m.BodyCase)
        {
            case ServerMessage.BodyOneofCase.Welcome:
                Welcome = m.Welcome;
                Player = m.Welcome.Player;
                Admin = m.Welcome.Admin;
                SignInNeeded = false;
                if (!m.Welcome.SignInRequired || Player.Length > 0) break;
                if (Credentials != null) SendSignIn(Credentials);
                else SignInNeeded = true;
                break;
            case ServerMessage.BodyOneofCase.SignedIn:
                Player = m.SignedIn.Name;
                Admin = m.SignedIn.Admin;
                Credentials = new SignIn { Token = m.SignedIn.Token };
                SignInNeeded = false;
                SignInError = "";
                break;
            case ServerMessage.BodyOneofCase.Error when m.ReplyTo != 0 && m.ReplyTo == _signInSeq:
                SignInError = m.Error.Message;
                // Refused credentials are dropped, so a reconnect doesn't try them again.
                Credentials = null;
                SignInNeeded = true;
                break;
        }
    }

    /// <summary>Signs in with a name and password, or makes the account first when create is set.</summary>
    public void SignIn(string name, string password, bool create = false, string inviteCode = "") =>
        SendSignIn(new SignIn { Name = name, Password = password, Create = create, InviteCode = inviteCode });

    void SendSignIn(SignIn s)
    {
        SignInNeeded = false;
        _signInSeq = Send(new ClientMessage { SignIn = s.Clone() });
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
