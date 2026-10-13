using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;

namespace Roboc.Core.Copy;

/// <summary>
/// Reads a copied file back and compares it against the source.
///
/// <b>What this catches that nothing else does.</b> Every layer between the two files
/// has its own protection -- SMB checksums the network, USB and SATA CRC the cable,
/// NVMe ECC the flash cells, TCP the packets -- and none of them checks the one thing
/// that matters here: that the bytes asked for are the bytes stored. Bad memory, a
/// driver fault, a controller writing to the wrong sector: each produces a file of the
/// right size, with the right timestamps, that opens without complaint and is wrong.
/// Windows does not check for this. CopyFileEx, which is what Explorer copies with, has
/// no such option and its documentation never mentions the possibility.
///
/// <b>Byte comparison, not checksums.</b> A checksum has to read the whole file before
/// it can say anything, and computing it costs more than comparing. Comparing stops at
/// the first difference, which for a corrupted file is usually the first block. Hashes
/// earn their cost when the value is kept and checked later; that is a different
/// feature and this is not it.
///
/// <b>Off unless asked for.</b> Verification reads both files end to end, so it roughly
/// doubles the read traffic of a copy. See docs/使用说明.md.
/// </summary>
internal static class Verifier
{
    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// Compares two files. Returns true when they are identical.
    ///
    /// A size difference is reported without reading anything: it is the cheapest
    /// possible answer and the most common way a copy goes wrong.
    /// </summary>
    public static bool Compare(string sourceExtended, string destinationExtended, out long mismatchOffset)
    {
        mismatchOffset = -1;

        nint source = Open(sourceExtended, out long sourceSize);
        if (source == 0)
        {
            return false;
        }

        try
        {
            nint destination = Open(destinationExtended, out long destinationSize);
            if (destination == 0)
            {
                return false;
            }

            try
            {
                if (sourceSize != destinationSize)
                {
                    // The offset reported is where the file stops being the same length,
                    // which is the shorter of the two.
                    mismatchOffset = Math.Min(sourceSize, destinationSize);
                    return false;
                }

                return CompareContents(source, destination, out mismatchOffset);
            }
            finally
            {
                Kernel32.CloseHandle(destination);
            }
        }
        finally
        {
            Kernel32.CloseHandle(source);
        }
    }

    private static nint Open(string path, out long size)
    {
        size = 0;

        nint handle = Kernel32.CreateFile(
            path, NativeConst.GENERIC_READ,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0, NativeConst.OPEN_EXISTING, NativeConst.FILE_FLAG_SEQUENTIAL_SCAN, 0);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            return 0;
        }

        unsafe
        {
            FILE_STANDARD_INFO standard;

            if (Kernel32.GetFileInformationByHandleEx(
                    handle, NativeConst.FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO)) == 0)
            {
                Kernel32.CloseHandle(handle);
                return 0;
            }

            size = standard.EndOfFile;
        }

        return handle;
    }

    /// <summary>
    /// The comparison itself. Two buffers, filled a block at a time, compared with the
    /// widest comparison the runtime offers -- a long mismatch is found immediately and
    /// a matching block costs one pass.
    /// </summary>
    private static bool CompareContents(nint source, nint destination, out long mismatchOffset)
    {
        mismatchOffset = -1;

        byte[] left = new byte[BlockSize];
        byte[] right = new byte[BlockSize];
        long position = 0;

        while (true)
        {
            int readLeft = Read(source, left);

            if (readLeft < 0)
            {
                return false;
            }

            if (readLeft == 0)
            {
                return true;
            }

            int readRight = Read(destination, right);

            if (readRight < 0)
            {
                return false;
            }

            if (readRight != readLeft)
            {
                mismatchOffset = position;
                return false;
            }

            int span = left.AsSpan(0, readLeft).CommonPrefixLength(right.AsSpan(0, readRight));

            if (span != readLeft)
            {
                mismatchOffset = position + span;
                return false;
            }

            position += readLeft;
        }
    }

    /// <summary>Reads one block, retrying until the buffer is full or the file ends.</summary>
    private static int Read(nint handle, byte[] buffer)
    {
        int filled = 0;

        while (filled < buffer.Length)
        {
            unsafe
            {
                uint got;

                fixed (byte* p = &buffer[filled])
                {
                    if (Kernel32.ReadFile(handle, p, (uint)(buffer.Length - filled), &got, null) == 0)
                    {
                        return -1;
                    }
                }

                if (got == 0)
                {
                    break;
                }

                filled += (int)got;
            }
        }

        return filled;
    }

    /// <summary>Human-readable description of a mismatch, for the error report.</summary>
    public static string Describe(long offset) => offset < 0
        ? "内容不一致"
        : $"内容在第 {offset:N0} 字节处开始不同";
}
