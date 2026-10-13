using System.Runtime.InteropServices;

namespace Roboc.Core.Interop;

/// <summary>
/// shell32 / user32 entry points for the clipboard and Explorer integration.
///
/// The file list travels as CF_HDROP so that copying files with this tool also
/// works with Explorer's own paste, and files copied by Explorer can be pasted
/// with this tool. A private clipboard format would have been easier and would
/// have made the two halves of the feature mutually exclusive.
/// </summary>
internal static partial class Shell32
{
    private const string Shell = "shell32.dll";
    private const string User = "user32.dll";

    /// <summary>DROPFILES header followed by a double-null-terminated wide path list.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DROPFILES
    {
        public uint pFiles;
        public int ptX;
        public int ptY;
        public int fNC;
        public int fWide;
    }

    /// <summary>
    /// Opens a folder and selects the given items in it.
    ///
    /// This is how Explorer reports the result of a paste: the new entries are left
    /// selected, so which ones were added is visible without reading a message. The
    /// selection is the report, which is also why no success dialog is shown.
    /// </summary>
    public static void SelectInFolder(string folder, IReadOnlyList<string> items)
    {
        if (items.Count == 0 || SHParseDisplayName(folder, 0, out nint folderPidl, 0, out _) != 0)
        {
            return;
        }

        var children = new nint[items.Count];
        int count = 0;

        try
        {
            foreach (string item in items)
            {
                if (SHParseDisplayName(item, 0, out nint child, 0, out _) == 0)
                {
                    children[count++] = child;
                }
            }

            if (count > 0)
            {
                SHOpenFolderAndSelectItems(folderPidl, (uint)count, children, 0);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        finally
        {
            for (int i = 0; i < count; i++)
            {
                CoTaskMemFree(children[i]);
            }

            CoTaskMemFree(folderPidl);
        }
    }

    [LibraryImport(Shell, EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(
        string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

    [LibraryImport(Shell)]
    private static partial int SHOpenFolderAndSelectItems(
        nint pidlFolder, uint cidl,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] apidl, uint dwFlags);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint pv);

    [LibraryImport(Shell, EntryPoint = "DragQueryFileW", SetLastError = true)]
    internal static unsafe partial uint DragQueryFile(nint hDrop, uint iFile, char* lpszFile, uint cch);

    [LibraryImport(User, SetLastError = true)]
    internal static partial int OpenClipboard(nint hWndNewOwner);

    [LibraryImport(User, SetLastError = true)]
    internal static partial int CloseClipboard();

    [LibraryImport(User, SetLastError = true)]
    internal static partial int EmptyClipboard();

    [LibraryImport(User, SetLastError = true)]
    internal static partial nint GetClipboardData(uint uFormat);

    [LibraryImport(User, SetLastError = true)]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport(User, SetLastError = true)]
    internal static partial int IsClipboardFormatAvailable(uint format);

    [LibraryImport(User, SetLastError = true)]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(User, EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(nint hWnd, string lpText, string lpCaption, uint uType);

    public const uint CF_HDROP = 15;

    /// <summary>
    /// Tells the shell that associations changed so it rebuilds its menus.
    ///
    /// Without this, adding context-menu verbs to the registry has no visible
    /// effect until Explorer happens to re-enumerate for some unrelated reason --
    /// which is why the entries appeared after a manual Explorer restart and then
    /// vanished again, with the registry provably intact the whole time.
    /// </summary>
    [LibraryImport(Shell)]
    internal static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;

    // MessageBox buttons and icons.
    public const uint MB_YESNO = 0x00000004;
    public const uint MB_YESNOCANCEL = 0x00000003;
    public const uint MB_ICONWARNING = 0x00000030;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_DEFBUTTON2 = 0x00000100;
    public const uint MB_SETFOREGROUND = 0x00010000;
    public const int IDYES = 6;
    public const int IDNO = 7;
    public const int IDCANCEL = 2;
}

/// <summary>Synthetic clipboard ownership announcements, so Explorer's paste
/// entry enables itself after this tool puts files on the clipboard.</summary>
internal static partial class User32Extra
{
    [LibraryImport("user32.dll", EntryPoint = "OpenClipboard", SetLastError = true)]
    internal static partial int Dummy();
}
