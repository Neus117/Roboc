using Roboc.Core.Interop;
using Roboc.Core.Model;

namespace Roboc.Core.Shell;

/// <summary>
/// Reads and writes the Windows file-list clipboard format (CF_HDROP).
///
/// Using the real clipboard rather than a private format means the tool
/// interoperates in both directions: copy here and paste in Explorer, or copy in
/// Explorer and paste with this tool. It also means the "copy" half of the
/// context menu needs no state of its own.
/// </summary>
internal static class ClipboardFiles
{
    private const int DropFilesHeaderSize = 20;   // DROPFILES is five DWORDs

    /// <summary>Puts the given paths on the clipboard as a file list.</summary>
    public static bool TrySet(IReadOnlyList<string> displayPaths, out string failure)
    {
        // Each path is NUL-terminated and the list gets an extra terminating NUL.
        int chars = 1;
        foreach (string path in displayPaths)
        {
            chars += path.Length + 1;
        }

        int totalBytes = DropFilesHeaderSize + (chars * sizeof(char));
        nint handle = Kernel32.GlobalAlloc(
            Kernel32.GMEM_MOVEABLE | Kernel32.GMEM_ZEROINIT, (nuint)totalBytes);

        if (handle == 0)
        {
            failure = "GlobalAlloc failed";
            return false;
        }

        nint buffer = Kernel32.GlobalLock(handle);
        if (buffer == 0)
        {
            Kernel32.GlobalFree(handle);
            failure = "GlobalLock failed";
            return false;
        }

        try
        {
            unsafe
            {
                var header = (Shell32.DROPFILES*)buffer;
                header->pFiles = DropFilesHeaderSize;
                header->fWide = 1;   // wide characters; the ANSI form is legacy

                var cursor = (char*)(buffer + DropFilesHeaderSize);
                foreach (string path in displayPaths)
                {
                    path.AsSpan().CopyTo(new Span<char>(cursor, path.Length));
                    cursor += path.Length + 1;
                }

                // The terminating NUL came from GMEM_ZEROINIT.
            }
        }
        finally
        {
            Kernel32.GlobalUnlock(handle);
        }

        // Retried, because losing the race for the clipboard is normal and temporary.
        //
        // The clipboard has one owner at a time, so any other program copying at that
        // instant makes this fail. Treating that as an error produced a dialog per
        // process during a large copy -- a wall of identical warnings about something
        // that resolves itself in milliseconds. Waiting is the correct response to
        // contention; reporting it is reserved for a clipboard that stays locked.
        bool opened = false;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Shell32.OpenClipboard(0) != 0)
            {
                opened = true;
                break;
            }

            Thread.Sleep(50);
        }

        if (!opened)
        {
            Kernel32.GlobalFree(handle);
            failure = Lang.Pick("剪贴板正被其他程序占用，暂时无法写入。请稍后重试。", "Another program is holding the clipboard. Try again in a moment.");
            return false;
        }

        try
        {
            if (Shell32.EmptyClipboard() == 0)
            {
                failure = "EmptyClipboard failed";
                return false;
            }

            // On success the block's ownership transfers to the clipboard, so it
            // must not be freed here.
            if (Shell32.SetClipboardData(Shell32.CF_HDROP, handle) == 0)
            {
                failure = "SetClipboardData failed";
                return false;
            }
        }
        finally
        {
            Shell32.CloseClipboard();
        }

        failure = string.Empty;
        return true;
    }

    /// <summary>Reads the file list currently on the clipboard.</summary>
    public static List<string> Get()
    {
        var result = new List<string>();

        if (Shell32.IsClipboardFormatAvailable(Shell32.CF_HDROP) == 0)
        {
            return result;
        }

        if (Shell32.OpenClipboard(0) == 0)
        {
            return result;
        }

        try
        {
            nint drop = Shell32.GetClipboardData(Shell32.CF_HDROP);
            if (drop == 0)
            {
                return result;
            }

            unsafe
            {
                uint count = Shell32.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                var buffer = new char[32768];   // long paths are legal; MAX_PATH is not the limit

                for (uint i = 0; i < count; i++)
                {
                    fixed (char* p = buffer)
                    {
                        uint length = Shell32.DragQueryFile(drop, i, p, (uint)buffer.Length);
                        if (length > 0)
                        {
                            result.Add(new string(p, 0, (int)length));
                        }
                    }
                }
            }
        }
        finally
        {
            Shell32.CloseClipboard();
        }

        return result;
    }
}
