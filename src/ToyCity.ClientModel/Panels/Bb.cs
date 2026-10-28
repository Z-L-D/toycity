using System.Globalization;

namespace ToyCity.ClientModel.Panels;

/// <summary>Writing Godot's BBCode for the panels: escaping, links, colours and the small text charts.</summary>
public static class Bb
{
    public const string Pos = "#86e08f", Neg = "#ff8a7a", Muted = "#9aa4b2", Warn = "#ffcc66", Gold = "#ffd15a", Blue = "#5ad1ff";
    public const string R = "#4caf50", C = "#4a80e0", I = "#e0b030";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Text as it is: only an opening bracket can start a tag.</summary>
    public static string Esc(string? s) => (s ?? "").Replace("[", "[lb]");
    public static string Link(string meta, string text) => $"[url={meta}]{text}[/url]";
    public static string Col(string colour, string text) => $"[color={colour}]{text}[/color]";
    public static string Dim(string text) => Col(Muted, text);
    public static string Small(string text) => $"[font_size=13]{text}[/font_size]";
    public static string H3(string text) => $"[font_size=20][b]{text}[/b][/font_size]\n";
    public static string H4(string text) => $"\n[b]{text}[/b]\n";
    public static string PosNeg(long v, string text) => Col(v >= 0 ? Pos : Neg, text);

    public static string Money(long n) => (n < 0 ? "-$" : "$") + Math.Abs(n).ToString("N0", Inv);
    public static string Num(long n) => n.ToString("N0", Inv);
    public static string Signed(long n) => n > 0 ? $"+{n}" : n.ToString(Inv);
    public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>A meta argument that may hold colons or spaces.</summary>
    public static string Arg(string s) => Uri.EscapeDataString(s);

    /// <summary>A bar of <paramref name="width"/> blocks, filled to a fraction.</summary>
    public static string Bar(double fraction, string colour, int width = 16)
    {
        int n = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return Col(colour, new string('█', n)) + Dim(new string('░', width - n));
    }

    /// <summary>A bar for a signed value, growing left (negative) or right (positive) from the middle.</summary>
    public static string SignedBar(double v, double max, int half = 8)
    {
        int n = (int)Math.Round(Math.Clamp(Math.Abs(v) / Math.Max(1, max), 0, 1) * half);
        string left = v < 0 ? Dim(new string('░', half - n)) + Col(Neg, new string('█', n)) : Dim(new string('░', half));
        string right = v > 0 ? Col(Pos, new string('█', n)) + Dim(new string('░', half - n)) : Dim(new string('░', half));
        return left + "│" + right;
    }

    /// <summary>A line chart in block characters: one column per sample, scaled between the values' least and most.</summary>
    public static string Spark(IReadOnlyList<double> values, string colour, int width = 30)
    {
        if (values.Count == 0) return "";
        const string Blocks = "▁▂▃▄▅▆▇█";
        double min = values.Min(), max = values.Max(), span = max - min;
        var chars = new char[Math.Min(width, values.Count)];
        for (int k = 0; k < chars.Length; k++)
        {
            double v = values[(int)((long)k * (values.Count - 1) / Math.Max(1, chars.Length - 1))];
            chars[k] = Blocks[span <= 0 ? 3 : (int)Math.Round((v - min) / span * (Blocks.Length - 1))];
        }
        return Col(colour, new string(chars));
    }

    /// <summary>A two-column key and value table.</summary>
    public static string Table(IEnumerable<(string Key, string Value)> rows, int columns = 2)
    {
        var cells = rows.Select(r => $"[cell]{r.Key}  [/cell][cell]{r.Value}[/cell]").ToList();
        return cells.Count == 0 ? "" : $"[table={columns}]{string.Concat(cells)}[/table]\n";
    }
}
