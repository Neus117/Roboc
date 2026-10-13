using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;

namespace Roboc.Core.Fs;

/// <summary>Native directory operations. Deliberately avoids System.IO.Directory,
/// which normalises the path again and stats the result, turning one syscall into
/// three.</summary>
internal static class DirectoryOps
{
    public static bool CreateIfMissing(string extendedPath, out int errorCode)
        => CreateIfMissing(extendedPath, out errorCode, out _);

    /// <summary>Additionally reports whether this call created the directory. A
    /// directory we just created is provably empty, which lets the copy path skip
    /// its per-file destination probe entirely.</summary>
    public static bool CreateIfMissing(string extendedPath, out int errorCode, out bool created)
    {
        if (Kernel32.CreateDirectory(extendedPath, 0) != 0)
        {
            errorCode = 0;
            created = true;
            return true;
        }

        errorCode = NativeError.LastWin32();
        created = false;

        // Races are expected when two workers descend into sibling trees that
        // share a parent; re-check rather than report failure.
        if (errorCode is NativeConst.ERROR_ALREADY_EXISTS or NativeConst.ERROR_ACCESS_DENIED
            && IsExistingDirectory(extendedPath))
        {
            errorCode = 0;
            return true;
        }

        return false;
    }

    public static bool IsExistingDirectory(string extendedPath)
    {
        uint attributes = Kernel32.GetFileAttributes(extendedPath);
        return attributes != 0xFFFFFFFF && (attributes & NativeConst.FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    public static bool Exists(string extendedPath) => Kernel32.GetFileAttributes(extendedPath) != 0xFFFFFFFF;

    /// <summary>Attributes, timestamps and size in one call. This is the whole
    /// skip decision's input, so it must stay one syscall.</summary>
    public static unsafe bool TryGetAttributes(string extendedPath, out WIN32_FILE_ATTRIBUTE_DATA data)
    {
        WIN32_FILE_ATTRIBUTE_DATA local;
        bool ok = Kernel32.GetFileAttributesEx(extendedPath, Kernel32.GetFileExInfoStandard, &local) != 0;
        data = ok ? local : default;
        return ok;
    }
}
