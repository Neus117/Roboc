using System.Collections.Concurrent;
using System.Diagnostics;
using Roboc.Core.Control;
using Roboc.Core.Diagnostics;
using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Model;
using Roboc.Core.Progress;

namespace Roboc.Core.Delete;

public sealed class DeleteOptions
{
    /// <summary>
    /// Required. Permanent deletion is refused without it.
    ///
    /// This exists because of a real incident: `delete D:` was accepted, and
    /// because Windows treats `D:` as a drive-relative path it resolved to the
    /// process's current directory and removed it. A destructive verb should not
    /// act on an argument nobody had to affirm.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// How long a staged item can be brought back before it is reclaimed.
    ///
    /// Default is a real undo window rather than an immediate unlink: the tree is
    /// detached from its location instantly (a single directory-entry update) and
    /// the bytes are reclaimed afterwards. Zero reclaims immediately, which is the
    /// old behaviour and is available, but is not the default any more.
    /// </summary>
    public TimeSpan UndoWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Skip staging and unlink in place. Faster to reclaim, no undo.</summary>
    public bool Direct { get; init; }

    public int WorkerThreads { get; init; }
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(120);
    public int QueueDepth { get; init; } = 16384;
    public bool SweepLeftoverTrash { get; init; } = true;
    public TimeSpan StallWarnAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StallAbortAfter { get; init; } = TimeSpan.FromSeconds(180);

    public static DeleteOptions Default { get; } = new();
}

public sealed class DeleteJob
{
    /// <summary>Files and/or directories to remove, exactly as the user typed them.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public DeleteOptions Options { get; init; } = DeleteOptions.Default;
    public JobControl Control { get; init; } = new();
    public ProgressAggregator Progress { get; init; } = new();

    public Action<JobError>? OnError { get; init; }
    public Action<ProgressSnapshot>? OnProgress { get; init; }
    public Action<string>? OnLog { get; init; }

    public bool Stream { get; init; }
}

public static class DeleteEngine
{
    public static int DefaultWorkerCount { get; } = Math.Clamp(Environment.ProcessorCount, 4, 32);

    private readonly record struct FailedItem(string Path, ErrorKind Kind, int Code, string Message, string Operation);

    public static JobResult Run(DeleteJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        LayoutAssert.Verify();

        if (!job.Options.Confirmed)
        {
            throw new IOException(
                "refusing to delete without confirmation. Pass --yes once you have checked the target.");
        }

        if (job.Options.SweepLeftoverTrash)
        {
            TrashStore.LoadAndSweep();
        }

        var sources = new List<string>(job.Sources.Count);

        foreach (string raw in job.Sources)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Order matters. The drive-relative check runs on the raw text, before
            // Path.GetFullPath silently turns "D:" into whatever the per-drive
            // current directory happens to be.
            GuardDriveRelative(raw);

            string extended = PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(raw));

            GuardAgainstVolumeRoot(extended, raw);
            GuardAgainstSelf(extended, raw);

