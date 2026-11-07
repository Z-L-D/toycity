using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Reads glTF 2.0 models (.gltf with its buffers, or .glb) into a <see cref="MeshData"/>, for props drawn in a
/// modelling program rather than in voxels. It takes every triangle mesh in the default scene with its node
/// transforms, the vertex colours if there are any, each material's base colour, and (RC part 5) its base colour
/// texture with the first texture coordinates, as a mapped finish ("model:" and a hash of its pixels) drawn by the
/// model's own coordinates. PNG textures are read here; others need <see cref="ImageDecode.Fallback"/>, and without
/// it a primitive keeps its base colour. Positions are in the file's metres, y up; the caller scales and places them.
/// </summary>
public static class GltfFile
{
    /// <param name="external">Fetches a buffer the file names by a relative uri, such as model.bin; null if missing.</param>
    public static MeshData Read(byte[] data, Func<string, byte[]?>? external = null)
    {
        byte[]? bin = null;
        string json;
        if (data.Length >= 12 && BitConverter.ToUInt32(data, 0) == 0x46546C67)  // "glTF": binary
        {
            int at = 12;
            json = "";
            while (at + 8 <= data.Length)
            {
                int length = BitConverter.ToInt32(data, at);
                uint type = BitConverter.ToUInt32(data, at + 4);
                if (length < 0 || at + 8 + length > data.Length) throw new InvalidDataException("glb chunk runs past the file");
                if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(data, at + 8, length);
                else if (type == 0x004E4942) bin = data[(at + 8)..(at + 8 + length)];
                at += 8 + length;
            }
            if (json.Length == 0) throw new InvalidDataException("glb has no JSON chunk");
        }
        else json = Encoding.UTF8.GetString(data);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement List(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : default;
        int Int(JsonElement e, string name, int fallback = 0) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        IEnumerable<JsonElement> Items(JsonElement e, string name) => List(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

        var buffers = Items(root, "buffers").Select(b =>
        {
            if (!b.TryGetProperty("uri", out var uri)) return bin ?? throw new InvalidDataException("buffer without data");
            string u = uri.GetString() ?? "";
            int comma = u.IndexOf(',');
            if (u.StartsWith("data:", StringComparison.Ordinal) && comma > 0) return Convert.FromBase64String(u[(comma + 1)..]);
            return external?.Invoke(Uri.UnescapeDataString(u)) ?? throw new InvalidDataException($"buffer {u} not found");
        }).ToList();
        var views = Items(root, "bufferViews").ToList();
        var accessors = Items(root, "accessors").ToList();
        var materials = Items(root, "materials").ToList();
        var meshes = Items(root, "meshes").ToList();
        var nodes = Items(root, "nodes").ToList();

        // An accessor as rows of floats (normalised integers scaled to 0..1) or as plain integers.
        float[][] Floats(int index)
        {
            var a = accessors[index];
            int count = Int(a, "count"), comp = Int(a, "componentType");
            int width = a.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, var t => throw new InvalidDataException($"accessor type {t} isn't read") };
            int size = comp switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException($"component type {comp} isn't read") };
            bool normalized = a.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
            var rows = new float[count][];
            if (!a.TryGetProperty("bufferView", out var bvi)) { for (int i = 0; i < count; i++) rows[i] = new float[width]; return rows; }
            var view = views[bvi.GetInt32()];
            var buffer = buffers[Int(view, "buffer")];
            int stride = Int(view, "byteStride", width * size);
            int start = Int(view, "byteOffset") + Int(a, "byteOffset");
            for (int i = 0; i < count; i++)
            {
                rows[i] = new float[width];
                for (int k = 0; k < width; k++)
                {
                    int at = start + i * stride + k * size;
                    if (at + size > buffer.Length) throw new InvalidDataException("accessor runs past its buffer");
                    rows[i][k] = comp switch
                    {
                        5126 => BitConverter.ToSingle(buffer, at),
                        5121 => normalized ? buffer[at] / 255f : buffer[at],
                        5120 => normalized ? Math.Max((sbyte)buffer[at] / 127f, -1) : (sbyte)buffer[at],
                        5123 => normalized ? BitConverter.ToUInt16(buffer, at) / 65535f : BitConverter.ToUInt16(buffer, at),
                        5122 => normalized ? Math.Max(BitConverter.ToInt16(buffer, at) / 32767f, -1) : BitConverter.ToInt16(buffer, at),
                        _ => BitConverter.ToUInt32(buffer, at),
                    };
                }
            }
            return rows;
        }

        Vector4 BaseColour(int material)
        {
            if (material < 0 || material >= materials.Count) return Vector4.One;
            if (materials[material].TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorFactor", out var f) && f.GetArrayLength() == 4)
                return new Vector4(f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle(), f[3].GetSingle());
            return Vector4.One;
        }

        var textures = Items(root, "textures").ToList();
        var images = Items(root, "images").ToList();
        var imageFinish = new Dictionary<int, string?>();

        // The finish for an image, read and added the first time it's used; null if it can't be read.
        string? ImageFinish(int image)
        {
            if (imageFinish.TryGetValue(image, out var known)) return known;
            string? id = null;
            if (image >= 0 && image < images.Count)
            {
                var im = images[image];
                byte[]? bytes = null;
                if (im.TryGetProperty("bufferView", out var bv) && bv.GetInt32() is var vi && vi >= 0 && vi < views.Count)
                {
                    var view = views[vi];
                    var buffer = buffers[Int(view, "buffer")];
                    int off = Int(view, "byteOffset"), len = Int(view, "byteLength");
                    if (off >= 0 && len > 0 && off + len <= buffer.Length) bytes = buffer[off..(off + len)];
                }
                else if (im.TryGetProperty("uri", out var uri))
                {
                    string u = uri.GetString() ?? "";
                    int comma = u.IndexOf(',');
                    bytes = u.StartsWith("data:", StringComparison.Ordinal) && comma > 0 ? Convert.FromBase64String(u[(comma + 1)..]) : external?.Invoke(Uri.UnescapeDataString(u));
                }
                if (bytes != null && ImageDecode.Read(bytes) is { } pic)
                {
                    uint hash = 2166136261;
                    for (int i = 0; i < pic.Rgba.Length; i += 7) hash = (hash ^ pic.Rgba[i]) * 16777619;
                    id = $"model:{hash:x8}{pic.W:x}{pic.H:x}";
                    string name = im.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "Texture" : "Texture";
                    if (!Assets.Finishes.Has(id)) Assets.Finishes.Add(id, name, pic.Rgba, pic.W, pic.H, 1, 1, mapped: true);
                }
            }
            return imageFinish[image] = id;
        }

        // A material's base colour texture: its finish and which texture coordinates it's drawn by.
        (string? Finish, int TexCoord) BaseTexture(int material)
        {
            if (material < 0 || material >= materials.Count) return (null, 0);
            if (!materials[material].TryGetProperty("pbrMetallicRoughness", out var pbr) || !pbr.TryGetProperty("baseColorTexture", out var bt)) return (null, 0);
            int tex = Int(bt, "index", -1);
            if (tex < 0 || tex >= textures.Count) return (null, 0);
            return (ImageFinish(Int(textures[tex], "source", -1)), Int(bt, "texCoord", 0));
        }

        var mesh = new MeshData();
        void AddMesh(int meshIndex, Matrix4x4 world)
        {
            Matrix4x4.Invert(world, out var inverse);
            var normalMatrix = Matrix4x4.Transpose(inverse);
            bool flip = world.GetDeterminant() < 0;
            foreach (var prim in Items(meshes[meshIndex], "primitives"))
            {
                if (Int(prim, "mode", 4) != 4) continue;  // triangles only
                var attrs = prim.GetProperty("attributes");
                if (!attrs.TryGetProperty("POSITION", out var pos)) continue;
                var p = Floats(pos.GetInt32());
                var n = attrs.TryGetProperty("NORMAL", out var nrm) ? Floats(nrm.GetInt32()) : null;
                var c = attrs.TryGetProperty("COLOR_0", out var col) ? Floats(col.GetInt32()) : null;
                int material = Int(prim, "material", -1);
                var baseColour = BaseColour(material);
                var (texFinish, texCoord) = BaseTexture(material);
                var uv = texFinish != null && attrs.TryGetProperty($"TEXCOORD_{texCoord}", out var tc) ? Floats(tc.GetInt32()) : null;
                if (uv == null || uv.Length < p.Length) texFinish = null;
                int fin = texFinish != null ? mesh.FinishIndex(texFinish) : 0;
                uint texColour = texFinish != null ? Assets.Finishes.VertexColour(texFinish) : 0;
                var idx = prim.TryGetProperty("indices", out var ii) ? Floats(ii.GetInt32()).Select(r => (int)r[0]).ToArray() : Enumerable.Range(0, p.Length).ToArray();
                int s = mesh.Positions.Count;
                for (int i = 0; i < p.Length; i++)
                {
                    var at = Vector3.Transform(new Vector3(p[i][0], p[i][1], p[i][2]), world);
                    var normal = n != null ? Vector3.Normalize(Vector3.TransformNormal(new Vector3(n[i][0], n[i][1], n[i][2]), normalMatrix)) : Vector3.Zero;
                    var rgba = baseColour * (c == null ? Vector4.One : new Vector4(c[i][0], c[i][1], c[i][2], c[i].Length > 3 ? c[i][3] : 1));
                    // A textured vertex's own colour is its texture's average, for a far model that's lost it.
                    mesh.Add(at, normal, fin > 0 ? texColour : Srgb(rgba), fin, fin > 0 ? new Vector2(uv![i][0], uv[i][1]) : Vector2.Zero);
                }
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], d = idx[t + 2];
                    if (a >= p.Length || b >= p.Length || d >= p.Length || a < 0 || b < 0 || d < 0) throw new InvalidDataException("index past the vertices");
                    if (flip) (b, d) = (d, b);
                    mesh.Indices.AddRange(new[] { s + a, s + b, s + d });
                }
                if (n == null) FaceNormals(mesh, s);
            }
        }

