using ToyCity.ClientModel.Graphics.Ground;
using ToyCity.ClientModel.Voxels;

/// <summary>Pictures of a ground texture to look at while writing it (RC part 6): see Program.cs for what's in them.</summary>
public static class GroundSheets
{
    /// <summary>Writes a texture's sheet and maps into a folder, and gives back its lit tile.</summary>
    public static byte[] Write(string folder, GroundTexture t)
    {
        int n = t.Size;
        var lit = Lit(t);
        var sheet = new byte[n * 2 * n * 2 * 4];
        Blit(sheet, n * 2, Opaque(t.Albedo), n, 0, 0);
        Blit(sheet, n * 2, lit, n, n, 0);
        Blit(sheet, n * 2, Shrunk(Tiled(lit, n, 2), n * 2, 2), n, 0, n);
        Blit(sheet, n * 2, Shrunk(Tiled(lit, n, 6), n * 6, 6), n, n, n);
        File.WriteAllBytes(Path.Combine(folder, $"{t.Id}-sheet.png"), ImageDecode.WritePng(n * 2, n * 2, sheet));

        var maps = new byte[n * 4 * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            int x = i % n, y = i / n;
            void Put(int panel, byte r, byte g, byte b) { int o = (y * n * 4 + panel * n + x) * 4; maps[o] = r; maps[o + 1] = g; maps[o + 2] = b; maps[o + 3] = 255; }
            byte h = t.Albedo[i * 4 + 3];
            Put(0, h, h, h);
            byte nx = t.Detail[i * 4], ny = t.Detail[i * 4 + 1];
            double fx = nx / 127.5 - 1, fy = ny / 127.5 - 1, fz = Math.Sqrt(Math.Max(0, 1 - fx * fx - fy * fy));
            Put(1, nx, ny, (byte)(127.5 + 127.5 * fz));
            Put(2, t.Detail[i * 4 + 2], t.Detail[i * 4 + 2], t.Detail[i * 4 + 2]);
            Put(3, t.Detail[i * 4 + 3], t.Detail[i * 4 + 3], t.Detail[i * 4 + 3]);
        }
        File.WriteAllBytes(Path.Combine(folder, $"{t.Id}-maps.png"), ImageDecode.WritePng(n * 4, n, maps));

        // A piece of the lit tile at three times its size, to see the pixels.
        int piece = Math.Min(n, 256), big = piece * 3;
        var zoom = new byte[big * big * 4];
        for (int y = 0; y < big; y++)
            for (int x = 0; x < big; x++)
                Buffer.BlockCopy(lit, ((n / 4 + y / 3) % n * n + (n / 4 + x / 3) % n) * 4, zoom, (y * big + x) * 4, 4);
        File.WriteAllBytes(Path.Combine(folder, $"{t.Id}-zoom.png"), ImageDecode.WritePng(big, big, zoom));
        return lit;
    }

    /// <summary>Several lit tiles side by side, up to six a row, as all.png.</summary>
    public static void WriteAll(string folder, IReadOnlyList<byte[]> lits, int n)
    {
        int per = Math.Min(lits.Count, 6), rows = (lits.Count + per - 1) / per;
        var all = new byte[n * per * n * rows * 4];
        for (int k = 0; k < lits.Count; k++) Blit(all, n * per, lits[k], n, k % per * n, k / per * n);
        File.WriteAllBytes(Path.Combine(folder, "all.png"), ImageDecode.WritePng(n * per, n * rows, all));
    }

    /// <summary>How far apart two colours are: the most any channel differs.</summary>
    public static int Off(uint a, uint b) => Math.Max(Math.Abs((int)(a >> 16 & 255) - (int)(b >> 16 & 255)), Math.Max(Math.Abs((int)(a >> 8 & 255) - (int)(b >> 8 & 255)), Math.Abs((int)(a & 255) - (int)(b & 255))));

    static byte[] Opaque(byte[] rgba)
    {
        var o = (byte[])rgba.Clone();
        for (int i = 3; i < o.Length; i += 4) o[i] = 255;
        return o;
    }

    /// <summary>The texture under a sun 35 degrees up in the north-west with a soft sky, level ground coming out as its own colour.</summary>
    public static byte[] Lit(GroundTexture t)
    {
        int n = t.Size;
        var o = new byte[n * n * 4];
        double lx = -0.58, ly = -0.58, lz = 0.57;
        for (int i = 0; i < n * n; i++)
        {
            double nx = t.Detail[i * 4] / 127.5 - 1, ny = t.Detail[i * 4 + 1] / 127.5 - 1, nz = Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny));
            double sun = Math.Max(0, nx * lx + ny * ly + nz * lz) / lz, sky = t.Detail[i * 4 + 3] / 255.0;
            double light = 0.38 * sky + 0.62 * sun;
            for (int ch = 0; ch < 3; ch++) o[i * 4 + ch] = (byte)GroundCanvas.Srgb(GroundCanvas.Linear(t.Albedo[i * 4 + ch]) * light);
            o[i * 4 + 3] = 255;
        }
        return o;
    }

    static byte[] Tiled(byte[] src, int n, int times)
    {
        int w = n * times;
        var o = new byte[w * w * 4];
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
                Buffer.BlockCopy(src, ((y % n) * n + x % n) * 4, o, (y * w + x) * 4, 4);
        return o;
    }

    /// <summary>Shrinks a picture w a side by a whole factor, averaging its light.</summary>
    static byte[] Shrunk(byte[] src, int w, int by)
    {
        int m = w / by;
        var o = new byte[m * m * 4];
        for (int y = 0; y < m; y++)
            for (int x = 0; x < m; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int j = 0; j < by; j++)
                    for (int i = 0; i < by; i++)
                    {
                        int p = ((y * by + j) * w + x * by + i) * 4;
                        r += GroundCanvas.Linear(src[p]); g += GroundCanvas.Linear(src[p + 1]); b += GroundCanvas.Linear(src[p + 2]);
                    }
                int q = (y * m + x) * 4, k = by * by;
                o[q] = (byte)GroundCanvas.Srgb(r / k); o[q + 1] = (byte)GroundCanvas.Srgb(g / k); o[q + 2] = (byte)GroundCanvas.Srgb(b / k); o[q + 3] = 255;
            }
        return o;
    }

    static void Blit(byte[] dst, int dstW, byte[] src, int n, int ox, int oy)
    {
        for (int y = 0; y < n; y++) Buffer.BlockCopy(src, y * n * 4, dst, ((oy + y) * dstW + ox) * 4, n * 4);
    }
}
