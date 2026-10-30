using ToyCity.Protocol.V0;
using static ToyCity.ClientModel.Panels.Bb;

namespace ToyCity.ClientModel.Panels;

/// <summary>
/// The region map's screen, with no engine types: the latest <see cref="RegionState"/>, the tile picked, what the
/// player is waiting on, and what each link does. While <see cref="Active"/> it replaces the city's whole interface.
/// Links: rg-open, rg-close, rg-pick:COL:ROW, rg-go, rg-found, rg-starter, rg-window.
/// </summary>
public sealed class RegionModel
{
    /// <summary>Pixels along a side of a tile's picture (the server's RegionMapping.PictureSize).</summary>
    public const int PictureSize = 32;

    readonly Func<ClientMessage, uint> _send;
    readonly System.Action _changed;

    public RegionModel(Func<ClientMessage, uint> send, System.Action changed)
    {
        _send = send;
        _changed = changed;
    }

    public Func<string> Me { get; set; } = () => "player";

    public RegionState? State { get; private set; }
    public bool Active { get; private set; }
    public (int Col, int Row)? Picked { get; private set; }
    /// <summary>What the player asked for and is waiting on ("Opening Westbrook…"), shown from the first frame.</summary>
    public string Pending { get; private set; } = "";
    /// <summary>The name typed for a new city.</summary>
    public string NewName { get; set; } = "New City";
    public bool StarterTown { get; private set; } = true;
    /// <summary>Goes up when the pictures or the grid change, so the view rebuilds its tiles.</summary>
    public int MapVersion { get; private set; }

    /// <summary>Open a founded city in a window of its own: its column and row.</summary>
    public event Action<int, int>? OwnWindow;
    /// <summary>Asks the view to bring up the city screen again after a move.</summary>
    public event System.Action? Closed;

    public RegionTileState? TileAt(int col, int row) => State?.Tiles.FirstOrDefault(t => t.Col == col && t.Row == row);
    public RegionTileState? PickedTile => Picked is { } p ? TileAt(p.Col, p.Row) : null;
    public RegionTileState? Here => State is { } s ? TileAt(s.HereCol, s.HereRow) : null;

    /// <summary>True when this player is mayor of any city here: mayors may slow the region's one clock.</summary>
    public bool IsMayor => State?.Tiles.Any(t => t.Founded && t.Mayor == Me()) == true;

    /// <summary>Takes a message from the server, after the city mirror has applied it.</summary>
    public void Handle(ServerMessage m)
    {
        switch (m.BodyCase)
        {
            case ServerMessage.BodyOneofCase.Region:
                State = m.Region;
                Picked ??= (m.Region.HereCol, m.Region.HereRow);
                if (Pending.StartsWith("Founding") && PickedTile?.Founded == true) Pending = "";
                MapVersion++;
                _changed();
                break;
            case ServerMessage.BodyOneofCase.Full when Pending.StartsWith("Opening"):
                // The other city has arrived on this connection: the move is done.
                Pending = "";
                SetActive(false);
                break;
            case ServerMessage.BodyOneofCase.Error when Pending.Length > 0:
                Pending = "";
                _changed();
                break;
        }
    }

    public void SetActive(bool on)
    {
        if (Active == on) return;
        Active = on;
        if (on)
        {
            _send(new ClientMessage { Region = new RegionRequest() });
            if (State != null) Picked = (State.HereCol, State.HereRow);
        }
        else Closed?.Invoke();
        _changed();
    }

    public void Click(string meta)
    {
        var a = meta.Split(':');
        switch (a[0])
        {
            case "rg-open": SetActive(true); return;
            case "rg-close": SetActive(false); return;
            case "rg-pick" when a.Length == 3 && int.TryParse(a[1], out int c) && int.TryParse(a[2], out int r):
                Picked = (c, r);
                _changed();
                return;
            case "rg-go" when PickedTile is { Founded: true } t:
                if (State is { } s && t.Col == s.HereCol && t.Row == s.HereRow) { SetActive(false); return; }
                Pending = $"Opening {t.Name}…";
                _send(new ClientMessage { GoTo = new GoToCity { Col = t.Col, Row = t.Row } });
                _changed();
                return;
            case "rg-found" when PickedTile is { Founded: false } t:
            {
                string name = string.IsNullOrWhiteSpace(NewName) ? "New City" : NewName.Trim();
                Pending = $"Founding {name}…";
                _send(new ClientMessage { FoundCity = new FoundCity { Col = t.Col, Row = t.Row, Name = name, StarterTown = StarterTown } });
                _changed();
                return;
            }
            case "rg-starter": StarterTown = !StarterTown; _changed(); return;
            case "rg-window" when PickedTile is { Founded: true } t: OwnWindow?.Invoke(t.Col, t.Row); return;
        }
    }

    static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    static readonly string[] SpeedNames = { "paused", "slow", "normal", "fast" };

    /// <summary>The line across the top of the screen: the region's name, date and size, and who holds the clock.</summary>
    public string Title()
    {
        if (State is not { } s) return "Region";
        string held = s.SpeedSetBy.Length > 0 ? $" · held at {SpeedNames[Math.Clamp(s.Speed, 0, 3)]} by {s.SpeedSetBy}" : "";
        return $"{s.Name}   {Months[(int)(s.Tick / 30 % 12)]} {2000 + s.Tick / 360} · {s.Cols} × {s.Rows} tiles{held}";
    }

    /// <summary>The side of the screen: the region's totals and the picked tile, with what can be done there.</summary>
    public string Render()
    {
        if (State is not { } s) return Dim("Loading the region…");
        var sb = new System.Text.StringBuilder();
        int cities = s.Tiles.Count(t => t.Founded);
        long pop = s.Tiles.Sum(t => (long)t.Population);
        sb.Append(Dim($"{Plural(cities, "city").Replace("citys", "cities")} · {Num(pop)} people in the region")).Append("\n\n");
        if (PickedTile is { } p)
        {
            bool here = p.Col == s.HereCol && p.Row == s.HereRow;
            if (p.Founded)
            {
                sb.Append(H3(Esc(p.Name)));
                sb.Append(Table(new[]
                {
                    ("Mayor", Esc(p.Mayor.Length > 0 ? p.Mayor : "nobody")),
                    ("Population", Num(p.Population)),
                    ("Treasury", Money(p.Funds)),
                    ("Here now", p.Players.Count > 0 ? Esc(string.Join(", ", p.Players)) : Dim("nobody")),
                }));
                sb.Append('\n');
                sb.Append(here ? Dim("You are here. ") + Link("rg-close", "Back to the city") : Link("rg-go", $"Open {Esc(p.Name)}"));
                sb.Append("   ").Append(Link("rg-window", "⧉ Own window"));
            }
            else
            {
                sb.Append(H3("Open land"));
                sb.Append(Dim($"Tile {p.Col + 1}, {p.Row + 1}. Nobody has built here yet. Found a city and you'll be its mayor.")).Append("\n\n");
                sb.Append(Link("rg-starter", (StarterTown ? "☑" : "☐") + " Start with a small working town")).Append("\n\n");
                sb.Append(Link("rg-found", "Found a city here"));
            }
        }
        if (Pending.Length > 0) sb.Append("\n\n").Append(Col(Warn, "⏳ " + Esc(Pending)));
        return sb.ToString();
    }
}
