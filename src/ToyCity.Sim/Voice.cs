using System.IO.Hashing;
using System.Text;
using System.Text.Json;

namespace ToyCity.Sim;

/// <summary>
/// The template voice layer. The sim decides what happens and hands over facts; the voice only words them. Variants
/// and names are picked from a hash of a seed the caller passes, never from the world's random numbers, so swapping
/// the voice (another language, or a language model later) cannot change the simulation.
/// </summary>
public sealed class Voice
{
    readonly Dictionary<string, List<string>> _templates = new();
    List<string> _first = new() { "Pat" }, _last = new() { "Smith" }, _streets = new() { "Main" }, _kinds = new() { "Street" };
    List<string> _districtFirst = new() { "Old" }, _districtKinds = new() { "Town" };

    readonly Dictionary<string, List<Quirk>> _quirks = new();

    public IReadOnlyList<string> StreetNames => _streets;
    public IReadOnlyList<string> StreetKinds => _kinds;

    /// <summary>A quirky statistic for a kind of building: so many <see cref="Unit"/> per person served, and how many of them came good.</summary>
    public sealed record Quirk(string Label, double Per, string Unit);

    /// <summary>The quirky statistics for a building category ("park", "education"), empty when it has none.</summary>
    public IReadOnlyList<Quirk> Quirks(string category) => _quirks.TryGetValue(category, out var q) ? q : Array.Empty<Quirk>();

    public IReadOnlyCollection<string> Keys => _templates.Keys;

    public static Voice Parse(string json)
    {
        var v = new Voice();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        List<string> Strings(string name) => root.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(e => e.GetString()!).ToList() : new();
        if (Strings("firstNames") is { Count: > 0 } f) v._first = f;
        if (Strings("lastNames") is { Count: > 0 } l) v._last = l;
        if (Strings("streetNames") is { Count: > 0 } s) v._streets = s;
        if (Strings("streetKinds") is { Count: > 0 } k) v._kinds = k;
        if (Strings("districtNames") is { Count: > 0 } dn) v._districtFirst = dn;
        if (Strings("districtKinds") is { Count: > 0 } dk) v._districtKinds = dk;
        if (root.TryGetProperty("quirks", out var qs))
            foreach (var cat in qs.EnumerateObject())
                v._quirks[cat.Name] = cat.Value.EnumerateArray()
                    .Select(q => new Quirk(q.GetProperty("label").GetString()!, q.GetProperty("per").GetDouble(), q.GetProperty("unit").GetString()!)).ToList();
        foreach (var t in root.GetProperty("templates").EnumerateObject())
            v._templates[t.Name] = t.Value.ValueKind == JsonValueKind.Array
                ? t.Value.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string> { t.Value.GetString()! };
        return v;
    }

    static ulong Hash(ulong seed, string salt) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(salt), (long)seed);

    static string Pick(List<string> list, ulong seed, string salt) => list[(int)(Hash(seed, salt) % (ulong)list.Count)];

    public bool Has(string key) => _templates.ContainsKey(key);

    /// <summary>A person's name, the same every time for the same seed.</summary>
    public string Name(ulong seed) => $"{Pick(_first, seed, "first")} {Pick(_last, seed, "last")}";

    /// <summary>
    /// A road's name from the line it runs along. North-south roads are numbered avenues by column (12th Avenue);
    /// east-west roads are named streets by row, every row a different name until the names run out.
    /// </summary>
    public string RoadName(bool northSouth, int line)
    {
        if (northSouth) return $"{Ordinal(line + 1)} Avenue";
        var kinds = _kinds.Where(k => k != "Avenue").DefaultIfEmpty("Street").ToList();
        int n = _streets.Count;
        int name = n == 1 ? 0 : Coprime(7, n) ? line * 7 % n : line % n;
        return $"{_streets[name]} {kinds[line / n % kinds.Count]}";
    }

    static bool Coprime(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a == 1; }

    static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>A district's name, the same every time for the same district number.</summary>
    public string District(int id)
    {
        ulong seed = (ulong)id * 2654435761UL + 17;
        return $"{Pick(_districtFirst, seed, "district")} {Pick(_districtKinds, seed, "districtKind")}";
    }

    /// <summary>Words a template. Unknown keys fall back to the key itself so a missing line is visible, not silent.</summary>
    public string Say(string key, ulong seed, IReadOnlyDictionary<string, string> facts)
    {
        if (!_templates.TryGetValue(key, out var list) || list.Count == 0) return key;
        var text = Pick(list, seed, key);
        var sb = new StringBuilder(text.Length + 32);
        for (int i = 0; i < text.Length; i++)
        {
            int close;
            if (text[i] == '{' && (close = text.IndexOf('}', i + 1)) > i)
            {
                var name = text.Substring(i + 1, close - i - 1);
                if (facts.TryGetValue(name, out var val)) { sb.Append(val); i = close; continue; }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}
