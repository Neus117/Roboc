using System.Reflection;
using System.Runtime.InteropServices;

namespace Roboc.App.Cli;

/// <summary>
/// Reads the selection out of the foreground Explorer window.
///
/// <b>Why this exists.</b> A static registry verb receives its selection through
/// <c>%1</c>, and the shell stops expanding that per item once the selection grows
/// past about fifteen. Beyond that it takes a different invocation path and the verb
/// is called once with only the item under the pointer -- so 彻底删除 deleted one
/// file out of two hundred, and 复制 put one path on the clipboard. Both verbs failed
/// the same way at the same threshold, which is what identifies this as a limit of
/// the <c>%1</c> mechanism rather than anything the verbs do.
///
/// <b>What it does instead.</b> Asks the shell what is selected, through the
/// <c>Shell.Application</c> automation object. That answers the question regardless
/// of how many times the verb was invoked, so the number of invocations stops
/// mattering -- along with the wait for sibling processes that the old design needed.
///
/// <b>Why reflection.</b> The object is late-bound IDispatch. Binding it through
/// <c>dynamic</c> would pull in the Microsoft.CSharp runtime binder and its trimming
/// annotations for one call site; reflection keeps the published executable smaller
/// and makes every failure mode an ordinary exception that can be caught.
/// </summary>
internal static class ExplorerSelection
{
    /// <summary>
    /// Paths selected in the Explorer window that currently has focus, or an empty
    /// list when it cannot be determined.
    ///
    /// Never throws. Every failure -- no Explorer running, nothing selected, COM
    /// refusing to hand over the object -- means "no better answer available", and
    /// the caller falls back to the paths it was given.
    /// </summary>
    public static List<string> TryGet(string clickedPath)
    {
        var paths = new List<string>();

        // The folder the clicked item lives in identifies the window.
        //
        // Focus does not. When the user right-clicks, the foreground window is the
        // menu, and on Windows 11 that menu is a XAML popup whose owner chain does
        // not lead back to the folder window -- so matching on the foreground window
        // found nothing and the verb fell back to the single path the shell passed,
        // which is the large-selection failure all over again. The clicked item's
        // parent folder is stated by the shell itself and cannot be ambiguous.
        string wanted = string.Empty;

        try
        {
            string? parent = Path.GetDirectoryName(clickedPath);
            wanted = parent is null ? string.Empty : parent.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            wanted = string.Empty;
        }

        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");

            if (shellType is null)
            {
                return paths;
            }

            object? shell = Activator.CreateInstance(shellType);

            if (shell is null)
            {
                return paths;
            }

            try
            {
                // The window the user is actually working in, and nothing else.
                //
                // An earlier version took the first window that had anything
                // selected. A window left open on a previous folder keeps its
                // selection, so after copying 255 files from one folder and pasting
                // them into another, deleting the pasted files deleted the originals
                // instead: the shell handed over one path, and a stale window's
                // selection was read as the answer.
                nint foreground = GetForegroundWindow();

                // A context menu belongs to the window behind it, so at the moment a
                // verb runs the foreground window is usually the menu. Its root owner
                // is the folder.
                nint owner = foreground == 0 ? 0 : GetAncestor(foreground, GA_ROOTOWNER);

                object? windows = shellType.InvokeMember(
                    "Windows", BindingFlags.InvokeMethod, null, shell, null);

                if (windows is null)
                {
                    return paths;
                }

                Type windowsType = windows.GetType();
                int count = Convert.ToInt32(
                    windowsType.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));

                for (int i = 0; i < count; i++)
                {
                    object? window = windowsType.InvokeMember(
                        "Item", BindingFlags.InvokeMethod, null, windows, [i]);

                    if (window is null)
                    {
                        continue;
                    }

                    Type windowType = window.GetType();

                    nint hwnd;

                    try
                    {
                        object? handle = windowType.InvokeMember(
                            "HWND", BindingFlags.GetProperty, null, window, null);

                        hwnd = handle is null ? 0 : new nint(Convert.ToInt64(handle));
                    }
                    catch (Exception ex) when (ex is MissingMemberException or COMException)
                    {
                        continue;
                    }

                    object? document = windowType.InvokeMember(
                        "Document", BindingFlags.GetProperty, null, window, null);

                    if (document is null)
                    {
                        continue;   // Not a file-system window.
                    }

                    // Skipped unless it is showing the folder the shell says the
                    // click came from. No match means no answer: falling back to the
                    // path the shell passed is always safer than acting on another
                    // folder's selection, which is how files get deleted from the
                    // wrong place.
                    if (wanted.Length > 0 && !ShowsFolder(document, wanted))
                    {
                        continue;
                    }

                    // Prefer the window in front when several show the same folder,
                    // which happens with duplicated windows. A tie is broken rather
                    // than refused, because both are showing the right place.
                    if (wanted.Length == 0 && hwnd != 0 && hwnd != foreground && hwnd != owner)
                    {
                        continue;
                    }

                    object? selected = document.GetType().InvokeMember(
                        "SelectedItems", BindingFlags.InvokeMethod, null, document, null);

                    if (selected is null)
                    {
                        continue;
                    }

                    Type selectedType = selected.GetType();
                    int selectedCount = Convert.ToInt32(
                        selectedType.InvokeMember("Count", BindingFlags.GetProperty, null, selected, null));

                    if (selectedCount == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < selectedCount; j++)
                    {
                        object? item = selectedType.InvokeMember(
                            "Item", BindingFlags.InvokeMethod, null, selected, [j]);

                        if (item is null)
                        {
                            continue;
                        }

                        object? path = item.GetType().InvokeMember(
                            "Path", BindingFlags.GetProperty, null, item, null);

                        if (path is string text && text.Length > 0)
                        {
                            paths.Add(text);
                        }
                    }

                    // The matching window is the only answer there is.
                    if (paths.Count > 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
        }
        catch (Exception ex)
        {
            // Reported, not swallowed. An earlier version caught only the exception
            // types it expected, and the failure it actually hit was none of them --
            // leaving a silent empty result that looked exactly like "nothing is
            // selected" and cost a full test cycle to notice.
            Console.Error.WriteLine(
                $"explorer selection failed: {ex.GetType().Name}: {ex.Message}");
        }

        return paths;
    }

    /// <summary>
    /// The <c>Shell.Application</c> automation object, or null when it cannot be created.
    ///
    /// Deliberately separate from the copy above: the selection reader is on the path
    /// every menu verb takes, and it was left exactly as it was. The two share a
    /// technique, not code.
    /// </summary>
    private static object? TryCreateShell()
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            return shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (Exception ex) when (IsExpected(ex) || ex is TypeLoadException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder a shell window is showing, as a real path, or null when it cannot be read.
    ///
    /// <b>Why not LocationURL.</b> The address bar string percent-encodes some
    /// characters and not others -- a space arrives as <c>%20</c> while a Chinese
    /// character does not -- so comparing it against a path from the command line fails
    /// on exactly the folders that are hardest to notice. The automation model's own
    /// <c>Folder.Self.Path</c> is the path, already decoded.
    /// </summary>
    private static string? FolderShownBy(object document)
    {
        try
        {
            object? current = document.GetType().InvokeMember(
                "Folder", BindingFlags.GetProperty, null, document, null);

            if (current is null)
            {
                return null;
            }

            object? self = current.GetType().InvokeMember(
                "Self", BindingFlags.GetProperty, null, current, null);

            if (self is null)
            {
                return null;
            }

            object? path = self.GetType().InvokeMember(
                "Path", BindingFlags.GetProperty, null, self, null);

            return path as string;
        }
        catch (Exception ex) when (ex is COMException
                                      or TargetInvocationException
                                      or InvalidOperationException
                                      or MissingMemberException)
        {
            return null;
        }
    }

    /// <summary>Whether a shell window is showing the given folder.</summary>
    private static bool ShowsFolder(object document, string folder)
    {
        try
        {
            object? current = document.GetType().InvokeMember(
                "Folder", BindingFlags.GetProperty, null, document, null);

            if (current is null)
            {
                return false;
            }

            object? self = current.GetType().InvokeMember(
                "Self", BindingFlags.GetProperty, null, current, null);

            if (self is null)
            {
                return false;
            }

            object? path = self.GetType().InvokeMember(
                "Path", BindingFlags.GetProperty, null, self, null);

            return path is string text &&
                   string.Equals(
                       text.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is COMException
                                      or TargetInvocationException
                                      or InvalidOperationException
                                      or MissingMemberException)
        {
            return false;
        }
    }

    /// <summary>
    /// Selects items in the window that already displays their folder.
    ///
    /// <b>Why this exists.</b> <c>SHOpenFolderAndSelectItems</c> is documented only as
    /// "opens a Windows Explorer window with specified items selected". Nothing in the
    /// documentation says it reuses a window that is already open, and in practice it
    /// does not: a paste that creates new entries leaves the destination window's view
    /// invalidated, and the call then opens a second window on the same folder. The
    /// destination was already on screen, so the extra window is a step backwards that
    /// the user has to close by hand, every time.
    ///
    /// The shell can be asked directly instead. The automation model exposes
    /// <c>IShellFolderViewDual</c> through a shell window's document, and that interface
    /// has <c>SelectItem</c> -- so the selection is set in the window that is already
    /// there and nothing is opened.
    ///
    /// <b>Why the COM interfaces are declared and the rest is late-bound.</b>
    /// <c>IShellFolderViewDual</c> is a short, stable interface: four methods before
    /// <c>SelectItem</c>, all of them declared in the SDK header, so its vtable order can
    /// be written out and checked. <c>Folder</c> and <c>FolderItem</c> are not -- they
    /// carry several dozen methods, mostly legacy -- and a vtable transcribed by hand at
    /// that length is a crash waiting for the one slot that was miscounted. Those two are
    /// therefore reached through the same late binding the selection reader above already
    /// uses, and only the <c>SelectItem</c> call is early-bound.
    ///
    /// This path needs no <c>IServiceProvider::QueryService</c>. That was tried and
    /// cannot work from another process: <c>QueryService</c> is declared <c>[local]</c>
    /// and the interfaces behind <c>SID_STopLevelBrowser</c> carry <c>[local]</c> methods
    /// of their own, so they are not marshallable at all -- Microsoft's own wording is
    /// that the caller must have "access to the site chain of the browser". Nothing a
    /// call site does can supply that.
    ///
    /// <b>Contract.</b> Returns false when no window shows the folder, when the shell
    /// cannot be reached, or when the selection cannot be set. False is the caller's
    /// signal to fall back to opening one, which is exactly the case where opening one is
    /// the right answer. It reports failure by returning false and never by throwing, so
    /// that a convenience cannot fail the copy that has already succeeded.
    /// </summary>
    public static bool TrySelectInExistingWindow(string folder, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return false;
        }

        object? shell = TryCreateShell();

        if (shell is null)
        {
            return false;
        }

        try
        {
            object? windows = shell.GetType().InvokeMember(
                "Windows", BindingFlags.InvokeMethod, null, shell, null);

            if (windows is null)
            {
                return false;
            }

            Type windowsType = windows.GetType();
            int count = Convert.ToInt32(
                windowsType.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));

            for (int i = 0; i < count; i++)
            {
                object? window = windowsType.InvokeMember(
                    "Item", BindingFlags.InvokeMethod, null, windows, [i]);

                if (window is null)
                {
                    continue;
                }

                object? document;

                try
                {
                    document = window.GetType().InvokeMember(
                        "Document", BindingFlags.GetProperty, null, window, null);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    continue;   // Not a file-system window: This PC, a search result, a zip.
                }

                if (document is null || !ShowsFolder(document, folder))
                {
                    continue;
                }

                // The window showing the folder has been found. Everything past this
                // point is about that window only.
                return SelectIn(shell, document, folder, items);
            }

            return false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Console.Error.WriteLine(
                $"roboc: cannot select in the open window: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    /// <summary>Builds the item list and sets the selection through the window's own view.</summary>
    private static bool SelectIn(object shell, object document, string folder, IReadOnlyList<string> items)
    {
        // The Folder object is what turns names into FolderItems, and it is asked for
        // through the automation model rather than through the window's view: whichever
        // comes back, it has ParseName, and the window's own view may not.
        object folderObject;

        try
        {
            folderObject = shell.GetType().InvokeMember(
                "NameSpace", BindingFlags.InvokeMethod, null, shell, [folder])!;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }

        if (folderObject is null)
        {
            return false;
        }

        var wanted = new object[items.Count];
        int n = 0;

        foreach (string item in items)
        {
            // Asked for by leaf name, because a Folder is addressed by the names of its
            // children. A path would be a different question, and one this object is not
            // documented to answer.
            string leaf = System.IO.Path.GetFileName(item.TrimEnd('\\', '/'));

            if (leaf.Length == 0)
            {
                continue;
            }

            try
            {
                object? child = folderObject.GetType().InvokeMember(
                    "ParseName", BindingFlags.InvokeMethod, null, folderObject, [leaf]);

                if (child is not null)
                {
                    wanted[n++] = child;
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // One name that cannot be resolved must not cost the others their
                // selection: a partially selected result still shows the user what
                // arrived, and a refusal shows nothing.
            }
        }

        if (n == 0)
        {
            return false;
        }

        // The view is reached from the window's document -- not from the window object
        // itself, which does not implement this interface.
        if (document is not IShellFolderViewDual view)
        {
            return false;
        }

        try
        {
            for (int i = 0; i < n; i++)
            {
                // SVSI_DESELECTOTHERS on the first item only.
                //
                // It means "deselect everything else", not "start a new selection", and
                // passing it with every item is not idempotent: each call clears what the
                // previous one selected. Three pasted folders ended up with only the last
                // one selected, which looks like success from the outside and is exactly
                // wrong. Passing it once, before anything is selected, clears whatever the
                // window had and leaves the rest of the loop free to add to the selection.
                view.SelectItem(wanted[i], i == 0 ? SelectionFlags : SelectionFlags & ~SvsiDeselectOthers);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>SVSI_*</c> from the SDK's <c>_SVSIF</c>: make this the focused item, replace the
    /// current selection with it, and scroll it into view.
    /// </summary>
    private const int SelectionFlags = SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused;

    private const int SvsiSelect = 0x1;
    private const int SvsiDeselectOthers = 0x4;
    private const int SvsiEnsureVisible = 0x8;
    private const int SvsiFocused = 0x10;

    /// <summary>
    /// The view behind a shell window's document.
    ///
    /// Declared rather than reached late-bound because this is the one call whose
    /// argument a late-bound call cannot express: <c>SelectItem</c> takes a VARIANT that
    /// carries an interface pointer, which <c>InvokeMember</c> has no way to build.
    /// Everything before it in this interface is declared only to keep the vtable slots
    /// aligned -- <c>IDispatch</c>'s four methods come first, then these in the order the
    /// SDK header lists them, with <c>SelectItem</c> sixth.
    /// </summary>
    [ComImport]
    [Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellFolderViewDual
    {
        [DispId(0)]
        object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

        [DispId(0)]
        object Parent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

        [DispId(0)]
        object Folder { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

        [DispId(0)]
        object SelectedItems { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

        [DispId(0)]
        object FocusedItem { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

        [DispId(0)]
        void SelectItem([MarshalAs(UnmanagedType.Struct)] object pvfi, int dwFlags);
    }

    /// <summary>
    /// The exceptions a COM call against a running shell can raise.
    ///
    /// Listed rather than caught broadly so that a defect in this file is not silently
    /// turned into "no window was found", which is indistinguishable from the ordinary
    /// case and would be found only by noticing the extra windows.
    /// </summary>
    private static bool IsExpected(Exception ex)
        => ex is COMException
           or TargetInvocationException
           or InvalidOperationException
           or MissingMemberException
           or ArgumentException;

    /// <summary>    /// The selection, when it is larger than what the verb was handed.
    ///
    /// Only larger: a selection smaller than the passed paths means the shell and
    /// this program disagree about which window is in front, and the paths that
    /// actually arrived are the safer of the two.
    /// </summary>
    public static List<string> PreferLarger(IReadOnlyList<string> passed)
    {
        string clicked = passed.Count > 0 ? passed[0] : string.Empty;
        List<string> selected = TryGet(clicked);

        return selected.Count > passed.Count ? selected : [];
    }
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);
}
