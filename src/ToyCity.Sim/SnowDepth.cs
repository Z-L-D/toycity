namespace ToyCity.Sim;

/// <summary>
/// How the city's snow lies over its ground (Beta 4, Part 4). Shared with the client model, which draws by it.
/// </summary>
public static class SnowDepth
{
    /// <summary>
    /// The snow lying at a place, in millimetres, from the city's depth: more on high ground (a quarter more for every
    /// 100 m above the city's median height, less below it), more on slopes facing north (out of the sun), and none on
    /// ground too steep to hold it. <paramref name="metresAboveMedian"/> is the ground's height over the median,
    /// <paramref name="slope"/> its rise over run, <paramref name="northness"/> -1 facing south to 1 facing north.
    /// The clients draw the same sum on the ground and on the roofs (roofs are level, so slope 0).
    /// </summary>
    public static float At(float groundMm, float metresAboveMedian, float slope, float northness)
    {
        if (groundMm <= 0) return 0;
        float height = Math.Clamp(1 + metresAboveMedian / 400f, 0.5f, 2f);
        float shade = 1 + 0.35f * Math.Clamp(northness, -1, 1);
        float hold = 1 - Math.Clamp((slope - 0.6f) / 0.4f, 0, 1);
        return groundMm * height * shade * hold;
    }
}
