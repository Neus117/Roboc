using System.Runtime.InteropServices;
using Roboc.Core.Model;

namespace Roboc.Core.Interop;

/// <summary>
/// kernel32 entry points, declared with [LibraryImport] (compile-time generated
/// stubs) rather than [DllImport]: fully NativeAOT-compatible, no runtime
/// marshalling stub, and raw pointers are allowed on the hot IO paths.
/// </summary>
internal static partial class Kernel32
{
    private const string Dll = "kernel32.dll";
    private const StringMarshalling Utf16 = StringMarshalling.Utf16;

    public const int STD_INPUT_HANDLE = -10;
    public const int STD_OUTPUT_HANDLE = -11;
    public const int STD_ERROR_HANDLE = -12;

    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    public const uint FORMAT_MESSAGE_ALLOCATE_BUFFER = 0x00000100;
    public const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;
    public const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;
    public const uint THREAD_TERMINATE = 0x0001;
    public const int GetFileExInfoStandard = 0;

    // ------------------------------------------------------------------ files

    [LibraryImport(Dll, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    /// <summary>
    /// Opens the list of streams belonging to a file. Returns false when the file has
    /// no named streams, when the volume cannot hold them (FAT32, exFAT, most network
    /// shares), or when the file cannot be opened at all -- three situations the caller
    /// treats alike, because in none of them is there anything to copy.
    /// </summary>
    // Four parameters, and the handle is the RETURN VALUE -- not an out parameter.
    //
    // It was first written with five, the handle among them, which is not what the
    // API declares. The call still compiled and still ran: the native function read a
    // garbage fourth argument and its handle return was marshalled as a Boolean, and
    // the process died with an access violation the moment a stream was found. The
    // signature below is the one from the header.
    //
    // DllImport rather than LibraryImport, because the source generator cannot marshal
    // WIN32_FIND_STREAM_DATA -- cStreamName is a fixed 296-character buffer, which it
    // rejects with SYSLIB1051. The runtime marshaller handles ByValTStr.
    [DllImport(Dll, EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindFirstStreamW(
        string lpFileName, int infoLevel, out WIN32_FIND_STREAM_DATA data, int flags);

    [DllImport(Dll, EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextStreamW(nint handle, out WIN32_FIND_STREAM_DATA data);

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int ReadFile(
        nint hFile, byte* lpBuffer, uint nNumberOfBytesToRead,
        uint* lpNumberOfBytesRead, OVERLAPPED* lpOverlapped);

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int WriteFile(
        nint hFile, byte* lpBuffer, uint nNumberOfBytesToWrite,
        uint* lpNumberOfBytesWritten, OVERLAPPED* lpOverlapped);

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int SetFilePointerEx(nint hFile, long liDistanceToMove, long* lpNewFilePointer, uint dwMoveMethod);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int SetEndOfFile(nint hFile);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int GetFileSizeEx(nint hFile, out long lpFileSize);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int FlushFileBuffers(nint hFile);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int CloseHandle(nint hObject);

    [LibraryImport(Dll, EntryPoint = "DeleteFileW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int DeleteFile(string lpFileName);

    [LibraryImport(Dll, EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [LibraryImport(Dll, EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    [LibraryImport(Dll, EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial uint GetFileAttributes(string lpFileName);

    [LibraryImport(Dll, EntryPoint = "SetFileAttributesW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int SetFileAttributes(string lpFileName, uint dwFileAttributes);

    /// <summary>Attributes, timestamps and size in one syscall. This is the cheap
    /// existence+match probe the copy skip decision is built on.</summary>
    [LibraryImport(Dll, EntryPoint = "GetFileAttributesExW", SetLastError = true, StringMarshalling = Utf16)]
    internal static unsafe partial int GetFileAttributesEx(string lpFileName, int fInfoLevelId, void* lpFileInformation);

    // --------------------------------------------------------------- metadata

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int GetFileInformationByHandleEx(
        nint hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int SetFileInformationByHandle(
        nint hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int GetFileInformationByHandle(nint hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    // ------------------------------------------------------------ enumeration

    [LibraryImport(Dll, EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = Utf16)]
    internal static unsafe partial nint FindFirstFileEx(
        string lpFileName, int fInfoLevelId, void* lpFindFileData,
        int fSearchOp, nint lpSearchFilter, uint dwAdditionalFlags);

    [LibraryImport(Dll, EntryPoint = "FindNextFileW", SetLastError = true)]
    internal static unsafe partial int FindNextFile(nint hFindFile, void* lpFindFileData);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int FindClose(nint hFindFile);

    // ------------------------------------------------------------- directories

    [LibraryImport(Dll, EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int CreateDirectory(string lpPathName, nint lpSecurityAttributes);

    [LibraryImport(Dll, EntryPoint = "RemoveDirectoryW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int RemoveDirectory(string lpPathName);

    // --------------------------------------------------------------- volumes

    [LibraryImport(Dll, EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    internal static unsafe partial int GetVolumeInformationByHandle(
        nint hFile, char* lpVolumeNameBuffer, uint nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags, char* lpFileSystemNameBuffer, uint nFileSystemNameSize);

    [LibraryImport(Dll, EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int GetDiskFreeSpaceEx(
        string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    /// <summary>Source of the real sector size, the alignment contract for
    /// FILE_FLAG_NO_BUFFERING. NOT the 64 KiB allocation granularity.</summary>
    [LibraryImport(Dll, EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial int GetDiskFreeSpace(
        string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    // ---------------------------------------------------------------- ioctl

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int DeviceIoControl(
        nint hDevice, uint dwIoControlCode, void* lpInBuffer, uint nInBufferSize,
        void* lpOutBuffer, uint nOutBufferSize, uint* lpBytesReturned, OVERLAPPED* lpOverlapped);

    // ---------------------------------------------------- async / cancellation

    [LibraryImport(Dll, EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial nint CreateEvent(nint lpEventAttributes, int bManualReset, int bInitialState, string? lpName);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int SetEvent(nint hEvent);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int ResetEvent(nint hEvent);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int GetOverlappedResult(nint hFile, OVERLAPPED* lpOverlapped, out uint lpNumberOfBytesTransferred, int bWait);

    /// <summary>The only reliable way to break a thread out of a hung
    /// synchronous filesystem call. Central to the watchdog design.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int CancelIoEx(nint hFile, OVERLAPPED* lpOverlapped);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int CancelSynchronousIo(nint hThread);

    /// <summary>The watchdog needs a real thread handle to unwedge a worker;
    /// CancelSynchronousIo requires THREAD_TERMINATE.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint OpenThread(uint dwDesiredAccess, int bInheritHandle, uint dwThreadId);

    // --------------------------------------------------------------- threads

    [LibraryImport(Dll)]
    internal static partial nint GetCurrentThread();

    [LibraryImport(Dll)]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int SetThreadPriority(nint hThread, int nPriority);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int SetThreadDescription(nint hThread, [MarshalAs(UnmanagedType.LPWStr)] string lpThreadDescription);

    [LibraryImport(Dll)]
    internal static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    // ------------------------------------------------------------------ misc

    [LibraryImport(Dll)]
    internal static partial uint GetLastError();

    [LibraryImport(Dll, EntryPoint = "FormatMessageW", SetLastError = true)]
    internal static unsafe partial uint FormatMessage(
        uint dwFlags, nint lpSource, uint dwMessageId, uint dwLanguageId,
        byte* lpBuffer, uint nSize, nint Arguments);

    [LibraryImport(Dll)]
    internal static partial nint LocalFree(nint hMem);

    [LibraryImport(Dll)]
    internal static partial int QueryPerformanceFrequency(out long lpFrequency);

    [LibraryImport(Dll)]
    internal static partial int QueryPerformanceCounter(out long lpPerformanceCount);

    [LibraryImport(Dll)]
    internal static partial nint GetConsoleWindow();

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int AttachConsole(uint dwProcessId);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int AllocConsole();

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GetStdHandle(int nStdHandle);

    // ------------------------------------------------------- console characters

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    /// <summary>
    /// Writes wide characters straight to a console. This is the only path that
    /// involves no code page at all, so the result cannot be mangled by one.
    /// </summary>
    [LibraryImport(Dll, EntryPoint = "WriteConsoleW", SetLastError = true)]
    internal static unsafe partial int WriteConsoleW(
        nint hConsoleOutput, char* lpBuffer, uint nNumberOfCharsToWrite,
        out uint lpNumberOfCharsWritten, void* lpReserved);

    [LibraryImport(Dll, SetLastError = true)]
    internal static unsafe partial int WideCharToMultiByte(
        uint codePage, uint dwFlags, char* lpWideCharStr, int cchWideChar,
        byte* lpMultiByteStr, int cbMultiByte, byte* lpDefaultChar, int* lpUsedDefaultChar);

    /// <summary>The code page a redirected reader will assume by default.</summary>
    [LibraryImport(Dll)]
    internal static partial uint GetOEMCP();

    [LibraryImport(Dll)]
    internal static partial uint GetConsoleOutputCP();

    // -------------------------------------------------------------- token query

    [LibraryImport(Dll)]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static unsafe partial int GetTokenInformation(
        nint tokenHandle, int tokenInformationClass, void* tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll")]
    internal static unsafe partial uint* GetSidSubAuthority(byte* sid, uint index);

    [LibraryImport("advapi32.dll")]
    internal static unsafe partial byte* GetSidSubAuthorityCount(byte* sid);

    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenIntegrityLevel = 25;

    /// <summary>
    /// The process token's mandatory integrity level.
    ///
    /// Worth reporting because Low integrity cannot write HKEY_CURRENT_USER\Software
    /// no matter whose account it is, and children inherit the level. A program at
    /// Low integrity therefore cannot register itself, and neither can anything it
    /// starts.
    /// </summary>
    public static unsafe string DescribeIntegrityLevel()
    {
        if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out nint token) == 0)
        {
            return Lang.Pick("无法打开进程令牌", "cannot open the process token");
        }

        try
        {
            uint size = 0;
            GetTokenInformation(token, TokenIntegrityLevel, null, 0, out size);

            if (size == 0 || size > 1024)
            {
                return Lang.Pick("无法查询完整性级别", "cannot query the integrity level");
            }

            var buffer = new byte[size];

            fixed (byte* p = buffer)
            {
                if (GetTokenInformation(token, TokenIntegrityLevel, p, size, out _) == 0)
                {
                    return Lang.Pick("无法查询完整性级别", "cannot query the integrity level");
                }

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; } -- the SID
                // pointer is first, so the SID bytes follow inline on 64-bit.
                byte* sid = *(byte**)p;
                byte* count = GetSidSubAuthorityCount(sid);

                if (count is null || *count == 0)
                {
                    return Lang.Pick("完整性级别 SID 无效", "the integrity level SID is not valid");
                }

                // GetSidSubAuthority returns a pointer to the DWORD, not the value.
                uint* rid = GetSidSubAuthority(sid, (uint)(*count - 1));
                uint level = rid is null ? 0 : *rid;

                return level switch
                {
                    0x0000 => "Untrusted (0)",
                    0x1000 => Lang.Pick("Low (4096) -- 无法写 HKCU\\Software", "Low (4096) -- cannot write to HKCU\\Software"),
                    0x2000 => "Medium (8192)",
                    0x3000 => "High (12288)",
                    0x4000 => "System (16384)",
                    _ => Lang.Pick($"未知 ({level})", $"unknown ({level})"),
                };
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>The ANSI code page, which is what a Windows pipe reader assumes.</summary>
    [LibraryImport(Dll)]
    internal static partial uint GetACP();

    // File creation flags used for reopening CONOUT$ by name.
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    // --------------------------------------------------- global memory (clipboard)

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int GlobalUnlock(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nuint GlobalSize(nint hMem);

    [LibraryImport(Dll, EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = Utf16)]
    internal static partial nint GetModuleHandle(string? lpModuleName);
}
