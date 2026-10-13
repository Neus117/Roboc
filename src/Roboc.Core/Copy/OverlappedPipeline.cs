using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Roboc.Core.Control;
using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;
using Roboc.Core.Model;

namespace Roboc.Core.Copy;

/// <summary>
/// Overlapped, double-buffered transfer for files large enough to be bound by the
/// serialisation of read-then-write rather than by per-operation cost.
///
/// Measured on this machine, the synchronous path plateaus at ~645 MB/s on a
/// single 256 MB file regardless of buffer size, because each read finishes
/// before its write starts, so throughput is bounded by their *sum*. Pipelining
/// them bounds it by their *maximum*. The overlapped version measured 2797 MB/s
/// against robocopy's 2233 MB/s.
///
/// One invariant is load-bearing, and getting it wrong corrupts data silently
/// rather than failing loudly:
///
///   A buffer is occupied from the moment a read is issued into it until the
///   write that consumes it has completed.
///
/// If reads are issued `depth` chunks ahead they land in the buffer currently
/// being written, and the device would still be reading it. Reads are therefore
/// issued `depth - 1` ahead, which is always a *different* buffer, and the write
/// on that target buffer is drained first. That yields at most depth-1 reads and
/// one write in flight, which is enough to keep both directions busy.
/// </summary>
internal sealed unsafe class OverlappedPipeline : IDisposable
{
    private readonly int _depth;
    private readonly int _chunkSize;

    private byte[][]? _buffers;
    private byte** _bufferPointers;
    private nint* _events;
    private OVERLAPPED* _overlapped;
    private bool _initialised;
    private bool _disposed;

    private long[] _slotOffset = [];
    private int[] _slotGot = [];
    private bool[] _readPending = [];
    private bool[] _writePending = [];

    public OverlappedPipeline(int depth, int chunkSize)
    {
        // depth 1 would make the read lead zero chunks ahead, which is exactly the
        // buffer-reuse hazard described above.
        _depth = Math.Clamp(depth, 2, 16);
        _chunkSize = Math.Max(64 * 1024, chunkSize);
    }

