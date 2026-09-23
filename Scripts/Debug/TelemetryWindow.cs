using System;

namespace VerdantCrown.Debug;

/// <summary>
/// A frame-time window that reports a DISTRIBUTION, never an average: p50/p95/p99 (nearest-rank),
/// max, jitter and the counts over the 16.67 / 33.33 ms thresholds — plus the mean of a
/// per-frame counter (renderer primitives) accumulated over EXACTLY the same frames, so a rate
/// and its counters can never describe different windows.
/// <para><c>Jitter</c> is σ÷μ (coefficient of variation), because this game's acceptance
/// rule is <c>jit &lt; 0.05</c>. A max-to-median ratio can be derived separately as
/// <c>max_ms ÷ p50_ms</c>; see docs/INSTRUMENTATION.md.</para>
/// <para>Ring mode has a fixed capacity and overwrites the oldest frame; growable mode
/// accumulates until <see cref="Reset"/>. Not thread-safe: only ever touched from the frame loop.</para>
/// </summary>
internal sealed class TelemetryWindow
{
    private double[] _ms = Array.Empty<double>();
    private double[] _prims = Array.Empty<double>();
    private readonly int _capacity; // 0 = growable
    private int _head;              // next write slot while filling/wrapping
    private int _count;

    private double _sumMs;
    private double _sumSqMs;
    private double _sumPrims;

    /// <summary>Ring window: keeps the most recent <paramref name="capacity"/> frames.</summary>
    internal TelemetryWindow(int capacity)
    {
        _capacity = capacity;
        _ms = new double[capacity];
        _prims = new double[capacity];
    }

    /// <summary>Growable window: every frame until <see cref="Reset"/> (zone summaries).</summary>
    internal TelemetryWindow()
    {
        _capacity = 0;
    }

    public int Count => _count;
    public double MeanMs => _count > 0 ? _sumMs / _count : 0;

    /// <summary>Mean of the per-frame counter sampled alongside the frame times (same window).</summary>
    public double MeanPrims => _count > 0 ? _sumPrims / _count : 0;

    /// <summary>
    /// Jitter = σ ÷ μ over the window's raw frame times. 0 is a perfectly even frame;
    /// the 60-lock acceptance rule is <c>jit &lt; 0.05</c>. Population stddev, no Bessel
    /// correction — the window is the whole population of interest, not a sample of one.
    /// </summary>
    public double Jitter
    {
        get
        {
            if (_count < 2)
            {
                return 0; // one frame has no dispersion; report 0, not NaN
            }

            double mean = _sumMs / _count;
            if (mean <= 0)
            {
                return 0;
            }

            double variance = (_sumSqMs / _count) - (mean * mean);
            return Math.Sqrt(Math.Max(variance, 0)) / mean;
        }
    }

    public void Add(double ms, double counter)
    {
        if (_count == _capacity && _capacity > 0)
        {
            // Ring full: retire the oldest slot before overwriting it.
            _sumMs -= _ms[_head];
            _sumSqMs -= _ms[_head] * _ms[_head];
            _sumPrims -= _prims[_head];
        }
        else if (_count == _ms.Length)
        {
            int next = Math.Max(_ms.Length * 2, 64);
            Array.Resize(ref _ms, next);
            Array.Resize(ref _prims, next);
        }

        int slot = (_capacity > 0 && _count == _capacity) ? _head : _count;
        _ms[slot] = ms;
        _prims[slot] = counter;
        _head = (_head + 1) % Math.Max(_capacity, 1);
        if (_capacity == 0 || _count < _capacity)
        {
            _count++;
        }

        _sumMs += ms;
        _sumSqMs += ms * ms;
        _sumPrims += counter;
    }

    public void Reset()
    {
        Array.Clear(_ms, 0, _ms.Length);
        Array.Clear(_prims, 0, _prims.Length);
        _head = 0;
        _count = 0;
        _sumMs = 0;
        _sumSqMs = 0;
        _sumPrims = 0;
    }

    /// <summary>Frames strictly above <paramref name="thresholdMs"/> (16.67 for over16, 33.33 for over33).</summary>
    public long Over(double thresholdMs)
    {
        long n = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_ms[i] > thresholdMs)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Nearest-rank percentiles over the raw frames (not interpolated): with ~60 frames per
    /// window an interpolation would imply a resolution the sample does not have. Copies once,
    /// sorts once — the caller needs p50/p95/p99/max from a single sort. Max is taken from the
    /// SAME snapshot (evicted frames must not be able to report themselves as the window max —
    /// a running max is stale the moment the ring wraps past it).
    /// </summary>
    public (double P50, double P95, double P99, double Max) Percentiles()
    {
        if (_count == 0)
        {
            return (0, 0, 0, 0);
        }

        double[] sorted = new double[_count];
        Array.Copy(_ms, sorted, _count);
        Array.Sort(sorted);
        return (Rank(sorted, 0.50), Rank(sorted, 0.95), Rank(sorted, 0.99), sorted[^1]);
    }

    private static double Rank(double[] sorted, double fraction)
    {
        int idx = (int)Math.Ceiling((fraction * sorted.Length) - 1);
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
