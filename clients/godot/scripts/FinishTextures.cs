using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using Material = Godot.Material;

namespace ToyCity.Godot;

/// <summary>
/// The finishes (RC part 5) as Godot textures and materials: each finish's pixels as a mipmapped texture, its relief
/// as a normal map, and a material for a mesh surface named by the finish, made from the material the rest of the
/// mesh is drawn with (the city's building shader, or the editors' plain one).
/// </summary>
public static class FinishTextures
{
    static readonly Dictionary<string, (ImageTexture Albedo, ImageTexture? Normal)> Textures = new();
    static readonly Dictionary<(ulong, string), Material> Materials = new();

    /// <summary>Lets the client model read an imported model's JPEG (or any picture Godot reads) as well as PNG.</summary>
    public static void InstallDecoder() => ImageDecode.Fallback ??= bytes =>
    {
        var img = new Image();
        if (img.LoadJpgFromBuffer(bytes) != Error.Ok && img.LoadPngFromBuffer(bytes) != Error.Ok && img.LoadWebpFromBuffer(bytes) != Error.Ok) return null;
        img.Convert(Image.Format.Rgba8);
        return (img.GetWidth(), img.GetHeight(), img.GetData());
    };

    public static (ImageTexture Albedo, ImageTexture? Normal)? Of(string id)
    {
        if (Textures.TryGetValue(id, out var t)) return t;
        if (Finishes.Get(id) is not { } f || f.Size == 0) return null;
        var img = Image.CreateFromData(f.Size, f.SizeY, false, Image.Format.Rgba8, f.Rgba);
        img.GenerateMipmaps();
        ImageTexture? normal = null;
        if (f.Height.Length == f.Size * f.SizeY)
        {
            var bump = Image.CreateFromData(f.Size, f.SizeY, false, Image.Format.L8, f.Height);
            bump.Convert(Image.Format.Rgba8);
            bump.BumpMapToNormalMap(4f);
            bump.GenerateMipmaps();
            normal = ImageTexture.CreateFromImage(bump);
        }
        return Textures[id] = (ImageTexture.CreateFromImage(img), normal);
    }

    /// <summary>The material for a finish's surface, made from the one the rest of the mesh uses.</summary>
    public static Material For(Material plain, string id)
    {
        var key = (plain.GetInstanceId(), id);
        if (Materials.TryGetValue(key, out var made)) return made;
        var f = Finishes.Get(id);
        var tex = Of(id);
        if (f == null || tex is not { } t) return plain;
        // Metre-mapped finishes tile by their size, the picture's top at the top of each tile.
        var scale = f.Mapped ? new Vector2(1, 1) : new Vector2((float)(1 / f.TileW), (float)(-1 / f.TileH));
        Material m;
        if (plain is ShaderMaterial sm)
        {
            var copy = (ShaderMaterial)sm.Duplicate();
            copy.SetShaderParameter("textured", true);
            copy.SetShaderParameter("finish_tex", t.Albedo);
            copy.SetShaderParameter("finish_normal", t.Normal);
            copy.SetShaderParameter("has_normal", t.Normal != null);
            copy.SetShaderParameter("uv_scale", scale);
            copy.SetShaderParameter("finish_roughness", f.Roughness);
            copy.SetShaderParameter("finish_metallic", f.Metallic);
            copy.SetShaderParameter("glassy", f.Glassy);
            m = copy;
        }
        else
        {
            var std = new StandardMaterial3D
            {
                AlbedoTexture = t.Albedo, Roughness = f.Roughness, Metallic = f.Metallic,
                Uv1Scale = new Vector3(scale.X, scale.Y, 1), TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            };
            if (t.Normal != null) { std.NormalEnabled = true; std.NormalTexture = t.Normal; std.NormalScale = 0.6f; }
            if (plain is StandardMaterial3D p) std.CullMode = p.CullMode;
            m = std;
        }
        return Materials[key] = m;
    }

    /// <summary>
    /// Gives a mesh's surfaces their materials: the plain one for its colours, a finish's for each surface named by one.
    /// Returns whether it had any finishes (an instance drawing it then mustn't override its material).
    /// </summary>
    public static bool Apply(Mesh? mesh, Material plain)
    {
        if (mesh is not ArrayMesh am || am.GetSurfaceCount() == 0) return false;
        bool any = false;
        for (int i = 0; i < am.GetSurfaceCount(); i++)
        {
            string name = am.SurfaceGetName(i);
            bool finished = name.Length > 0 && Finishes.Has(name);
            am.SurfaceSetMaterial(i, finished ? For(plain, name) : plain);
            any |= finished;
        }
        return any;
    }

    /// <summary>Sets an instance's mesh and its materials: an override when it's all plain colours, the surfaces' own otherwise.</summary>
    public static void Show(MeshInstance3D mi, Mesh? mesh, Material plain)
    {
        mi.Mesh = mesh;
        mi.MaterialOverride = Apply(mesh, plain) ? null : plain;
    }
}
