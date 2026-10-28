using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ToyCity.ClientModel.Tests;

/// <summary>The real ToyCity server on a spare local port, with its saves in a temporary folder.</summary>
public sealed class ServerProcess : IDisposable
{
    readonly Process _process;
    readonly string _saves = Path.Combine(Path.GetTempPath(), "toycity-test-" + Guid.NewGuid().ToString("N"));
    public readonly Uri Ws;

    public ServerProcess()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        string dll = Path.Combine(AppContext.BaseDirectory, "ToyCity.Server.dll");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{port}")
        {
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["ToyCity__SaveDir"] = _saves;
        psi.Environment["ToyCity__ContentDir"] = FindContent();
        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Ws = new Uri($"ws://127.0.0.1:{port}/ws");
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try { if (http.GetAsync($"http://127.0.0.1:{port}/health").Result.IsSuccessStatusCode) return; } catch { }
            Thread.Sleep(200);
        }
        throw new TimeoutException("the server didn't start");
    }

    static string FindContent()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content"))) return Path.Combine(d.FullName, "content");
        throw new DirectoryNotFoundException("content");
    }

    public void Dispose()
    {
        try { _process.Kill(true); _process.WaitForExit(5000); } catch { }
        try { Directory.Delete(_saves, true); } catch { }
    }
}
