using System.Text.Json;
using Godot;
using ToyCity.ClientModel;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// Joining a server: shown when a dedicated server wants a name and password, or when the server can't be reached,
/// with the address to change. Tokens are kept per server in user://tokens.json so the next start signs in by
/// itself; the last server joined is remembered in user://server.txt.
/// </summary>
public partial class SignInPanel : CanvasLayer
{
    public const string ServerFile = "user://server.txt";
    const string TokenFile = "user://tokens.json";

    readonly CityClient _client;
    readonly string _server;
    LineEdit? _address, _name, _password, _invite;
    Label? _title, _error;
    Control? _account;
    Label? _inviteLabel;
    double _offline;
    string _savedToken = "";

    /// <summary>Called with a new address: the client is started again pointing there.</summary>
    public event Action<string>? Reconnect;

    public SignInPanel(CityClient client, string server)
    {
        _client = client;
        _server = server;
    }

    /// <summary>The token kept for a server from an earlier sign-in, if any.</summary>
    public static SignIn? StoredCredentials(string server) =>
        Tokens().TryGetValue(server, out var t) && t.Length > 0 ? new SignIn { Token = t } : null;

    static Dictionary<string, string> Tokens()
    {
        try
        {
            if (global::Godot.FileAccess.FileExists(TokenFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(global::Godot.FileAccess.GetFileAsString(TokenFile)) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    static void StoreToken(string server, string token)
    {
        var all = Tokens();
        if (token.Length > 0) all[server] = token; else all.Remove(server);
        using var f = global::Godot.FileAccess.Open(TokenFile, global::Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(all));
    }

    public override void _Ready()
    {
        Layer = 7;
        Visible = false;
        var centre = new CenterContainer();
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(420, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Floating());
        centre.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(_title);
        _error = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _error.AddThemeColorOverride("font_color", UiTheme.Bad);
        box.AddChild(_error);

        box.AddChild(new Label { Text = "Server" });
        var row = new HBoxContainer();
        box.AddChild(row);
        _address = new LineEdit { Text = _server, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(_address);
        var connect = new Button { Text = "Connect" };
        connect.Pressed += () =>
        {
            string a = _address.Text.Trim();
            if (a.Length > 0 && a != _server) Reconnect?.Invoke(a);
        };
        row.AddChild(connect);

        var account = new VBoxContainer();
        _account = account;
        box.AddChild(account);
        account.AddChild(new Label { Text = "Name" });
        account.AddChild(_name = new LineEdit { MaxLength = 24 });
        account.AddChild(new Label { Text = "Password" });
        account.AddChild(_password = new LineEdit { Secret = true });
        account.AddChild(_inviteLabel = new Label { Text = "Invite code (only for a new account)" });
        account.AddChild(_invite = new LineEdit());
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        account.AddChild(buttons);
        var create = new Button { Text = "Make an account" };
        create.Pressed += () => Send(create: true);
        buttons.AddChild(create);
        var signIn = new Button { Text = "Sign in" };
        signIn.Pressed += () => Send(create: false);
        buttons.AddChild(signIn);
        _password.TextSubmitted += _ => Send(create: false);
    }

    void Send(bool create)
    {
        string name = _name!.Text.Trim();
        if (name.Length == 0 || _password!.Text.Length == 0) { _error!.Text = "Type a name and a password."; return; }
        _error!.Text = "";
        _client.Connection.SignIn(name, _password.Text, create, _invite!.Text.Trim());
    }

    public override void _Process(double delta)
    {
        var conn = _client.Connection;
        // Keep the token the server gave, or forget one it refused.
        string token = conn.Credentials?.Token ?? "";
        if (token != _savedToken) { _savedToken = token; StoreToken(_server, token); }

        _offline = conn.Connected ? 0 : _offline + delta;
        bool unreachable = _offline > 5;
        bool show = unreachable || conn.SignInNeeded;
        if (show && !Visible) _error!.Text = conn.SignInError;
        Visible = show;
        if (!show) return;
        _title!.Text = unreachable ? "Can't reach the server" : $"Sign in to {conn.Welcome?.ServerName ?? "this server"}";
        if (unreachable) _error!.Text = $"Still trying {_server}. Check the address, or start the server.";
        else if (conn.SignInError.Length > 0) _error!.Text = conn.SignInError;
        _account!.Visible = !unreachable;
        bool invite = conn.Welcome?.InviteRequired == true;
        _inviteLabel!.Visible = _invite!.Visible = invite;
    }
}
