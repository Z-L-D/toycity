using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToyCity.Server;

/// <summary>One player's account on this server. The password is kept only as a salted PBKDF2 hash.</summary>
public sealed class Account
{
    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime Created { get; set; }
    /// <summary>Sign-in tokens, as SHA-256 hashes, with when each stops working.</summary>
    public List<AccountToken> Tokens { get; set; } = new();
}

public sealed class AccountToken
{
    public string Hash { get; set; } = "";
    public DateTime Expires { get; set; }
}

/// <summary>What a sign-in came to: the account's name and a fresh token, or why not.</summary>
public sealed record SignInResult(string? Name, string? Token, string? Error)
{
    public bool Ok => Name != null;
}

/// <summary>
/// The accounts this server keeps, in one JSON file next to its saves. Nothing outside the server is involved, so a
/// self-hosted or offline server works the same. Names are unique ignoring case; the first spelling is kept.
/// </summary>
public sealed partial class AccountStore
{
    const int Iterations = 100_000;
    static readonly TimeSpan TokenLife = TimeSpan.FromDays(30);
    const int MaxTokensPerAccount = 8;

    readonly string _path;
    readonly object _lock = new();
    readonly Dictionary<string, Account> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<DateTime> _now;

    public AccountStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.UtcNow);
        if (File.Exists(path))
            foreach (var a in JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(path)) ?? new())
                _byName[a.Name] = a;
    }

    public int Count { get { lock (_lock) return _byName.Count; } }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _-]{1,23}$")]
    private static partial Regex NameRule();

    /// <summary>Why a name can't be used for an account, or null if it can.</summary>
    public static string? CheckName(string name) =>
        NameRule().IsMatch(name) && name == name.Trim() ? null : "Names are 2 to 24 letters, numbers, spaces, - and _, starting with a letter or number.";

    public SignInResult Create(string name, string password)
    {
        if (CheckName(name) is { } bad) return new(null, null, bad);
        if (password.Length < 6) return new(null, null, "Passwords need at least 6 characters.");
        lock (_lock)
        {
            if (_byName.ContainsKey(name)) return new(null, null, $"There is already a player called {name} here.");
            var salt = RandomNumberGenerator.GetBytes(16);
            var a = new Account { Name = name, Salt = Convert.ToBase64String(salt), Hash = HashPassword(password, salt), Created = _now() };
            _byName[name] = a;
            return Issue(a);
        }
    }

    public SignInResult SignIn(string name, string password)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var a)) return new(null, null, "Wrong name or password.");
            var expected = Convert.FromBase64String(a.Hash);
            var got = Convert.FromBase64String(HashPassword(password, Convert.FromBase64String(a.Salt)));
            if (!CryptographicOperations.FixedTimeEquals(expected, got)) return new(null, null, "Wrong name or password.");
            return Issue(a);
        }
    }

    /// <summary>Signs in with a token from an earlier sign-in. The token is used up and a new one given.</summary>
    public SignInResult SignInWithToken(string token)
    {
        string hash = HashToken(token);
        lock (_lock)
        {
            var now = _now();
            foreach (var a in _byName.Values)
            {
                int i = a.Tokens.FindIndex(t => t.Hash == hash);
                if (i < 0) continue;
                bool live = a.Tokens[i].Expires > now;
                a.Tokens.RemoveAt(i);
                if (!live) { Save(); return new(null, null, "That sign-in has expired. Sign in with your password."); }
                return Issue(a);
            }
        }
        return new(null, null, "That sign-in isn't known here. Sign in with your password.");
    }

    public bool Exists(string name) { lock (_lock) return _byName.ContainsKey(name); }

    /// <summary>The account's name as first spelled, for a name in any case.</summary>
    public string? Canonical(string name) { lock (_lock) return _byName.TryGetValue(name, out var a) ? a.Name : null; }

    SignInResult Issue(Account a)
    {
        var now = _now();
        a.Tokens.RemoveAll(t => t.Expires <= now);
        if (a.Tokens.Count >= MaxTokensPerAccount) a.Tokens.RemoveRange(0, a.Tokens.Count - MaxTokensPerAccount + 1);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        a.Tokens.Add(new AccountToken { Hash = HashToken(token), Expires = now + TokenLife });
        Save();
        return new(a.Name, token, null);
    }

    static string HashPassword(string password, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32));

    static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Written to a temporary file and moved over the old one, so a crash never leaves half a file.</summary>
    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_byName.Values.OrderBy(a => a.Created).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
