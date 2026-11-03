using System.Text.Json;

namespace ToyCity.ClientModel.Tools;

/// <summary>
/// The UI's icons (Beta 4, part 2): 24 × 24 stroke drawings from content/ui-icons.json, the same set the web client
/// draws. Each comes out as a whole SVG in a colour, for an engine to rasterise.
/// </summary>
public static class UiIcons
{
    static readonly Dictionary<string, string> Bodies = Load();

    static Dictionary<string, string> Load()
    {
        using var s = typeof(UiIcons).Assembly.GetManifestResourceStream("ui-icons.json");
        if (s == null) return new();
        var all = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        all.Remove("_about");
        return all;
    }

    public static IReadOnlyCollection<string> Names => Bodies.Keys;

    public static bool Has(string name) => Bodies.ContainsKey(name);

    /// <summary>An icon as a whole SVG document, stroked in a colour ("#e6e9ef").</summary>
    public static string Svg(string name, string colour = "#e6e9ef", double strokeWidth = 1.8)
    {
        string body = Bodies.TryGetValue(name, out var b) ? b : Bodies.GetValueOrDefault("civic", "");
        return "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' width='24' height='24' fill='none' " +
               $"stroke='{colour}' stroke-width='{strokeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}' stroke-linecap='round' stroke-linejoin='round'>{body}</svg>";
    }
}