            if (DirectoryOps.Exists(extended))
            {
                sources.Add(extended);
            }
        }

        if (sources.Count == 0)
        {
            throw new FileNotFoundException(Lang.Pick("指定的项目都不存在，可能已经被删除或移动过了。", "None of the given items exist; they may already have been deleted or moved."));
        }

        var stopwatch = Stopwatch.StartNew();
        var errors = new ConcurrentQueue<JobError>();

        void Report(FailedItem item)
        {
            var error = new JobError(item.Kind, PathUtil.ToDisplay(item.Path), item.Operation, item.Code, item.Message, 0);
            errors.Enqueue(error);
            job.OnError?.Invoke(error);
        }

        if (!job.Stream)
        {
            long items = 0;
            long bytes = 0;
            foreach (string source in sources)
            {
                CountTree(source, ref items, ref bytes);
            }

            job.Progress.SetTotals(items, bytes);
        }

        // Sampling is what turns the counters into snapshots. CopyEngine has had this
        // line all along and DeleteEngine never did, so OnProgress was declared,
        // wired all the way to the window and the terminal, and then never invoked --
        // a delete of any size reported nothing at all.
        using var sampler = new JobSampler(job.Progress, job.OnProgress, stopwatch);
        sampler.Start();

        JobState state = JobState.Completed;

        try
        {
            foreach (string source in sources)
            {
                job.Control.ThrowIfCancellationRequested();

                if (!DirectoryOps.IsExistingDirectory(source))
                {
                    DeleteSingle(job, source, directory: false, Report);
                    continue;
                }

                // Count before detaching: afterwards there is nothing to walk.
                long items = 0;
                long bytes = 0;
                CountTree(source, ref items, ref bytes);

                // A zero undo window means "no undo", which should unlink in place
                // rather than stage something the sweeper has no way to find.
                bool stageable = !job.Options.Direct && job.Options.UndoWindow > TimeSpan.Zero;

                if (stageable && TryStage(job, source, items, bytes, Report))
                {
                    continue;
                }

                DeleteTree(job, source, Report);
            }
        }
        catch (OperationCanceledException)
        {
            state = JobState.Cancelled;
        }
        catch (Exception ex)
        {
            state = JobState.Failed;
            Report(new FailedItem(job.Sources[0], ErrorKind.Unknown, 0, ex.Message, "Delete"));
        }

        stopwatch.Stop();

        if (state != JobState.Failed && job.Control.IsCancellationRequested)
        {
            state = JobState.Cancelled;
        }

        long failed = job.Progress.FilesFailed;
        if (state == JobState.Completed && failed > 0)
        {
            state = JobState.CompletedWithErrors;
        }

        return new JobResult
        {
            State = state,
            FilesCopied = job.Progress.FilesDone,
            FilesSkipped = job.Progress.FilesSkipped,
            FilesFailed = failed,
            BytesTransferred = job.Progress.BytesDone,
            Elapsed = stopwatch.Elapsed,
            Errors = errors.ToArray(),
        };
    }

    /// <summary>
    /// Detaches the tree to a same-volume staging directory and records it with
    /// an expiry. Returns false when staging is not possible, in which case the
    /// caller deletes in place.
    /// </summary>
    private static bool TryStage(DeleteJob job, string source, long items, long bytes, Action<FailedItem> report)
    {
        string parent = PathUtil.GetDirectoryName(PathUtil.TrimTrailingSeparator(source));

        if (string.IsNullOrEmpty(parent) || parent.Length < 3)
        {
            return false;
        }

        if (job.Options.SweepLeftoverTrash)
        {
            TrashStore.SweepSiblings(parent);
        }

        string trashDirectory = TrashStore.NewTrashDirectory(parent);

        if (!FastDeleter.TryMoveToTrash(source, trashDirectory, out string movedPath, out int moveError))
        {
            report(new FailedItem(
                source, ErrorKind.Delete, moveError,
                $"staging unavailable ({NativeError.DescribeWin32(moveError)}); deleting in place",
                "MoveFileEx"));
            return false;
        }

        TrashStore.Stage(trashDirectory, movedPath, source, job.Options.UndoWindow);
        job.Progress.AddItemsCompleted(items, bytes);

        job.OnLog?.Invoke(
            $"detached to {PathUtil.ToDisplay(trashDirectory)}; recoverable for " +
            $"{job.Options.UndoWindow.TotalMinutes:F0} minutes");

        return true;
    }

    // ------------------------------------------------------------------ guards

    /// <summary>
    /// Refuses a drive-relative path.
    ///
    /// `D:` is not "the D: drive" to Windows; it means "the current directory on
    /// D:", which is whatever some earlier process left there. This is not
    /// theoretical: in testing, `delete D:` resolved to the directory the process
    /// happened to be sitting in and removed it, and from a different working
    /// directory the same input resolved to a different tree entirely. A user
    /// typing `D:` intends the whole volume and gets an arbitrary directory
    /// deleted instead, so this must be refused before resolution.
    /// </summary>
    private static void GuardDriveRelative(string raw)
    {
        if (raw.Length < 2 || !char.IsLetter(raw[0]) || raw[1] != ':')
        {
            return;
        }

        // "D:\..." is absolute and fine. "D:" and "D:foo" are drive-relative.
        if (raw.Length == 2 || (raw[2] != '\\' && raw[2] != '/'))
        {
            throw new IOException(
                $"refusing a drive-relative path: '{raw}'. " +
                $"Use '{char.ToUpperInvariant(raw[0])}:\\<path>' for an absolute path.");
        }
    }

    /// <summary>
    /// Refuses a volume root. Deleting one is never what a caller means, and the
    /// cost of being wrong is the user's entire disk.
    /// </summary>
    private static void GuardAgainstVolumeRoot(string extendedPath, string raw)
    {
        string root = PathUtil.GetVolumeRoot(extendedPath);

        if (string.Equals(PathUtil.EnsureTrailingSeparator(extendedPath), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"refusing to delete a volume root: {raw}");
        }
    }

    /// <summary>
    /// Refuses any target that contains the running program or the current
    /// directory. Cheap insurance: the program cannot delete itself out from under
    /// itself, and a shell's working directory cannot be pulled away.
    /// </summary>
    private static void GuardAgainstSelf(string extendedPath, string raw)
    {
        string[] anchors =
        [
            PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(AppContext.BaseDirectory)),
            PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(Environment.CurrentDirectory)),
        ];

        string candidate = PathUtil.EnsureTrailingSeparator(extendedPath);

        foreach (string anchor in anchors)
        {
            if (string.IsNullOrEmpty(anchor))
            {
                continue;
            }

            if (PathUtil.EnsureTrailingSeparator(anchor).StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"refusing to delete '{raw}': it contains the running program or the current directory");
            }
        }
    }

    // ---------------------------------------------------------------- counting

    private static void CountTree(string root, ref long items, ref long bytes)
    {
        string start = PathUtil.TrimTrailingSeparator(root);

        if (!DirectoryOps.IsExistingDirectory(start))
        {
            items++;
            return;
        }

        var pending = new Stack<string>();
        var entries = new List<FindEntry>(64);
        pending.Push(start);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            items++;

            if (!DirectoryList.Enumerate(directory, entries))
            {
                continue;
            }

            foreach (FindEntry entry in entries)
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    pending.Push(entry.FullPath);
                }
                else
                {
                    items++;
                    bytes += entry.Size;
                }
            }
        }
    }

    // -------------------------------------------------------------------- work

    private static void DeleteSingle(DeleteJob job, string path, bool directory, Action<FailedItem> report)
    {
        for (int attempt = 0; ; attempt++)
        {
            job.Control.WaitWhilePaused();
            job.Control.ThrowIfCancellationRequested();

            if (FastDeleter.TryDelete(path, directory, out ErrorKind kind, out int code, out string message))
            {
                job.Progress.AddFileCompleted(0);
                return;
            }

            if (attempt >= job.Options.MaxRetries)
            {
                job.Progress.AddFileFailed();
                report(new FailedItem(path, kind, code, message,
                    directory ? "NtSetInformationFile(dir)" : "NtSetInformationFile"));
                return;
            }

            Thread.Sleep(job.Options.RetryDelay);
        }
    }

    private static void DeleteTree(DeleteJob job, string root, Action<FailedItem> report)
    {
        string start = PathUtil.TrimTrailingSeparator(root);

        int workerCount = job.Options.WorkerThreads > 0 ? job.Options.WorkerThreads : DefaultWorkerCount;

        using var queue = new BlockingCollection<string>(job.Options.QueueDepth);
        using var watchdog = new Watchdog(
            job.Options.StallWarnAfter, job.Options.StallAbortAfter, workerCount, job.OnLog);

        var workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            int index = i;
            var thread = new Thread(() =>
            {
                WatchdogSlot slot = watchdog.Register(index);
                try
                {
                    foreach (string path in queue.GetConsumingEnumerable(job.Control.Token))
                    {
                        slot.Report(path, 1);
                        DeleteSingle(job, path, directory: false, report);
                        slot.Report(null, 0);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            })
            {
                IsBackground = true,
                Name = $"roboc-delete-{i}",
            };

            thread.Start();
            workers[index] = thread;
        }

        // Deepest first: a directory can only be removed once its children are
        // gone, so bucketing by depth makes every directory within a bucket
        // independent, and therefore safe to remove in parallel.
        var byDepth = new List<List<string>>();
        var pending = new Stack<(string Path, int Depth)>();
        var entries = new List<FindEntry>(64);

        pending.Push((start, 0));

        try
        {
            while (pending.Count > 0)
            {
                job.Control.ThrowIfCancellationRequested();

                (string directory, int depth) = pending.Pop();

                while (byDepth.Count <= depth)
                {
                    byDepth.Add([]);
                }

                byDepth[depth].Add(directory);

                if (!DirectoryList.Enumerate(directory, entries))
                {
                    int code = NativeError.LastWin32();
                    report(new FailedItem(directory, ErrorKind.Enumerate, code,
                        NativeError.DescribeWin32(code), "FindFirstFileEx"));
                    continue;
                }

                foreach (FindEntry entry in entries)
                {
                    if (entry.IsDirectory)
                    {
                        if (entry.IsReparsePoint)
                        {
                            queue.Add(entry.FullPath, job.Control.Token);
                        }
                        else
                        {
                            pending.Push((entry.FullPath, depth + 1));
                        }
                    }
                    else
                    {
                        queue.Add(entry.FullPath, job.Control.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            queue.CompleteAdding();
            foreach (Thread thread in workers)
            {
                thread.Join();
            }
        }

        if (job.Control.IsCancellationRequested)
        {
            return;
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = job.Control.Token,
        };

        for (int depth = byDepth.Count - 1; depth >= 0; depth--)
        {
            if (job.Control.IsCancellationRequested)
            {
                return;
            }

            try
            {
                Parallel.ForEach(byDepth[depth], parallelOptions, dir =>
                    DeleteSingle(job, dir, directory: true, report));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Public entry point for the undo command.</summary>
    public static (int Restored, int Failed) RestoreStaged(
        IReadOnlyList<string> parentDirectories, Action<string>? log = null)
        => TrashStore.RestoreAll(parentDirectories, log);
}
