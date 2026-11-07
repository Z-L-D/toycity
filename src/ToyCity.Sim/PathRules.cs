namespace ToyCity.Paths;

// This file is compiled into both the simulation and the client model (linked from ToyCity.ClientModel.csproj), so
// the path designer's preview shows exactly what the city will make of a design. Its types are internal to each.

/// <summary>A kind of strip a path's cross-section is made of: its name, colour, width limits and what it's for.</summary>
internal readonly record struct StripKind(string Id, string Name, string Colour, double Default, double Min, double Max, string About);

/// <summary>What the city makes of a cross-section: the numbers a built-in path type has in content/profiles.json.</summary>
internal sealed record PathSummary(
    bool Cars, bool Bikes, bool Pedestrians, bool OneWay, bool Frontage, int SpeedKmh, int CapacityPerHour, int CostPerCell,
    double MaintenancePerCell, int NoiseDb, double WidthM, string Tram, int MaxGradePct, bool Stairs, string[] Surfaces,
    int WinterTier, bool Drained, string Color, string Kind);

/// <summary>
/// The path designer's rules (RC part 4): a path laid out left to right as strips, as Streetmix does (sidewalks,
/// planting, bike lanes, parking, drive lanes, turn lanes, tram tracks, medians, shoulders), and what follows from
/// them. They're fitted to the built-in types, so a design laid out like a built-in road comes out close to it:
/// <list type="bullet">
/// <item>Who uses it: cars with a drive, turn, tram or shared lane; people with a sidewalk or a shared path; bikes
/// with a bike lane or a shared path, or in the traffic of a town road up to 50 km/h.</item>
/// <item>A path with cars and shoulders but no sidewalk is a highway: no frontage, 90 to 110 km/h by its lanes.</item>
/// <item>A town road is 30 km/h with lanes narrower than 3.25 m or trams in the traffic, 60 with three lanes or more
/// and nothing to slow it (no parking, bikes, trees or median: a stroad), 40 split by a median with a lane each way,
/// and 50 otherwise.</item>
/// <item>Capacity is per lane: 1,200 an hour on a highway, 600 on a town lane (300 if narrow, 250 with trams in it),
/// 15% more one way, 300 more with a turn lane. Paths carry 400 people an hour a metre of sidewalk and 750 bikes a
/// bike lane.</item>
/// <item>Cost is by the metre of each strip; upkeep a hundredth of it a month; noise rises with speed and lanes and
/// falls a little with trees and a median.</item>
/// </list>
/// </summary>
internal static class PathRules
{
    public static readonly StripKind[] Kinds =
    {
        new("walk", "Sidewalk", "#cfc9bb", 2, 1.5, 6, "People on foot."),
        new("planter", "Trees", "#5e8c3a", 1.5, 1, 4, "Street trees: shade, and a little quieter."),
        new("bike", "Bike lane", "#3f9a5a", 1.8, 1.2, 3, "Bikes, in a lane painted green."),
        new("shared-path", "Shared path", "#b9a27a", 3, 2, 5, "Bikes and people together, no cars."),
        new("parking", "Parking lane", "#6b6d72", 2.4, 2, 3, "Cars parked along the kerb."),
        new("car", "Drive lane", "#4a4a4e", 3.5, 2.8, 4, "Cars, buses and trucks. Under 3.25 m it's a slow street lane."),
        new("turn", "Turn lane", "#5a5a60", 3.5, 3, 4, "Turning cars wait here out of the traffic."),
        new("car-tram", "Lane with tram tracks", "#57505a", 4, 3.5, 4.5, "Trams running in the traffic."),
        new("tram", "Tram track", "#7a6a58", 3.25, 3, 3.5, "Trams on their own reservation."),
        new("median", "Median", "#6f8f4a", 2, 1, 6, "Splits the traffic going each way."),
        new("shoulder", "Shoulder", "#77777a", 2, 1, 3, "Room to pull over. With no sidewalk, a highway."),
        new("shared", "Shared lane", "#8b6b47", 6, 4, 8, "Everyone at once, slowly: a country lane."),
    };

    public static StripKind? Kind(string id)
    {
        foreach (var k in Kinds) if (k.Id == id) return k;
        return null;
    }

    public const int MaxStrips = 16;
    public const double MinWidthM = 2, MaxWidthM = 40;

    /// <summary>Why a cross-section can't be built, or null when it can.</summary>
    public static string? Check(IReadOnlyList<(string Type, double WidthM)> strips, bool oneWay)
    {
        if (strips.Count == 0) return "A path needs at least one strip.";
        if (strips.Count > MaxStrips) return $"A path has at most {MaxStrips} strips.";
        double total = 0;
        foreach (var (type, w) in strips)
        {
            if (Kind(type) is not { } k) return $"'{type}' isn't a kind of strip.";
            if (double.IsNaN(w) || w < k.Min - 1e-6 || w > k.Max + 1e-6) return $"A {k.Name.ToLowerInvariant()} is {k.Min:0.##} to {k.Max:0.##} m wide.";
            total += w;
        }
        if (total < MinWidthM - 1e-6 || total > MaxWidthM + 1e-6) return $"A path is {MinWidthM:0} to {MaxWidthM:0} m wide across.";
        int Count(string t) { int n = 0; foreach (var s in strips) if (s.Type == t) n++; return n; }
        int lanes = Count("car") + Count("car-tram");
        if (Count("turn") > 0 && lanes == 0) return "A turn lane needs drive lanes beside it.";
        if (Count("parking") > 0 && lanes + Count("shared") == 0) return "A parking lane needs traffic to park off.";
        if (oneWay && lanes == 0) return "Only a road with drive lanes can be one way.";
        if (Count("shared") > 0 && lanes > 0) return "A shared lane is a lane on its own: it doesn't go with drive lanes.";
        return null;
    }

