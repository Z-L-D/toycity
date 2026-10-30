using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ToyCity.ClientModel.Tests;

/// <summary>
/// The real ToyCity server on a spare local port, with its saves in a temporary folder. Given a host.json it runs as
/// a dedicated server, where players sign in.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    readonly Process _process;
    readonly string _saves = Path.Combine(Path.GetTempPath(), "toycity-test-" + Guid.NewGuid().ToString("N"));
    public readonly Uri Ws;
    public readonly int Port;
    /// <summary>The server's save folder.</summary>
    public string Saves => _saves;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _output = new();
    /// <summary>What the server has printed, for a failing test to show.</summary>
    public string Output => string.Join("\n", _output.TakeLast(80));

    /// <summary>
    /// <paramref name="args"/> go on the server's command line, with {port} replaced by its own port (a tile host's
    /// --public address, say).
    /// </summary>
    public ServerProcess(string? hostJson = null, string args = "")
    {
        if (hostJson != null)
        {
            Directory.CreateDirectory(_saves);
            File.WriteAllText(Path.Combine(_saves, "host.json"), hostJson);
        }
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        string dll = Path.Combine(AppContext.BaseDirectory, "ToyCity.Server.dll");
        Port = port;
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{port}{(hostJson != null ? " --dedicated" : "")} {args.Replace("{port}", port.ToString())}")
        {
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["ToyCity__SaveDir"] = _saves;
        psi.Environment["ToyCity__ContentDir"] = FindContent();
        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.Enqueue(e.Data); };
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
        throw new TimeoutException("the server didn't start:\n" + Output);
    }

    static string FindContent()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content"))) return Path.Combine(d.FullName, "content");
        throw new DirectoryNotFoundException("content");
    }

    /// <summary>Stops the server at once, as a crash or a pulled plug would.</summary>
    public void Kill()
    {
        try { _process.Kill(true); _process.WaitForExit(5000); } catch { }
    }

    public void Dispose()
    {
        try { _process.Kill(true); _process.WaitForExit(5000); } catch { }
        try { Directory.Delete(_saves, true); } catch { }
    }
}
