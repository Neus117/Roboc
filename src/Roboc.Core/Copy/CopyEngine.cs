using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Roboc.Core.Control;
using Roboc.Core.Delete;
using Roboc.Core.Diagnostics;
using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Journal;
using Roboc.Core.Model;
using Roboc.Core.Progress;

namespace Roboc.Core.Copy;

/// <summary>One file to transfer. Produced by the enumerator, consumed by a worker.</summary>
internal readonly struct WorkItem(
    string source, string relativePath, long size, long creationTime,
    long lastAccessTime, long lastWriteTime, uint attributes, bool destinationDirFresh)
{
    public readonly string Source = source;
    public readonly string RelativePath = relativePath;
    public readonly long Size = size;
    public readonly long CreationTime = creationTime;
    public readonly long LastAccessTime = lastAccessTime;
    public readonly long LastWriteTime = lastWriteTime;
    public readonly uint Attributes = attributes;

    /// <summary>True when this item's destination directory was created by this run
    /// and is therefore provably empty, which lets the worker skip its per-file
    /// existence probe -- one wasted syscall per file on a fresh copy.</summary>
    public readonly bool DestinationDirFresh = destinationDirFresh;
}

public sealed class CopyJob
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required string Destination { get; init; }

    public CopyOptions Options { get; init; } = CopyOptions.Default;
    public JobControl Control { get; init; } = new();
    public IJobJournal Journal { get; init; } = NullJobJournal.Instance;
    public ProgressAggregator Progress { get; init; } = new();

    public Action<JobError>? OnError { get; init; }

    /// <summary>Called with the relative path of each item that was actually
    /// written. Skipped items are not reported.</summary>
    public Action<string>? OnItemWritten { get; init; }
    public Action<ProgressSnapshot>? OnProgress { get; init; }
    public Action<string>? OnLog { get; init; }

    /// <summary>Delete each source item after it has been copied successfully.</summary>
    public bool Move { get; init; }

    /// <summary>Skip the pre-count pass: starts sooner, no percentage or ETA until
    /// enumeration finishes.</summary>
    public bool Stream { get; init; }

    /// <summary>Diagnostics only; leave null in normal operation.</summary>
    public CopyTimings? Timings { get; init; }
}

public static class CopyEngine
{
    /// <summary>
    /// The bottleneck for many small files is not CPU or bandwidth but NTFS
    /// metadata serialisation, so oversubscribing past the core count keeps more
    /// metadata operations in flight -- up to a point.
    ///
    /// Measured on 20,000 files of 4 KB: 8 workers 2867 files/s, 16 workers 3204,
    /// 24 workers 2700, 48 workers 2546. The regression past 16 is create and
    /// rename contending on the same parent directory's lock, so more threads
    /// queue rather than overlap. 16 is the cap rather than ProcessorCount
    /// because a wider machine would otherwise be pushed further into the
    /// contended regime by default.
    /// </summary>
    public static int DefaultWorkerCount { get; } = Math.Clamp(Environment.ProcessorCount, 4, 16);

    public static JobResult Run(CopyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        LayoutAssert.Verify();

        string destinationRoot = PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(job.Destination));
        bool destinationRootWasCreated = false;

        if (!DirectoryOps.Exists(destinationRoot))
        {
            if (!DirectoryOps.CreateIfMissing(destinationRoot, out int createError, out destinationRootWasCreated))
            {
                throw NativeError.CreateWin32("CreateDirectory", job.Destination, createError);
            }
        }
        else if (!DirectoryOps.IsExistingDirectory(destinationRoot))
        {
            throw new IOException($"destination is not a directory: {job.Destination}");
        }

        destinationRoot = PathUtil.EnsureTrailingSeparator(destinationRoot);

        List<SourcePlan> plans = BuildPlans(job, destinationRoot);

        var stopwatch = Stopwatch.StartNew();
        var errors = new ConcurrentQueue<JobError>();

        // Set when the job stops because the volume filled. Read once, after the
        // workers finish, to decide whether to look for staged files left behind.
        bool abortedOnDiskFull = false;   // NOSONAR - set from a worker thread

        void Report(JobError error)
        {
            errors.Enqueue(error);
            job.OnError?.Invoke(error);

            // A full disk stops the job instead of failing every remaining file.
            //
            // Without this the option was declared and never read, so a copy onto a
            // full volume attempted every remaining file in turn, failed each one, and
            // only reported the situation after the whole queue had been walked --
            // thousands of doomed writes and a long wait to learn one fact. Nothing is
            // lost by stopping: the writes are staged and renamed, so no destination
            // file is ever left half written.
            if (job.Options.AbortOnDiskFull && error.Kind == ErrorKind.DiskFull)
            {
                abortedOnDiskFull = true;

                job.OnLog?.Invoke(
                    Lang.Pick("磁盘空间不足，任务已中止。已完成的项目保留，尚未处理的项目未开始。", "Out of disk space; the job was stopped. Finished items are kept, unstarted ones were not begun."));

                job.Control.Cancel();
            }
        }

        if (!job.Stream)
        {
            long files = 0;
            long bytes = 0;

            foreach (SourcePlan plan in plans)
            {
                if (plan.IsDirectory)
                {
                    CountDirectory(plan.Source, ref files, ref bytes);
                }
                else
                {
                    files++;
                    bytes += plan.Size;
                }
            }

            job.Progress.SetTotals(files, bytes);
        }

