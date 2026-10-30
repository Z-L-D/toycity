using System.Diagnostics;

namespace ToyCity.Server;

/// <summary>A token bucket: a burst of messages at once, then so many a second.</summary>
public sealed class RateLimiter(int burst, double perSecond, Func<double>? seconds = null)
{
    readonly Func<double> _now = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    double _tokens = burst, _at = double.NaN;

    /// <summary>Takes one token; false when the bucket is empty.</summary>
    public bool Take()
    {
        double now = _now();
        if (!double.IsNaN(_at)) _tokens = Math.Min(burst, _tokens + (now - _at) * perSecond);
        _at = now;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