    /// <summary>What the city makes of a cross-section that passes <see cref="Check"/>.</summary>
    public static PathSummary Derive(IReadOnlyList<(string Type, double WidthM)> strips, bool oneWay)
    {
        int N(string t) { int n = 0; foreach (var s in strips) if (s.Type == t) n++; return n; }
        double W(string t) { double w = 0; foreach (var s in strips) if (s.Type == t) w += s.WidthM; return w; }
        double width = 0;
        foreach (var s in strips) width += s.WidthM;
        int lanes = N("car") + N("car-tram"), turn = N("turn"), shared = N("shared");
        bool cars = lanes + turn + shared > 0;
        bool walkers = N("walk") + N("shared-path") + shared > 0;
        bool highway = cars && N("walk") + N("shared-path") == 0 && shared == 0 && N("shoulder") > 0;
        bool bikeStrips = N("bike") + N("shared-path") + shared > 0;
        oneWay &= lanes > 0;
        string tram = N("tram") > 0 ? "dedicated" : N("car-tram") > 0 ? "shared" : "";

        int speed;
        if (!cars) speed = N("bike") > 0 ? 18 : 5;
        else if (shared > 0) speed = 25;
        else if (highway) speed = lanes <= 2 ? 90 : lanes <= 4 ? 100 : 110;
        else
        {
            double laneW = lanes > 0 ? (W("car") + W("car-tram")) / lanes : 3.5;
            bool calmed = N("parking") + N("bike") + N("planter") + N("median") > 0;
            speed = laneW < 3.25 || N("car-tram") > 0 ? 30
                : lanes + turn >= 3 && !calmed ? 60
                : N("median") > 0 && lanes <= 2 ? 40
                : 50;
        }

        int capacity;
        if (!cars) capacity = (int)Math.Round(W("walk") * 400 + W("shared-path") * 130 + N("bike") * 750);
        else if (shared > 0) capacity = 300;
        else
        {
            double c = 0;
            foreach (var s in strips)
                c += s.Type switch
                {
                    "car" => highway ? 1200 : s.WidthM < 3.25 ? 300 : 600,
                    "car-tram" => 250,
                    _ => 0,
                };
            if (oneWay) c *= 1.15;
            c += turn * 300;
            capacity = (int)(Math.Round(c / 100) * 100);
        }

        double cost = 0;
        foreach (var s in strips)
            cost += s.WidthM * s.Type switch
            {
                "walk" => 1, "planter" => 4, "bike" => 1.5, "shared-path" => 1.5, "parking" => 1, "car" => highway ? 6 : 1.5,
                "turn" => 1.5, "car-tram" => 6, "tram" => 7, "median" => 1.5, "shoulder" => 2.5, "shared" => 0.8, _ => 1,
            };
        int costPerCell = Math.Max(1, (int)Math.Round(cost));
        double upkeep = Math.Max(0.05, Math.Round(costPerCell / 100.0, 2));

        int noise = !cars ? 30
            : (int)Math.Round(Math.Clamp(35 + speed * 0.35 + lanes + turn - N("planter") - (N("median") > 0 ? 1 : 0), 30, 85));

        bool bikes = bikeStrips || (cars && !highway && speed <= 50);
        int grade = !cars ? (N("walk") + N("shared-path") > 0 ? 120 : 10)
            : shared > 0 ? 25
            : highway ? 6
            : tram.Length > 0 || N("median") > 0 ? 8
            : speed <= 30 ? 25 : 10;
        bool stairs = !cars && N("walk") + N("shared-path") > 0;
        string[] surfaces = !cars ? (N("walk") == 0 && N("shared-path") > 0 ? new[] { "dirt", "gravel", "brick" }
                : N("walk") == 0 ? new[] { "asphalt", "concrete", "gravel" } : new[] { "concrete", "brick", "asphalt" })
            : shared > 0 ? new[] { "dirt", "gravel", "limestone" }
            : highway || tram.Length > 0 ? new[] { "asphalt", "concrete" }
            : speed <= 30 ? new[] { "asphalt", "brick", "concrete", "gravel" }
            : new[] { "asphalt", "concrete", "brick" };
        int tier = highway ? 0 : cars && speed >= 50 ? 1 : 2;
        bool drained = highway || lanes + turn >= 4 || (cars && speed >= 50 && N("median") > 0);
        string kind = !cars ? (N("bike") > 0 && N("walk") == 0 ? "bike path" : "path")
            : highway ? "highway" : shared > 0 ? "lane" : tram.Length > 0 ? "tram road" : speed >= 60 ? "stroad" : N("median") > 0 ? "boulevard" : speed <= 30 ? "street" : "road";
        string color = kind switch
        {
            "path" => "#d8d2c4", "bike path" => "#5fae6b", "highway" => "#3d3d48", "lane" => "#a07a4f", "tram road" => "#7a6a90",
            "boulevard" => "#55705a", "street" => "#8c8c8c", "stroad" => "#5a5a5a", _ => "#6f6f78",
        };
        return new PathSummary(cars, bikes, walkers, oneWay, !highway, speed, capacity, costPerCell, upkeep, noise, Math.Round(width, 2), tram,
            grade, stairs, surfaces, tier, drained, color, kind);
    }
}
