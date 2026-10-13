using System.Diagnostics;
using Roboc.Core.Model;

namespace Roboc.Core.Progress;

/// <summary>
/// Feeds the rate estimator on a steady clock and publishes snapshots. Decoupled
/// from the work path on purpose: sampling per item would put a shared lock in the
/// hot loop and is exactly the mistake that makes Explorer's dialog slow.
/// </summary>
internal sealed class JobSampler : IDisposable
{
    private readonly ProgressAggregator _progress;
    private readonly Action<ProgressSnapshot>? _onProgress;
    private readonly Stopwatch _elapsed;
    private readonly RateEstimator _rate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _stopping = new(false);
    private int _disposed;

    public JobSampler(ProgressAggregator progress, Action<ProgressSnapshot>? onProgress, Stopwatch elapsed)
    {
        _progress = progress;
        _onProgress = onProgress;
        _elapsed = elapsed;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "roboc-sampler",
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
    }

    private void Loop()
    {
        _started.Set();

        while (!_stopping.Wait(100))
        {
            _rate.Add(_progress.FilesDone, _progress.BytesDone);

            Action<ProgressSnapshot>? callback = _onProgress;
            callback?.Invoke(_progress.Snapshot(_rate, _elapsed.Elapsed));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Set the flag *and* wake the loop. Waiting out a fixed sleep here would be
        // added to every job's wall clock -- invisible in a long copy, glaring in a
        // short one. This was a real measured defect once.
        if (_started.IsSet)
        {
            _stopping.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
        _stopping.Dispose();
    }
}
