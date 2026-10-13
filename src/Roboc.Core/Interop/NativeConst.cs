// Win32 / NT constants, enums and access masks.
//
// const rather than enum wherever the native signature is a DWORD: no casts leak
// upward and comparing/OR-ing generates no better code as an enum.

namespace Roboc.Core.Interop;

internal static class NativeConst
{
    public static readonly nint INVALID_HANDLE_VALUE = -1;

    // ------------------------------------------------------------ file access
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint DELETE = 0x00010000;
    public const uint READ_CONTROL = 0x00020000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_WRITE_DATA = 0x0002;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_TRAVERSE = 0x0020;

    // ------------------------------------------------------------ share modes
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;

    // -------------------------------------------------------- creation modes
    public const uint CREATE_NEW = 1;
    public const uint CREATE_ALWAYS = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint OPEN_ALWAYS = 4;
    public const uint TRUNCATE_EXISTING = 5;

    // ---------------------------------------------------------------- attrs
    public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_ARCHIVE = 0x00000020;
    public const uint FILE_ATTRIBUTE_DEVICE = 0x00000040;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_ATTRIBUTE_TEMPORARY = 0x00000100;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    public const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    public const uint FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 0x00002000;
    public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x00004000;
    public const uint FILE_ATTRIBUTE_INTEGRITY_STREAM = 0x00008000;
    public const uint FILE_ATTRIBUTE_NO_SCRUB_DATA = 0x00020000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    public const uint FILE_ATTRIBUTE_PINNED = 0x00080000;
    public const uint FILE_ATTRIBUTE_UNPINNED = 0x00100000;

    /// <summary>Attributes that describe where a file lives rather than what it
    /// is; copying them to a new location is wrong or needs privileges.</summary>
    public const uint FILE_ATTRIBUTE_UNCOPYABLE =
        FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_DEVICE | FILE_ATTRIBUTE_REPARSE_POINT |
        FILE_ATTRIBUTE_SPARSE_FILE | FILE_ATTRIBUTE_COMPRESSED | FILE_ATTRIBUTE_ENCRYPTED |
        FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN |
        FILE_ATTRIBUTE_INTEGRITY_STREAM | FILE_ATTRIBUTE_PINNED | FILE_ATTRIBUTE_UNPINNED |
        FILE_ATTRIBUTE_NO_SCRUB_DATA;

    // ------------------------------------------------------------ file flags
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const uint FILE_BEGIN = 0;

    // ------------------------------------------------------ find first ex flags
    public const uint FIND_FIRST_EX_LARGE_FETCH = 0x00000002;
    public const int FindExInfoStandard = 0;
    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;

    // ------------------------------------------------------------ error codes
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_WRITE_PROTECT = 19;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_HANDLE_DISK_FULL = 39;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_DISK_FULL = 112;
    public const int ERROR_INVALID_NAME = 123;
    public const int ERROR_INVALID_DRIVE = 15;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_DIR_NOT_EMPTY = 145;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_DIRECTORY = 267;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_IO_INCOMPLETE = 996;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_CANCELLED = 1223;
    public const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    public const int ERROR_BUFFER_OVERFLOW = 111;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    // -------------------------------------------------------------- move flags
    public const int ERROR_WRITE_FAULT = 29;

    public const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
    public const uint MOVEFILE_COPY_ALLOWED = 0x00000002;
    public const uint MOVEFILE_WRITE_THROUGH = 0x00000008;

    // ------------------------------------------------- get/set file info class
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileRenameInfo = 3;
    public const int FileDispositionInfo = 4;
    public const int FileAllocationInfo = 5;
    public const int FileEndOfFileInfo = 6;
    public const int FileIdInfo = 18;
    public const int FileIdExtdDirectoryInfo = 19;
    public const int FileDispositionInfoEx = 21;
    public const int FileRenameInfoEx = 22;

    // ------------------------------- FILE_DISPOSITION_INFO_EX flags (Win10 1709+)
    public const uint FILE_DISPOSITION_FLAG_DELETE = 0x00000001;
    /// <summary>The name disappears while handles are still open, so
    /// open/delete/close pipelines instead of serialising on the last handle.</summary>
    public const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x00000002;
    public const uint FILE_DISPOSITION_FLAG_FORCE_IMAGE_SECTION_CHECK = 0x00000004;
    public const uint FILE_DISPOSITION_FLAG_ON_CLOSE = 0x00000008;
    /// <summary>Removes the need for a separate read-only clear pass, which is a
    /// whole extra open/set/close per read-only file.</summary>
    public const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x00000010;

    // ------------------------------- FILE_RENAME_INFORMATION_EX flags (RS1+)
    public const uint FILE_RENAME_FLAG_REPLACE_IF_EXISTS = 0x00000001;
    public const uint FILE_RENAME_FLAG_POSIX_SEMANTICS = 0x00000002;

