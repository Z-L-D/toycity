using Godot;
using ToyCity.ClientModel;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>The colours of the ground, roads, water and zones, shared by the 3D view and the minimap (as the web client's).</summary>
public static class Palette
{
    public static readonly Color Road = new(0.33f, 0.33f, 0.36f), Water = new(0.20f, 0.42f, 0.70f, 0.82f);
    static readonly Color[] Zone = { default, Color.FromHtml("#4caf50"), Color.FromHtml("#4a7fe0"), Color.FromHtml("#e0b030") };

    /// <summary>Lowland green to upland olive to rocky brown, or the lake bed under water.</summary>
    public static Color Ground(int h, int water)
    {
        if (h < water)
        {
            float d = Mathf.Min(1, (water - h) / 6f);
            return new Color((60 - 30 * d) / 255f, (130 - 50 * d) / 255f, (200 - 40 * d) / 255f);
        }
        float t = Mathf.Min(1, (h - water) / 18f);
        if (t < 0.6f)
        {
            float k = t / 0.6f;
            return new Color((110 + 40 * k) / 255f, (170 - 20 * k) / 255f, (80 - 10 * k) / 255f);
        }
        float j = (t - 0.6f) / 0.4f;
        return new Color((150 + 20 * j) / 255f, (150 - 30 * j) / 255f, (70 + 20 * j) / 255f);
    }

    /// <summary>A cell's ground as drawn: road, a zone's tint over the ground, or bare ground.</summary>
    public static Color Cell(CityMirror c, int i)
    {
        bool strip = RoadShape.UnderStrip(c, i);
        if (c.Paths[i] != 0 && !strip) return Road;
        var g = Ground(c.Heights[i], c.WaterLevel);
        // Under an arc or diagonal: open ground, with the zone beside it filling the triangles (the road is drawn over it).
        int z = strip ? RoadShape.FillZone(c, i) : c.Zones[i];
        return z != 0 && c.BuildingAt[i] == 0 ? g.Lerp(Zone[z / 3 % 4], 0.28f + z % 3 * 0.1f) : g;
    }

    public static Color Of(BuildingTypeInfo t) => Color.FromHtml(string.IsNullOrEmpty(t.Color) ? "#999999" : t.Color);
}
