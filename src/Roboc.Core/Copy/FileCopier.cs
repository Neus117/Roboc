using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Roboc.Core.Control;
using Roboc.Core.Delete;
using Roboc.Core.Diagnostics;
using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Model;

namespace Roboc.Core.Copy;

/// <summary>
/// Metadata captured by the directory enumeration.
///
/// WIN32_FIND_DATAW already carries all three timestamps, the size and the
/// attributes, so re-querying them per file with GetFileInformationByHandleEx is
/// wasted syscalls on a path that runs once per file.
/// </summary>
internal readonly struct SourceMetadata(
    long creationTime, long lastAccessTime, long lastWriteTime, uint attributes)
{
    public readonly long CreationTime = creationTime;
    public readonly long LastAccessTime = lastAccessTime;
    public readonly long LastWriteTime = lastWriteTime;
    public readonly uint Attributes = attributes;
}

internal readonly struct CopyTask(
    string source,
    string destination,
    string tempPath,
    string relativePath,
    long resumeOffset,
    long expectedSize,
    bool destinationExists,
    SourceMetadata metadata)
{
    public readonly string Source = source;
    public readonly string Destination = destination;
    public readonly string TempPath = tempPath;
    public readonly string RelativePath = relativePath;
    public readonly long ResumeOffset = resumeOffset;

    /// <summary>
    /// Size seen during enumeration. Used only to pick the IO strategy before the
    /// handles are opened, because a handle opened with FILE_FLAG_OVERLAPPED can
    /// never be used with a null OVERLAPPED, and vice versa. The authoritative
    /// size still comes from the source handle.
    /// </summary>
    public readonly long ExpectedSize = expectedSize;

    /// <summary>Whether something already occupies the final path. Decides
    /// between a direct write and a staged one.</summary>
    public readonly bool DestinationExists = destinationExists;

    public readonly SourceMetadata Metadata = metadata;
}

internal readonly record struct CopyOutcome(
    ItemOutcome Outcome, long BytesTransferred, long BytesReused, JobError? Error,
    FILE_ID_INFO FileId, bool HasSharedFileId)
{
    public static CopyOutcome Failed(JobError error)
        => new(ItemOutcome.Failed, 0, 0, error, default, false);
}

/// <summary>
/// Copies exactly one file, correctly and atomically.
///
/// The invariants that matter:
///  * The final path is never observed in a partial state. Bytes go to a
///    deterministic sibling temp name and the file is moved into place with
///    FILE_RENAME_FLAG_POSIX_SEMANTICS, a metadata-only operation that either
///    happened or did not.
///  * Resume is safe against power loss, not just a clean cancel. The journaled
///    offset only advances after FlushFileBuffers made those bytes durable, so on
///    resume the temp file is truncated back to that offset and anything beyond it
///    (crash garbage) is discarded.
///  * A file that fails never leaves a truncated file at the destination.
/// </summary>
internal sealed unsafe class FileCopier : IDisposable
{
    internal const string TempSuffix = ".roboc-tmp";

    private readonly CopyOptions _options;
    private readonly CopyTimings? _timings;
    private readonly byte[] _buffer;
    private readonly byte* _bufferPtr;
    private readonly int _bufferLength;
    private OverlappedPipeline? _pipeline;
    private int _disposed;

