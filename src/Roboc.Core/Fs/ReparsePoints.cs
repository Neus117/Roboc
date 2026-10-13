using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;

namespace Roboc.Core.Fs;

/// <summary>
/// Reads and recreates NTFS reparse points.
///
/// Until this existed, a junction or symlink in the source was reported and
/// skipped, which made the copied tree incomplete. It was loud rather than silent,
/// but incomplete is incomplete.
///
/// Tags split into three groups, and treating them alike is what causes trouble:
///
///  * <b>Junctions and symlinks</b> are reproduced byte for byte. Their reparse
///    data holds the target path, and the same buffer is valid at the destination.
///
///  * <b>Provider tags</b> (OneDrive placeholders, cloud files, WCI, dedup) must
///    not be copied. Their data encodes state owned by a driver that will not
///    exist at the destination, so writing it produces an entry nothing can open.
///    These are instead <i>followed</i>, so the file's actual content is copied.
///    Skipping them would lose the data entirely, which for a cloud placeholder is
///    the common case rather than a corner case.
///
///  * <b>AppExecLink</b> and friends are neither reproducible nor meaningful
///    elsewhere; they are followed as well.
/// </summary>
internal static class ReparsePoints
{
    /// <summary>MAXIMUM_REPARSE_DATA_BUFFER_SIZE.</summary>
    public const int MaxBufferSize = 16 * 1024;

    /// <summary>
    /// Reads the raw reparse buffer. Requires the handle to be opened with
    /// FILE_FLAG_OPEN_REPARSE_POINT, otherwise the filesystem follows the link and
    /// reports the target instead.
    /// </summary>
    public static bool TryRead(string extendedPath, out byte[] data, out uint tag, out int error)
    {
        data = [];
        tag = 0;
        error = 0;

        nint handle = Kernel32.CreateFile(
            extendedPath,
            NativeConst.FILE_READ_ATTRIBUTES,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0,
            NativeConst.OPEN_EXISTING,
            NativeConst.FILE_FLAG_OPEN_REPARSE_POINT | NativeConst.FILE_FLAG_BACKUP_SEMANTICS,
            0);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            error = NativeError.LastWin32();
            return false;
        }

        try
        {
            var buffer = new byte[MaxBufferSize];

            unsafe
            {
                // lpBytesReturned is a pointer in the native signature, not an out
                // parameter; a local plus its address is the faithful call.
                uint returned;

                fixed (byte* p = buffer)
                {
                    if (Kernel32.DeviceIoControl(
                            handle, NativeConst.FSCTL_GET_REPARSE_POINT,
                            null, 0, p, (uint)buffer.Length, &returned, null) == 0)
                    {
                        error = NativeError.LastWin32();
                        return false;
                    }

                    if (returned < sizeof(uint) * 2)
                    {
                        error = NativeConst.ERROR_INVALID_NAME;
                        return false;
                    }

                    // ReparseTag is the first DWORD; ReparseDataLength the second.
                    tag = BitConverter.ToUInt32(buffer, 0);

                    int payload = (int)BitConverter.ToUInt16(buffer, 4);
                    int total = 8 + payload;

                    if (total > buffer.Length || total > returned)
                    {
                        error = NativeConst.ERROR_INVALID_NAME;
                        return false;
                    }

                    // The trailing 4-byte alignment padding is not part of the data.
                    data = new byte[total];
                    Array.Copy(buffer, data, total);
                }
            }

            return true;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Writes a previously read reparse buffer to a new path. The target must not
    /// already exist: a reparse point is set on a freshly created entry.
    /// </summary>
    public static bool TryWrite(string extendedPath, bool directory, byte[] data, out int error)
    {
        uint flags = NativeConst.FILE_FLAG_OPEN_REPARSE_POINT;
        uint disposition = NativeConst.CREATE_NEW;

        if (directory)
        {
            // A directory reparse point has to start as a real directory. Asking
            // CreateFile to create the path with CREATE_NEW produces an ordinary
            // empty FILE even when FILE_FLAG_BACKUP_SEMANTICS is set, and setting a
            // mount-point reparse point on that fails. Creating the directory first
            // and then setting the reparse point on it is what mklink itself does.
            flags |= NativeConst.FILE_FLAG_BACKUP_SEMANTICS;
            disposition = NativeConst.OPEN_EXISTING;

            if (!DirectoryOps.CreateIfMissing(extendedPath, out int createError))
            {
                error = createError;
                return false;
            }
        }

        nint handle = Kernel32.CreateFile(
            extendedPath,
            NativeConst.GENERIC_WRITE,
            0,
            0,
            disposition,
            flags,
            0);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            error = NativeError.LastWin32();

            // Do not leave a half-made entry behind for a link that does not work.
            if (directory)
            {
                Kernel32.RemoveDirectory(extendedPath);
            }

            return false;
        }

        bool set = false;

        try
        {
            unsafe
            {
                uint returned;

                fixed (byte* p = data)
                {
                    if (Kernel32.DeviceIoControl(
                            handle, NativeConst.FSCTL_SET_REPARSE_POINT,
                            p, (uint)data.Length, null, 0, &returned, null) == 0)
                    {
                        error = NativeError.LastWin32();
                        return false;
                    }
                }
            }

            set = true;
            error = 0;
            return true;
        }
        finally
        {
            Kernel32.CloseHandle(handle);

            // A directory created for a link that could not be made must not be
            // left behind: an empty directory with the link's name looks like a
            // successful copy to anything that only checks that the path exists,
            // which is worse than the entry being absent and reported.
            if (directory && !set)
            {
                Kernel32.RemoveDirectory(extendedPath);
            }
        }
    }

    /// <summary>True when the reparse data can be reproduced verbatim at a new
    /// location. Everything else has to be followed so its content is not lost.</summary>
    public static bool IsReproducible(uint tag)
        => tag is NativeConst.IO_REPARSE_TAG_MOUNT_POINT or NativeConst.IO_REPARSE_TAG_SYMLINK;
}
