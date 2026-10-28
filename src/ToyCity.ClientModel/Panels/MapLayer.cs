namespace ToyCity.ClientModel.Panels;

/// <summary>
/// The map layers as pictures, one RGBA pixel per cell, as the web client colours them: "good" runs blue to green,
/// "bad" amber to red, "diverging" red through clear to green around 100 (desirability is sent as value + 100),
/// and "binary" red for none, amber for some, green for enough.
/// </summary>
public static class MapLayer
{
    public static (byte R, byte G, byte B, byte A) Colour(byte v, string scheme)
    {
        static byte B(double x) => (byte)Math.Clamp(Math.Round(x), 0, 255);
        if (scheme == "binary") return v == 0 ? ((byte)200, (byte)40, (byte)40, B(0.45 * 255)) : v < 200 ? ((byte)240, (byte)170, (byte)30, B(0.5 * 255)) : ((byte)60, (byte)200, (byte)90, B(0.35 * 255));
        if (v == 0 && scheme != "diverging") return (0, 0, 0, 0);
        if (scheme == "diverging")
        {
            int d = v - 100;
            return d >= 0 ? ((byte)40, B(150 + Math.Min(105, d)), (byte)70, B(Math.Min(0.7, 0.15 + d / 120.0) * 255))
                : ((byte)220, (byte)50, (byte)40, B(Math.Min(0.7, 0.15 + -d / 120.0) * 255));
        }
        double a = 0.15 + v / 255.0 * 0.6;
        return scheme == "bad"
            ? (B(180 + v * 75 / 255.0), B(120 - v * 100 / 255.0), (byte)30, B(a * 255))
            : ((byte)40, B(120 + v * 135 / 255.0), B(200 - v * 120 / 255.0), B(a * 255));
    }

    /// <summary>An overlay's values as RGBA bytes, row by row.</summary>
    public static byte[] Pixels(ReadOnlySpan<byte> values, string scheme)
    {
        var px = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            var (r, g, b, a) = Colour(values[i], scheme);
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
        }
        return px;
    }

    /// <summary>The underground view: the ground dimmed and the water pipes picked out in blue.</summary>
    public static byte[] Pipes(CityMirror c)
    {
        var px = new byte[c.Width * c.Height * 4];
        for (int i = 0; i < c.Width * c.Height; i++)
        {
            bool pipe = c.WaterPipes[i] != 0;
            (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = pipe ? ((byte)70, (byte)170, (byte)255, (byte)235) : ((byte)40, (byte)36, (byte)32, (byte)170);
        }
        return px;
    }
}
