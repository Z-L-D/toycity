using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// The client's root: connects to the server, keeps the city mirror current and hands it to the views.
/// Command line (after "--"): --server ws://host:port/ws, and --smoke to connect, check the city loads and quit.
/// </summary>
public partial class Main : Node
{
    CityClient? _client;
    MapView? _map;
    Label? _status;
    bool _smoke;
    double _smokeTime;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        string server = "ws://localhost:5180/ws";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            if (args[i] == "--smoke") _smoke = true;
        }
        _client = new CityClient(new Uri(server));
        _client.Connection.StatusChanged += open => SetStatus(open ? "Connected" : $"Connecting to {server}…");

        _map = new MapView(_client.City);
        AddChild(_map);
        var hud = new CanvasLayer();
        AddChild(hud);
        _status = new Label { Text = $"Connecting to {server}…", Position = new Vector2(12, 8) };
        hud.AddChild(_status);
    }

    void SetStatus(string text) { if (_status != null) _status.Text = text; }

    public override void _Process(double delta)
    {
        _client!.Poll();
        var c = _client.City;
        if (c.Loaded) SetStatus($"{c.Stats.CityName}  ·  {c.Stats.Year}-{c.Stats.Month:00}-{c.Stats.Day:00}  ·  pop {c.Stats.Population:N0}  ·  ${c.Stats.Funds:N0}");
        if (!_smoke) return;
        _smokeTime += delta;
        if (c.Loaded && _map!.Drawn)
        {
            GD.Print($"SMOKE OK {c.Width}x{c.Height} buildings={c.Buildings.Count} tick={c.Stats.Tick}");
            GetTree().Quit(0);
        }
        else if (_smokeTime > 30)
        {
            GD.PrintErr("SMOKE FAILED: no city within 30 s");
            GetTree().Quit(1);
        }
    }

    public override void _ExitTree() => _client?.Dispose();
}
