using System.Runtime.InteropServices;
using Roboc.Core.Interop;

namespace Roboc.Core.Diagnostics;

/// <summary>Per-worker liveness record. Written by the worker on every chunk, read
/// by the watchdog thread.</summary>
internal sealed class WatchdogSlot
{
    private long _lastProgressTicks;
    private string? _currentPath;
    private int _stage;
    private uint _threadId;
    private int _warned;

    public long LastProgressTicks => Interlocked.Read(ref _lastProgressTicks);
    public string? CurrentPath => Volatile.Read(ref _currentPath);
    public int Stage => Volatile.Read(ref _stage);
    public uint ThreadId => Volatile.Read(ref _threadId);
    public bool Warned => Volatile.Read(ref _warned) != 0;

    public void MarkWarned() => Volatile.Write(ref _warned, 1);
    public void ClearWarned() => Volatile.Write(ref _warned, 0);

    /// <summary>Called by the worker thread itself so it can capture its own id
    /// for the cancellation path.</summary>
    public void BindThread() => Volatile.Write(ref _threadId, Kernel32.GetCurrentThreadId());

    public void Report(string? path, int stage)
    {
        Volatile.Write(ref _currentPath, path);
        Volatile.Write(ref _stage, stage);
        Interlocked.Exchange(ref _lastProgressTicks, Environment.TickCount64);
    }

    public void Disarm() => Interlocked.Exchange(ref _lastProgressTicks, 0);
}

/// <summary>
/// Detects workers wedged inside a filesystem call and, if they stay wedged,
/// forcibly cancels that call.
///
/// The failure this exists for is not a crash: it is the progress bar that stops
/// moving forever because one unreachable share or one bad sector is holding a
/// synchronous ReadFile. The watchdog turns that into a reported per-item failure
/// while the rest of the job continues.
/// </summary>
internal sealed class Watchdog : IDisposable
{
    private readonly TimeSpan _warnAfter;
    private readonly TimeSpan _abortAfter;
    private readonly WatchdogSlot[] _slots;
    private readonly Action<string>? _log;
    private readonly Thread _thread;

    /// <summary>Signalled to stop. The poll loop waits on this rather than
    /// sleeping, so shutdown is immediate: a fixed sleep here was a measured
    /// one-second tax on every job.</summary>
    private readonly ManualResetEventSlim _stop = new(false);

    public Watchdog(TimeSpan warnAfter, TimeSpan abortAfter, int workerCount, Action<string>? log)
    {
        _warnAfter = warnAfter;
        _abortAfter = abortAfter;
        _log = log;
        _slots = new WatchdogSlot[Math.Max(1, workerCount)];

        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new WatchdogSlot();
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "roboc-watchdog",
            Priority = ThreadPriority.AboveNormal,
        };

        _thread.Start();
    }

    public WatchdogSlot Register(int index)
    {
        WatchdogSlot slot = _slots[index];
        slot.BindThread();
        slot.Report(null, 0);
        return slot;
    }

    private void Loop()
    {
        double warnMs = _warnAfter.TotalMilliseconds;
        double abortMs = _abortAfter.TotalMilliseconds;

        while (true)
        {
            if (_stop.Wait(1000))
            {
                return;
            }

            long now = Environment.TickCount64;

            for (int i = 0; i < _slots.Length; i++)
            {
                WatchdogSlot slot = _slots[i];
                long last = slot.LastProgressTicks;

                // 0 means idle or not started; not a stall.
                if (last == 0)
                {
                    continue;
                }

                double stalled = now - last;

                if (stalled < warnMs)
                {
                    slot.ClearWarned();
                    continue;
                }

                string path = slot.CurrentPath ?? "(unknown)";

                if (!slot.Warned)
                {
                    slot.MarkWarned();
                    _log?.Invoke($"worker {i} stalled {stalled / 1000:F0}s during {DescribeStage(slot.Stage)}: {path}");
                }

                if (stalled >= abortMs)
                {
                    Abort(i, slot, path, stalled);
                }
            }
        }
    }

    /// <summary>CancelSynchronousIo is the only thing that reliably unwedges a
    /// thread sitting in a synchronous filesystem call.</summary>
    private void Abort(int index, WatchdogSlot slot, string path, double stalled)
    {
        uint threadId = slot.ThreadId;
        if (threadId == 0)
        {
            return;
        }

        nint handle = Kernel32.OpenThread(Kernel32.THREAD_TERMINATE, 0, threadId);
        if (handle == 0)
        {
            _log?.Invoke($"worker {index} stalled {stalled / 1000:F0}s and its thread handle could not be opened");
            return;
        }

        try
        {
            if (Kernel32.CancelSynchronousIo(handle) != 0)
            {
                _log?.Invoke($"cancelled stalled IO on worker {index} after {stalled / 1000:F0}s: {path}");
                slot.Report(null, 0);
                slot.ClearWarned();
            }
            else
            {
                _log?.Invoke($"worker {index} stalled {stalled / 1000:F0}s; CancelSynchronousIo failed: " +
                             NativeError.DescribeWin32(NativeError.LastWin32()));
            }
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    private static string DescribeStage(int stage) => stage switch
    {
        0 => "idle",
        1 => "file transfer",
        2 => "metadata/rename",
        _ => "unknown",
    };

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