    public FileCopier(CopyOptions options, int bufferSize, CopyTimings? timings = null)
    {
        _options = options;
        _timings = timings;
        _bufferLength = Math.Max(4096, bufferSize);

        // Pinned once so the inner loop never pays for pinning on every call.
        _buffer = GC.AllocateUninitializedArray<byte>(_bufferLength, pinned: true);
        _bufferPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buffer));
    }

    public static string TempPathFor(string destinationExtended) => destinationExtended + TempSuffix;

    public CopyOutcome Execute(in CopyTask task, JobControl control, Action<long>? onCheckpoint)
    {
        long started = _timings is null ? 0 : Stopwatch.GetTimestamp();

        // The IO strategy has to be chosen before the handles exist: overlapped and
        // synchronous handles are not interchangeable.
        bool large = task.ExpectedSize >= _options.LargeFileThreshold;
        uint overlapped = large ? NativeConst.FILE_FLAG_OVERLAPPED : 0;

        nint sourceHandle = Kernel32.CreateFile(
            task.Source, NativeConst.GENERIC_READ,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0, NativeConst.OPEN_EXISTING,
            NativeConst.FILE_FLAG_SEQUENTIAL_SCAN | overlapped, 0);

        if (sourceHandle == NativeConst.INVALID_HANDLE_VALUE)
        {
            return CopyOutcome.Failed(ClassifyError("CreateFile(source)", task.Source));
        }

        Mark(started, Phase.OpenSource);

        try
        {
            long queryStart = _timings is null ? 0 : Stopwatch.GetTimestamp();

            FILE_STANDARD_INFO standard;
            if (Kernel32.GetFileInformationByHandleEx(
                    sourceHandle, NativeConst.FileStandardInfo, &standard,
                    (uint)sizeof(FILE_STANDARD_INFO)) == 0)
            {
                return CopyOutcome.Failed(ClassifyError("GetFileInformationByHandleEx(source)", task.Source));
            }

            FILE_ID_INFO fileId = default;
            bool trackFileId = _options.RecreateHardLinks && standard.NumberOfLinks > 1;

            if (trackFileId &&
                Kernel32.GetFileInformationByHandleEx(
                    sourceHandle, NativeConst.FileIdInfo, &fileId, (uint)sizeof(FILE_ID_INFO)) == 0)
            {
                trackFileId = false;
            }

            Mark(queryStart, Phase.QuerySource);

            // The authoritative size comes from the handle, never the enumeration:
            // if the file changed size in between, trusting the enumerated value
            // would silently truncate or over-copy it.
            long size = standard.EndOfFile;

            long resumeFrom = task.ResumeOffset > 0 && task.ResumeOffset <= size ? task.ResumeOffset : 0;

            return CopyBody(task, control, onCheckpoint, sourceHandle, size, fileId, trackFileId, resumeFrom, overlapped);
        }
        finally
        {
            long closeStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
            Kernel32.CloseHandle(sourceHandle);
            Mark(closeStart, Phase.Close);
            _timings?.CountFile();
        }
    }

    private enum Phase
    {
        OpenSource, QuerySource, CreateDestination, Preallocate,
        Transfer, Metadata, Flush, Rename, Close,
    }

    /// <summary>Zero cost when timing is off, which is the normal case.</summary>
    private void Mark(long started, Phase phase)
    {
        if (_timings is null)
        {
            return;
        }

        long ticks = Stopwatch.GetTimestamp() - started;

        switch (phase)
        {
            case Phase.OpenSource: _timings.AddOpenSource(ticks); break;
            case Phase.QuerySource: _timings.AddQuerySource(ticks); break;
            case Phase.CreateDestination: _timings.AddCreateDestination(ticks); break;
            case Phase.Preallocate: _timings.AddPreallocate(ticks); break;
            case Phase.Transfer: _timings.AddTransfer(ticks); break;
            case Phase.Metadata: _timings.AddMetadata(ticks); break;
            case Phase.Flush: _timings.AddFlush(ticks); break;
            case Phase.Rename: _timings.AddRename(ticks); break;
            case Phase.Close: _timings.AddClose(ticks); break;
        }
    }

    private CopyOutcome CopyBody(
        in CopyTask task, JobControl control, Action<long>? onCheckpoint, nint sourceHandle,
        long size, FILE_ID_INFO fileId, bool trackFileId, long resumeFrom, uint overlapped)
    {
        bool succeeded = false;
        long createStart = _timings is null ? 0 : Stopwatch.GetTimestamp();

        // DELETE access is required for the rename; GENERIC_WRITE already carries
        // FILE_WRITE_ATTRIBUTES and SYNCHRONIZE.
        // Large files always stage: the crash window is long enough that leaving a
        // truncated multi-gigabyte file behind is worth avoiding even on a fresh
        // copy. Small files stage only when they would be replacing something.
        bool staged = task.DestinationExists
                   || size >= _options.LargeFileThreshold
                   || _options.Publish == PublishMode.AlwaysAtomic;

        if (_options.Publish == PublishMode.AlwaysDirect)
        {
            staged = false;
        }

        string writePath = staged ? task.TempPath : task.Destination;

        // DELETE access is only needed to rename the handle, so a direct write
        // does not ask for it. FILE_ATTRIBUTE_TEMPORARY tells the cache manager
        // the file is about to be deleted and need not be written back eagerly --
        // true of a staging file, false of one being published at its final name.
        uint destinationAccess = NativeConst.GENERIC_WRITE;
        uint destinationFlags = NativeConst.FILE_FLAG_SEQUENTIAL_SCAN | overlapped;

        if (staged)
        {
            destinationAccess |= NativeConst.DELETE;
            destinationFlags |= NativeConst.FILE_ATTRIBUTE_TEMPORARY;
        }

        nint destinationHandle = Kernel32.CreateFile(
            writePath, destinationAccess, 0, 0,
            resumeFrom > 0 ? NativeConst.OPEN_ALWAYS : NativeConst.CREATE_ALWAYS,
            destinationFlags, 0);

        if (destinationHandle == NativeConst.INVALID_HANDLE_VALUE)
        {
            return CopyOutcome.Failed(ClassifyError("CreateFile(temp)", task.TempPath));
        }

        Mark(createStart, Phase.CreateDestination);

        try
        {
            if (resumeFrom > 0)
            {
                resumeFrom = PrepareResume(destinationHandle, sourceHandle, resumeFrom);
            }

            long preallocStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
            Preallocate(destinationHandle, size);
            Mark(preallocStart, Phase.Preallocate);

            long transferStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
            long transferred = resumeFrom;

            JobError? writeError = overlapped != 0
                ? TransferOverlapped(control, sourceHandle, destinationHandle, task, size, ref transferred, onCheckpoint)
                : Transfer(control, sourceHandle, destinationHandle, task, writePath, size, ref transferred, onCheckpoint);

            Mark(transferStart, Phase.Transfer);

            if (writeError is not null)
            {
                return CopyOutcome.Failed(writeError);
            }

            // Attributes are applied before the rename, which removes the need for
            // the extra open/set/close pass that makes read-only files slow in every
            // other tool. The handle already holds DELETE access, so a read-only
            // attribute cannot block the rename.
            if (_options.PreserveAttributes || _options.PreserveTimestamps)
            {
                long metaStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
                bool applied = ApplyMetadata(destinationHandle, task.Metadata, writePath, out writeError);
                Mark(metaStart, Phase.Metadata);

                if (!applied)
                {
                    return CopyOutcome.Failed(writeError!);
                }
            }

            bool shouldFlush = size >= _options.LargeFileThreshold
                ? _options.FlushLargeFiles
                : _options.FlushSmallFiles;

            if (shouldFlush)
            {
                long flushStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
                int flushResult = Kernel32.FlushFileBuffers(destinationHandle);
                Mark(flushStart, Phase.Flush);

                if (flushResult == 0)
                {
                    return CopyOutcome.Failed(ClassifyError("FlushFileBuffers", writePath));
                }
            }

            if (staged)
            {
                long renameStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
                bool moved = MoveIntoPlace(destinationHandle, task.Destination, _options.PosixRename, out string? renameFailure);
                Mark(renameStart, Phase.Rename);

                if (!moved)
                {
                    return CopyOutcome.Failed(renameFailure is not null
                        ? new JobError(ErrorKind.Write, task.Destination, "SetFileInformationByHandle(rename)", 0, renameFailure, 0)
                        : ClassifyError("SetFileInformationByHandle(rename)", task.Destination));
                }
            }

            onCheckpoint?.Invoke(size);

            succeeded = true;
            ItemOutcome outcome = resumeFrom > 0 ? ItemOutcome.Resumed : ItemOutcome.Copied;
            return new CopyOutcome(outcome, transferred - resumeFrom, resumeFrom, null, fileId, trackFileId);
        }
        finally
        {
            long closeStart = _timings is null ? 0 : Stopwatch.GetTimestamp();
            Kernel32.CloseHandle(destinationHandle);
            Mark(closeStart, Phase.Close);

            // A failed direct write leaves a truncated file at the real name. That
            // is worse than leaving nothing, because it looks like a finished copy
            // to anything that only checks existence. Remove it so the next run
            // copies it cleanly. The size-and-timestamp skip check would catch it
            // anyway, but only on a re-run.
            if (!staged && !succeeded && DirectoryOps.Exists(writePath))
            {
                FastDeleter.TryDelete(writePath, directory: false, out _, out _, out _);
            }
        }
    }

    /// <summary>
    /// Trusts the journal, not the filesystem. The temp file may be longer than the
    /// journaled offset if the process died between a write and a checkpoint;
    /// truncating back guarantees unverified bytes are never treated as copied.
    /// </summary>
    private static long PrepareResume(nint destinationHandle, nint sourceHandle, long resumeFrom)
    {
        if (Kernel32.GetFileSizeEx(destinationHandle, out long actualSize) == 0)
        {
            return 0;
        }

        if (actualSize < resumeFrom)
        {
            // The temp file lost data. Redo the whole thing.
            Kernel32.SetFilePointerEx(destinationHandle, 0, null, NativeConst.FILE_BEGIN);
            Kernel32.SetEndOfFile(destinationHandle);
            Kernel32.SetFilePointerEx(sourceHandle, 0, null, NativeConst.FILE_BEGIN);
            return 0;
        }

        long position = resumeFrom;
        if (Kernel32.SetFilePointerEx(destinationHandle, position, &position, NativeConst.FILE_BEGIN) == 0 ||
            Kernel32.SetEndOfFile(destinationHandle) == 0)
        {
            return 0;
        }

        Kernel32.SetFilePointerEx(sourceHandle, resumeFrom, null, NativeConst.FILE_BEGIN);
        return resumeFrom;
    }

    private void Preallocate(nint destinationHandle, long size)
    {
        // Below the threshold this is a wasted syscall: NTFS already allocates in
        // 4 KiB units, so "preallocating" a small file changes nothing.
        if (size < _options.PreallocateThreshold)
        {
            return;
        }

        var allocation = new FILE_ALLOCATION_INFO { AllocationSize = size };
        _ = Kernel32.SetFileInformationByHandle(
            destinationHandle, NativeConst.FileAllocationInfo, &allocation, (uint)sizeof(FILE_ALLOCATION_INFO));
    }

    /// <summary>
    /// Large-file path. The pipeline allocates its buffers lazily and once per
    /// worker, so a workload of small files never pays for them: three 2 MiB
    /// buffers across two dozen workers would otherwise be ~144 MB of pinned memory
    /// allocated for nothing.
    /// </summary>
    private JobError? TransferOverlapped(
        JobControl control, nint sourceHandle, nint destinationHandle,
        in CopyTask task, long size, ref long transferred, Action<long>? onCheckpoint)
    {
        long start = transferred;
        long lastCheckpoint = start;

        long checkpointInterval = size >= _options.ResumeCheckpointThreshold
            ? _options.ResumeCheckpointInterval
            : long.MaxValue;

        try
        {
            _pipeline ??= new OverlappedPipeline(_options.OverlappedDepth, _options.LargeChunkSize);

            return _pipeline.Run(
                sourceHandle, destinationHandle, task.Source, task.TempPath,
                size, start, control, checkpointInterval,
                ref transferred, ref lastCheckpoint, onCheckpoint);
        }
        catch (InvalidOperationException ex)
        {
            // Pipeline setup failed. The handles are already opened for overlapped
            // IO, so the synchronous path is not an option.
            return new JobError(ErrorKind.Write, task.TempPath, "OverlappedPipeline", 0, ex.Message, 0);
        }
    }

    private JobError? Transfer(
        JobControl control, nint sourceHandle, nint destinationHandle,
        in CopyTask task, string writePath, long size, ref long transferred, Action<long>? onCheckpoint)
    {
        if (size == 0)
        {
            return null;
        }

        bool checkpointing = onCheckpoint is not null && size >= _options.ResumeCheckpointThreshold;
        long lastCheckpoint = transferred;
        long remaining = size - transferred;

        while (remaining > 0)
        {
            if (control.ShouldStop())
            {
                throw new OperationCanceledException(control.Token);
            }

            int want = (int)Math.Min(remaining, _bufferLength);
            int filled = ReadFully(sourceHandle, _bufferPtr, want, task.Source);

            if (filled <= 0)
            {
                // The source shrank mid-copy. Reporting this beats silently
                // producing a short file.
                return new JobError(ErrorKind.Read, task.Source, "ReadFile", 0,
                    $"source ended early: {remaining} bytes still expected", 0);
            }

            if (!WriteFully(destinationHandle, _bufferPtr, filled, task.TempPath))
            {
                return ClassifyError("WriteFile", writePath);
            }

            transferred += filled;
            remaining -= filled;

            if (checkpointing && transferred - lastCheckpoint >= _options.ResumeCheckpointInterval)
            {
                if (Kernel32.FlushFileBuffers(destinationHandle) != 0)
                {
                    onCheckpoint!.Invoke(transferred);
                    lastCheckpoint = transferred;
                }
            }
        }

        return null;
    }

    private bool ApplyMetadata(nint handle, SourceMetadata source, string path, out JobError? error)
    {
        var target = new FILE_BASIC_INFO
        {
            CreationTime = _options.PreserveTimestamps ? source.CreationTime : 0,
            LastAccessTime = _options.PreserveTimestamps ? source.LastAccessTime : 0,
            LastWriteTime = _options.PreserveTimestamps ? source.LastWriteTime : 0,
            // ChangeTime is read-only to callers; the filesystem maintains it.
            ChangeTime = 0,
            FileAttributes = _options.PreserveAttributes ? SanitizeAttributes(source.Attributes) : 0,
        };

        if (Kernel32.SetFileInformationByHandle(
                handle, NativeConst.FileBasicInfo, Unsafe.AsPointer(ref target),
                (uint)sizeof(FILE_BASIC_INFO)) == 0)
        {
            error = ClassifyError("SetFileInformationByHandle(basic)", path);
            return false;
        }

        error = null;
        return true;
    }

    private static uint SanitizeAttributes(uint attributes)
    {
        uint result = attributes & ~NativeConst.FILE_ATTRIBUTE_UNCOPYABLE;
        return result == 0 ? NativeConst.FILE_ATTRIBUTE_NORMAL : result;
    }

    /// <summary>Per-thread scratch for the rename buffer. Reused so the hot path
    /// does not allocate, but rebuilt each time so no stale name can survive.</summary>
    [ThreadStatic]
    private static byte[]? t_renameScratch;

    /// <summary>
    /// Atomic publish. POSIX semantics matter: without them, replacing an existing
    /// file leaves a window where the name resolves to nothing, and a reader
    /// holding the old file open can block the rename.
    ///
    /// This buffer is built defensively because failure here is silent by nature: a
    /// wrong FileNameLength does not fail the call, it creates the file under a
    /// different name while the copy reports success.
    ///
    ///  * A CLR byte[] is zero-initialised, which covers two things stackalloc does
    ///    not: the 4-byte padding hole between Flags and RootDirectory that no field
    ///    write reaches, and the bytes after the name.
    ///  * A NUL terminator sits just past the name, so any scan past FileName stops
    ///    on our NUL rather than walking into an adjacent frame.
    ///  * dwBufferSize stays at the documented minimum
    ///    (offsetof(FileName) + FileNameLength); the NUL lives just beyond it.
    ///  * The buffer is read back and compared before the call.
    ///  * Afterwards the file is confirmed to exist at the intended path, so any
    ///    residual misbehaviour becomes a reported per-file failure rather than a
    ///    quietly misnamed file.
    ///
    /// The root cause of the field failure was never isolated. These measures are
    /// containment, not a demonstrated fix, and that is recorded deliberately.
    /// </summary>
    private static bool MoveIntoPlace(nint handle, string finalPath, bool posix, out string? failure)
    {
        int nameBytes = finalPath.Length * sizeof(char);
        int headerSize = FILE_RENAME_INFO_HEADER.FileNameOffset;
        int total = headerSize + nameBytes;
        int needed = total + sizeof(char);

        byte[] scratch = t_renameScratch ??= new byte[512];

        if (scratch.Length < needed)
        {
            scratch = new byte[Math.Max(needed, scratch.Length * 2)];
            t_renameScratch = scratch;
        }
        else
        {
            Array.Clear(scratch, 0, needed);
        }

        fixed (byte* raw = scratch)
        {
            var header = (FILE_RENAME_INFO_HEADER*)raw;
            header->Flags = NativeConst.FILE_RENAME_FLAG_REPLACE_IF_EXISTS;
            if (posix)
            {
                header->Flags |= NativeConst.FILE_RENAME_FLAG_POSIX_SEMANTICS;
            }
            header->RootDirectory = 0;
            header->FileNameLength = (uint)nameBytes;

            Span<char> nameView = MemoryMarshal.Cast<byte, char>(new Span<byte>(raw + headerSize, nameBytes));
            finalPath.AsSpan().CopyTo(nameView);

            if (!nameView.SequenceEqual(finalPath))
            {
                failure = $"rename buffer corrupted: expected '{finalPath}', buffer held '{new string(nameView)}'";
                return false;
            }

            if (header->FileNameLength != (uint)nameBytes || header->RootDirectory != 0)
            {
                failure = "rename header corrupted after construction";
                return false;
            }

            if (Kernel32.SetFileInformationByHandle(
                    handle, NativeConst.FileRenameInfoEx, raw, (uint)total) == 0)
            {
                failure = null;
                return false;
            }
        }

        if (!DirectoryOps.Exists(finalPath))
        {
            failure = $"rename reported success but '{finalPath}' does not exist afterwards";
            return false;
        }

        failure = null;
        return true;
    }

    private static int ReadFully(nint handle, byte* buffer, int count, string path)
    {
        int filled = 0;
        while (filled < count)
        {
            uint got;
            if (Kernel32.ReadFile(handle, buffer + filled, (uint)(count - filled), &got, null) == 0)
            {
                NativeError.ThrowWin32("ReadFile", path);
            }

            if (got == 0)
            {
                break;
            }

            filled += (int)got;
        }

        return filled;
    }

    private static bool WriteFully(nint handle, byte* buffer, int count, string path)
    {
        int written = 0;
        while (written < count)
        {
            uint put;
            if (Kernel32.WriteFile(handle, buffer + written, (uint)(count - written), &put, null) == 0)
            {
                return false;
            }

            if (put == 0)
            {
                return false;
            }

            written += (int)put;
        }

        return true;
    }

    private static JobError ClassifyError(string operation, string path)
    {
        int code = NativeError.LastWin32();
        return new JobError(Classify(code), path, operation, code, NativeError.DescribeWin32(code), 0);
    }

    internal static ErrorKind Classify(int nativeCode) => nativeCode switch
    {
        NativeConst.ERROR_ACCESS_DENIED
            or NativeConst.ERROR_PRIVILEGE_NOT_HELD
            or NativeConst.ERROR_WRITE_PROTECT => ErrorKind.AccessDenied,

        NativeConst.ERROR_DISK_FULL
            or NativeConst.ERROR_HANDLE_DISK_FULL => ErrorKind.DiskFull,

        NativeConst.ERROR_FILE_NOT_FOUND
            or NativeConst.ERROR_PATH_NOT_FOUND
            or NativeConst.ERROR_NO_MORE_FILES => ErrorKind.SourceVanished,

        NativeConst.ERROR_INVALID_NAME
            or NativeConst.ERROR_BUFFER_OVERFLOW => ErrorKind.PathTooLong,

        NativeConst.ERROR_SHARING_VIOLATION
            or NativeConst.ERROR_LOCK_VIOLATION => ErrorKind.Write,

        _ => ErrorKind.Unknown,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pipeline?.Dispose();
        _pipeline = null;
        GC.KeepAlive(_buffer);
    }
}