    // ------------------------------------------------------------ NT statuses
    public const int STATUS_SUCCESS = 0x00000000;
    public const int STATUS_PENDING = 0x00000103;
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    public const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);
    public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    public const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
    public const int STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);
    public const int STATUS_DELETE_PENDING = unchecked((int)0xC0000056);
    public const int STATUS_NOT_A_DIRECTORY = unchecked((int)0xC0000103);
    public const int STATUS_DIRECTORY_NOT_EMPTY = unchecked((int)0xC0000101);
    public const int STATUS_DISK_FULL = unchecked((int)0xC000007F);
    public const int STATUS_FILE_IS_A_DIRECTORY = unchecked((int)0xC00000BA);
    public const int STATUS_NOT_SAME_DEVICE = unchecked((int)0xC00000D4);

    // ------------------------------------------- NtCreateFile / NtOpenFile bits
    public const uint FILE_OPEN = 1;
    public const uint FILE_OPEN_IF = 3;
    public const uint FILE_DIRECTORY_FILE = 0x00000001;
    public const uint FILE_SEQUENTIAL_ONLY = 0x00000004;
    public const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    public const uint FILE_NON_DIRECTORY_FILE = 0x00000040;
    public const uint FILE_DELETE_ON_CLOSE = 0x00001000;
    public const uint FILE_OPEN_BY_FILE_ID = 0x00002000;
    public const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

    public const uint OBJ_CASE_INSENSITIVE = 0x00000040;

    // ----------------------------------------------- NT file information class
    public const int NtFileBasicInformation = 4;
    public const int NtFileStandardInformation = 5;
    public const int NtFileRenameInformation = 10;
    public const int NtFileDispositionInformation = 13;
    public const int NtFileAllocationInformation = 19;
    public const int NtFileEndOfFileInformation = 20;
    public const int NtFileNetworkOpenInformation = 34;
    public const int NtFileIdExtdDirectoryInformation = 60;
    public const int NtFileIdInformation = 59;
    public const int NtFileDispositionInformationEx = 64;
    public const int NtFileRenameInformationEx = 65;

    // ------------------------------------------------------------ device ioctl
    private const uint FILE_DEVICE_FILE_SYSTEM = 0x00000009;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;
    private const uint FILE_SPECIAL_ACCESS = 0;
    private const uint FILE_READ_ACCESS = 1;
    private const uint FILE_WRITE_ACCESS = 2;

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    /// <summary>ReFS / NTFS block clone: a server-side extent copy that never
    /// touches user space, making same-volume copies near-instant.</summary>
    public static readonly uint FSCTL_DUPLICATE_EXTENTS_TO_FILE =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 209, METHOD_BUFFERED, FILE_WRITE_ACCESS);

    public static readonly uint FSCTL_SET_SPARSE =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 49, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);

    public static readonly uint FSCTL_SET_ZERO_DATA =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 50, METHOD_BUFFERED, FILE_WRITE_ACCESS);

    public static readonly uint FSCTL_GET_REPARSE_POINT =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 42, METHOD_BUFFERED, FILE_ANY_ACCESS);

    /// <summary>
    /// FILE_ANY_ACCESS, not FILE_WRITE_ACCESS.
    ///
    /// The access bits are part of the IOCTL's identity, not a permission check. The
    /// documented definition is CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 41, METHOD_BUFFERED,
    /// FILE_ANY_ACCESS), which is 0x000900A4; declaring it with FILE_WRITE_ACCESS
    /// produces 0x0009C0A4, and the kernel does not recognise that request at all.
    ///
    /// It answers ERROR_INVALID_FUNCTION -- "incorrect function" -- which reads like a
    /// refusal by the filesystem and was taken for one for as long as this existed. The
    /// real meaning is that no such control code was ever asked for, so every junction
    /// and symlink in a copy was reported as a metadata failure that could not be
    /// reproduced.
    /// </summary>
    public static readonly uint FSCTL_SET_REPARSE_POINT =
        CtlCode(FILE_DEVICE_FILE_SYSTEM, 41, METHOD_BUFFERED, FILE_ANY_ACCESS);

    // ------------------------------------------------------------ volume flags
    public const uint FILE_CASE_SENSITIVE_SEARCH = 0x00000001;
    public const uint FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000;

    // ------------------------------------------------------------- wait/event
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;

    public const int THREAD_PRIORITY_LOWEST = -2;
    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    public const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    // ------------------------------------------------------- reparse point tags
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    public const uint IO_REPARSE_TAG_DEDUP = 0x80000013;
    public const uint IO_REPARSE_TAG_APPEXECLINK = 0x8000001B;
    public const uint IO_REPARSE_TAG_WCI = 0x80000018;
    public const uint IO_REPARSE_TAG_CLOUD = 0x9000001A;
    public const uint IO_REPARSE_TAG_AF_UNIX = 0x80000023;

    /// <summary>Tags that must never be copied as raw bytes: they are volume- or
    /// provider-specific and copying them corrupts the destination entry.</summary>
    public static bool IsUncopyableReparseTag(uint tag) =>
        (tag & 0x80000000) != 0 && (
            tag == IO_REPARSE_TAG_DEDUP ||
            tag == IO_REPARSE_TAG_WCI ||
            tag == IO_REPARSE_TAG_CLOUD ||
            tag == IO_REPARSE_TAG_APPEXECLINK ||
            tag == IO_REPARSE_TAG_AF_UNIX ||
            (tag & 0xFFFF0000) == 0x90000000);
}
