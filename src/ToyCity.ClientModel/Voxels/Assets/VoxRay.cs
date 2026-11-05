namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// What a ray from the camera points at in a voxel model: the first solid voxel and the face it came in through,
/// or, missing them all, the ground under the model. Coordinates are the model's voxels, x east, y south, z up.
/// </summary>
public readonly record struct VoxHit(int X, int Y, int Z, int Nx, int Ny, int Nz, bool Ground)
{
    /// <summary>The empty voxel in front of the face that was hit, where an added voxel goes.</summary>
    public (int X, int Y, int Z) Front => (X + Nx, Y + Ny, Z + Nz);
}

public static class VoxRay
{
    /// <summary>
    /// Walks the ray voxel by voxel (Amanatides and Woo) through the model's box. Missing every voxel, a ray going
    /// down that meets the ground (z 0) inside the model's footprint hits the ground: the hit is then the voxel just
    /// under it (z −1), facing up, so its <see cref="VoxHit.Front"/> is the voxel on the ground.
    /// </summary>
    public static VoxHit? Cast(VoxModel m, double ox, double oy, double oz, double dx, double dy, double dz, double maxT = 2000)
    {
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-12) return null;
        (dx, dy, dz) = (dx / len, dy / len, dz / len);
        // Clip the ray to the model's box, so a camera far outside starts at its surface.
        double t0 = 0, t1 = maxT;
        if (!Slab(ox, dx, m.SizeX, ref t0, ref t1) || !Slab(oy, dy, m.SizeY, ref t0, ref t1) || !Slab(oz, dz, m.SizeZ, ref t0, ref t1))
            return GroundHit(m, ox, oy, oz, dx, dy, dz);
        double e = 1e-7;
        double px = ox + dx * (t0 + e), py = oy + dy * (t0 + e), pz = oz + dz * (t0 + e);
        int x = Math.Clamp((int)Math.Floor(px), 0, m.SizeX - 1), y = Math.Clamp((int)Math.Floor(py), 0, m.SizeY - 1), z = Math.Clamp((int)Math.Floor(pz), 0, m.SizeZ - 1);
        int sx = Math.Sign(dx), sy = Math.Sign(dy), sz = Math.Sign(dz);
        double Next(double p, int cell, int s, double d) => s == 0 ? double.PositiveInfinity : ((s > 0 ? cell + 1 : cell) - p) / d;
        double tx = t0 + Next(px, x, sx, dx), ty = t0 + Next(py, y, sy, dy), tz = t0 + Next(pz, z, sz, dz);
        double ddx = sx == 0 ? double.PositiveInfinity : Math.Abs(1 / dx), ddy = sy == 0 ? double.PositiveInfinity : Math.Abs(1 / dy), ddz = sz == 0 ? double.PositiveInfinity : Math.Abs(1 / dz);
        // The face a ray starting inside first enters through: the side the ray came from on its main axis.
        int nx = 0, ny = 0, nz = 0;
        double ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
        if (ax >= ay && ax >= az) nx = -sx; else if (ay >= az) ny = -sy; else nz = -sz;
        while (m.In(x, y, z))
        {
            if (m[x, y, z] != 0) return new VoxHit(x, y, z, nx, ny, nz, false);
            if (tx <= ty && tx <= tz) { if (tx > t1) break; x += sx; tx += ddx; (nx, ny, nz) = (-sx, 0, 0); }
            else if (ty <= tz) { if (ty > t1) break; y += sy; ty += ddy; (nx, ny, nz) = (0, -sy, 0); }
            else { if (tz > t1) break; z += sz; tz += ddz; (nx, ny, nz) = (0, 0, -sz); }
        }
        return GroundHit(m, ox, oy, oz, dx, dy, dz);
    }

    static VoxHit? GroundHit(VoxModel m, double ox, double oy, double oz, double dx, double dy, double dz)
    {
        if (dz >= 0 || oz <= 0) return null;
        double t = -oz / dz;
        int x = (int)Math.Floor(ox + dx * t), y = (int)Math.Floor(oy + dy * t);
        if (x < 0 || y < 0 || x >= m.SizeX || y >= m.SizeY) return null;
        return new VoxHit(x, y, -1, 0, 0, 1, true);
    }

    static bool Slab(double o, double d, int size, ref double t0, ref double t1)
    {
        if (Math.Abs(d) < 1e-12) return o >= 0 && o <= size;
        double a = (0 - o) / d, b = (size - o) / d;
        if (a > b) (a, b) = (b, a);
        t0 = Math.Max(t0, a);
        t1 = Math.Min(t1, b);
        return t0 <= t1;
    }
}
