namespace ToyCity.Sim;

/// <summary>Seeded xoshiro256** generator. The only source of randomness in the simulation.</summary>
public sealed class Rng
{
    ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    Rng() { }

    static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextU64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform integer in [0, n).</summary>
    public int Next(int n) => n <= 1 ? 0 : (int)(NextU64() % (ulong)n);

    /// <summary>True with probability percent/100.</summary>
    public bool Chance(int percent) => Next(100) < percent;

    public void Write(BinaryWriter w) { w.Write(_s0); w.Write(_s1); w.Write(_s2); w.Write(_s3); }

    public static Rng Read(BinaryReader r) => new() { _s0 = r.ReadUInt64(), _s1 = r.ReadUInt64(), _s2 = r.ReadUInt64(), _s3 = r.ReadUInt64() };
}
