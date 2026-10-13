using Roboc.Core.Interop;

namespace Roboc.Core.Fs;

internal readonly record struct FindEntry(
    string Name,
    string FullPath,
    long Size,
    long CreationTime,
    long LastAccessTime,
    long LastWriteTime,
    uint Attributes,
    bool IsDirectory,
    bool IsReparsePoint);

/// <summary>
/// Shared directory enumeration, used by copy, delete and the trash sweeper.
///
/// Two flags do the heavy lifting and are easy to omit by accident:
/// FindExInfoBasic skips the 8.3 short-name lookup, and
/// FIND_FIRST_EX_LARGE_FETCH pulls 64 KiB of directory data per call instead of
/// one entry, which is what keeps enumeration from becoming the bottleneck on a
/// many-small-files workload.
/// </summary>
internal static class DirectoryList
{
    public static unsafe bool Enumerate(string directoryExtended, List<FindEntry> into)
    {
        into.Clear();

        string pattern = PathUtil.CombineExtended(directoryExtended, "*");

        WIN32_FIND_DATAW data;
        nint handle = Kernel32.FindFirstFileEx(
            pattern, NativeConst.FindExInfoBasic, &data,
            NativeConst.FindExSearchNameMatch, 0, NativeConst.FIND_FIRST_EX_LARGE_FETCH);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            return false;
        }

        try
        {
            do
            {
                string name = new(data.cFileName);
                if (name is "." or "..")
                {
                    continue;
                }

                into.Add(new FindEntry(
                    Name: name,
                    FullPath: PathUtil.CombineExtended(directoryExtended, name),
                    Size: data.FileSize,
                    CreationTime: data.ftCreationTime.ToLong(),
                    LastAccessTime: data.ftLastAccessTime.ToLong(),
                    LastWriteTime: data.ftLastWriteTime.ToLong(),
                    Attributes: data.dwFileAttributes,
                    IsDirectory: data.IsDirectory,
                    IsReparsePoint: data.IsReparsePoint));
            }
            while (Kernel32.FindNextFile(handle, &data) != 0);
        }
        finally
        {
            Kernel32.FindClose(handle);
        }

        return true;
    }
}
