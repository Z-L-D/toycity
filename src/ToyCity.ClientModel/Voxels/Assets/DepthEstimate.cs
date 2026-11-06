namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// Something that guesses a depth map from a single photo: a monocular depth model run on the player's machine. None
/// ships yet; the photo importer's "Depth from the photo" button stays off until one is set in
/// <see cref="DepthEstimators.Current"/>. Its map goes through the same path as a depth map the player loads
/// (<see cref="PhotoImport.ReliefFromDepthMap"/>), so nothing past this needs to know where the depth came from.
/// See docs/notes/depth-from-a-photo.md for the planned model (Depth Anything V2 Small through ONNX Runtime).
/// </summary>
public interface IDepthEstimator
{
    /// <summary>What to call it in the editor, such as "Depth Anything V2 Small".</summary>
    string Name { get; }

    /// <summary>A depth map the same size as the photo, lighter nearer. May take a second or more: call it off the main thread.</summary>
    Photo Estimate(Photo photo, CancellationToken cancel = default);
}

/// <summary>The depth model the editor uses, if one is installed, and turning a model's raw output into a depth map.</summary>
public static class DepthEstimators
{
    /// <summary>The installed estimator, or null for none (the button is off).</summary>
    public static IDepthEstimator? Current { get; set; }

    /// <summary>
    /// A model's raw output (one value a pixel, row by row from the top, <paramref name="width"/> × <paramref name="height"/>)
    /// as a greyscale depth map, lighter nearer, resized to <paramref name="outWidth"/> × <paramref name="outHeight"/>.
    /// Models differ in which way round their numbers go: Depth Anything gives relative inverse depth (bigger is
    /// nearer); give <paramref name="biggerIsNearer"/> false for one that gives distance. The 2nd to 98th percentile is
    /// stretched to black and white, so a few wild values (the sky, a lens flare) don't flatten the rest.
    /// </summary>
    public static Photo ToDepthMap(float[] values, int width, int height, int outWidth, int outHeight, bool biggerIsNearer = true)
    {
        if (values.Length != width * height) throw new ArgumentException("values don't match the size", nameof(values));
        var sorted = values.Where(float.IsFinite).ToArray();
        Array.Sort(sorted);
        float lo = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * 0.02)], hi = sorted.Length == 0 ? 1 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.98))];
        float span = Math.Max(1e-6f, hi - lo);
        var map = new Photo(outWidth, outHeight);
        for (int y = 0; y < outHeight; y++)
            for (int x = 0; x < outWidth; x++)
            {
                // Nearest source value: the depth map is read again between the corners, averaged per voxel.
                int sx = Math.Min(width - 1, x * width / outWidth), sy = Math.Min(height - 1, y * height / outHeight);
                float v = values[sx + width * sy];
                double t = float.IsFinite(v) ? Math.Clamp((v - lo) / span, 0, 1) : 0;
                if (!biggerIsNearer) t = 1 - t;
                byte g = (byte)Math.Round(t * 255);
                map[x, y] = VoxModel.Rgba(g, g, g);
            }
        return map;
    }
}
