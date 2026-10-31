using Godot;
using ToyCity.ClientModel;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>The colours of the ground, roads, water and zones, shared by the 3D view and the minimap (as the web client's).</summary>
public static class Palette
{
    /// <summary>Asphalt, weathered a little grey, and water as it looks from the air.</summary>
    public static readonly Color Road = new(0.27f, 0.27f, 0.28f), Water = new(0.13f, 0.30f, 0.38f, 0.88f);
    static readonly Color[] Zone = { default, Color.FromHtml("#5f9e4a"), Color.FromHtml("#5b7fb8"), Color.FromHtml("#c09a45") };

    /// <summary>The ground as the 3D view's material paints it, by height above the water (a step is 2 m): meadow,
    /// drier grass, then rock and snow on the heights; the bed darkening with depth under water.</summary>
    public static Color Ground(int h, int water)
    {
        if (h < water)
        {
            float d = Mathf.Min(1, (water - h) / 5f);
            return new Color(0.45f, 0.42f, 0.32f).Lerp(new Color(0.12f, 0.17f, 0.16f), d);
        }
        float m = (h - water) * 2f;
        if (m < 1.5f) return new Color(0.71f, 0.65f, 0.50f);
        var meadow = new Color(0.33f, 0.42f, 0.20f);
        if (m < 60) return meadow.Lerp(new Color(0.45f, 0.45f, 0.26f), m / 60f);
        if (m < 200) return new Color(0.45f, 0.45f, 0.26f).Lerp(new Color(0.47f, 0.45f, 0.42f), (m - 60) / 140f);
        if (m < 300) return new Color(0.47f, 0.45f, 0.42f);
        return new Color(0.47f, 0.45f, 0.42f).Lerp(new Color(0.93f, 0.94f, 0.96f), Mathf.Min(1, (m - 300) / 40f));
    }

    /// <summary>A cell's ground as the minimap draws it: road, a zone's tint over the ground, or bare ground.</summary>
    public static Color Cell(CityMirror c, int i)
    {
        var m = Mark(c, i);
        var g = Ground(c.Heights[i], c.WaterLevel);
        return g.Lerp(new Color(m.R, m.G, m.B), m.A);
    }

    /// <summary>What the city lays over the 3D view's ground on a cell, with how much it covers (alpha): asphalt on
    /// a road cell (the arcs and diagonals draw their own), a light zone tint on empty zoned land, nothing otherwise.</summary>
    public static Color Mark(CityMirror c, int i)
    {
        bool strip = RoadShape.UnderStrip(c, i);
        if (c.Paths[i] != 0 && !strip) return Road;
        int z = strip ? RoadShape.FillZone(c, i) : c.Zones[i];
        if (z == 0 || c.BuildingAt[i] != 0) return new Color(0, 0, 0, 0);
        var t = Zone[z / 3 % 4];
        return new Color(t.R, t.G, t.B, 0.22f + z % 3 * 0.06f);
    }

    public static Color Of(BuildingTypeInfo t) => Color.FromHtml(string.IsNullOrEmpty(t.Color) ? "#999999" : t.Color);
}
