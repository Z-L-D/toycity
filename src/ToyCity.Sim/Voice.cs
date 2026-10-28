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

    /// <summary>A street name for a cell. Cells in the same 8×8 block share a name, so a neighbourhood stays recognisable.</summary>
    public string Street(int x, int y)
    {
        ulong seed = (ulong)(x / 8) * 7919 + (ulong)(y / 8) * 104729;
        return $"{Pick(_streets, seed, "street")} {Pick(_kinds, seed, "kind")}";
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
