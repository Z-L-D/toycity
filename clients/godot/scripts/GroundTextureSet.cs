using Godot;
using ToyCity.ClientModel.Graphics.Ground;

namespace ToyCity.Godot;

/// <summary>
/// The ground's materials (RC part 6) as two texture arrays for the terrain shader, one layer a material in the order
/// of <see cref="GroundTextures.List"/>: the colour with the height in alpha, and the detail (normal, roughness,
/// ambient occlusion). They're made in code the first time at each size, on every core, and kept on disk after, so
/// later starts only read them.
/// </summary>
public static class GroundTextureSet
{
    static readonly Dictionary<int, (Texture2DArray Albedo, Texture2DArray Detail)> Made = new();

    /// <summary>How long the last set took to get ready, and whether it came off the disk.</summary>
    public static (double Seconds, bool FromDisk) Last { get; private set; }

    /// <summary>The arrays at a size (pixels a side), made or read the first time they're asked for.</summary>
    public static (Texture2DArray Albedo, Texture2DArray Detail) Get(int size)
    {
        if (Made.TryGetValue(size, out var got)) return got;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var textures = Read(size);
        bool fromDisk = textures != null;
        if (textures == null)
        {
            textures = GroundTextures.MakeAll(size);
            Write(size, textures);
        }
        var albedo = new global::Godot.Collections.Array<Image>();
        var detail = new global::Godot.Collections.Array<Image>();
        foreach (var t in textures)
        {
            var a = Image.CreateFromData(t.Size, t.Size, false, Image.Format.Rgba8, t.Albedo);
            a.GenerateMipmaps();
            albedo.Add(a);
            var d = Image.CreateFromData(t.Size, t.Size, false, Image.Format.Rgba8, t.Detail);
            d.GenerateMipmaps();
            detail.Add(d);
        }
        var aa = new Texture2DArray();
        aa.CreateFromImages(albedo);
        var da = new Texture2DArray();
        da.CreateFromImages(detail);
        Last = (clock.Elapsed.TotalSeconds, fromDisk);
        GD.Print($"GROUND textures {textures.Length} at {size} px {(fromDisk ? "read" : "made")} in {clock.Elapsed.TotalSeconds:0.0} s");
        return Made[size] = (aa, da);
    }

    /// <summary>Each material's tile size in metres, in layer order, for the shader.</summary>
    public static float[] Metres => GroundTextures.List.Select(l => l.Metres).ToArray();

    static string PathOf(int size) => ProjectSettings.GlobalizePath($"user://ground/v{GroundTextures.Version}-{size}.bin");

    static GroundTexture[]? Read(int size)
    {
        try
        {
            string path = PathOf(size);
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            int count = r.ReadInt32(), n = r.ReadInt32();
            if (count != GroundTextures.List.Length || n != size) return null;
            var all = new GroundTexture[count];
            for (int i = 0; i < count; i++)
            {
                var (id, name, metres, _) = GroundTextures.List[i];
                if (r.ReadString() != id) return null;
                all[i] = new GroundTexture
                {
                    Id = id, Name = name, Metres = metres, Size = n, Relief = r.ReadSingle(), Roughness = r.ReadSingle(), Average = r.ReadUInt32(),
                    Albedo = r.ReadBytes(n * n * 4), Detail = r.ReadBytes(n * n * 4),
                };
                if (all[i].Albedo.Length != n * n * 4 || all[i].Detail.Length != n * n * 4) return null;
            }
            return all;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException) { return null; }
    }

    static void Write(int size, GroundTexture[] all)
    {
        try
        {
            string path = PathOf(size);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var w = new BinaryWriter(File.Create(path + ".part"));
            w.Write(all.Length); w.Write(size);
            foreach (var t in all) { w.Write(t.Id); w.Write(t.Relief); w.Write(t.Roughness); w.Write(t.Average); w.Write(t.Albedo); w.Write(t.Detail); }
            w.Close();
            File.Move(path + ".part", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { GD.Print($"GROUND couldn't keep the textures: {e.Message}"); }
    }
}
