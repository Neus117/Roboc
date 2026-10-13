using System.Runtime.InteropServices;

namespace Roboc.Core.Interop;

// ---------------------------------------------------------------------------
// Layout notes that are easy to get silently wrong, and why several of these are
// hand-rolled instead of reusing BCL types:
//
//  * FILETIME is two DWORDs (alignment 4). Declaring it as `long` inside
//    WIN32_FIND_DATAW would force 8-byte alignment and shift every following
//    field by 4 bytes with no compiler complaint.
//
//  * FILE_RENAME_INFO's trailing FileName[] starts at offset 20, but the CLR
//    would size the header as 24 (8-byte align after FileNameLength). That
//    buffer is built by hand with an explicit offset instead of trusting sizeof.
//
//  * FILE_ID_EXTD_DIR_INFORMATION headers are walked by raw pointer arithmetic,
//    so the header must be exactly 88 bytes.
// ---------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
internal struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;

    public readonly long ToLong() => (long)(((ulong)dwHighDateTime << 32) | dwLowDateTime);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WIN32_FIND_DATAW
{
    public uint dwFileAttributes;
    public FILETIME ftCreationTime;
    public FILETIME ftLastAccessTime;
    public FILETIME ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;
    public uint dwReserved1;
    public fixed char cFileName[260];
    public fixed char cAlternateFileName[14];

    public readonly long FileSize => (long)(((ulong)nFileSizeHigh << 32) | nFileSizeLow);
    public readonly bool IsDirectory => (dwFileAttributes & NativeConst.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public readonly bool IsReparsePoint => (dwFileAttributes & NativeConst.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}

/// <summary>GetFileAttributesExW result: attributes, all three timestamps and the
/// size in one call. FILETIME stays the 4-byte-aligned pair so the layout matches.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WIN32_FILE_ATTRIBUTE_DATA
{
    public uint dwFileAttributes;
    public FILETIME ftCreationTime;
    public FILETIME ftLastAccessTime;
    public FILETIME ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;

    public readonly long FileSize => (long)(((ulong)nFileSizeHigh << 32) | nFileSizeLow);
    public readonly bool IsDirectory => (dwFileAttributes & NativeConst.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public readonly bool IsReparsePoint => (dwFileAttributes & NativeConst.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OVERLAPPED
{
    public nint Internal;
    public nint InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public nint hEvent;

    public void SetOffset(long offset)
    {
        Offset = (uint)offset;
        OffsetHigh = (uint)((ulong)offset >> 32);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_STATUS_BLOCK
{
    /// <summary>Union of NTSTATUS and PVOID; pointer-sized either way on x64.</summary>
    public nint Status;
    public nint Information;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OBJECT_ATTRIBUTES
{
    public uint Length;
    public nint RootDirectory;
    public nint ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_BASIC_INFO
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_STANDARD_INFO
{
    public long AllocationSize;
    public long EndOfFile;
    public uint NumberOfLinks;
    public byte DeletePending;
    public byte Directory;

    public readonly bool IsDirectory => Directory != 0;
}

/// <summary>
/// One entry from FindFirstStreamW / FindNextStreamW.
///
/// cStreamName is a fixed 296-wide character buffer in the native definition
/// (MAX_PATH + 36). It is declared ByValTStr with the full SizeConst rather than as a
/// string pointer because that is what the native struct is -- and because marshalling
/// it as a pointer would read past the end of the buffer the API filled.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WIN32_FIND_STREAM_DATA
{
    public long StreamSize;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
    public string? cStreamName;
}

/// <summary>Native FILE_ID_128 is a 16-byte array with alignment 1. Two ulongs
/// keep the same offsets here because the field already sits on an 8-byte
/// boundary inside both FILE_ID_INFO and FILE_ID_EXTD_DIR_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_128
{
    public ulong Low;
    public ulong High;

    public readonly bool IsZero => Low == 0 && High == 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_INFO
{
    public ulong VolumeSerialNumber;
    public FILE_ID_128 FileId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ALLOCATION_INFO
{
    public long AllocationSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_DISPOSITION_INFO_EX
{
    public uint Flags;
}

/// <summary>
/// Header of FILE_RENAME_INFORMATION / _EX. Explicit layout with Size=20 because
/// that is the true offset of the trailing FileName[]; letting the CLR compute it
/// yields 24 and the rename fails, or worse, is built wrong by hand.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct FILE_RENAME_INFO_HEADER
{
    /// <summary>FILE_RENAME_INFORMATION_EX flags; doubles as ReplaceIfExists.</summary>
    [FieldOffset(0)] public uint Flags;
    [FieldOffset(8)] public nint RootDirectory;
    [FieldOffset(16)] public uint FileNameLength;

    public const int FileNameOffset = 20;
}

/// <summary>Header of FILE_ID_EXTD_DIR_INFORMATION; FileName[] starts at offset 88.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_EXTD_DIR_INFORMATION
{
    public uint NextEntryOffset;
    public uint FileIndex;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long EndOfFile;
    public long AllocationSize;
    public uint FileAttributes;
    public uint FileNameLength;
    public uint EaSize;
    public uint ReparsePointTag;
    public FILE_ID_128 FileId;

    public const int FileNameOffset = 88;

    public readonly bool IsDirectory => (FileAttributes & NativeConst.FILE_ATTRIBUTE_DIRECTORY) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DUPLICATE_EXTENTS_DATA
{
    public nint FileHandle;
    public long SourceFileOffset;
    public long TargetFileOffset;
    public long ByteCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ZERO_DATA_INFORMATION
{
    public long FileOffset;
    public long BeyondFinalZero;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BY_HANDLE_FILE_INFORMATION
{
    public uint dwFileAttributes;
    public FILETIME ftCreationTime;
    public FILETIME ftLastAccessTime;
    public FILETIME ftLastWriteTime;
    public uint dwVolumeSerialNumber;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint nNumberOfLinks;
    public uint nFileIndexHigh;
    public uint nFileIndexLow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_INFO
{
    public ushort wProcessorArchitecture;
    public ushort wReserved;
    public uint dwPageSize;
    public nint lpMinimumApplicationAddress;
    public nint lpMaximumApplicationAddress;
    public nint dwActiveProcessorMask;
    public uint dwNumberOfProcessors;
    public uint dwProcessorType;
    public uint dwAllocationGranularity;
    public ushort wProcessorLevel;
    public ushort wProcessorRevision;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_FS_SECTOR_SIZE_INFORMATION
{
    public uint LogicalBytesPerSector;
    public uint PhysicalBytesPerSectorForAtomicity;
    public uint PhysicalBytesPerSectorForPerformance;
    public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
    public uint Flags;
    public uint ByteOffsetForSectorAlignment;
    public uint ByteOffsetForPartitionAlignment;
}

/// <summary>
/// Startup validation of the hand-written layouts. LayoutAssert.Verify() runs at
/// the top of every job, so a mismatch fails fast instead of corrupting IO.
/// </summary>
internal static class LayoutAssert
{
    public static void Verify()
    {
        Check<FILE_ID_EXTD_DIR_INFORMATION>(88, nameof(FILE_ID_EXTD_DIR_INFORMATION));
        Check<FILE_BASIC_INFO>(40, nameof(FILE_BASIC_INFO));
        Check<FILE_STANDARD_INFO>(24, nameof(FILE_STANDARD_INFO));
        Check<FILE_ID_INFO>(24, nameof(FILE_ID_INFO));
        Check<WIN32_FIND_DATAW>(592, nameof(WIN32_FIND_DATAW));
        Check<WIN32_FILE_ATTRIBUTE_DATA>(36, nameof(WIN32_FILE_ATTRIBUTE_DATA));
        Check<OVERLAPPED>(32, nameof(OVERLAPPED));
        Check<IO_STATUS_BLOCK>(16, nameof(IO_STATUS_BLOCK));
        Check<OBJECT_ATTRIBUTES>(48, nameof(OBJECT_ATTRIBUTES));
        Check<DUPLICATE_EXTENTS_DATA>(32, nameof(DUPLICATE_EXTENTS_DATA));
        Check<FILE_RENAME_INFO_HEADER>(FILE_RENAME_INFO_HEADER.FileNameOffset, nameof(FILE_RENAME_INFO_HEADER));
    }

    private static void Check<T>(int expected, string name) where T : unmanaged
    {
        int actual = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Native layout mismatch for {name}: expected {expected} bytes, got {actual}. " +
                "Interop structs are load-bearing; do not ship this build.");
        }
    }
}
