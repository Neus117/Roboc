using Roboc.Core.Diagnostics;
using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Model;

namespace Roboc.Core.Delete;

/// <summary>
/// Native delete primitives.
///
/// Two things make this faster than DeleteFileW for bulk work:
///  * FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE removes a read-only file in
///    the same call. DeleteFileW cannot, so every tool built on it needs an extra
///    open/set-attribute/close round trip per read-only file -- which is exactly
///    what PowerShell's Remove-Item -Force does.
///  * FILE_DISPOSITION_FLAG_POSIX_SEMANTICS lets the name disappear while handles
///    are still open, so open/delete/close pipelines instead of serialising.
/// </summary>
internal static class FastDeleter
{
    private const uint ShareAll =
        NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE;

    public static bool TryDelete(
        string extendedPath, bool directory, out ErrorKind kind, out int win32Code, out string message)
    {
        string trimmed = PathUtil.TrimTrailingSeparator(extendedPath);

        int status = Open(trimmed, directory, out nint handle);

        // A mislabelled entry is cheap to recover from. Reparse points in
        // particular can present as either kind.
        if (status != NativeConst.STATUS_SUCCESS)
        {
            status = Open(trimmed, !directory, out handle);
        }

        if (status != NativeConst.STATUS_SUCCESS)
        {
            win32Code = (int)Ntdll.RtlNtStatusToDosError(status);
            kind = Classify(win32Code);
            message = NativeError.DescribeNtStatus(status);
            return false;
        }

        try
        {
            var disposition = new FILE_DISPOSITION_INFO_EX
            {
                Flags = NativeConst.FILE_DISPOSITION_FLAG_DELETE
                      | NativeConst.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                      | NativeConst.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
            };

            unsafe
            {
                status = Ntdll.NtSetInformationFile(
                    handle, out _, &disposition, (uint)sizeof(FILE_DISPOSITION_INFO_EX),
                    NativeConst.NtFileDispositionInformationEx);
            }

            if (status != NativeConst.STATUS_SUCCESS)
            {
                win32Code = (int)Ntdll.RtlNtStatusToDosError(status);
                kind = Classify(win32Code);
                message = NativeError.DescribeNtStatus(status);
                return false;
            }

            kind = ErrorKind.Delete;
            win32Code = 0;
            message = string.Empty;
            return true;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    private static unsafe int Open(string extendedPath, bool directory, out nint handle)
    {
        uint options = NativeConst.FILE_OPEN_REPARSE_POINT
                     | NativeConst.FILE_SYNCHRONOUS_IO_NONALERT
                     | (directory ? NativeConst.FILE_DIRECTORY_FILE : NativeConst.FILE_NON_DIRECTORY_FILE);

        uint access = NativeConst.DELETE | NativeConst.SYNCHRONIZE | NativeConst.FILE_READ_ATTRIBUTES;

        string ntPath = PathUtil.ToNtPath(PathUtil.TrimTrailingSeparator(extendedPath));

        fixed (char* nameBuffer = ntPath)
        {
            UNICODE_STRING name;
            NtString.Init(&name, nameBuffer, ntPath.Length);

            var attributes = new OBJECT_ATTRIBUTES
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                RootDirectory = 0,
                ObjectName = (nint)(&name),
                Attributes = NativeConst.OBJ_CASE_INSENSITIVE,
                SecurityDescriptor = 0,
                SecurityQualityOfService = 0,
            };

            return Ntdll.NtOpenFile(out handle, access, ref attributes, out _, ShareAll, options);
        }
    }

    private static ErrorKind Classify(int win32Code) => win32Code switch
    {
        NativeConst.ERROR_ACCESS_DENIED
            or NativeConst.ERROR_PRIVILEGE_NOT_HELD
            or NativeConst.ERROR_WRITE_PROTECT => ErrorKind.AccessDenied,

        NativeConst.ERROR_FILE_NOT_FOUND
            or NativeConst.ERROR_PATH_NOT_FOUND
            or NativeConst.ERROR_NO_MORE_FILES => ErrorKind.SourceVanished,

        NativeConst.ERROR_DIR_NOT_EMPTY => ErrorKind.Delete,
        NativeConst.ERROR_INVALID_NAME or NativeConst.ERROR_BUFFER_OVERFLOW => ErrorKind.PathTooLong,
        _ => ErrorKind.Delete,
    };

    /// <summary>
    /// Single-threaded, failure-tolerant tree delete for background reclaim.
    ///
    /// Iterative with an explicit stack: a recursive version overflows the thread
    /// stack on a pathologically deep tree, which is exactly the kind of tree a
    /// cleanup routine is most likely to meet.
    /// </summary>
    public static void DeleteTreeQuiet(string rootExtended)
    {
        string root = PathUtil.TrimTrailingSeparator(rootExtended);

        if (!DirectoryOps.Exists(root))
        {
            return;
        }

        if (!DirectoryOps.IsExistingDirectory(root))
        {
            TryDelete(root, directory: false, out _, out _, out _);
            return;
        }

        var directories = new List<string>(64);
        var pending = new Stack<string>();
        var entries = new List<FindEntry>(64);

        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            directories.Add(directory);

            if (!DirectoryList.Enumerate(directory, entries))
            {
                continue;
            }

            foreach (FindEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (entry.IsReparsePoint)
                    {
                        // Delete the link, never descend into it: a junction can
                        // point anywhere, including outside the tree being
                        // removed, and following it would delete unrelated data.
                        TryDelete(entry.FullPath, directory: true, out _, out _, out _);
                        continue;
                    }

                    pending.Push(entry.FullPath);
                }
                else
                {
                    TryDelete(entry.FullPath, directory: false, out _, out _, out _);
                }
            }
        }

