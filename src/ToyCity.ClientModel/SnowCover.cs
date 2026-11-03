using ToyCity.Sim;

namespace ToyCity.ClientModel;

/// <summary>
/// Snow on the ground and the roofs (Beta 4, Part 4), as both clients draw it. The server sends one depth, the snow on
/// open level ground at the city's median height; <see cref="SnowDepth.At"/> spreads it by height, slope and the way
/// the ground faces, the same sum the Godot terrain shader works on the GPU.
/// </summary>
public static class SnowCover
{
    /// <summary>The snow on open level ground at the median height, in millimetres, from the city's stats.</summary>
    public static int GroundMm(CityMirror c) => c.Stats.Roads?.SnowMm ?? 0;

    /// <summary>The median height of the city's dry ground, in metres above the water.</summary>
    public static float MedianMetres(CityMirror c)
    {
        if (c.Heights.Length == 0) return 0;
        var counts = new int[256];
        int dry = 0;
        for (int i = 0; i < c.Heights.Length; i++)
            if (!c.IsWater(i)) { counts[c.Heights[i]]++; dry++; }
        if (dry == 0) return 0;
        int seen = 0;
        for (int h = 0; h < 256; h++)
            if ((seen += counts[h]) * 2 >= dry) return (h - c.WaterLevel) * 2f;
        return 0;
    }

    /// <summary>How white a place looks for its depth: nothing under 3 mm, patchy under 2 cm, whole by 4 cm.</summary>
    public static float Whiteness(float mm) => Math.Clamp((mm - 3) / 37f, 0, 1);

    /// <summary>The snow on a cell, in millimetres: by its height over the median, its slope and its facing.</summary>
    public static float AtCell(CityMirror c, int x, int y, float medianMetres)
    {
        int mm = GroundMm(c);
        if (mm <= 0 || !c.InBounds(x, y)) return 0;
        int i = c.Idx(x, y);
        if (c.IsWater(i)) return 0;
        // Slope and facing from the cell's neighbours, in steps of 2 m over 16 m cells.
        int H(int px, int py) => c.InBounds(px, py) ? c.Heights[c.Idx(px, py)] : c.Heights[i];
        float dx = (H(x + 1, y) - H(x - 1, y)) * 2f / 32f, dy = (H(x, y + 1) - H(x, y - 1)) * 2f / 32f;
        float slope = MathF.Sqrt(dx * dx + dy * dy);
        // The ground faces north (row 0 is north) where it falls away northwards: higher to the south.
        float northness = slope > 0.02f ? dy / slope : 0;
        return SnowDepth.At(mm, (c.Heights[i] - c.WaterLevel) * 2f - medianMetres, slope, northness);
    }
}
