using Roboc.Core.Model;

namespace Roboc.Core.Progress;

/// <summary>
/// Counters shared by every worker, sampled by the UI on a timer.
///
/// Workers increment these and never touch the UI thread. Pushing one event per
/// file to the UI is precisely what makes Explorer's copy dialog slow on large
/// trees, so the data flows the other way: the UI polls at ~10 Hz and the hot
/// path only does an Interlocked.Add.
/// </summary>
public sealed class ProgressAggregator
{
    private long _filesDone;
    private long _filesFailed;
    private long _filesSkipped;
    private long _bytesDone;
    private long _filesTotal = -1;
    private long _bytesTotal = -1;
    private string? _currentItem;

    /// <summary>Bytes counted as already present, so the UI does not show 100%
    /// while work is still running.</summary>
    private long _bytesReused;

    public long FilesDone => Interlocked.Read(ref _filesDone);
    public long FilesFailed => Interlocked.Read(ref _filesFailed);
    public long FilesSkipped => Interlocked.Read(ref _filesSkipped);
    public long BytesDone => Interlocked.Read(ref _bytesDone);
    public long BytesReused => Interlocked.Read(ref _bytesReused);

    /// <summary>-1 while the totals are still unknown (streaming enumeration).</summary>
    public long FilesTotal => Interlocked.Read(ref _filesTotal);
    public long BytesTotal => Interlocked.Read(ref _bytesTotal);

    public string? CurrentItem => Volatile.Read(ref _currentItem);

    public void SetTotals(long files, long bytes)
    {
        Interlocked.Exchange(ref _filesTotal, files);
        Interlocked.Exchange(ref _bytesTotal, bytes);
    }

    /// <summary>
    /// Adds bytes as they are written, rather than only when a file completes.
    ///
    /// Without this a copy of several large files in parallel showed no movement at
    /// all: every worker finished at roughly the same moment, so the counters sat at
    /// zero for the whole operation and then jumped to their final values.
    /// </summary>
    public void AddBytes(long delta)
    {
        if (delta > 0)
        {
            Interlocked.Add(ref _bytesDone, delta);
        }
    }

    public void AddFileCompleted(long bytes)
    {
        Interlocked.Increment(ref _filesDone);
        Interlocked.Add(ref _bytesDone, bytes);
    }

    public void AddFileResumed(long bytesTransferred, long bytesReused)
    {
        Interlocked.Increment(ref _filesDone);
        Interlocked.Add(ref _bytesDone, bytesTransferred);
        Interlocked.Add(ref _bytesReused, bytesReused);
    }

    public void AddFileSkipped(long bytes)
    {
        Interlocked.Increment(ref _filesSkipped);
        Interlocked.Add(ref _bytesReused, bytes);
    }

    public void AddFileFailed() => Interlocked.Increment(ref _filesFailed);

    /// <summary>Records a whole batch at once. Used by instant delete, where one
    /// directory rename makes an entire tree unreachable and there is no per-item
    /// event to report. Negative counts undo a batch.</summary>
    public void AddItemsCompleted(long count, long bytes)
    {
        Interlocked.Add(ref _filesDone, count);
        Interlocked.Add(ref _bytesDone, bytes);
    }

    public void SetCurrentItem(string? item) => Volatile.Write(ref _currentItem, item);

    public ProgressSnapshot Snapshot(RateEstimator rate, TimeSpan elapsed)
    {
        long filesTotal = FilesTotal;
        long bytesTotal = BytesTotal;
        long filesDone = FilesDone;
        long skipped = FilesSkipped;
        long bytesDone = BytesDone;

        long filesRemaining = filesTotal < 0 ? -1 : Math.Max(0, filesTotal - filesDone - skipped);
        long bytesRemaining = bytesTotal < 0 ? -1 : Math.Max(0, bytesTotal - bytesDone - BytesReused);

        (double filesPerSecond, double bytesPerSecond) = rate.Rates();

        return new ProgressSnapshot(
            FilesTotal: filesTotal,
            FilesDone: filesDone,
            FilesFailed: FilesFailed,
            FilesSkipped: skipped,
            BytesTotal: bytesTotal,
            BytesDone: bytesDone,
            FilesPerSecond: filesPerSecond,
            BytesPerSecond: bytesPerSecond,
            Elapsed: elapsed,
            Remaining: rate.Estimate(filesRemaining, bytesRemaining),
            CurrentItem: CurrentItem);
    }
}