        void Visit(int node, Matrix4x4 parent, int depth)
        {
            if (depth > 64 || node < 0 || node >= nodes.Count) return;
            var e = nodes[node];
            var local = Matrix4x4.Identity;
            if (e.TryGetProperty("matrix", out var mx) && mx.GetArrayLength() == 16)
            {
                var v = mx.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                // glTF matrices are column-major with column vectors; System.Numerics uses row vectors, so this reads straight in.
                local = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            }
            else
            {
                var sc = e.TryGetProperty("scale", out var s3) ? new Vector3(s3[0].GetSingle(), s3[1].GetSingle(), s3[2].GetSingle()) : Vector3.One;
                var rot = e.TryGetProperty("rotation", out var r4) ? new Quaternion(r4[0].GetSingle(), r4[1].GetSingle(), r4[2].GetSingle(), r4[3].GetSingle()) : Quaternion.Identity;
                var tr = e.TryGetProperty("translation", out var t3) ? new Vector3(t3[0].GetSingle(), t3[1].GetSingle(), t3[2].GetSingle()) : Vector3.Zero;
                local = Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(tr);
            }
            var world = local * parent;
            if (e.TryGetProperty("mesh", out var mi) && mi.GetInt32() is var k && k >= 0 && k < meshes.Count) AddMesh(k, world);
            foreach (var child in Items(e, "children")) Visit(child.GetInt32(), world, depth + 1);
        }

