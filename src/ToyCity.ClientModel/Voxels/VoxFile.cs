using System.Text;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Reads and writes MagicaVoxel .vox files (version 150+): the first model's SIZE and XYZI chunks and the RGBA
/// palette. Scene graph, materials and extra models are skipped on reading, so an artist's file exported with
/// them still loads.
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
        return model;
    }

    public static VoxModel Read(string path) { using var f = File.OpenRead(path); return Read(f); }

    public static void Write(VoxModel m, Stream s)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        int n = m.Count;
        int sizeChunk = 12 + 12, xyziChunk = 12 + 4 + 4 * n, rgbaChunk = 12 + 1024;
        w.Write("VOX ".ToCharArray()); w.Write(150);
        w.Write("MAIN".ToCharArray()); w.Write(0); w.Write(sizeChunk + xyziChunk + rgbaChunk);
        w.Write("SIZE".ToCharArray()); w.Write(12); w.Write(0); w.Write(m.SizeX); w.Write(m.SizeY); w.Write(m.SizeZ);
        w.Write("XYZI".ToCharArray()); w.Write(4 + 4 * n); w.Write(0); w.Write(n);
        for (int z = 0; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                    if (m[x, y, z] is var c and > 0) { w.Write((byte)x); w.Write((byte)y); w.Write((byte)z); w.Write(c); }
        w.Write("RGBA".ToCharArray()); w.Write(1024); w.Write(0);
        for (int i = 0; i < 256; i++) w.Write(i < 255 ? m.Palette[i + 1] : 0u);
    }

    public static void Write(VoxModel m, string path) { using var f = File.Create(path); Write(m, f); }
}
