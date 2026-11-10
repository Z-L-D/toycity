using static ToyCity.ClientModel.Graphics.Ground.GroundNoise;

namespace ToyCity.ClientModel.Graphics.Ground;

public static partial class GroundTextures
{
    /// <summary>
    /// A meadow from above: turf in tussocks a hand or two across, lusher and drier in patches a few metres wide,
    /// tens of thousands of blades leaning out from their tussocks with the odd straw-coloured one among them,
    /// clover in the lush parts and a scatter of small white and yellow flowers.
    /// </summary>
    /// Dry grass is the same turf late in a dry summer: straw-coloured, most blades dead, no clover.
    internal static GroundTexture Grass(int n, float metres, bool dry)
    {
        float px = n / metres;  // pixels a metre
        Paint soil = dry ? Paint.Of(0x4A3F2A) : Paint.Of(0x342D1D), lush = dry ? Paint.Of(0x666A3C) : Paint.Of(0x3A6329), mid = dry ? Paint.Of(0x807A48) : Paint.Of(0x506E2F),
            dryish = dry ? Paint.Of(0x958959) : Paint.Of(0x737840), straw = dry ? Paint.Of(0xB09E70) : Paint.Of(0xA89964);
        var c = new GroundCanvas(n, soil, 0f, 0.95f);
        const int tussocks = 26;  // across the tile: about 30 cm each

        // How lush each spot is, 0 dry to 1 lush: patches a few metres across with a finer mottle over them.
        float Lushness(float u, float v) => Math.Clamp(0.55f + 0.5f * Fbm(u, v, 3, 4, 11) + 0.18f * Fbm(u, v, 11, 3, 23), 0f, 1f);
        Cell Tussock(float u, float v) => Cells(u + 0.010f * Fbm(u, v, 14, 2, 5), v + 0.010f * Fbm(u, v, 14, 2, 6), tussocks, 31, 0.95f);

        // The turf under the blades: each tussock a low dome, darker between them where the soil shows through.
        c.Each((i, u, v) =>
        {
            float l = Lushness(u, v);
            var t = Tussock(u, v);
            float dome = Smooth(0.72f, 0.05f, t.F1);
            float own = 0.86f + 0.26f * ((t.Id & 1023) / 1023f);
            var green = Paint.Ramp(l, dryish, mid, lush) * own;
            c.Set(i, soil.To(green * (0.60f + 0.25f * dome), 0.5f + 0.4f * dome));
            c.H[i] = 0.12f + 0.24f * dome + 0.04f * Fbm(u, v, 40, 2, 77);
            c.Rough[i] = 0.93f;
        });

        // The blades: each rooted at a random spot, most leaning away from their tussock's middle and the rest any
        // way at all, lighter and yellower towards the tip. A few are last year's, straw-coloured, more where it's dry.
        var r = new Scatter(1001);
        for (int k = 0; k < 40000; k++)
        {
            float u = r.Next(), v = r.Next();
            float l = Lushness(u, v);
            var t = Tussock(u, v);
            float lean = r.Chance(0.65f) ? MathF.Atan2(-t.Dy, -t.Dx) + r.Range(-1.0f, 1.0f) : r.Range(0, MathF.Tau);
            bool dead = r.Chance(dry ? 0.45f + 0.3f * (1 - l) : 0.05f + 0.30f * (1 - l) * (1 - l));
            var col = dead ? straw.To(dryish, r.Next() * 0.6f) : Paint.Ramp(Math.Clamp(l + r.Range(-0.25f, 0.25f), 0f, 1f), dryish, mid, lush);
            col *= r.Range(0.80f, 1.20f);
            // Mostly short, a few long.
            float len = (0.045f + 0.20f * r.Next() * r.Next()) * px, h0 = c.H[c.At((int)(u * n), (int)(v * n))];
            c.Blade(u, v, lean, len, r.Range(-0.9f, 0.9f), MathF.Max(1f, 0.013f * px),
                col * 0.70f, col.To(Paint.Of(0xC2CB7C), 0.18f) * 1.10f, h0, h0 + r.Range(0.20f, 0.46f), 0.92f);
        }

        // Clover and other low leaves in the lusher parts: small round dark-green leaves in threes.
        for (int k = 0; k < 900; k++)
        {
            float u = r.Next(), v = r.Next();
            if (dry || Lushness(u, v) < 0.5f + 0.3f * r.Next()) continue;
            var leaf = Paint.Of(0x3B642D) * r.Range(0.85f, 1.15f);
            float a = r.Range(0, MathF.Tau), size = r.Range(0.010f, 0.018f) * px;
            for (int p = 0; p < 3; p++)
            {
                float pa = a + p * MathF.Tau / 3;
                c.Lump(u + MathF.Cos(pa) * size / n, v + MathF.Sin(pa) * size / n, size, size, 0, 0.50f, 0.60f, (d, _) => leaf * (1.08f - 0.25f * d), 0.8f, 0.2f);
            }
        }

        // Flowers, small and scattered thin so no one of them marks the tile: daisies and something yellow.
        for (int k = 0; k < 260; k++)
        {
            float u = r.Next(), v = r.Next();
            if (Fbm(u, v, 5, 2, 301) < -0.05f) continue;
            bool yellow = r.Chance(0.4f);
            float size = (yellow ? 0.012f : 0.015f) * px;
            c.Dab(u, v, size, yellow ? Paint.Of(0xDCC244) : Paint.Of(0xE6E5D8), 0.85f, 0.82f, 1f);
            if (!yellow) c.Dab(u, v, size * 0.35f, Paint.Of(0xD2B040), 0.9f, 0.84f, 1f);
        }

        c.ShadeHollows(0.24f);
        return c.Finish(dry ? "dry-grass" : "meadow", "", metres, 0.07f, soften: 1);
    }
}