        var scenes = Items(root, "scenes").ToList();
        int scene = Int(root, "scene", 0);
        IEnumerable<int> roots = scenes.Count > scene
            ? Items(scenes[scene], "nodes").Select(x => x.GetInt32())
            : Enumerable.Range(0, nodes.Count).Except(nodes.SelectMany(n => Items(n, "children").Select(c => c.GetInt32())));
        foreach (int r in roots.ToList()) Visit(r, Matrix4x4.Identity, 0);
        if (nodes.Count == 0) for (int k = 0; k < meshes.Count; k++) AddMesh(k, Matrix4x4.Identity);
        return mesh;
    }

    /// <summary>Normals for vertices from <paramref name="from"/> on, from the triangles that use them.</summary>
    static void FaceNormals(MeshData m, int from)
    {
        var sum = new Vector3[m.Positions.Count - from];
        for (int t = 0; t + 2 < m.Indices.Count; t += 3)
        {
            int a = m.Indices[t], b = m.Indices[t + 1], c = m.Indices[t + 2];
            if (a < from) continue;
            var n = Vector3.Cross(m.Positions[b] - m.Positions[a], m.Positions[c] - m.Positions[a]);
            sum[a - from] += n; sum[b - from] += n; sum[c - from] += n;
        }
        for (int i = 0; i < sum.Length; i++) m.Normals[from + i] = sum[i].LengthSquared() > 0 ? Vector3.Normalize(sum[i]) : Vector3.UnitY;
    }

    /// <summary>A linear glTF colour as packed sRGB bytes, as the voxel palettes hold them.</summary>
    static uint Srgb(Vector4 c)
    {
        static byte Gamma(float v)
        {
            v = Math.Clamp(v, 0, 1);
            float s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
            return (byte)Math.Round(s * 255);
        }
        return VoxModel.Rgba(Gamma(c.X), Gamma(c.Y), Gamma(c.Z), (byte)Math.Round(Math.Clamp(c.W, 0, 1) * 255));
    }

    /// <summary>
    /// A model in metres set on a lot w × d cells: one cell is 16 m, it stands on the ground at the lot's corner,
    /// and one too big for the lot is shrunk to fit.
    /// </summary>
    public static MeshData FitToLot(MeshData m, int w, int d)
    {
        if (m.Positions.Count == 0) return m;
        var min = m.Positions.Aggregate(Vector3.Min);
        var max = m.Positions.Aggregate(Vector3.Max);
        float s = 1f / 16;
        var size = (max - min) * s;
        s *= Math.Min(1, Math.Min(size.X > 0 ? w / size.X : 1, size.Z > 0 ? d / size.Z : 1));
        var r = new MeshData();
        r.Normals.AddRange(m.Normals);
        r.Colors.AddRange(m.Colors);
        r.Indices.AddRange(m.Indices);
        r.UVs.AddRange(m.UVs);
        r.Finish.AddRange(m.Finish);
        r.FinishIds.Clear();
        r.FinishIds.AddRange(m.FinishIds);
        foreach (var p in m.Positions) r.Positions.Add((p - min) * s);
        return r;
    }

    /// <summary>
    /// Writes a mesh (world axes, metres) as a .glb (RC part 5), for a modelling program or for the city to draw: one
    /// primitive for its plain colours (as vertex colours) and one for each finish, with its texture as a PNG inside
    /// the file and its coordinates in the texture's own (a by-the-metre finish's divided by its tile).
    /// </summary>
    public static byte[] Write(MeshData mesh)
    {
        mesh = new MeshData().Append(mesh).Pad();
        var bin = new MemoryStream();
        var views = new System.Text.Json.Nodes.JsonArray();
        var accessors = new System.Text.Json.Nodes.JsonArray();
        int View(byte[] bytes, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var v = new System.Text.Json.Nodes.JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = bytes.Length };
            if (target is { } t) v["target"] = t;
            bin.Write(bytes);
            views.Add(v);
            return views.Count - 1;
        }
        int Accessor(float[] data, int width, string type, bool minMax = false)
        {
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            var a = new System.Text.Json.Nodes.JsonObject { ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = data.Length / width, ["type"] = type };
            if (minMax && data.Length >= width)
            {
                var lo = new float[width]; var hi = new float[width];
                for (int k = 0; k < width; k++) { lo[k] = float.MaxValue; hi[k] = float.MinValue; }
                for (int i = 0; i < data.Length; i++) { lo[i % width] = Math.Min(lo[i % width], data[i]); hi[i % width] = Math.Max(hi[i % width], data[i]); }
                a["min"] = new System.Text.Json.Nodes.JsonArray(lo.Select(x => (System.Text.Json.Nodes.JsonNode)x).ToArray());
                a["max"] = new System.Text.Json.Nodes.JsonArray(hi.Select(x => (System.Text.Json.Nodes.JsonNode)x).ToArray());
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }
        int Indices(int[] data)
        {
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            accessors.Add(new System.Text.Json.Nodes.JsonObject { ["bufferView"] = View(bytes, 34963), ["componentType"] = 5125, ["count"] = data.Length, ["type"] = "SCALAR" });
            return accessors.Count - 1;
        }
        static float Linear(byte b) { float v = b / 255f; return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f); }

        var materials = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["name"] = "colours", ["pbrMetallicRoughness"] = new System.Text.Json.Nodes.JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 0.9 } } };
        var images = new System.Text.Json.Nodes.JsonArray();
        var textures = new System.Text.Json.Nodes.JsonArray();
        var prims = new System.Text.Json.Nodes.JsonArray();
        // The triangles by finish (their first corner's), each group a primitive with its own vertices.
        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int f = mesh.Textured ? mesh.Finish[mesh.Indices[t]] : 0;
            if (f > 0 && Assets.Finishes.Get(mesh.FinishIds[f]) == null) f = 0;
            (groups.TryGetValue(f, out var g) ? g : groups[f] = new List<int>()).Add(t);
        }
        foreach (var (f, tris) in groups.OrderBy(g => g.Key))
        {
            var finish = f > 0 ? Assets.Finishes.Get(mesh.FinishIds[f]) : null;
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            var idx = new List<int>();
            foreach (int t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int v = mesh.Indices[t + k];
                    if (!map.TryGetValue(v, out int at)) { map[v] = at = order.Count; order.Add(v); }
                    idx.Add(at);
                }
            var attrs = new System.Text.Json.Nodes.JsonObject
            {
                ["POSITION"] = Accessor(order.SelectMany(v => new[] { mesh.Positions[v].X, mesh.Positions[v].Y, mesh.Positions[v].Z }).ToArray(), 3, "VEC3", minMax: true),
                ["NORMAL"] = Accessor(order.SelectMany(v => { var n = mesh.Normals[v]; n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY; return new[] { n.X, n.Y, n.Z }; }).ToArray(), 3, "VEC3"),
            };
            int material = 0;
            if (finish != null)
            {
                attrs["TEXCOORD_0"] = Accessor(order.SelectMany(v =>
                {
                    var uv = mesh.UVs[v];
                    return finish.Mapped ? new[] { uv.X, uv.Y } : new[] { (float)(uv.X / finish.TileW), (float)(-uv.Y / finish.TileH) };
                }).ToArray(), 2, "VEC2");
                images.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = finish.Id, ["mimeType"] = "image/png", ["bufferView"] = View(ImageDecode.WritePng(finish.Size, finish.SizeY, finish.Rgba), null) });
                textures.Add(new System.Text.Json.Nodes.JsonObject { ["source"] = images.Count - 1 });
                materials.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = finish.Name,
                    ["pbrMetallicRoughness"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["baseColorTexture"] = new System.Text.Json.Nodes.JsonObject { ["index"] = textures.Count - 1 },
                        ["metallicFactor"] = finish.Metallic, ["roughnessFactor"] = finish.Roughness,
                    },
                });
                material = materials.Count - 1;
            }
            else attrs["COLOR_0"] = Accessor(order.SelectMany(v => { var (r, g, b, a) = VoxModel.Unpack(mesh.Colors[v]); return new[] { Linear(r), Linear(g), Linear(b), a / 255f }; }).ToArray(), 4, "VEC4");
            prims.Add(new System.Text.Json.Nodes.JsonObject { ["attributes"] = attrs, ["indices"] = Indices(idx.ToArray()), ["material"] = material, ["mode"] = 4 });
        }
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["asset"] = new System.Text.Json.Nodes.JsonObject { ["version"] = "2.0", ["generator"] = "ToyCity asset editor" },
            ["scene"] = 0,
            ["scenes"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["nodes"] = new System.Text.Json.Nodes.JsonArray { 0 } } },
            ["nodes"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["mesh"] = 0 } },
            ["meshes"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["primitives"] = prims } },
            ["materials"] = materials, ["accessors"] = accessors, ["bufferViews"] = views,
            ["buffers"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["byteLength"] = bin.Length } },
        };
        if (images.Count > 0) { root["images"] = images; root["textures"] = textures; }
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonLen = (json.Length + 3) / 4 * 4;
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonLen + 8 + bin.Length));
        w.Write((uint)jsonLen); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jsonLen; i++) w.Write((byte)' ');
        w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
        w.Flush();
        return o.ToArray();
    }
}