        int workerCount = job.Options.WorkerThreads > 0 ? job.Options.WorkerThreads : DefaultWorkerCount;

        // Never spin up more workers than there is work for: a one-file job would
        // otherwise pay for two dozen threads and two dozen pinned megabyte buffers.
        if (job.Progress.FilesTotal > 0 && job.Progress.FilesTotal < workerCount)
        {
            workerCount = (int)Math.Max(2, job.Progress.FilesTotal);
        }

        using var queue = new BlockingCollection<WorkItem>(job.Options.QueueDepth);
        using var sampler = new JobSampler(job.Progress, job.OnProgress, stopwatch);
        using var watchdog = new Watchdog(
            job.Options.StallWarnAfter, job.Options.StallAbortAfter, workerCount, job.OnLog);

        var workers = new Thread[workerCount];
        var copiers = new FileCopier[workerCount];

        // Shared by every worker: file id -> the first destination that holds it.
        //
        // Only consulted when RecreateHardLinks is on, and only for files the
        // enumeration reported as having more than one link, so an ordinary copy never
        // touches it.
        var sharedFiles = new ConcurrentDictionary<HardLinkKey, string>();
        int linkMapFull = 0;

        for (int i = 0; i < workerCount; i++)
        {
            int index = i;
            copiers[index] = new FileCopier(job.Options, job.Options.CopyBufferSize, job.Timings);

            var thread = new Thread(() => WorkerLoop(job, queue, copiers[index], watchdog, index, destinationRoot, sharedFiles, () => Interlocked.Increment(ref linkMapFull), Report))
            {
                IsBackground = true,
                Name = $"roboc-copy-{index}",
            };

            thread.Start();
            workers[index] = thread;
        }

        JobState state = JobState.Completed;

        try
        {
            sampler.Start();

            foreach (SourcePlan plan in plans)
            {
                job.Control.ThrowIfCancellationRequested();

                if (plan.IsDirectory)
                {
                    ProduceDirectory(job, plan.Source, plan.RelativePrefix, destinationRoot, queue, Report);
                }
                else
                {
                    ProduceFile(job, plan.Source, plan.RelativePrefix, plan.Size,
                        plan.CreationTime, plan.LastAccessTime, plan.LastWriteTime, plan.Attributes,
                        destinationRootWasCreated, queue);
                }
            }
        }
        catch (OperationCanceledException)
        {
            state = JobState.Cancelled;
        }
        catch (Exception ex)
        {
            state = JobState.Failed;
            Report(new JobError(ErrorKind.Enumerate, job.Destination, "Enumerate", 0, ex.Message, 0));
        }
        finally
        {
            queue.CompleteAdding();

            foreach (Thread thread in workers)
            {
                thread.Join();
            }

            foreach (FileCopier copier in copiers)
            {
                copier.Dispose();
            }
        }

        stopwatch.Stop();
        job.Journal.Flush();

        if (state != JobState.Failed && job.Control.IsCancellationRequested)
        {
            state = JobState.Cancelled;
        }

        long failed = job.Progress.FilesFailed;
        if (state == JobState.Completed && failed > 0)
        {
            state = JobState.CompletedWithErrors;
        }

        // Reported, never removed.
        //
        // A staged write creates 目标.roboc-tmp before renaming it into place,
        // so an abort can leave one behind. Deleting them here would be this code
        // deciding on its own to destroy a file it did not create in this run -- a
        // leftover from an earlier crash is indistinguishable from one from this
        // abort. Naming them in the log costs nothing and answers the question the
        // moment it is asked.
        if (abortedOnDiskFull)
        {
            ReportLeftoverTemporaries(job);
        }

