using System.Runtime.InteropServices;
using Roboc.Core.Interop;

namespace Roboc.Core.Diagnostics;

/// <summary>Turns Win32 / NTSTATUS codes into messages a human can act on, and
/// carries the context needed to decide retry vs elevation vs permanent skip.</summary>
internal static class NativeError
{
    public static string DescribeWin32(int code)
    {
        if (code == 0)
        {
            return "Success";
        }

        unsafe
        {
            byte* buffer = null;
            uint length = Kernel32.FormatMessage(
                Kernel32.FORMAT_MESSAGE_ALLOCATE_BUFFER |
                Kernel32.FORMAT_MESSAGE_FROM_SYSTEM |
                Kernel32.FORMAT_MESSAGE_IGNORE_INSERTS,
                0, (uint)code, 0, (byte*)&buffer, 0, 0);

            if (length == 0 || buffer == null)
            {
                return $"Win32 error {code}";
            }

            try
            {
                string message = new string((char*)buffer, 0, (int)length).TrimEnd('\r', '\n', ' ', '.');
                return $"{message} (0x{code:X8})";
            }
            finally
            {
                Kernel32.LocalFree((nint)buffer);
            }
        }
    }

    public static string DescribeNtStatus(int ntStatus)
    {
        if (ntStatus == NativeConst.STATUS_SUCCESS)
        {
            return "Success";
        }

        uint win32 = Ntdll.RtlNtStatusToDosError(ntStatus);
        string mapped = win32 != 0 ? DescribeWin32((int)win32) : $"NTSTATUS 0x{ntStatus:X8}";
        return $"{mapped} [NTSTATUS 0x{ntStatus:X8}]";
    }

    public static int LastWin32() => (int)Kernel32.GetLastError();

    public static void ThrowWin32(string operation, string path)
        => throw CreateWin32(operation, path, LastWin32());

    public static void ThrowWin32(string operation, string path, int code)
        => throw CreateWin32(operation, path, code);

    public static NativeIoException CreateWin32(string operation, string path, int code)
        => new(operation, path, code, DescribeWin32(code));

    public static NativeIoException CreateNtStatus(string operation, string path, int ntStatus)
        => new(operation, path, ntStatus, DescribeNtStatus(ntStatus));

    public static void ThrowNtStatus(string operation, string path, int ntStatus)
    {
        if (ntStatus != NativeConst.STATUS_SUCCESS)
        {
            throw CreateNtStatus(operation, path, ntStatus);
        }
    }
}

/// <summary>An IO failure with the context needed to classify it.</summary>
public sealed class NativeIoException : Exception
{
    public string Operation { get; }
    public string Path { get; }
    public int NativeCode { get; }

    public NativeIoException(string operation, string path, int nativeCode, string message)
        : base($"{operation} failed for '{path}': {message}")
    {
        Operation = operation;
        Path = path;
        NativeCode = nativeCode;
    }

    /// <summary>Worth an automatic retry: open elsewhere, volume briefly busy.</summary>
    public bool IsTransient =>
        NativeCode is NativeConst.ERROR_SHARING_VIOLATION
            or NativeConst.ERROR_LOCK_VIOLATION
            or NativeConst.ERROR_NOT_READY
            or NativeConst.ERROR_IO_PENDING
            or NativeConst.ERROR_IO_INCOMPLETE;

    /// <summary>Needs an elevated token. Drives the per-job escalation prompt
    /// rather than a blanket requireAdministrator manifest.</summary>
    public bool NeedsElevation =>
        NativeCode is NativeConst.ERROR_ACCESS_DENIED
            or NativeConst.ERROR_PRIVILEGE_NOT_HELD
            or NativeConst.ERROR_WRITE_PROTECT;

    public bool IsNotFound =>
        NativeCode is NativeConst.ERROR_FILE_NOT_FOUND
            or NativeConst.ERROR_PATH_NOT_FOUND
            or NativeConst.ERROR_NO_MORE_FILES;

    public bool IsDiskFull =>
        NativeCode is NativeConst.ERROR_DISK_FULL or NativeConst.ERROR_HANDLE_DISK_FULL;
}