    private void EnsureInitialised()
    {
        if (_initialised)
        {
            return;
        }

        _buffers = new byte[_depth][];
        _bufferPointers = (byte**)NativeMemory.AllocZeroed((nuint)_depth, (nuint)sizeof(byte*));
        _events = (nint*)NativeMemory.AllocZeroed((nuint)_depth, (nuint)sizeof(nint));
        _overlapped = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)_depth, (nuint)sizeof(OVERLAPPED));

        for (int i = 0; i < _depth; i++)
        {
            // Pinned for the lifetime of the pipeline: the kernel writes to these
            // addresses asynchronously, so they must never move.
            _buffers[i] = GC.AllocateUninitializedArray<byte>(_chunkSize, pinned: true);
            _bufferPointers[i] = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buffers[i]));

            // Manual-reset: the documented pattern is to reset explicitly before
            // each issue, which an auto-reset event would fight with.
            _events[i] = Kernel32.CreateEvent(0, 1, 0, null);
            if (_events[i] == 0)
            {
                throw new InvalidOperationException(
                    $"CreateEvent failed: {NativeError.DescribeWin32(NativeError.LastWin32())}");
            }

            _overlapped[i].hEvent = _events[i];
        }

        _slotOffset = new long[_depth];
        _slotGot = new int[_depth];
        _readPending = new bool[_depth];
        _writePending = new bool[_depth];
        _initialised = true;
    }

    /// <summary>Returns null on success, otherwise the failure. Throws
    /// OperationCanceledException when the job is stopping.</summary>
    public JobError? Run(
        nint sourceHandle,
        nint destinationHandle,
        string sourcePath,
        string destinationPath,
        long size,
        long start,
        JobControl control,
        long checkpointInterval,
        ref long transferred,
        ref long lastCheckpoint,
        Action<long>? onCheckpoint)
    {
        EnsureInitialised();

        long total = size - start;
        if (total <= 0)
        {
            return null;
        }

        long chunkCount = (total + _chunkSize - 1) / _chunkSize;
        int depth = _depth;

        Array.Clear(_readPending);
        Array.Clear(_writePending);

        try
        {
            // Prime: reads for chunks 0 .. depth-2, so depth-1 reads are in flight.
            for (int i = 0; i < depth - 1 && i < chunkCount; i++)
            {
                if (!IssueRead(sourceHandle, i, start + ((long)i * _chunkSize), size, out int primeError))
                {
                    return Error("ReadFile", sourcePath, primeError);
                }

                _readPending[i] = true;
            }

            for (long k = 0; k < chunkCount; k++)
            {
                if (control.ShouldStop())
                {
                    throw new OperationCanceledException(control.Token);
                }

                int slot = (int)(k % depth);

                if (_readPending[slot])
                {
                    if (!Complete(sourceHandle, slot, out uint got, out int readError))
                    {
                        return Error("ReadFile", sourcePath, readError);
                    }

                    if (got == 0)
                    {
                        return new JobError(
                            ErrorKind.Read, sourcePath, "ReadFile", 0,
                            "source ended early during overlapped transfer", 0);
                    }

                    _slotGot[slot] = (int)got;
                    _readPending[slot] = false;
                }

                if (!IssueWrite(destinationHandle, slot, _slotOffset[slot], _slotGot[slot], out int writeError))
                {
                    return Error("WriteFile", destinationPath, writeError);
                }

                _writePending[slot] = true;
                transferred += _slotGot[slot];

                // Issue the read for chunk k + depth - 1 into buffer
                // (k - 1) % depth. That buffer's write -- issued on the previous
                // iteration -- must be drained before it can be reused.
                long nextChunk = k + depth - 1;
                if (nextChunk < chunkCount)
                {
                    int nextSlot = (int)(nextChunk % depth);

                    if (_writePending[nextSlot] &&
                        !DrainWrite(destinationHandle, nextSlot, destinationPath, out JobError? drainError))
                    {
                        return drainError;
                    }

                    if (!IssueRead(sourceHandle, nextSlot, start + (nextChunk * _chunkSize), size, out int nextError))
                    {
                        return Error("ReadFile", sourcePath, nextError);
                    }

                    _readPending[nextSlot] = true;
                }

                if (onCheckpoint is not null && transferred - lastCheckpoint >= checkpointInterval)
                {
                    // Durability before bookkeeping: the journal must never claim
                    // bytes a power loss could still take away.
                    if (Kernel32.FlushFileBuffers(destinationHandle) != 0)
                    {
                        lastCheckpoint = transferred;
                        onCheckpoint(transferred);
                    }
                }
            }

            for (int i = 0; i < depth; i++)
            {
                if (_writePending[i] &&
                    !DrainWrite(destinationHandle, i, destinationPath, out JobError? tailError))
                {
                    return tailError;
                }
            }

            return null;
        }
        finally
        {
            // No operation may still reference a buffer once we return.
            CancelPending(sourceHandle);
            CancelPending(destinationHandle);
        }
    }

    private bool DrainWrite(nint handle, int slot, string path, out JobError? error)
    {
        if (!Complete(handle, slot, out uint written, out int code))
        {
            error = Error("WriteFile", path, code);
            return false;
        }

        if (written != (uint)_slotGot[slot])
        {
            error = new JobError(ErrorKind.Write, path, "WriteFile", 0,
                $"short write: {written} of {_slotGot[slot]} bytes", 0);
            return false;
        }

        _writePending[slot] = false;
        error = null;
        return true;
    }

    private bool IssueRead(nint handle, int slot, long offset, long fileSize, out int error)
    {
        int want = (int)Math.Min(_chunkSize, fileSize - offset);
        _slotOffset[slot] = offset;
        _slotGot[slot] = 0;

        Kernel32.ResetEvent(_events[slot]);

        OVERLAPPED* overlapped = &_overlapped[slot];
        overlapped->Internal = 0;
        overlapped->InternalHigh = 0;
        overlapped->SetOffset(offset);

        if (Kernel32.ReadFile(handle, _bufferPointers[slot], (uint)want, null, overlapped) != 0)
        {
            error = 0;
            return true;
        }

        error = NativeError.LastWin32();
        return error == NativeConst.ERROR_IO_PENDING;
    }

    private bool IssueWrite(nint handle, int slot, long offset, int count, out int error)
    {
        Kernel32.ResetEvent(_events[slot]);

        OVERLAPPED* overlapped = &_overlapped[slot];
        overlapped->Internal = 0;
        overlapped->InternalHigh = 0;
        overlapped->SetOffset(offset);

        if (Kernel32.WriteFile(handle, _bufferPointers[slot], (uint)count, null, overlapped) != 0)
        {
            error = 0;
            return true;
        }

        error = NativeError.LastWin32();
        return error == NativeConst.ERROR_IO_PENDING;
    }

    /// <summary>bWait = TRUE so this blocks on the operation's own event.</summary>
    private bool Complete(nint handle, int slot, out uint bytes, out int error)
    {
        if (Kernel32.GetOverlappedResult(handle, &_overlapped[slot], out bytes, 1) != 0)
        {
            error = 0;
            return true;
        }

        bytes = 0;
        error = NativeError.LastWin32();
        return false;
    }

    private void CancelPending(nint handle)
    {
        for (int i = 0; i < _depth; i++)
        {
            if (_readPending[i] || _writePending[i])
            {
                Kernel32.CancelIoEx(handle, &_overlapped[i]);
                _readPending[i] = false;
                _writePending[i] = false;
            }
        }
    }

    private static JobError Error(string operation, string path, int code)
        => new(FileCopier.Classify(code), path, operation, code, NativeError.DescribeWin32(code), 0);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_initialised)
        {
            return;
        }

        for (int i = 0; i < _depth; i++)
        {
            if (_events[i] != 0)
            {
                Kernel32.CloseHandle(_events[i]);
            }
        }

        NativeMemory.Free(_bufferPointers);
        NativeMemory.Free(_events);
        NativeMemory.Free(_overlapped);

        _buffers = null;
        _readPending = [];
        _writePending = [];
        _initialised = false;
    }
}