        if (Volatile.Read(ref linkMapFull) > 0)
        {
            job.OnLog?.Invoke(
                Lang.Pick($"硬链接映射已达上限 {LinkMapLimit:N0} 条，其后的多名称文件按独立副本复制。", $"The hard-link map reached its limit of {LinkMapLimit:N0}; files with several names after that point were copied as independent copies.") +
                Lang.Pick("目标占用会大于源，内容不受影响。", "The destination will use more space than the source. The contents are unaffected."));
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
    /// Names any staged files left behind by an aborted transfer.
    ///
    /// Bounded on purpose: it walks the destination only, stops after a set number of
    /// entries, and gives up rather than delaying the report the user is waiting for.
    /// A scan that hangs would be worse than no scan.
    /// </summary>
    private static void ReportLeftoverTemporaries(CopyJob job)
    {
        try
        {
            List<LeftoverTemp> found = FindLeftoverTemporaries(job.Destination);

            if (found.Count == 0)
            {
                job.OnLog?.Invoke(Lang.Pick("未发现残留的暂存文件。", "No leftover staging files were found."));
                return;
            }

            long total = found.Sum(f => f.Size);

            job.OnLog?.Invoke(
                Lang.Pick($"发现 {found.Count} 个残留的暂存文件，共 {Describe(total)}（未删除，供你确认后再处理）：", $"Found {found.Count} leftover staging files, {Describe(total)} in total. They have not been deleted, so you can look before deciding:"));

            foreach (LeftoverTemp item in found.Take(20))
            {
                job.OnLog?.Invoke($"  {Describe(item.Size),12}  {PathUtil.ToDisplay(item.Path)}");
            }

            if (found.Count > 20)
            {
                job.OnLog?.Invoke($"  ...{found.Count - 20} more not listed");
            }

            // The line that turns a report into a remedy. Without it the user is told
            // the disk filled up and shown files they are not supposed to delete, with
            // no stated connection between the two -- so the obvious next move, retrying
            // after freeing space elsewhere, fails again for a reason nothing explained.
            job.OnLog?.Invoke(
                Lang.Pick($"这些是上一次传输留下的临时文件。确认无用后删除它们可释放 {Describe(total)}，然后重新执行复制。", $"These are temporary files left by an earlier transfer. Once you are satisfied they are of no use, deleting them frees {Describe(total)}; then run the copy again."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            job.OnLog?.Invoke(Lang.Pick($"枚举残留暂存文件时出错：{ex.Message}", $"Error while scanning for leftover staging files: {ex.Message}"));
        }
    }

    /// <summary>A staged file left behind, and how much room it is holding.</summary>
    internal readonly record struct LeftoverTemp(string Path, long Size);

    /// <summary>
    /// Bytes as a person reads them. Local to this file because Core does not reference
    /// the App layer where the other formatter lives, and a shared one would have to
    /// move somewhere both can see.
    /// </summary>
    private static string Describe(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// Finds staged files left behind under a destination.
    ///
    /// Separate from the reporting so it can be tested without a full disk. The walk
    /// is the part with something to get wrong -- an unbounded queue, a suffix test
    /// that matches the wrong thing, a depth it never stops at -- and every one of
    /// those failures makes the abort path worse than the disk-full it is reporting on.
    /// A test can build a tree and call this; it cannot build a full disk.
    ///
    /// Bounded on purpose: stops after <paramref name="limit"/> finds and after
    /// <paramref name="visitBudget"/> entries examined, whichever comes first. A scan
    /// that hangs would delay the report the user is waiting for, which is worse than
    /// not scanning at all.
    /// </summary>
    internal static List<LeftoverTemp> FindLeftoverTemporaries(
        string destination, int limit = 200, int visitBudget = 20_000)
    {
        var found = new List<LeftoverTemp>();

        if (!DirectoryOps.IsExistingDirectory(PathUtil.ToExtended(destination)))
        {
            return found;
        }

        var pending = new Queue<string>();
        pending.Enqueue(PathUtil.ToExtended(destination));
        int visited = 0;

        while (pending.Count > 0 && found.Count < limit && visited < visitBudget)
        {
            string current = pending.Dequeue();
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(PathUtil.ToDisplay(current));
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (string entry in entries)
            {
                // Checked here as well as at the top of the loop, because one directory
                // can hold any number of files.
                //
                // With the test only in the while condition, a directory containing
                // forty staged files put all forty into the list before that condition
                // was read again. The bound was therefore "limit, rounded up to the size
                // of one directory" -- and a directory holding a hundred thousand
                // leftovers would allocate a hundred thousand paths during the abort,
                // which is the runaway this ceiling exists to prevent.
                if (found.Count >= limit || visited >= visitBudget)
                {
                    break;
                }

                visited++;

                if (entry.EndsWith(FileCopier.TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // The size is what makes the report actionable. A staged file is
                    // as large as the file it was staging, so one leftover can be
                    // holding every byte the retry needs -- which is exactly what the
                    // disk-full test showed: a 240 MB orphan on a 241 MB volume.
                    long size = 0;

                    try
                    {
                        var info = new FileInfo(PathUtil.ToDisplay(entry));
                        if (info.Exists)
                        {
                            size = info.Length;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }

                    found.Add(new LeftoverTemp(entry, size));
                    continue;
                }

                try
                {
                    if (Directory.Exists(entry))
                    {
                        pending.Enqueue(PathUtil.ToExtended(entry));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return found;
    }
    // ------------------------------------------------------------------- plans

    private readonly record struct SourcePlan(
        string Source, string RelativePrefix, bool IsDirectory,
        long Size, long CreationTime, long LastAccessTime, long LastWriteTime, uint Attributes);

    private readonly record struct MetadataOnly(
        long Size, long CreationTime, long LastAccessTime, long LastWriteTime, uint Attributes);

    private static List<SourcePlan> BuildPlans(CopyJob job, string destinationRoot)
    {
        var plans = new List<SourcePlan>(job.Sources.Count);
        string destinationDisplay = PathUtil.ToDisplay(destinationRoot);

        foreach (string raw in job.Sources)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Checked before ToExtended, which would otherwise treat a shell namespace
            // item as a relative path and hand the file APIs something silently wrong.
            //
            // Without this the failure reads "source not found" followed by the device's
            // namespace identifier -- which names neither the problem nor what to do
            // about it. See PathUtil.LooksLikeFileSystemPath for the shape of these paths.
            if (!PathUtil.LooksLikeFileSystemPath(raw))
            {
                throw new NotSupportedException(Lang.Pick(
                    $"⚠️这个来源不是文件系统里的项目，本工具读不了：\r\n\r\n{raw}\r\n\r\n" +
                    "它看起来是便携设备（手机、相机等以 MTP 连接）、库、搜索结果或压缩包里的内容 —— " +
                    "这些由资源管理器用 shell 命名空间表示，程序无法用文件 API 打开。\r\n\r\n" +
                    "请先用资源管理器把它复制到本地磁盘，再用本工具处理。",
                    $"⚠️This source is not an item in the file system, so this tool cannot read it:\r\n\r\n{raw}\r\n\r\n" +
                    "It appears to be inside a portable device (a phone or camera connected over MTP), " +
                    "a library, a search result or an archive -- all of which Explorer represents " +
                    "through the shell namespace, which file APIs cannot open.\r\n\r\n" +
                    "Copy it to a local disk with Explorer first, then use this tool on that copy."));
            }

            string source = PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(raw));

            if (!DirectoryOps.Exists(source))
            {
                throw new FileNotFoundException($"source not found: {raw}", raw);
            }

            bool isDirectory = DirectoryOps.IsExistingDirectory(source);
            string leaf = PathUtil.GetFileName(source);

            if (isDirectory)
            {
                GuardAgainstSelfCopy(source, leaf, destinationRoot, raw, destinationDisplay);
                plans.Add(new SourcePlan(source, leaf + '\\', true, 0, 0, 0, 0, 0));
            }
            else
            {
                MetadataOnly metadata = ReadMetadata(source);
                plans.Add(new SourcePlan(source, leaf, false, metadata.Size, metadata.CreationTime,
                    metadata.LastAccessTime, metadata.LastWriteTime, metadata.Attributes));
            }
        }

        if (plans.Count == 0)
        {
            throw new ArgumentException("no usable sources supplied", nameof(job));
        }

        return plans;
    }

    private static MetadataOnly ReadMetadata(string extendedPath)
    {
        if (DirectoryOps.TryGetAttributes(extendedPath, out WIN32_FILE_ATTRIBUTE_DATA data))
        {
            return new MetadataOnly(data.FileSize, data.ftCreationTime.ToLong(),
                data.ftLastAccessTime.ToLong(), data.ftLastWriteTime.ToLong(), data.dwFileAttributes);
        }

        return new MetadataOnly(0, 0, 0, 0, NativeConst.FILE_ATTRIBUTE_NORMAL);
    }

    /// <summary>
    /// Refuses a copy that would walk the directory it is writing into.
    ///
    /// Left unchecked that does not merely duplicate work: the enumeration finds what has
    /// just been written, writes it again, and never terminates.
    ///
    /// <b>What was wrong with the second test.</b> It refused whenever the source sat
    /// anywhere beneath the destination -- "the source is inside the destination" -- and
    /// that is not the same condition. Lifting a folder up a level is the ordinary case:
    /// taking H:\a\b\c to H:\a creates H:\a\c, a sibling of H:\a\b and nowhere near the
    /// source. Explorer allows it; this refused it, from the menu, with nothing the user
    /// could act on. Reported as "paste does nothing".
    ///
    /// The condition that matters is where the copy will <i>land</i>. It is recursive only
    /// when that path is inside a source -- H:\a\b\c into H:\a\b would create H:\a\b\c\c.
    /// </summary>
    private static void GuardAgainstSelfCopy(
        string sourceDir, string leaf, string destinationRoot, string rawSource, string destinationDisplay)
    {
        string sourceWithSeparator = PathUtil.EnsureTrailingSeparator(sourceDir);

        if (destinationRoot.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"refusing to copy '{rawSource}' into itself: destination '{destinationDisplay}' is inside the source");
        }

        // Copying a directory into the folder that already holds it lands on the source
        // itself. Not recursion -- the conflict pass deals with the name collision -- but it
        // is also never what was meant, and saying so is clearer than a folder that refuses
        // to be replaced by itself.
        if (string.Equals(destinationRoot, sourceDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"refusing to copy '{rawSource}' into itself: '{destinationDisplay}' is where it already is");
        }

        if (leaf.Length == 0)
        {
            // A volume root has no name to land under. Guarded elsewhere, and nothing here
            // can say anything useful about it.
            return;
        }

        // The leaf is passed in rather than worked out again, so this test and the plan that
        // decides where the copy actually goes cannot disagree. Computed twice, the two would
        // be free to drift -- and a guard checking a different path from the one the copy
        // uses is worse than no guard, because it reports safety it never verified.
        string landing = PathUtil.EnsureTrailingSeparator(Path.Combine(destinationRoot, leaf));

        if (landing.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"refusing to copy '{rawSource}' into '{destinationDisplay}': it would land inside itself");
        }
    }

    // -------------------------------------------------------------- count pass

    private static void CountDirectory(string directory, ref long files, ref long bytes)
    {
        var entries = new List<FindEntry>(64);
        if (!DirectoryList.Enumerate(directory, entries))
        {
            return;
        }

        foreach (FindEntry entry in entries)
        {
            if (entry.IsDirectory)
            {
                if (!entry.IsReparsePoint)
                {
                    CountDirectory(entry.FullPath, ref files, ref bytes);
                }
            }
            else
            {
                files++;
                bytes += entry.Size;
            }
        }
    }

    // ------------------------------------------------------------ produce pass

    private static void ProduceDirectory(
        CopyJob job, string sourceDirectory, string relativePrefix,
        string destinationRoot, BlockingCollection<WorkItem> queue,
        Action<JobError> report)
    {
        job.Control.ThrowIfCancellationRequested();

        string destinationDirectory = PathUtil.CombineExtended(destinationRoot, relativePrefix.TrimEnd('\\'));

        if (!DirectoryOps.CreateIfMissing(destinationDirectory, out int createError, out bool freshlyCreated))
        {
            report(new JobError(
                FileCopier.Classify(createError), PathUtil.ToDisplay(destinationDirectory),
                "CreateDirectory", createError, NativeError.DescribeWin32(createError), 0));
            job.Progress.AddFileFailed();
            return;
        }

        var entries = new List<FindEntry>(64);
        if (!DirectoryList.Enumerate(sourceDirectory, entries))
        {
            int code = NativeError.LastWin32();
            report(new JobError(
                ErrorKind.Enumerate, PathUtil.ToDisplay(sourceDirectory), "FindFirstFileEx",
                code, NativeError.DescribeWin32(code), 0));
            return;
        }

        // A directory this run created is provably empty, so every file landing in
        // it is new. That removes the per-file existence probe below.
        bool destinationDirFresh = freshlyCreated;

        // A directory that was created is itself a change, even if nothing is ever
        // written inside it. Empty folders produced no notification at all, because
        // only files reported, so they were never selected after a paste -- which is
        // why the omission looked intermittent: only wholly empty ones were missed.
        if (freshlyCreated && relativePrefix.Length > 0)
        {
            job.OnItemWritten?.Invoke(relativePrefix.TrimEnd('\\'));
        }

        foreach (FindEntry entry in entries)
        {
            job.Control.ThrowIfCancellationRequested();

            string relative = relativePrefix + entry.Name;

            // Handled before the directory/file split, because a reparse point can
            // be either and neither wants the normal treatment.
            if (entry.IsReparsePoint && !job.Options.FollowReparsePoints &&
                TryReproduceReparsePoint(job, entry, relative, destinationRoot, report))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                ProduceDirectory(job, entry.FullPath, relative + '\\', destinationRoot, queue, report);
            }
            else
            {
                ProduceFile(job, entry.FullPath, relative, entry.Size,
                    entry.CreationTime, entry.LastAccessTime, entry.LastWriteTime, entry.Attributes,
                    destinationDirFresh, queue);
            }
        }
    }

    /// <summary>
    /// Reproduces a reparse point at the destination, or reports why it cannot.
    ///
    /// Returns true when the entry is fully handled and the caller should move on,
    /// false when the caller should treat it as an ordinary file or directory and
    /// follow it.
    ///
    /// The split matters. A junction or symlink carries a target path that is valid
    /// anywhere, so the buffer is written verbatim. A cloud placeholder or a dedup
    /// entry does not: its data describes state owned by a driver that will not
    /// exist at the destination, so writing it would create an entry nothing can
    /// open. Those are followed instead, which copies the actual content. Skipping
    /// them would lose the data, and for OneDrive placeholders that is the normal
    /// case rather than a rare one.
    /// </summary>
    private static bool TryReproduceReparsePoint(
        CopyJob job, FindEntry entry, string relative, string destinationRoot, Action<JobError> report)
    {
        string display = PathUtil.ToDisplay(entry.FullPath);

        if (!ReparsePoints.TryRead(entry.FullPath, out byte[] data, out uint tag, out int readError))
        {
            job.Progress.AddFileFailed();
            report(new JobError(
                ErrorKind.Metadata, display, "FSCTL_GET_REPARSE_POINT", readError,
                NativeError.DescribeWin32(readError), 0));
            return true;
        }

        if (!ReparsePoints.IsReproducible(tag))
        {
            job.OnLog?.Invoke($"following reparse point 0x{tag:X8}: {display}");
            return false;
        }

        string destination = PathUtil.CombineExtended(destinationRoot, relative);

        if (!ReparsePoints.TryWrite(destination, entry.IsDirectory, data, out int writeError))
        {
            job.Progress.AddFileFailed();
            report(new JobError(
                ErrorKind.Metadata, display, "FSCTL_SET_REPARSE_POINT", writeError,
                $"{NativeError.DescribeWin32(writeError)} (tag 0x{tag:X8}, {data.Length} bytes, directory={entry.IsDirectory})", 0));
            return true;
        }

        job.Progress.AddFileCompleted(0);
        return true;
    }

    private static void ProduceFile(
        CopyJob job, string source, string relative, long size,
        long creationTime, long lastAccessTime, long lastWriteTime, uint attributes,
        bool destinationDirFresh, BlockingCollection<WorkItem> queue)
    {
        // A journal hit means this exact item already completed in an earlier run.
        if (job.Journal.Lookup(relative) is ItemOutcome done
            && done is ItemOutcome.Copied or ItemOutcome.Resumed or ItemOutcome.Replaced)
        {
            job.Progress.AddFileSkipped(size);
            return;
        }

        var item = new WorkItem(source, relative, size, creationTime, lastAccessTime,
            lastWriteTime, attributes, destinationDirFresh);

        try
        {
            queue.Add(item, job.Control.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with a cancellation; nothing left to do.
        }
    }

    // ------------------------------------------------------------ worker loop

    private static void WorkerLoop(
        CopyJob job, BlockingCollection<WorkItem> queue, FileCopier copier,
        Watchdog watchdog, int workerIndex, string destinationRoot,
        ConcurrentDictionary<HardLinkKey, string> sharedFiles, Action onMapFull,
        Action<JobError> report)
    {
        WatchdogSlot slot = watchdog.Register(workerIndex);
        var sink = new CheckpointSink(job, slot);

        try
        {
            foreach (WorkItem item in queue.GetConsumingEnumerable(job.Control.Token))
            {
                try
                {
                    job.Control.WaitWhilePaused();
                    if (job.Control.IsCancellationRequested)
                    {
                        return;
                    }

                    ProcessItem(job, copier, item, slot, sink, destinationRoot, sharedFiles, onMapFull, report);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A worker must never die and silently strand the remaining
                    // queue; convert anything unexpected into a reported failure.
                    job.Progress.AddFileFailed();
                    report(new JobError(ErrorKind.Unknown, item.Source, "Worker", 0,
                        $"{ex.GetType().Name}: {ex.Message}", 0));
                }
                finally
                {
                    // Between items this worker is idle, and an idle worker is not a
                    // stalled one.
                    //
                    // GetConsumingEnumerable blocks while the queue is empty, but the
                    // slot kept whatever timestamp the previous file left behind. A
                    // worker that finished its share early therefore looked stalled
                    // from thirty seconds later until the queue produced something
                    // else -- and when the queue never did, the log filled with
                    // "stalled ... during idle: (unknown)" for workers that were
                    // behaving perfectly. Disarming here means the stall check only
                    // ever sees a worker that is inside a copy, because Mark() re-arms
                    // the slot the moment one starts.
                    slot.Disarm();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static void ProcessItem(
        CopyJob job, FileCopier copier, in WorkItem item, WatchdogSlot slot,
        CheckpointSink sink, string destinationRoot,
        ConcurrentDictionary<HardLinkKey, string> sharedFiles, Action onMapFull,
        Action<JobError> report)
    {
        string relative = item.RelativePath;
        string destination = PathUtil.CombineExtended(destinationRoot, relative);
        string tempPath = FileCopier.TempPathFor(destination);

        CopyOptions options = job.Options;

        // One syscall decides skip vs proceed, before opening the source. Skipped
        // entirely when the destination directory was created by this run, because
        // then nothing can already be there.
        bool destinationExists = false;

        if (!item.DestinationDirFresh && options.Overwrite != OverwriteMode.OverwriteAll)
        {
            if (DirectoryOps.TryGetAttributes(destination, out WIN32_FILE_ATTRIBUTE_DATA existing))
            {
                destinationExists = true;
                if (existing.IsDirectory)
                {
                    job.Progress.AddFileFailed();
                    report(new JobError(ErrorKind.Write, PathUtil.ToDisplay(destination), "Copy", 0,
                        "a directory already exists with this name; not merged", 0));
                    return;
                }

                switch (options.Overwrite)
                {
                    case OverwriteMode.SkipIfSame
                        when existing.FileSize == item.Size
                          && existing.ftLastWriteTime.ToLong() == item.LastWriteTime:
                        job.Progress.AddFileSkipped(item.Size);
                        job.Journal.RecordCompleted(relative, ItemOutcome.SkippedSame, item.Size);
                        return;

                    case OverwriteMode.FailIfExists:
                        job.Progress.AddFileFailed();
                        report(new JobError(ErrorKind.Write, PathUtil.ToDisplay(destination), "Copy",
                            NativeConst.ERROR_FILE_EXISTS, NativeError.DescribeWin32(NativeConst.ERROR_FILE_EXISTS), 0));
                        return;

                    case OverwriteMode.SkipExisting:
                        job.Progress.AddFileSkipped(item.Size);
                        job.Journal.RecordCompleted(relative, ItemOutcome.SkippedExisting, item.Size);
                        return;

                    case OverwriteMode.OverwriteIfNewer
                        when existing.ftLastWriteTime.ToLong() >= item.LastWriteTime:
                        job.Progress.AddFileSkipped(item.Size);
                        job.Journal.RecordCompleted(relative, ItemOutcome.SkippedExisting, item.Size);
                        return;
                }
            }
        }

        long resumeOffset = job.Journal.GetCheckpoint(relative);
        var metadata = new SourceMetadata(
            item.CreationTime, item.LastAccessTime, item.LastWriteTime, item.Attributes);
        var task = new CopyTask(item.Source, destination, tempPath, relative, resumeOffset, item.Size, destinationExists, metadata);

        CopyOutcome outcome = ExecuteWithRetries(job, copier, task, slot, sink);

        if (outcome.Error is not null)
        {
            job.Progress.AddFileFailed();
            job.Journal.RecordError(outcome.Error);
            report(outcome.Error);
            return;
        }

        if (outcome.Outcome == ItemOutcome.Resumed)
        {
            job.Progress.AddFileResumed(
                Math.Max(0, outcome.BytesTransferred - sink.Reported), outcome.BytesReused);
        }
        else
        {
            // The remainder only. Everything already reported through a checkpoint was
            // added as it was written, so adding the full size here would double count.
            job.Progress.AddFileCompleted(Math.Max(0, outcome.BytesTransferred - sink.Reported));
        }

        job.Journal.RecordCompleted(relative, outcome.Outcome, item.Size);

        // One file with several names must arrive as one file with several names.
        //
        // Done before the progress accounting and before OnItemWritten, because both
        // describe a file that is finished -- and a file whose link has not been made
        // yet is not finished.
        bool wasLinked = false;

        if (outcome.HasSharedFileId)
        {
            wasLinked = ReplaceCopyWithLink(job, sharedFiles, destination, outcome.FileId, report, onMapFull);
        }

        // Named streams, when asked for. Off by default: detecting them costs one
        // FindFirstStreamW per file, measured at 19 us against a 16 us baseline, on a
        // path whose whole purpose is moving very many very small files that almost
        // never carry one. See AlternateStreams for the measurement and the reasoning.
        if (job.Options.CopyAlternateDataStreams)
        {
            CopyNamedStreams(item.Source, destination, report);
        }

        // Read back and compare, when asked for.
        //
        // Not applied to a hard link: the link and its target are the same file, so
        // comparing them would read one file twice and prove nothing. The first copy
        // of that file was verified when it was made.
        if (job.Options.VerifyChecksum && !wasLinked)
        {
            VerifyCopy(item.Source, destination, report);
        }

        // Reported only for items that were written, never for ones that were
        // skipped. A paste needs to know which top-level items changed so it can
        // select those and only those.
        job.OnItemWritten?.Invoke(relative);

        if (job.Move)
        {
            RemoveSource(item.Source, report);
        }
    }

    /// <summary>
    /// Reads the copy back and compares it with the source.
    ///
    /// Failure is reported against the file, not treated as a reason to stop: a copy
    /// that verified every file but one has done its job, and abandoning the rest would
    /// leave a tree that is neither verified nor complete. The caller decides what to do
    /// with a mismatch -- the copy is left exactly as it is, because deleting it would
    /// destroy the only remaining candidate for the data.
    /// </summary>
    private static void VerifyCopy(string source, string destination, Action<JobError> report)
    {
        if (Verifier.Compare(source, destination, out long offset))
        {
            return;
        }

        report(new JobError(
            ErrorKind.Verify, PathUtil.ToDisplay(destination), "Verify", 0,
            Lang.Pick($"复制后校验失败：{Verifier.Describe(offset)}（目标文件已保留，未删除）", $"Verification failed after copying: {Verifier.Describe(offset)} (the destination file was kept, not deleted)"), 0));
    }

    /// <summary>
    /// Carries a file's named streams across.
    ///
    /// Reported, never fatal. A stream that cannot be copied does not make the file
    /// itself wrong -- the contents arrived intact -- and on a destination that cannot
    /// hold streams at all (FAT32, exFAT, most network shares) every file would fail,
    /// turning a successful copy into a wall of errors. The one failure that matters is
    /// silent loss, and that is what the report prevents.
    /// </summary>
    private static void CopyNamedStreams(string source, string destination, Action<JobError> report)
    {
        List<string> names = AlternateStreams.Named(source);

        if (names.Count == 0)
        {
            return;
        }

        List<string> failed = AlternateStreams.Copy(source, destination, names, out int error);

        if (failed.Count == 0)
        {
            return;
        }

        report(new JobError(
            ErrorKind.Metadata, PathUtil.ToDisplay(destination), "AlternateStreams", error,
            Lang.Pick(
                $"未复制的数据流：{string.Join("、", failed)}",
                $"Streams not copied: {string.Join(", ", failed)}"), 0));
    }

    /// <summary>
    /// Identifies one file across all of its names. The volume is part of the key
    /// because file IDs are only unique within a volume.
    /// </summary>
    private readonly record struct HardLinkKey(ulong Volume, ulong Low, ulong High);

    /// <summary>
    /// Turns a freshly written copy into a hard link to an earlier copy of the same
    /// file, or records this path as the one the others should link to.
    ///
    /// <b>The order of operations is the whole point.</b> The obvious way -- delete the
    /// copy, then create the link -- destroys the file when the link fails, and a link
    /// can fail for reasons the caller cannot foresee (a destination that does not
    /// support them, a permission that allows writing but not linking). So the link is
    /// made first, at a temporary name beside the copy, and only then moved over it
    /// with MOVEFILE_REPLACE_EXISTING. At every instant the destination path names
    /// valid data: either the copy that was just written, or a link to the first copy.
    ///
    /// <b>Failing is allowed.</b> If the link cannot be made, the copy stays. The result
    /// is a correct copy that merely occupies more space than it could have -- which is
    /// a far better outcome than a missing file, and the same one the program produced
    /// before any of this existed.
    /// </summary>
    /// <summary>
    /// How many file identities the link map will remember.
    ///
    /// One entry per file that has more than one name. A corpus of ten million hard
    /// links would hold ten million keys and paths -- over a gigabyte -- and the map is
    /// not the work, it is a note kept to make the work smaller. Past the cap the copy
    /// stops deduplicating and simply writes another copy: more disk used, nothing
    /// lost, and the run finishes. Reported once so the trade is visible rather than
    /// silent.
    /// </summary>
    private const int LinkMapLimit = 1_000_000;

    private static bool ReplaceCopyWithLink(
        CopyJob job, ConcurrentDictionary<HardLinkKey, string> sharedFiles,
        string destination, FILE_ID_INFO fileId, Action<JobError> report,
        Action onMapFull)
    {
        var key = new HardLinkKey(fileId.VolumeSerialNumber, fileId.FileId.Low, fileId.FileId.High);

        // The first name wins and the rest link to it. TryAdd is the decision: exactly
        // one worker gets true, and every other one reads a path that already exists.
        if (sharedFiles.TryAdd(key, destination))
        {
            if (sharedFiles.Count == LinkMapLimit)
            {
                onMapFull();
            }

            return false;
        }

        if (sharedFiles.Count >= LinkMapLimit)
        {
            // Past the ceiling this file is copied rather than linked. Correct, just
            // larger -- which is the state the program was in before any of this.
            return false;
        }

        if (!sharedFiles.TryGetValue(key, out string? first) ||
            string.Equals(first, destination, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string linkTemp = FileCopier.TempPathFor(destination);

        // The copy's own temporary name may still be around if a previous attempt left
        // one; MoveFileEx with REPLACE_EXISTING would take it, but CreateHardLink will
        // not, so clear it.
        Kernel32.DeleteFile(linkTemp);

        if (Kernel32.CreateHardLink(linkTemp, first, 0) == 0)
        {
            report(new JobError(
                ErrorKind.Metadata, PathUtil.ToDisplay(destination), "CreateHardLink",
                NativeError.LastWin32(),
                $"could not link to the first copy; the duplicate was kept instead", 0));

            return false;
        }

        if (Kernel32.MoveFileEx(linkTemp, destination, NativeConst.MOVEFILE_REPLACE_EXISTING) == 0)
        {
            int error = NativeError.LastWin32();
            Kernel32.DeleteFile(linkTemp);

            report(new JobError(
                ErrorKind.Metadata, PathUtil.ToDisplay(destination), "MoveFileEx(link)",
                error, "could not put the new link in place; the duplicate was kept", 0));

            return false;
        }

        // No progress adjustment: the bytes really were written and the file really is
        // there. What changed is how much of the disk it occupies, and the counters
        // here describe work done and items completed, not sectors consumed.
        return true;
    }

    private static CopyOutcome ExecuteWithRetries(
        CopyJob job, FileCopier copier, in CopyTask task, WatchdogSlot slot, CheckpointSink sink)
    {
        CopyOptions options = job.Options;
        TimeSpan delay = options.RetryDelay;
        JobError? last = null;

        sink.Bind(task.RelativePath, task.Source);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            slot.Report(task.Source, 1);

            // sink.Callback is created once per worker, so the hot path does not
            // allocate a closure per file.
            CopyOutcome outcome = copier.Execute(task, job.Control, sink.Callback);
            slot.Report(task.Source, 2);

            if (outcome.Error is null)
            {
                return outcome;
            }

            last = outcome.Error with { Attempts = attempt + 1 };

            if (attempt >= options.MaxRetries || !IsRetryable(last))
            {
                break;
            }

            Thread.Sleep(delay);
            delay = TimeSpan.FromMilliseconds(
                Math.Min(delay.TotalMilliseconds * 2, options.RetryDelayMax.TotalMilliseconds));
        }

        return CopyOutcome.Failed(last!);
    }

    /// <summary>Only conditions another attempt could plausibly clear.</summary>
    private static bool IsRetryable(JobError error) => error.NativeCode switch
    {
        NativeConst.ERROR_SHARING_VIOLATION => true,
        NativeConst.ERROR_LOCK_VIOLATION => true,
        NativeConst.ERROR_NOT_READY => true,
        NativeConst.ERROR_IO_PENDING => true,
        _ => false,
    };

    private static void RemoveSource(string source, Action<JobError> report)
    {
        // Uses the fast deleter so `move` gets the same one-call read-only handling.
        if (FastDeleter.TryDelete(source, directory: false, out ErrorKind kind, out int code, out string message))
        {
            return;
        }

        report(new JobError(kind, PathUtil.ToDisplay(source), "DeleteFile(source)", code, message, 0));
    }

    /// <summary>
    /// Mutable checkpoint target reused by one worker. Exists so the copy loop can
    /// hand the copier a stable delegate: building a closure per file would allocate
    /// once per item on a path that runs tens of thousands of times a second.
    /// </summary>
    private sealed class CheckpointSink
    {
        private readonly CopyJob _job;
        private readonly WatchdogSlot _slot;
        private string _relativePath = string.Empty;
        private string _source = string.Empty;

        public CheckpointSink(CopyJob job, WatchdogSlot slot)
        {
            _job = job;
            _slot = slot;
            Callback = OnCheckpoint;
        }

        public Action<long> Callback { get; }

        /// <summary>Bytes already counted toward progress for the current item, so
        /// completion adds only the remainder.</summary>
        public long Reported { get; private set; }

        public void Bind(string relativePath, string source)
        {
            _relativePath = relativePath;
            _source = source;
            Reported = 0;

            // The window shows the name of what is being worked on. Nothing ever set
            // it, so it read Lang.Pick("正在准备…", "Preparing...") for the entire operation.
            _job.Progress.SetCurrentItem(source);
        }

        private void OnCheckpoint(long offset)
        {
            _slot.Report(_source, 1);
            _job.Journal.RecordCheckpoint(_relativePath, offset);

            long delta = offset - Reported;

            if (delta > 0)
            {
                Reported += delta;
                _job.Progress.AddBytes(delta);
            }
        }
    }
}
