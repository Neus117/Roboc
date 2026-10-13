namespace Roboc.Core.Model;

/// <summary>Tuning knobs for copy and move. Defaults are the values that measured
/// best on NVMe; every one is overridable so a slow share or a spinning disk can
/// be dialled in without a rebuild.</summary>
public sealed class CopyOptions
{
    public OverwriteMode Overwrite { get; init; } = OverwriteMode.SkipIfSame;
    public bool PreserveTimestamps { get; init; } = true;
    public bool PreserveAttributes { get; init; } = true;
    /// <summary>
    /// Alternate data streams are NOT copied. This is false, and no code reads it.
    ///
    /// It was true, which was worse than not having the option at all: a default of
    /// true reads as a promise that streams are preserved, and anyone auditing the
    /// copy path -- including me, more than once -- took it for one. A copy that
    /// silently drops streams loses data on files that carry them, and the honest
    /// state of affairs is a flag that says so.
    ///
    /// Implementing it means enumerating with FindFirstStreamW, copying each named
    /// stream, and deciding what to do when the destination cannot hold them. Until
    /// that exists this stays false.
    /// </summary>
    public bool CopyAlternateDataStreams { get; init; }
    public bool RecreateHardLinks { get; init; } = true;

    /// <summary>Off by default: following a junction can leave the requested tree
    /// and loop forever. Reparse points are recreated instead.</summary>
    public bool FollowReparsePoints { get; init; }

    public int WorkerThreads { get; init; }

    /// <summary>At or above this size, files take the overlapped double-buffered
    /// path; below it the per-operation setup cost dominates.</summary>
    public long LargeFileThreshold { get; init; } = 2L * 1024 * 1024;

    /// <summary>At or above this size, files get mid-file resume checkpoints.
    /// Smaller files are cheap to redo and journaling them byte-by-byte would cost
    /// more than the retry.</summary>
    public long ResumeCheckpointThreshold { get; init; } = 4L * 1024 * 1024;
    /// <summary>
    /// How much may be written between resume checkpoints.
    ///
    /// Each checkpoint is flushed to disk before the journal records it, so the writer
    /// cannot run further ahead of the platter than this. Raising it was tried and
    /// reverted: the measurements came back 1.02s at 32 MB, 1.18s at 256 MB and 4.11s
    /// at 1 GB on the same 2 GB corpus, which is not a curve any of this explains --
    /// and --no-resume measured slower than leaving resume on, which no mechanism
    /// accounts for either. The runs were sequential and each wrote 2 GB, so the source
    /// was being evicted from cache part-way through the series; what the numbers
    /// describe is the cache, not the interval.
    ///
    /// Left at the value the program shipped with. --checkpoint-interval exists so it
    /// can be tuned against a controlled measurement, which has not been made.
    /// </summary>
    public long ResumeCheckpointInterval { get; init; } = 32L * 1024 * 1024;

    public bool VerifyChecksum { get; init; }

    // UseUnbufferedIo was here, defaulting to true and read by nothing.
    //
    // Deleted rather than implemented. FILE_FLAG_NO_BUFFERING requires the offset, the
    // length and the buffer address all to be sector-aligned, which would mean an
    // alignment layer through the whole write path; and the benchmark says there is
    // nothing to win -- large-file copies already run at 1034 MB/s against a volume
    // whose measured sustained write speed is 967 MB/s, so the copy is at the device's
    // limit and bypassing a cache it is not waiting on cannot help. A switch that
    // claims a behaviour and performs none is worse than no switch.

    /// <summary>
    /// Flush each large file before declaring it done, so a power loss cannot leave a
    /// renamed file with unflushed contents. A deliberate throughput-for-durability
    /// trade; turn it off only for throwaway data.
    ///
    /// <b>This does not control the checkpoint flush.</b> A file large enough to be
    /// checkpointed is also flushed at every checkpoint, and that one is required for
    /// the resume journal to stay truthful -- it is governed by whether journaling is
    /// on at all (<c>--no-resume</c>), not by this flag. Turning this off while leaving
    /// resume enabled therefore changes little on a large file, which is what the
    /// benchmark measured: <c>--no-flush</c> moved 2.46s to 2.52s, while
    /// <c>--no-resume</c> moved it to 2.20s.
    /// </summary>
    public bool FlushLargeFiles { get; init; } = true;
    public bool FlushSmallFiles { get; init; }

    public int QueueDepth { get; init; } = 16384;
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan RetryDelayMax { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan StallWarnAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StallAbortAfter { get; init; } = TimeSpan.FromSeconds(180);

    public bool AbortOnDiskFull { get; init; } = true;

    /// <summary>Bytes buffered per worker on the synchronous small-file path.
    /// Measured throughput rises steeply up to about 1 MiB per operation.</summary>
    public int CopyBufferSize { get; init; } = 1024 * 1024;

    /// <summary>Buffer count for the overlapped large-file pipeline. Two is the
    /// minimum that allows a read and a write in flight at once.</summary>
    public int OverlappedDepth { get; init; } = 3;

    /// <summary>Per-operation size on the overlapped path. A buffer sweep peaked
    /// at 2 MiB and degraded at 8 MiB, so this is not "as large as possible".</summary>
    public int LargeChunkSize { get; init; } = 2 * 1024 * 1024;

    /// <summary>Only preallocate at or above this size. NTFS already allocates in
    /// 4 KiB units, so preallocating a small file costs a syscall for nothing.</summary>
    public long PreallocateThreshold { get; init; } = 1024 * 1024;

    /// <summary>
    /// Publish with FILE_RENAME_FLAG_POSIX_SEMANTICS. Off falls back to a plain
    /// replace-on-rename. Exists so the cost of the POSIX flag can be measured
    /// rather than assumed; atomicity is identical either way.
    /// </summary>
    public bool PosixRename { get; init; } = true;

    /// <summary>
    /// How a completed file is published. See <see cref="PublishMode"/>.
    /// </summary>
    public PublishMode Publish { get; init; } = PublishMode.Split;


    public static CopyOptions Default { get; } = new();
}
