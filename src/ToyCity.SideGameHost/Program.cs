using System.Net;
using System.Net.Sockets;
using ToyCity.Contract;
using ToyCity.Emergency;
using ToyCity.Protocol;
using ToyCity.Transport;

// Hosts side games for cities on other processes or machines. A city server points at it from host.json:
//   "sideGames": [ { "id": "transport", "address": "127.0.0.1:5190", "deadlineMs": 2000 } ]
// Usage: ToyCity.SideGameHost [--port 5190] [--listen 0.0.0.0]

int port = 5190;
var bind = IPAddress.Loopback;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--listen") bind = IPAddress.Parse(args[i + 1]);
}

var games = new Dictionary<string, Func<ISideGame>>
{
    [TransportGame.Id] = () => new TransportGame(),
    [TransportGame.RivalId] = () => new TransportGame(TransportGame.RivalId, TransportGame.RivalName),
    [EmergencyGame.Id] = () => new EmergencyGame(),
};
var server = new SideGameServer(id => games.TryGetValue(id, out var f) ? f() : null, Console.WriteLine);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine($"Side-game host on {bind}:{port}: {string.Join(", ", games.Keys)}");
await server.ServeAsync(new TcpListener(bind, port), stop.Token);
