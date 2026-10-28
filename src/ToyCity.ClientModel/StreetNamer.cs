namespace ToyCity.ClientModel;

/// <summary>
/// Names roads from the line they run along, exactly as the server's Voice.RoadName does: north-south roads are
/// numbered avenues by column, east-west roads named streets by row.
/// </summary>
public sealed class StreetNamer
{
    readonly IReadOnlyList<string> _names;
    readonly List<string> _rowKinds;

    public StreetNamer(IReadOnlyList<string> names, IReadOnlyList<string> kinds)
    {
        _names = names.Count > 0 ? names : new[] { "Main" };
        _rowKinds = kinds.Where(k => k != "Avenue").ToList();
        if (_rowKinds.Count == 0) _rowKinds.Add("Street");
    }

    public string RoadName(bool northSouth, int line)
    {
        if (northSouth) return $"{Ordinal(line + 1)} Avenue";
        int n = _names.Count;
        int name = n == 1 ? 0 : Gcd(7, n) == 1 ? line * 7 % n : line % n;
        return $"{_names[name]} {_rowKinds[line / n % _rowKinds.Count]}";
    }

    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
