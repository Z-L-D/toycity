using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>A dedicated server whose bus company runs in a separate side-game host process.</summary>
public class SideGameHostTests
{
    [Fact]
    public void TheServerRunsItsBusCompanyOnTheHostAndPlayersSeeIt()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        string dll = Path.Combine(AppContext.BaseDirectory, "ToyCity.SideGameHost.dll");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --port {port}") { RedirectStandardOutput = true, UseShellExecute = false };
        using var hostProcess = Process.Start(psi)!;
        var ready = new ManualResetEventSlim();
        var lines = new List<string>();
        hostProcess.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (lines) lines.Add(e.Data); if (e.Data.StartsWith("Side-game host on")) ready.Set(); };
        hostProcess.BeginOutputReadLine();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "the side-game host didn't start");
            using var server = new ServerProcess($$"""{ "name": "Remote buses", "join": "open", "sideGames": [ { "id": "transport", "address": "127.0.0.1:{{port}}", "deadlineMs": 5000 } ] }""");
            using var client = new CityClient(server.Ws, "test");
            SideGameView? view = null;
            client.Message += m => { if (m.BodyCase == ServerMessage.BodyOneofCase.SideGame) view = m.SideGame; };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!client.Connection.SignInNeeded && DateTime.UtcNow < deadline) { client.Poll(); Thread.Sleep(10); }
            client.Connection.SignIn("Remy", "secret1", create: true);
            while ((view == null || !client.City.Loaded) && DateTime.UtcNow < deadline) { client.Poll(); Thread.Sleep(10); }
            Assert.NotNull(view);
            Assert.Equal("transport", view!.Game);
            var v = Transport.Wire.View.Parser.ParseFrom(view.Payload);
            Assert.False(string.IsNullOrEmpty(v.Name));
            lock (lines) Assert.Contains(lines, l => l.Contains("a city opened transport"));
        }
        finally
        {
            try { hostProcess.Kill(true); hostProcess.WaitForExit(5000); } catch { }
        }
    }
}
