using System.Runtime.InteropServices;
using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;

namespace Roboc.Core.Fs;

/// <summary>
/// Alternate data streams: the extra named streams an NTFS file can carry.
///
/// <b>What they are.</b> A file's ordinary contents live in a stream called
/// <c>::$DATA</c>. NTFS lets a file carry more, each with its own name -- the one a
/// user is most likely to meet is <c>Zone.Identifier</c>, the mark Windows puts on
/// files that arrived from the internet. Delete and re-create a file and every named
/// stream is gone, silently: the file still opens, the size is unchanged, and nothing
/// anywhere says a part of it was left behind.
///
/// <b>Why this is not on by default.</b> Detecting streams costs one
/// <c>FindFirstStreamW</c> per file -- measured at 19 µs against a 16 µs baseline for
/// reading a timestamp, or roughly 4-7% of the throughput of a copy made of many
/// small files. That is the case this program exists for, and almost none of those
/// files carry streams at all. Paying the cost on every file to serve the few is the
/// wrong default; <c>--ads</c> is the way to ask for it.
///
/// <b>Only NTFS carries them.</b> FAT32, exFAT and most network shares cannot hold a
/// named stream at all, so a copy to one of those loses them however this is set. That
/// case is reported rather than passed over.
/// </summary>
internal static class AlternateStreams
{
    private const int FindStreamInfoStandard = 0;

    /// <summary>What FindFirstStreamW returns when there is nothing to enumerate.</summary>
    private static readonly nint InvalidHandleValue = -1;

    /// <summary>The default stream, which every file has and which is the file itself.</summary>
    private static readonly string[] DefaultStreamNames = ["::$DATA", ":$DATA"];

    /// <summary>Every stream name from this API ends with the stream type it holds.</summary>
    private const string StreamTypeSuffix = ":$DATA";

    /// <summary>
    /// Names of the streams a file carries that are not its ordinary contents.
    /// Empty when the file is not on NTFS, does not exist, or simply has none.
    /// </summary>
    public static List<string> Named(string extendedPath)
    {
        var names = new List<string>();

        nint handle = Kernel32.FindFirstStreamW(
            extendedPath, FindStreamInfoStandard, out WIN32_FIND_STREAM_DATA data, 0);

        if (handle == InvalidHandleValue)
        {
            return names;
        }


        try
        {
            while (true)
            {
                // The name arrives as ":name:$DATA", with a leading colon -- the
                // file's own contents being "::$DATA".
                //
                // Reading it as "name:$DATA" and splitting at the first colon produced
                // the whole string as the stream name, because the first colon is at
                // index zero and the split was guarded against that. Every path built
                // from it was then malformed and every stream failed to open.
                string raw = data.cStreamName ?? string.Empty;

                if (!DefaultStreamNames.Contains(raw, StringComparer.Ordinal))
                {

                    string name = raw.TrimStart(':');
                    int suffix = name.LastIndexOf(StreamTypeSuffix, StringComparison.Ordinal);

                    if (suffix >= 0)
                    {
                        name = name[..suffix];
                    }

                    if (name.Length > 0)
                    {
                        names.Add(name);
                    }
                }

                if (!Kernel32.FindNextStreamW(handle, out data))
                {
                    break;
                }
            }
        }
        finally
        {
            Kernel32.FindClose(handle);
        }

        return names;
    }

    /// <summary>
    /// Copies every named stream from one file to another.
    ///
    /// Returns the names it could not copy, so a caller can report them. Streams are
    /// copied one at a time and a failure on one does not abandon the rest: a file
    /// that keeps three of its four streams is closer to correct than one that keeps
    /// none.
    /// </summary>
    public static List<string> Copy(
        string sourceExtended, string destinationExtended, IReadOnlyList<string> names,
        out int lastError)
    {
        var failed = new List<string>();
        lastError = 0;

        foreach (string name in names)
        {
            string from = sourceExtended + ":" + name;
            string to = destinationExtended + ":" + name;

            if (!CopyOne(from, to, out int error))
            {
                failed.Add(name);
                lastError = error;
            }
        }

        return failed;
    }

    /// <summary>One stream, in chunks, with the destination opened or created.</summary>
    private static bool CopyOne(string from, string to, out int error)
    {
        error = 0;

        nint source = Kernel32.CreateFile(
            from, NativeConst.GENERIC_READ,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0, NativeConst.OPEN_EXISTING, 0, 0);

        if (source == NativeConst.INVALID_HANDLE_VALUE)
        {
            error = NativeError.LastWin32();
            return false;
        }

        try
        {
            nint destination = Kernel32.CreateFile(
                to, NativeConst.GENERIC_WRITE,
                NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
                0, NativeConst.CREATE_ALWAYS, 0, 0);

            if (destination == NativeConst.INVALID_HANDLE_VALUE)
            {
                error = NativeError.LastWin32();
                return false;
            }

            try
            {
                return Pump(source, destination, out error);
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

    /// <summary>
    /// Moves the bytes. A stream is usually tiny -- the internet mark is about a
    /// hundred bytes -- but a stream can be as large as the file it belongs to, so
    /// this reads in blocks rather than assuming it fits in one buffer.
    /// </summary>
    private static bool Pump(nint source, nint destination, out int error)
    {
        error = 0;
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            uint read;
            uint written;

            unsafe
            {
                fixed (byte* p = buffer)
                {
                    if (Kernel32.ReadFile(source, p, (uint)buffer.Length, &read, null) == 0)
                    {
                        error = NativeError.LastWin32();
                        return false;
                    }

                    if (read == 0)
                    {
                        return true;
                    }

                    if (Kernel32.WriteFile(destination, p, read, &written, null) == 0)
                    {
                        error = NativeError.LastWin32();
                        return false;
                    }
                }
            }

            if (written != read)
            {
                error = NativeConst.ERROR_WRITE_FAULT;
                return false;
            }
        }
    }
}
