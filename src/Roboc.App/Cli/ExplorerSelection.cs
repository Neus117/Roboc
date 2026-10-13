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