using System.IO.Compression;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Pictures from files as RGBA pixels, for an imported model's textures (RC part 5). PNG is read here (8 bits a
/// channel, not interlaced, the commonest kind); anything else (JPEG, interlaced PNG) goes to <see cref="Fallback"/>,
/// which a client with its own image loader sets, so the client model needs no image library.
/// </summary>
public static class ImageDecode
{
    /// <summary>Decodes what <see cref="Png"/> can't: a client's own loader, or null for none.</summary>
    public static Func<byte[], (int W, int H, byte[] Rgba)?>? Fallback;

    /// <summary>A picture's pixels, RGBA by row from the top; null if it can't be read.</summary>
    public static (int W, int H, byte[] Rgba)? Read(byte[] data)
    {
        try { return Png(data) ?? Fallback?.Invoke(data); }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return Fallback?.Invoke(data); }
    }

    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>A PNG's pixels, or null if it isn't one this reads.</summary>
    public static (int W, int H, byte[] Rgba)? Png(byte[] data)
    {
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int at = 8, w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, alpha = null;
        using var idat = new MemoryStream();
        static int Be(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
        while (at + 8 <= data.Length)
        {
            int len = Be(data, at);
            string kind = System.Text.Encoding.ASCII.GetString(data, at + 4, 4);
            if (len < 0 || at + 12 + len > data.Length) return null;
            int body = at + 8;
            switch (kind)
            {
                case "IHDR":
                    w = Be(data, body); h = Be(data, body + 4); depth = data[body + 8]; type = data[body + 9]; interlace = data[body + 12];
                    break;
                case "PLTE": palette = data[body..(body + len)]; break;
                case "tRNS": alpha = data[body..(body + len)]; break;
                case "IDAT": idat.Write(data, body, len); break;
                case "IEND": at = data.Length; continue;
            }
            at += 12 + len;
        }
        if (w <= 0 || h <= 0 || (long)w * h > 4096L * 4096 || depth != 8 || interlace != 0) return null;
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || type == 3 && palette == null) return null;
        int stride = w * channels;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);
        // Undo each row's filter, in place, against the row above.
        var rows = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            byte filter = raw[y * (stride + 1)];
            int src = y * (stride + 1) + 1, dst = y * stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? rows[dst + x - channels] : 0;
                int b = y > 0 ? rows[dst - stride + x] : 0;
                int c = x >= channels && y > 0 ? rows[dst - stride + x - channels] : 0;
                int v = raw[src + x];
                rows[dst + x] = (byte)(filter switch
                {
                    0 => v,
                    1 => v + a,
                    2 => v + b,
                    3 => v + (a + b) / 2,
                    4 => v + Paeth(a, b, c),
                    _ => throw new InvalidDataException("unknown PNG filter"),
                });
            }
        }
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4, s = i * channels;
            switch (type)
            {
                case 0: rgba[o] = rgba[o + 1] = rgba[o + 2] = rows[s]; rgba[o + 3] = 255; break;
                case 4: rgba[o] = rgba[o + 1] = rgba[o + 2] = rows[s]; rgba[o + 3] = rows[s + 1]; break;
                case 2: rgba[o] = rows[s]; rgba[o + 1] = rows[s + 1]; rgba[o + 2] = rows[s + 2]; rgba[o + 3] = 255; break;
                case 6: rgba[o] = rows[s]; rgba[o + 1] = rows[s + 1]; rgba[o + 2] = rows[s + 2]; rgba[o + 3] = rows[s + 3]; break;
                default:
                    int p = rows[s];
                    if (p * 3 + 2 >= palette!.Length) return null;
                    rgba[o] = palette[p * 3]; rgba[o + 1] = palette[p * 3 + 1]; rgba[o + 2] = palette[p * 3 + 2];
                    rgba[o + 3] = alpha != null && p < alpha.Length ? alpha[p] : (byte)255;
                    break;
            }
        }
        return (w, h, rgba);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>A PNG of RGBA pixels (no filtering, deflated): for the checks, and anything that saves a texture.</summary>
    public static byte[] WritePng(int w, int h, byte[] rgba)
    {
        using var o = new MemoryStream();
        o.Write(Signature);
        void Chunk(string kind, byte[] body)
        {
            var k = System.Text.Encoding.ASCII.GetBytes(kind);
            o.Write(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length });
            o.Write(k); o.Write(body);
            uint crc = Crc(k.Concat(body));
            o.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        }
        Chunk("IHDR", new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0 });
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        Chunk("IDAT", packed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    static uint Crc(IEnumerable<byte> bytes)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
