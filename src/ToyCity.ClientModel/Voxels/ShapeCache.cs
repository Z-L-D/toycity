using System.Numerics;
using System.Text;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Smooth shapes made once and kept on disk: a model, its smooth materials, its scale and the triangle budget
/// make a key, and the mesh for that key is read back next time instead of being made again. A file that won't
/// read is made again and replaced. Without a folder it only keeps them in memory.
/// </summary>
public sealed class ShapeCache
{
    readonly string? _dir;
    readonly Dictionary<ulong, MeshData> _memory = new();

    public ShapeCache(string? dir) => _dir = dir;

    /// <summary>Meshes read from disk, and meshes made, since this cache was opened.</summary>
    public int Hits { get; private set; }
    public int Builds { get; private set; }

    public MeshData Get(VoxModel m, float scale, int budget, int start)
    {
        ulong key = Key(m, scale, budget, start);
        if (_memory.TryGetValue(key, out var mesh)) return mesh;
        string? path = _dir == null ? null : Path.Combine(_dir, $"{key:x16}.mesh");
        if (path != null && File.Exists(path))
            try
            {
                using var f = File.OpenRead(path);
                mesh = MeshFile.Read(f);
                Hits++;
                return _memory[key] = mesh;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { }
        mesh = ShapeMesher.Mesh(m, scale, budget, start);
        Builds++;
        if (path != null)
            try
            {
                Directory.CreateDirectory(_dir!);
                // Written beside and moved into place, so a crash never leaves half a file under the key.
                string tmp = path + ".tmp";
                using (var f = File.Create(tmp)) MeshFile.Write(mesh, f);
                File.Move(tmp, path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return _memory[key] = mesh;
    }

    /// <summary>The key for a model's smooth shapes: 64-bit FNV-1a over everything that changes them.</summary>
    public static ulong Key(VoxModel m, float scale, int budget, int start)
    {
        ulong h = 14695981039346656037ul;
        void Byte(byte b) { h ^= b; h *= 1099511628211ul; }
        void Int(int v) { for (int k = 0; k < 4; k++) Byte((byte)(v >> 8 * k)); }
        Int(ShapeMesher.Version);
        Int(m.SizeX); Int(m.SizeY); Int(m.SizeZ);
        foreach (byte v in m.Voxels) Byte(v);
        foreach (uint c in m.Palette) Int((int)c);
        foreach (bool s in m.Smooth) Byte(s ? (byte)1 : (byte)0);
        Int(BitConverter.SingleToInt32Bits(scale));
        Int(budget);
        Int(start);
        return h;
    }
}

/// <summary>A <see cref="MeshData"/> as a small binary file.</summary>
public static class MeshFile
{
    const int Magic = 0x534d4354;  // "TCMS"

    public static void Write(MeshData m, Stream s)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        w.Write(Magic); w.Write(ShapeMesher.Version);
        w.Write(m.Positions.Count); w.Write(m.Indices.Count);
        for (int i = 0; i < m.Positions.Count; i++)
        {
            var p = m.Positions[i]; var n = m.Normals[i];
            w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(n.X); w.Write(n.Y); w.Write(n.Z); w.Write(m.Colors[i]);
        }
        foreach (int i in m.Indices) w.Write(i);
    }

    public static MeshData Read(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (r.ReadInt32() != Magic || r.ReadInt32() != ShapeMesher.Version) throw new InvalidDataException("not a mesh of this version");
        int nv = r.ReadInt32(), ni = r.ReadInt32();
        if (nv is < 0 or > 16_000_000 || ni is < 0 or > 48_000_000 || ni % 3 != 0) throw new InvalidDataException("bad mesh size");
        var m = new MeshData();
        for (int i = 0; i < nv; i++)
        {
            m.Positions.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            m.Normals.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            m.Colors.Add(r.ReadUInt32());
        }
        for (int i = 0; i < ni; i++)
        {
            int k = r.ReadInt32();
            if (k < 0 || k >= nv) throw new InvalidDataException("bad mesh index");
            m.Indices.Add(k);
        }
        return m;
    }
}
