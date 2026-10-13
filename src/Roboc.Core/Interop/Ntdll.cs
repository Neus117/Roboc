using System.Runtime.InteropServices;

namespace Roboc.Core.Interop;

/// <summary>
/// ntdll entry points. The delete engine goes straight here because the Win32
/// wrapper costs more than the operation once the file is cached: DeleteFileW
/// also fails on a read-only file, forcing callers into an extra
/// SetFileAttributes open/set/close round trip per file, whereas
/// FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE does it in one call.
/// </summary>
internal static partial class Ntdll
{
    private const string Dll = "ntdll.dll";

    [LibraryImport(Dll)]
    internal static partial int NtOpenFile(
        out nint fileHandle,
        uint desiredAccess,
        ref OBJECT_ATTRIBUTES objectAttributes,
        out IO_STATUS_BLOCK ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    [LibraryImport(Dll)]
    internal static unsafe partial int NtCreateFile(
        out nint fileHandle,
        uint desiredAccess,
        ref OBJECT_ATTRIBUTES objectAttributes,
        out IO_STATUS_BLOCK ioStatusBlock,
        long* allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        void* eaBuffer,
        uint eaLenth);

    [LibraryImport(Dll)]
    internal static unsafe partial int NtSetInformationFile(
        nint fileHandle,
        out IO_STATUS_BLOCK ioStatusBlock,
        void* fileInformation,
        uint length,
        int fileInformationClass);

    [LibraryImport(Dll)]
    internal static unsafe partial int NtQueryInformationFile(
        nint fileHandle,
        out IO_STATUS_BLOCK ioStatusBlock,
        void* fileInformation,
        uint length,
        int fileInformationClass);

    [LibraryImport(Dll)]
    internal static unsafe partial int NtQueryDirectoryFile(
        nint fileHandle,
        nint eventHandle,
        nint apcRoutine,
        nint apcContext,
        out IO_STATUS_BLOCK ioStatusBlock,
        void* fileInformation,
        uint length,
        int fileInformationClass,
        byte returnSingleEntry,
        UNICODE_STRING* fileName,
        byte restartScan);

    [LibraryImport(Dll)]
    internal static partial uint RtlNtStatusToDosError(int status);
}

/// <summary>Builds a UNICODE_STRING over a buffer that must stay pinned for the
/// lifetime of the enclosing fixed block.</summary>
internal static unsafe class NtString
{
    public static void Init(UNICODE_STRING* target, char* buffer, int charCount)
    {
        target->Length = (ushort)(charCount * sizeof(char));
        target->MaximumLength = (ushort)(charCount * sizeof(char));
        target->Buffer = (nint)buffer;
    }
}
