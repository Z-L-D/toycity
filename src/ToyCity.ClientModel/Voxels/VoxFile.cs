using System.Text;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Reads and writes MagicaVoxel .vox files (version 150+): the first model's SIZE and XYZI chunks, the RGBA
/// palette, and from the MATL materials which palette entries are smooth (a <c>_smooth</c> key of 1). The scene
/// graph, other material settings and extra models are skipped on reading, so an artist's file exported with them
/// still loads. Since MagicaVoxel itself can't set <c>_smooth</c>, a text file beside the model can instead: see
/// <see cref="ApplyShapes"/>.
/// </summary>
public static class VoxFile
{
    public static VoxModel Read(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (new string(r.ReadChars(4)) != "VOX ") throw new InvalidDataException("not a .vox file");
        r.ReadInt32(); // version
        if (new string(r.ReadChars(4)) != "MAIN") throw new InvalidDataException("no MAIN chunk");
        int mainContent = r.ReadInt32(), mainChildren = r.ReadInt32();
        r.ReadBytes(mainContent);
        long end = s.Position + mainChildren;
        VoxModel? model = null;
        (int X, int Y, int Z)? size = null;
        uint[]? palette = null;
        var smooth = new bool[256];
        while (s.Position < end)
        {
            string id = new(r.ReadChars(4));
            int content = r.ReadInt32(), children = r.ReadInt32();
            long next = s.Position + content + children;
            switch (id)
            {
                case "SIZE" when model == null:
                    size = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    break;
                case "XYZI" when model == null && size is { } sz:
                    model = new VoxModel(sz.X, sz.Y, sz.Z);
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        byte x = r.ReadByte(), y = r.ReadByte(), z = r.ReadByte(), c = r.ReadByte();
                        if (model.In(x, y, z)) model[x, y, z] = c;
                    }
                    break;
                case "MATL":
                {
                    int matId = r.ReadInt32();
                    foreach (var (key, value) in ReadDict(r))
                        if (key == "_smooth" && matId is > 0 and < 256) smooth[matId] = value.Trim() == "1";
                    break;
                }
                case "RGBA":
                    palette = new uint[256];
                    // The file's entry i is palette index i + 1; its last entry is unused.
                    for (int i = 0; i < 256; i++) { uint c = r.ReadUInt32(); if (i < 255) palette[i + 1] = c; }
                    break;
            }
            s.Position = next;
        }
        if (model == null) throw new InvalidDataException("no model in .vox file");
        if (palette != null) Array.Copy(palette, model.Palette, 256);
        else for (int i = 1; i < 256; i++) model.Palette[i] = VoxModel.Rgba((byte)i, (byte)i, (byte)i); // no palette: greys
        Array.Copy(smooth, model.Smooth, 256);
        return model;
    }

    public static VoxModel Read(string path) { using var f = File.OpenRead(path); return Read(f); }

    static List<(string Key, string Value)> ReadDict(BinaryReader r)
    {
        string Str() { int n = r.ReadInt32(); return n is < 0 or > 4096 ? throw new InvalidDataException("bad string in .vox") : Encoding.UTF8.GetString(r.ReadBytes(n)); }
        int count = r.ReadInt32();
        if (count is < 0 or > 256) throw new InvalidDataException("bad dictionary in .vox");
        var list = new List<(string, string)>();
        for (int i = 0; i < count; i++) list.Add((Str(), Str()));
        return list;
    }

    /// <summary>
    /// A model's shape file: lines such as <c>smooth = 6 7 40-52</c> or <c>sharp = 3</c> marking palette entries
    /// (1..255) smooth or sharp, over whatever the .vox file said. Blank lines and lines from <c>#</c> are ignored.
    /// Returns the problems found, one line each; the lines it understood still apply.
    /// </summary>
    public static List<string> ApplyShapes(VoxModel m, string text)
    {
        var problems = new List<string>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            string key = eq < 0 ? "" : line[..eq].Trim().ToLowerInvariant();
            if (key is not ("smooth" or "sharp")) { problems.Add($"line {n}: expected \"smooth = ...\" or \"sharp = ...\""); continue; }
            foreach (var part in line[(eq + 1)..].Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var ends = part.Split('-');
                if (ends.Length > 2 || !int.TryParse(ends[0], out int a) || !int.TryParse(ends[^1], out int b) || a < 1 || b > 255 || a > b)
                {
                    problems.Add($"line {n}: \"{part}\" isn't a palette entry from 1 to 255 or a range of them");
                    continue;
                }
                for (int i = a; i <= b; i++) m.Smooth[i] = key == "smooth";
            }
        }
        return problems;
    }

    public static void Write(VoxModel m, Stream s)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        int n = m.Count;
        int sizeChunk = 12 + 12, xyziChunk = 12 + 4 + 4 * n, rgbaChunk = 12 + 1024;
        // A material for each smooth entry: {_type: _diffuse, _smooth: 1}.
        var smooth = Enumerable.Range(1, 255).Where(i => m.Smooth[i]).ToList();
        int matContent = 4 + 4 + (4 + 5 + 4 + 8) + (4 + 7 + 4 + 1);
        int matlChunks = smooth.Count * (12 + matContent);
        w.Write("VOX ".ToCharArray()); w.Write(150);
        w.Write("MAIN".ToCharArray()); w.Write(0); w.Write(sizeChunk + xyziChunk + rgbaChunk + matlChunks);
        w.Write("SIZE".ToCharArray()); w.Write(12); w.Write(0); w.Write(m.SizeX); w.Write(m.SizeY); w.Write(m.SizeZ);
        w.Write("XYZI".ToCharArray()); w.Write(4 + 4 * n); w.Write(0); w.Write(n);
        for (int z = 0; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                    if (m[x, y, z] is var c and > 0) { w.Write((byte)x); w.Write((byte)y); w.Write((byte)z); w.Write(c); }
        w.Write("RGBA".ToCharArray()); w.Write(1024); w.Write(0);
        for (int i = 0; i < 256; i++) w.Write(i < 255 ? m.Palette[i + 1] : 0u);
        void Str(string v) { w.Write(v.Length); w.Write(Encoding.ASCII.GetBytes(v)); }
        foreach (int i in smooth)
        {
            w.Write("MATL".ToCharArray()); w.Write(matContent); w.Write(0);
            w.Write(i); w.Write(2);
            Str("_type"); Str("_diffuse");
            Str("_smooth"); Str("1");
        }
    }

    public static void Write(VoxModel m, string path) { using var f = File.Create(path); Write(m, f); }
}