        // Reverse of a pre-order walk: every child before its parent.
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            TryDelete(directories[i], directory: true, out _, out _, out _);
        }
    }

    /// <summary>
    /// Same-volume "instant" detach: moving a directory into a sibling scratch
    /// directory is a single directory-entry update, so a 500,000-file tree
    /// disappears in the time it takes to rename one entry.
    ///
    /// Deliberately does NOT pass MOVEFILE_COPY_ALLOWED: a cross-volume "move" is
    /// a full copy plus delete, strictly worse than just deleting. If it cannot be
    /// done as a rename, this fails and the caller falls back.
    /// </summary>
    public static bool TryMoveToTrash(
        string extendedTarget, string trashDirectory, out string movedPath, out int win32Code)
    {
        string trimmed = PathUtil.TrimTrailingSeparator(extendedTarget);

        if (!DirectoryOps.CreateIfMissing(trashDirectory, out win32Code))
        {
            movedPath = string.Empty;
            return false;
        }

        // Hidden so the staging directory does not flash into view in Explorer.
        Kernel32.SetFileAttributes(trashDirectory, NativeConst.FILE_ATTRIBUTE_HIDDEN);

        string leaf = PathUtil.GetFileName(trimmed);
        movedPath = PathUtil.CombineExtended(trashDirectory, leaf);

        if (Kernel32.MoveFileEx(trimmed, movedPath, 0) == 0)
        {
            win32Code = NativeError.LastWin32();
            Kernel32.RemoveDirectory(trashDirectory);
            movedPath = string.Empty;
            return false;
        }

        win32Code = 0;
        return true;
    }

    /// <summary>Moves a staged item back where it came from. Returns false and a
    /// code when the original location is occupied or gone.</summary>
    public static bool TryRestore(string stagedPath, string originalPath, out int win32Code)
    {
        string parent = PathUtil.GetDirectoryName(PathUtil.TrimTrailingSeparator(originalPath));

        if (!string.IsNullOrEmpty(parent) && !DirectoryOps.Exists(parent) &&
            !DirectoryOps.CreateIfMissing(parent, out win32Code))
        {
            return false;
        }

        if (Kernel32.MoveFileEx(stagedPath, originalPath, 0) == 0)
        {
            win32Code = NativeError.LastWin32();
            return false;
        }

        win32Code = 0;
        return true;
    }
}
