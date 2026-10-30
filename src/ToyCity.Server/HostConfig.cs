using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToyCity.Server;

/// <summary>Who may make an account on a dedicated server.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JoinPolicy>))]
public enum JoinPolicy { Open, Invite, List }

/// <summary>A side game hosted elsewhere: its id, where its host listens, and how long a day or a command may take it.</summary>
public sealed class RemoteGame
{
    public string Id { get; set; } = "";
    public string Address { get; set; } = "127.0.0.1:5190";
    public int DeadlineMs { get; set; } = 2000;
}

/// <summary>
/// How a dedicated server is run, from host.json beside its saves (written with the defaults on first start, for the
/// host to edit). Solo play doesn't use it: the one local player is signed in automatically and is the admin.
/// </summary>
public sealed class HostConfig
{
    /// <summary>Shown to players when they connect.</summary>
    public string Name { get; set; } = "ToyCity server";
    /// <summary>Open: anyone may make an account. Invite: they need the invite code. List: only the names in Allowed.</summary>
    public JoinPolicy Join { get; set; } = JoinPolicy.Open;
    public string InviteCode { get; set; } = "";
    public List<string> Allowed { get; set; } = new();
    /// <summary>Players who may save, load, start a new city and set the speed. When empty, the first account made becomes one.</summary>
    public List<string> Admins { get; set; } = new();

    /// <summary>Mechanics this server forbids in every city, whatever the players allow ("Bribery", "Sabotage", ...).</summary>
    public List<string> Forbid { get; set; } = new();

    /// <summary>How long after a seat owner's last connection drops the computer takes their seats, at the level they chose. 0 never.</summary>
    public int AwayAfterSeconds { get; set; } = 120;

    /// <summary>Side games run on another process or machine (see ToyCity.SideGameHost) instead of in this one.</summary>
    public List<RemoteGame> SideGames { get; set; } = new();

    /// <summary>
    /// Federated regions (part 8): the key a tile host must give to run one of this region's cities. Empty: no tile
    /// hosts may join.
    /// </summary>
    public string LinkKey { get; set; } = "";
    /// <summary>How long the region holds for a tile host that stops answering before a computer mayor runs its city here.</summary>
    public int TakeOverSeconds { get; set; } = 120;
    /// <summary>Where players reach this server's /ws (ws://host:port/ws), for redirects from tile hosts. Optional.</summary>
    public string PublicAddress { get; set; } = "";

    /// <summary>Milliseconds a day takes at the fastest speed. 833 by default (a year in five minutes); scripted runs go faster.</summary>
    public int FastDayMs { get; set; } = 833;

    [JsonIgnore] public bool Dedicated { get; set; }
    [JsonIgnore] public string? Path { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The solo setting: no sign-in, one local admin.</summary>
    public static HostConfig Solo() => new() { Name = "ToyCity" };

    /// <summary>Reads host.json, writing it with the defaults first if it isn't there.</summary>
    public static HostConfig LoadOrCreate(string path)
    {
        HostConfig c;
        if (File.Exists(path)) c = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), Options) ?? new();
        else
        {
            c = new();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(c, Options));
        }
        c.Dedicated = true;
        c.Path = path;
        return c;
    }

    public bool IsAdmin(string name) => Admins.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes the first account the admin when the host named nobody, and writes that down.</summary>
    public void PromoteFirst(string name)
    {
        if (Admins.Count > 0) return;
        Admins.Add(name);
        if (Path != null) File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>Why this name can't make an account here, or null if it can.</summary>
    public string? MayCreate(string name, string inviteCode) => Join switch
    {
        JoinPolicy.Invite when inviteCode != InviteCode || InviteCode.Length == 0 => "This server needs an invite code to join. Ask its host.",
        JoinPolicy.List when !Allowed.Contains(name, StringComparer.OrdinalIgnoreCase) => "This server only lets in players its host has listed.",
        _ => null,
    };
}
