using System.Text;
using Roboc.Core.Control;
using Roboc.Core.Copy;
using Roboc.Core.Model;
using Roboc.Core.Progress;
using Roboc.Core.Interop;
using Roboc.Core.Shell;

namespace Roboc.App.Cli;

/// <summary>Clipboard, paste and Explorer registration verbs.</summary>
internal static class ShellCommand
{
    /// <summary>The running executable, which is what the menu must point at.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Roboc.exe");

    /// <summary>
    /// Called on every start. A registration that points at a path this
    /// executable no longer occupies is worse than no registration: Explorer
    /// would offer a verb that launches nothing. Since this is a portable tool
    /// that gets moved, repairing on start is the only way it stays correct.
    /// </summary>
    public static void SelfHeal(string command = "", Action<string>? log = null)
    {
        if (!ContextMenuInstaller.IsInstalled)
        {
            return;
        }

        if (ContextMenuInstaller.IsStale(ExecutablePath))
        {
            log?.Invoke("executable moved; re-registering context-menu entries");
            ContextMenuInstaller.Install(ExecutablePath, out _, log);
            return;
        }

        // A restart empties the clipboard, so 粘贴 has to go back to grey without
        // anyone asking. SyncPaste writes only when the value would change, so the
        // common case here is two reads.
        //
        // Skipped for 复制: that command sets the clipboard itself and syncs the menu
        // from what it has just written, so probing here would only add a clipboard
        // open to every one of the processes Explorer starts for one selection.
        if (!string.Equals(command, "clipboard", StringComparison.OrdinalIgnoreCase))
        {
            ContextMenuInstaller.SyncPaste(ClipboardFiles.Get().Count > 0, out _);
        }

        // The program's own entry takes the icon for the current Windows app theme.
        //
        // After the sync above rather than before it, and for the same reason: this is the
        // path every launch takes, so it is the only place the theme can be noticed at all.
        // Nothing here is expensive -- a registry read and a string comparison in the
        // ordinary case -- and the comparison is against the value already in the registry,
        // so a theme that has not changed writes nothing.
        //
        // Failures are not reported. A menu icon is the least important thing this program
        // does, and a registry that refuses the write has already been reported by Install.
        ContextMenuInstaller.SyncThemeIcon(ExecutablePath, log);
    }

