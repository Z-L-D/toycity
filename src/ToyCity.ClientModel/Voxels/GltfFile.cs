using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Reads glTF 2.0 models (.gltf with its buffers, or .glb) into a <see cref="MeshData"/>, for props drawn in a
/// modelling program rather than in voxels. It takes every triangle mesh in the default scene with its node
/// transforms, the vertex colours if there are any and each material's base colour; textures are not read.
/// Positions are in the file's metres, y up; the caller scales and places them.
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
                var baseColour = BaseColour(Int(prim, "material", -1));
                var idx = prim.TryGetProperty("indices", out var ii) ? Floats(ii.GetInt32()).Select(r => (int)r[0]).ToArray() : Enumerable.Range(0, p.Length).ToArray();
                int s = mesh.Positions.Count;
                for (int i = 0; i < p.Length; i++)
                {
                    mesh.Positions.Add(Vector3.Transform(new Vector3(p[i][0], p[i][1], p[i][2]), world));
                    mesh.Normals.Add(n != null ? Vector3.Normalize(Vector3.TransformNormal(new Vector3(n[i][0], n[i][1], n[i][2]), normalMatrix)) : Vector3.Zero);
                    var rgba = baseColour * (c == null ? Vector4.One : new Vector4(c[i][0], c[i][1], c[i][2], c[i].Length > 3 ? c[i][3] : 1));
                    mesh.Colors.Add(Srgb(rgba));
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
        foreach (var p in m.Positions) r.Positions.Add((p - min) * s);
        return r;
    }
}
