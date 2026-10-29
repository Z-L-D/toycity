using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToyCity.Server;

/// <summary>Who may make an account on a dedicated server.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JoinPolicy>))]
public enum JoinPolicy { Open, Invite, List }

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

    /// <summary>How long after a seat owner's last connection drops the computer takes their seats, at the level they chose. 0 never.</summary>
    public int AwayAfterSeconds { get; set; } = 120;

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