    /// <summary>Puts the selected paths on the clipboard as a file list.</summary>
    public static int Clipboard(Args args)
    {
        bool fromMenu = args.Flag("--gui-errors");
        List<string> paths = args.Positionals();

        if (paths.Count == 0)
        {
            MaintenanceCommands.ReportProblemPublic(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。\r\n\r\n", "⚠️No items were received.\r\n\r\n") +
                Lang.Pick("如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。", "If this came from the context menu, the selection did not reach the submenu."));
            return 2;
        }

        // Logged before the claim, by every invocation.
        //
        // The batch-size line further down is written only by the process that wins
        // the claim, so it cannot answer "how many times did Explorer call this" --

        if (!MaintenanceCommands.TryCollectFromMenu(fromMenu, "clipboard", paths, out List<string> collected))
        {
            return 0;
        }

        paths = collected;

        if (paths.Count == 0)
        {
            MaintenanceCommands.ReportProblemPublic(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."));
            return 2;
        }


        if (!ClipboardFiles.TrySet(paths, out string failure))
        {
            MaintenanceCommands.ReportProblemPublic(fromMenu, Lang.Pick($"⚠️写入剪贴板失败：{failure}", $"⚠️Could not write to the clipboard: {failure}"));
            return 1;
        }



        // 粘贴 is disabled while the clipboard holds no files, so putting files
        // there is also what enables the menu entry.
        // Every state change happens before any dialog. A modal dialog blocks the
        // thread, so anything sequenced after one is deferred until the user
        // dismisses it -- and a dialog that gets covered by another window leaves the
        // feature visibly broken until it is found and closed.
        if (!ContextMenuInstaller.SyncPaste(hasClipboardFiles: true, out string syncFailure))
        {
            Console.Error.WriteLine(Lang.Pick($"roboc: 无法更新粘贴菜单项的状态：{syncFailure}", $"roboc: cannot update the paste menu entry: {syncFailure}"));
        }

        // No confirmation. Explorer shows none when you copy, and 粘贴 lighting up
        // in the menu already reports that the clipboard holds something.

        if (!args.Flag("--quiet"))
        {
            Console.WriteLine(Lang.Pick($"已复制 {paths.Count} 项到剪贴板", $"Copied {paths.Count} items to the clipboard"));
        }

        return 0;
    }

    /// <summary>Reads the clipboard file list and copies it into the target.</summary>
    public static int Paste(Args args, bool move)
    {
        bool fromMenu = args.Flag("--gui-errors");

        // Single instance. Explorer invokes a verb once per selected item even when
        // it takes no arguments, and 粘贴 takes none -- so it never entered the
        // batching path and produced one dialog per selected file. 回收空间 had the
        // same defect.
        //
        // TryClaimImmediate, not TryClaim. The paragraph above describes exactly what
        // TryClaim was supposed to do here, and it did not do it: TryClaim returns
        // true without taking the lock when the path is empty, so all four processes
        // of a four-file selection claimed, and a paste that failed opened four
        // dialogs. Tested on 2026-10-15: four concurrent instances, window peak four.
        //
        // The waiting half of TryClaim is not wanted here anyway, and its own remarks
        // say so: nothing needs reassembling because the clipboard is the batch. What
        // is wanted is only that one process reads it -- which is what
        // TryClaimImmediate provides, without the settle window.
        //
        // The clipboard is read, never written, so the "two hundred processes writing
        // the clipboard in turn" hazard TryClaim's comments describe does not apply;
        // the lock is here so one paste happens, not to serialise writes.
        if (fromMenu && !PendingSelection.TryClaimImmediate("paste"))
        {
            return 0;
        }

        string destination = args.Option("--to", Environment.CurrentDirectory);

        // If Explorer did not substitute %V, the literal token arrives and the
        // paste would look for a folder by that name and fail silently. Explorer
        // sets the working directory to the folder being browsed, so that is the
        // right answer in exactly the case where the substitution did not happen.
        if (destination is "%V" or "%v" || destination.Contains('%'))
        {
            destination = Environment.CurrentDirectory;
        }

        // %V expands to the selected item, and that is a file whenever files are
        // selected -- so the target arrived as a file path and the copy had nowhere
        // to go. Explorer pastes into the containing folder in that case, and so
        // does this now: 右键粘贴 behaves the same whether it was opened on a
        // selection or on empty space.
        // %V is the folder only for the background verb. On a selection it is the
        // selected item -- a file, or with several selected the first of them -- so
        // the paste belongs in its containing folder. Without this a multi-selection
        // pasted into whichever item happened to be first in the list.
        if (!args.Flag("--here"))
        {
            string candidate = destination.TrimEnd('\\', '/');

            string? parent = Directory.Exists(candidate) || File.Exists(candidate)
                ? Path.GetDirectoryName(candidate)
                : null;

            if (!string.IsNullOrEmpty(parent))
            {
                destination = parent;
            }
        }

        List<string> sources = ClipboardFiles.Get();

        // Logged because the two ways a paste does nothing look identical from the
        // result: an empty clipboard, and a clipboard naming paths that no longer
        // exist. Both end with the engine reporting zero of everything, and the
        // count plus the first path tells them apart in one line.

        if (sources.Count == 0)
        {
            MaintenanceCommands.ReportProblemPublic(fromMenu,
                Lang.Pick("⚠️剪贴板里没有文件。\r\n\r\n请先对要复制的项目使用 Roboc → 复制。", "⚠️There are no files on the clipboard.\r\n\r\nUse Roboc → Copy on what you want to copy first."));
            return 2;
        }

        // Re-enter the normal copy path so paste gets the same engine, progress,
        // verification and error handling as an explicit copy.
        //
        // --gui-errors is forwarded, not dropped. Without it the copy's summary and
        // its errors went to a console that does not exist, so a paste whose sources
        // had gone -- for instance because they had just been recycled -- produced
        // no output at all and only the journal directory was left behind.
        // Ask before overwriting. Without this a second paste merged into the first
        // result: files were replaced in place, so the folder no longer held what
        // either operation had put there.
        string overwriteArguments = string.Empty;

        if (fromMenu)
        {
            // 300 ms is enough to finish any realistic paste outright; past that the
            // dialog says "at least" rather than making the user wait for a number.
            ConflictScan.Result scan = ConflictScan.Run(destination, sources, budgetMs: 300);

            // Nothing to decide when nothing clashes, so no prompt: the paste simply
            // proceeds and the new items are selected afterwards.
            if (scan.Conflicts > 0)
            {
                int answer = MaintenanceCommands.AskOverwrite(scan);

                if (answer == 2)
                {
                    return 0;
                }

                // SkipExisting, not a whole-item filter. Explorer's behaviour -- and
                // the behaviour a user expects from 否 -- is to merge same-named
                // folders and then decide inside them: a file that is already there is
                // left alone, a file that is not is copied. Dropping the top-level
                // item, which an earlier version did, skipped new files along with the
                // existing ones.
                overwriteArguments = answer == 0 ? "--overwrite all" : "--overwrite skip-existing";
            }
        }

        // Recorded before the copy so the new entries can be selected afterwards,
        // the way Explorer reports a paste.
        // Every target, not only the ones that did not exist. A merge into an
        // existing folder changed its contents, and selecting nothing left the user
        // with no indication that anything had happened.
        List<string> touched = fromMenu
            ? [.. sources
                .Select(s => Path.GetFileName(s.TrimEnd('\\', '/')))
                .Where(leaf => leaf.Length > 0)
                .Select(leaf => Path.Combine(destination, leaf))]
            : [];

        int extra = fromMenu ? 1 : 0;
        string[] overwriteParts = overwriteArguments.Length > 0
            ? overwriteArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        var forwarded = new string[3 + sources.Count + extra + overwriteParts.Length];
        forwarded[0] = move ? "move" : "copy";
        forwarded[1] = "--to";
        forwarded[2] = destination;

        for (int i = 0; i < sources.Count; i++)
        {
            forwarded[3 + i] = sources[i];
        }

        for (int i = 0; i < overwriteParts.Length; i++)
        {
            forwarded[3 + sources.Count + i] = overwriteParts[i];
        }

        if (fromMenu)
        {
            forwarded[3 + sources.Count + overwriteParts.Length] = "--gui-errors";
        }

        var changedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int result = CopyCommand.Run(new Args(forwarded), move, fromMenu ? changedRoots : null);

        if (fromMenu && changedRoots.Count > 0)
        {
            // Only the items that actually received something. Selecting the whole
            // selection implied every one of them had changed, which was wrong
            // whenever some were already complete.
            List<string> changed = [.. touched.Where(t => changedRoots.Contains(Path.GetFileName(t)))];

            if (changed.Count > 0)
            {
                // The window already showing the destination gets the selection, so that
                // a paste into a folder the user is looking at does not open a second
                // window on the same folder.
                //
                // SHOpenFolderAndSelectItems is documented only as "opens a Windows
                // Explorer window with specified items selected" -- it promises nothing
                // about reusing one that is already open, and it does not: creating the
                // new entries invalidates the destination window's view and the call
                // opens another window beside it. False here means no window was showing
                // the folder, and that is exactly the case where opening one is right.
                if (!ExplorerSelection.TrySelectInExistingWindow(destination, changed))
                {
                    Shell32.SelectInFolder(destination, changed);
                }
            }
        }

        return result;
    }

    public static int Install(Args args)
    {
        bool quiet = args.Flag("--quiet");
        Action<string>? log = quiet ? null : Console.WriteLine;

        // Writing the registry can be refused in ways this program cannot see or
        // fix, so the registration is also available as a file to import with
        // regedit, which nothing interferes with.
        string exportPath = args.Option("--export-reg", string.Empty);

        if (exportPath.Length > 0)
        {
            return ExportRegistry(ContextMenuInstaller.BuildRegistryFile(ExecutablePath), exportPath);
        }

        // The context menu could not be made to appear on this machine, so the
        // SendTo menu is offered as a target that does not depend on the shell
        // choosing to list a registry verb.
        if (args.Flag("--sendto"))
        {
            int sendToCount = SendToMenu.Install(ExecutablePath, out string sendToFailure, log);

            if (sendToCount < 0)
            {
                Console.Error.WriteLine($"roboc: {sendToFailure}");
                return 1;
            }

            if (!quiet)
            {
                Console.WriteLine();
                Console.WriteLine(Lang.Pick($"入口位置：{SendToMenu.SendToDirectory}", $"Installed at: {SendToMenu.SendToDirectory}"));
                Console.WriteLine(Lang.Pick("右键任意文件 → 发送到 → 选择 Roboc 复制 / 回收 / 彻底删除。", "Right-click any file → Send to → pick Roboc Copy / Recycle / Delete."));
                Console.WriteLine(Lang.Pick("多选时全部选中项会一次传给程序。", "With several items selected, all of them are passed in one go."));
            }

            return 0;
        }

        // The cascade is the requested presentation; the flat layout is the
        // fallback for a context where Explorer will not pass the selection through
        // a submenu.
        ContextMenuInstaller.FlatLayout = args.Flag("--flat");
        ContextMenuInstaller.MachineScope = args.Flag("--machine");

        if (ContextMenuInstaller.MachineScope && !IsElevated() && !quiet)
        {
            Console.WriteLine(Lang.Pick("提示：--machine 写入 HKLM，需要管理员权限；当前进程未提权，可能失败。", "Note: --machine writes to HKLM and needs administrator rights. This process is not elevated, so it may fail."));
        }

        if (ContextMenuInstaller.FlatLayout && !quiet)
        {
            Console.WriteLine(Lang.Pick("使用平级菜单布局（非级联）", "Using the flat menu layout (not cascaded)"));
        }

        int count = ContextMenuInstaller.Install(ExecutablePath, out string failure, log);

        if (count < 0)
        {
            Console.Error.WriteLine($"roboc: {failure}");
            return 1;
        }

        if (!quiet)
        {
            Console.WriteLine();
            Console.WriteLine("Explorer integration installed.");
            Console.WriteLine($"  executable : {ExecutablePath}");
            Console.WriteLine($"  verbs      : {count}");
            Console.WriteLine();
            Console.WriteLine("On Windows 11 these appear under 'Show more options'. The");
            Console.WriteLine("entries live in HKEY_CURRENT_USER and need no administrator.");
        }

        return 0;
    }

    public static int Uninstall(Args args)
    {
        bool quiet = args.Flag("--quiet");
        Action<string>? log = quiet ? null : Console.WriteLine;

        string exportPath = args.Option("--export-reg", string.Empty);

        if (exportPath.Length > 0)
        {
            return ExportRegistry(ContextMenuInstaller.BuildRegistryRemovalFile(), exportPath);
        }

        if (args.Flag("--sendto"))
        {
            SendToMenu.Uninstall(log);
            return 0;
        }

        ContextMenuInstaller.Uninstall(log);

        if (!quiet)
        {
            Console.WriteLine("Explorer integration removed. No trace is left in the registry.");
        }

        return 0;
    }

    /// <summary>Whether this process is running with administrator rights.</summary>
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Writes a .reg file, or prints it when the path is "-".
    ///
    /// UTF-16 with a byte order mark, because that is what the "Windows Registry
    /// Editor Version 5.00" header announces and what regedit expects.
    /// </summary>
    private static int ExportRegistry(string content, string path)
    {
        if (path == "-")
        {
            Console.Write(content);
            return 0;
        }

        try
        {
            File.WriteAllText(path, content, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(Lang.Pick($"roboc: 无法写入 {path}：{ex.Message}", $"roboc: cannot write {path}: {ex.Message}"));
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(Lang.Pick($"roboc: 无法写入 {path}：{ex.Message}", $"roboc: cannot write {path}: {ex.Message}"));
            return 1;
        }

        Console.WriteLine(Lang.Pick($"已导出注册表文件：{path}", $"Registry file written: {path}"));
        Console.WriteLine();
        Console.WriteLine(Lang.Pick("双击导入即可（regedit 是 Windows 受信任程序，不受针对本程序的限制）。", "Double-click it to import. (regedit is a trusted Windows program and is not subject to the restrictions placed on this one.)"));
        Console.WriteLine(Lang.Pick("导入前可以先用记事本打开查看内容。", "You can open it in Notepad first to see what it contains."));
        Console.WriteLine(Lang.Pick("导入后如未立即生效，重启资源管理器或注销重登。", "If it does not take effect immediately, restart Explorer or sign out and back in."));
        return 0;
    }

    /// <summary>Whether a source's top-level target already exists at the destination.</summary>
    internal static bool TargetExists(string destination, string source)
    {
        string leaf = Path.GetFileName(source.TrimEnd('\\', '/'));

        return leaf.Length > 0 &&
               Core.Fs.DirectoryOps.Exists(
                   Core.Fs.PathUtil.ToExtended(Path.Combine(destination, leaf)));
    }

    /// <summary>
    /// How many of the sources would land on something that is already there.
    ///
    /// Roboc nests each source under its own name, so the top-level target is
    /// the destination plus the source's leaf name.
    /// </summary>
    internal static int CountConflicts(string destination, IReadOnlyList<string> sources)
    {
        int conflicts = 0;

        foreach (string source in sources)
        {
            string leaf = Path.GetFileName(source.TrimEnd('\\', '/'));

            if (leaf.Length == 0)
            {
                continue;
            }

            if (Core.Fs.DirectoryOps.Exists(Core.Fs.PathUtil.ToExtended(Path.Combine(destination, leaf))))
            {
                conflicts++;
            }
        }

        return conflicts;
    }

    /// <summary>Counts how many of the given paths are files and how many are folders.</summary>
    internal static (int Files, int Folders) CountKinds(IEnumerable<string> paths)
    {
        int files = 0;
        int folders = 0;

        foreach (string path in paths)
        {
            if (Core.Fs.DirectoryOps.IsExistingDirectory(Core.Fs.PathUtil.ToExtended(path)))
            {
                folders++;
            }
            else
            {
                files++;
            }
        }

        return (files, folders);
    }

    /// <summary>
    /// "3 个文件、2 个文件夹", omitting a kind that is absent so the text does not
    /// read as "0 个文件".
    /// </summary>
    internal static string Describe(int files, int folders)
    {
        var parts = new List<string>(2);

        if (files > 0)
        {
            parts.Add(Lang.Pick($"{files:N0} 个文件", $"{files:N0} files"));
        }

        if (folders > 0)
        {
            parts.Add(Lang.Pick($"{folders:N0} 个文件夹", $"{folders:N0} folders"));
        }

        if (parts.Count == 0)
        {
            return Lang.Pick(" 0 项", " 0 items");
        }

        // The separator belongs to the language, not to the join: Chinese uses an
        // enumeration comma, English a comma and a space.
        return " " + string.Join(Lang.Pick("、", ", "), parts);
    }

    public static int Status(Args args)
    {
        _ = args;
        string? installed = ContextMenuInstaller.InstalledPath;

        Console.WriteLine($"executable : {ExecutablePath}");
        Console.WriteLine($"log        : {ConsoleHost.LogPath ?? ConsoleHost.LogLocationHint()}");
        string sendToState = SendToMenu.IsInstalled
            ? Lang.Pick("已安装", "installed")
            : Lang.Pick("未安装", "not installed");

        Console.WriteLine(Lang.Pick($"发送到菜单 : {sendToState}", $"Send To    : {sendToState}"));

        if (installed is null)
        {
            Console.WriteLine("registered : no");
        }
        else
        {
            Console.WriteLine($"registered : {installed}");
            Console.WriteLine(ContextMenuInstaller.IsStale(ExecutablePath)
                ? "state      : STALE (will repair on next start)"
                : "state      : current");
        }

        List<string> onClipboard = ClipboardFiles.Get();
        Console.WriteLine($"clipboard  : {onClipboard.Count} file(s)");
        foreach (string path in onClipboard.Take(5))
        {
            Console.WriteLine($"             {path}");
        }

        return 0;
    }
}
