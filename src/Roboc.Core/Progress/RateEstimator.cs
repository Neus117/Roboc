namespace Roboc.Core.Progress;

/// <summary>
/// Throughput over a sliding window, and the ETA derived from it.
///
/// ETA is computed from the byte rate, not the file rate. For a homogeneous
/// workload the two are proportional and agree; for a mixed one the byte rate is
/// the only correct answer, because a few large files left at the end dominate
/// the remaining time while the file count says "almost done". Taking
/// max(etaByFiles, etaByBytes) is badly pessimistic at the start of a mixed job
/// and is deliberately not done here.
/// </summary>
public sealed class RateEstimator
{
    private const int Capacity = 128;

    private readonly Sample[] _samples = new Sample[Capacity];
    private readonly TimeSpan _window;
    private int _count;
    private int _next;

    private readonly record struct Sample(long TimestampMs, long Files, long Bytes);

    public RateEstimator(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(5);

    /// <summary>Called on the sampler tick (~10 Hz), never per file.</summary>
    public void Add(long files, long bytes)
    {
        _samples[_next] = new Sample(Environment.TickCount64, files, bytes);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    public (double FilesPerSecond, double BytesPerSecond) Rates()
    {
        if (_count < 2)
        {
            return (0, 0);
        }

        Sample newest = _samples[(_next - 1 + Capacity) % Capacity];
        long cutoff = newest.TimestampMs - (long)_window.TotalMilliseconds;

        Sample oldest = _samples[(_next - 2 + Capacity) % Capacity];
        for (int back = 2; back < _count; back++)
        {
            Sample candidate = _samples[(_next - 1 - back + (Capacity * 2)) % Capacity];
            if (candidate.TimestampMs < cutoff)
            {
                break;
            }

            oldest = candidate;
        }

        double seconds = (newest.TimestampMs - oldest.TimestampMs) / 1000.0;
        if (seconds < 0.2)
        {
            return (0, 0);
        }

        double files = newest.Files - oldest.Files;
        double bytes = newest.Bytes - oldest.Bytes;

        // A window in which nothing finished must not report a fake rate.
        return (files <= 0 ? 0 : files / seconds, bytes <= 0 ? 0 : bytes / seconds);
    }

    public TimeSpan? Estimate(long remainingFiles, long remainingBytes)
    {
        (double filesPerSecond, double bytesPerSecond) = Rates();

        if (remainingBytes > 0 && bytesPerSecond > 64 * 1024)
        {
            return Clamp(remainingBytes / bytesPerSecond);
        }

        if (remainingFiles > 0 && filesPerSecond > 0.5)
        {
            return Clamp(remainingFiles / filesPerSecond);
        }

        if (remainingBytes <= 0 && remainingFiles <= 0)
        {
            return TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>An ETA of "14 years" is noise, not information.</summary>
    private static TimeSpan? Clamp(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return null;
        }

        return seconds > TimeSpan.FromDays(1).TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
    }
}
