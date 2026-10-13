namespace Roboc.Core.Model;

public enum OverwriteMode
{
    /// <summary>Skip when size and last-write time match; otherwise overwrite.</summary>
    SkipIfSame = 0,

    /// <summary>Leave an existing destination untouched, whatever it holds.</summary>
    SkipExisting,
    OverwriteIfNewer,
    OverwriteAll,
    FailIfExists,
    RenameIfExists,
}

public enum JobState
{
    Pending, Planning, Running, Paused, Cancelling,
    Completed, CompletedWithErrors, Cancelled, Failed,
}

public enum ItemOutcome
{
    Copied,
    /// <summary>Bytes already present from a previous interrupted run were kept.</summary>
    Resumed,
    SkippedSame,
    SkippedExisting,
    Replaced,
    Renamed,
    Deleted,
    Failed,
}

public enum ErrorKind
{
    Read, Write, Metadata, Delete, Enumerate, Verify,
    /// <summary>Blocked by a permission the current token lacks; a candidate for
    /// an elevated retry rather than a permanent failure.</summary>
    AccessDenied,
    DiskFull, PathTooLong, SourceVanished, Stalled, RefusedTarget, Unknown,
}

/// <summary>
/// One item that could not be completed. Carries the native code so the UI can
/// group and explain failures, and so an elevation retry can select the subset
/// that needs a different token.
/// </summary>
public sealed record JobError(
    ErrorKind Kind,
    string Path,
    string Operation,
    int NativeCode,
    string Message,
    int Attempts);

public readonly record struct ProgressSnapshot(
    long FilesTotal,
    long FilesDone,
    long FilesFailed,
    long FilesSkipped,
    long BytesTotal,
    long BytesDone,
    double FilesPerSecond,
    double BytesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string? CurrentItem)
{
    public bool HasTotals => FilesTotal >= 0;
    public double FilesFraction => FilesTotal <= 0 ? 0 : Math.Clamp((double)FilesDone / FilesTotal, 0, 1);
    public double BytesFraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

public sealed class JobResult
{
    public JobState State { get; init; }
    public long FilesCopied { get; init; }
    public long FilesResumed { get; init; }
    public long FilesSkipped { get; init; }
    public long FilesFailed { get; init; }
    public long BytesTransferred { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<JobError> Errors { get; init; } = [];

    public bool Success => FilesFailed == 0 && State is JobState.Completed or JobState.CompletedWithErrors;

    /// <summary>True when every failure was a permission problem, so an elevated
    /// retry is worth offering instead of declaring defeat.</summary>
    public bool OnlyAccessDenied => FilesFailed > 0 && Errors.All(e => e.Kind == ErrorKind.AccessDenied);
}

/// <summary>
/// How a copied file becomes visible at its final path.
///
/// The staged design (temp file plus rename) is atomic: the destination name
/// either does not exist or is complete. It also costs two directory-entry
/// updates per file, and measurement showed create-plus-rename accounting for
/// 89% of a small-file copy, with the actual data transfer at 2.8%.
///
/// Direct publish writes the final name itself, halving that metadata work. It
/// is only used when the destination does not already exist, because on an
/// overwrite the direct path truncates the original immediately -- a failure
/// partway through would destroy the old file as well as failing. On a fresh
/// copy nothing is lost: the worst case is a truncated file, whose size will not
/// match the source, so the existing size-and-timestamp skip check detects it
/// and recopies it on the next run.
/// </summary>
public enum PublishMode
{
    /// <summary>Direct when the destination is new, staged when replacing. Default.</summary>
    Split = 0,

    /// <summary>Always stage and rename. Atomic, and measurably slower.</summary>
    AlwaysAtomic,

    /// <summary>Always write the final path. For measurement only.</summary>
    AlwaysDirect,
}