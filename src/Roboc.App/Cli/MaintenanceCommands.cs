using Microsoft.Win32;
using Roboc.Core.Control;
using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Model;

namespace Roboc.App.Cli;

/// <summary>
/// The verbs the context menu invokes: 回收, 回收空间, 彻底删除, plus a registry
/// diagnostic that exists because installing the menu can fail in a way the code
/// cannot see from the inside.
/// </summary>
internal static class MaintenanceCommands
{
    /// <summary>回收 — move the selection into this tool's own recycle bin.</summary>
    /// <summary>
    /// Says what is about to happen before it happens.
    ///
    /// The verb is labelled 回收 whether or not the selection is in the bin, so the
    /// user is told which of the two things this press will do and can back out. A
    /// restore that runs without warning would be indistinguishable from a discard.
    /// </summary>
    private static bool ConfirmRestore(List<string> inBin, int discardCount)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine(Lang.Pick($"♻️选中的 {inBin.Count:N0} 项在 Roboc 回收站里。", $"♻️{inBin.Count:N0} of the selected items are in the Roboc recycle bin."));
        text.AppendLine();
        text.AppendLine(Lang.Pick("回收站中的项目无法再次回收，可以取回它们原来的位置：", "Items already in the bin cannot be recycled again. They can be put back where they came from:"));
        text.AppendLine();
        text.AppendLine(Lang.Pick("是：取回原位置", "Yes: put them back"));
        text.AppendLine(Lang.Pick("否：什么都不做", "No: do nothing"));

        if (discardCount > 0)
        {
            text.AppendLine();
            text.AppendLine(Lang.Pick($"另有 {discardCount:N0} 项不在回收站中，将在取回之后照常回收。", $"{discardCount:N0} more are not in the bin and will be recycled as usual afterwards."));
        }

        return ShowDialog(
            text.ToString().TrimEnd(), Lang.Pick("Roboc — 取回确认", "Roboc — Confirm restore"),
            warning: false, ask: true, centre: true);
    }

    /// <summary>
    /// Runs the restore path with its console output captured.
    ///
    /// Reused rather than reimplemented: the guards, the collision refusal and the
    /// empty-scaffolding cleanup are the parts that must not drift apart between the
    /// two verbs that can trigger a restore.
    /// </summary>
    private static int RunRestoreQuietly(List<string> paths, out string errors)
    {
        var forwarded = new List<string> { "restore", "--quiet" };
        forwarded.AddRange(paths);

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            int exitCode = Restore(new Args([.. forwarded]));
            errors = captured.ToString().Trim();
            return exitCode;
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>Migrates every bin on this machine to the flat layout.</summary>
    internal static void MigrateBins()
    {
        foreach (string root in RecycleBin.ExistingRoots())
        {
            RecycleBin.Migrate(root);
        }
    }

    public static int Recycle(Args args)
    {
        List<string> paths = args.Positionals();

        bool fromMenuEarly = args.Flag("--gui-errors");

        if (paths.Count == 0)
        {
            ReportProblem(fromMenuEarly, Lang.Pick("⚠️没有收到任何选中的项目。\r\n\r\n", "⚠️No items were received.\r\n\r\n") +
                Lang.Pick("如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。", "If this was invoked from the context menu, the selection did not reach the submenu."));
            return 2;
        }

        if (!TryCollectFromMenu(fromMenuEarly, "recycle", paths, out List<string> collected))
        {
            return 0;
        }

        paths = collected;

        if (paths.Count == 0)
        {
            ReportProblem(fromMenuEarly, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."));
            return 2;
        }

        // 回收 means two things now.
        //
        // An item already in the bin cannot be discarded again -- the only useful
        // action left is to put it back, so this verb offers that instead. A static
        // verb's label cannot change with the selection, and AppliesTo cannot express
        // a path condition here (four expressions were tried; all of them simply hid
        // the entry), so the meaning is chosen by the command and announced before
        // anything moves.
        var inBin = new List<string>();
        var toDiscard = new List<string>();

        foreach (string path in paths)
        {
            if (RecycleBin.IsInsideAnyBin(PathUtil.ToExtended(path)))
            {
                inBin.Add(path);
            }
            else
            {
                toDiscard.Add(path);
            }
        }

        if (inBin.Count > 0)
        {
            if (fromMenuEarly && !ConfirmRestore(inBin, toDiscard.Count))
            {
                return 0;
            }

            int restoreExit = RunRestoreQuietly(inBin, out string restoreErrors);

            if (fromMenuEarly && restoreErrors.Length > 0)
            {
                ReportProblem(true, restoreErrors);
            }

            if (toDiscard.Count == 0)
            {
                return restoreExit;
            }

            paths = toDiscard;
        }

        // Brought up to the flat layout before anything is added to it, so a bin that
        // predates the change ends up uniform rather than a mixture that only this
        // program can read.
        MigrateBins();

        if (fromMenuEarly)
        {
            // Invoked from the context menu, where a console message would never be
            // seen. Report through a dialog instead.
            return RecycleWithDialog(paths);
        }

        int moved = 0;
        int failed = 0;

        foreach (string path in paths)
        {
            string extended = PathUtil.ToExtended(path);

            if (RecycleBin.TryPut(extended, out string stored, out int error))
            {
                moved++;
                if (!args.Flag("--quiet"))
                {
                    Console.WriteLine(Lang.Pick($"已回收 {path}", $"Recycled {path}"));
                    Console.WriteLine($"    -> {PathUtil.ToDisplay(stored)}");
                }
            }
            else
            {
                failed++;
                Console.Error.WriteLine(Lang.Pick($"roboc: 无法回收 {path}：{DescribeBinError(error)}", $"roboc: cannot recycle {path}: {DescribeBinError(error)}"));
            }
        }

        if (!args.Flag("--quiet"))
        {
            Console.WriteLine(Lang.Pick($"回收完成：成功 {moved} 项，失败 {failed} 项", $"Recycle finished: {moved} succeeded, {failed} failed"));
        }

        return failed == 0 ? 0 : 3;
    }

    private static int RecycleWithDialog(List<string> paths)
    {
        var failures = new List<string>();
        int moved = 0;

        foreach (string path in paths)
        {
            if (RecycleBin.TryPut(PathUtil.ToExtended(path), out _, out int error))
            {
                moved++;
            }
            else
            {
                failures.Add($"{path}\r\n    {DescribeBinError(error)}");
            }
        }

        if (failures.Count == 0)
        {
            return 0;
        }

        string text = Lang.Pick($"⚠️已回收 {moved} 项，{failures.Count} 项失败：\r\n\r\n", $"⚠️Recycled {moved}, {failures.Count} failed:\r\n\r\n") +
                      string.Join("\r\n\r\n", failures.Take(10));

        if (failures.Count > 10)
        {
            text += Lang.Pick($"\r\n\r\n（另有 {failures.Count - 10} 项未显示）", $"\r\n\r\n({failures.Count - 10} more not shown)");
        }

        ShowDialog(text, Lang.Pick("Roboc 回收", "Roboc — Recycle"), warning: true, centre: true);
        return 3;
    }

    /// <summary>回收空间 — report or reclaim what the bins are holding.</summary>
    /// <summary>
    /// Puts recycled items back where they came from.
    ///
    /// The bin mirrors the source path, so the original is recoverable by arithmetic
    /// rather than from an index -- that is what <see cref="RecycleBin.OriginalPathOf"/>
    /// does, and it is why no bookkeeping file exists. Nothing is copied: the bin sits
    /// on the volume that held the item, so putting it back is a rename, as fast as
    /// discarding it was.
    /// </summary>
    public static int Restore(Args args)
    {
        List<string> paths = args.Positionals();
        bool fromMenu = args.Flag("--gui-errors");

        if (paths.Count == 0)
        {
            ReportProblem(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."));
            return 2;
        }

        if (!TryCollectFromMenu(fromMenu, "restore", paths, out List<string> collected))
        {
            return 0;
        }

        paths = collected;

        int restored = 0;
        var failures = new List<string>();
        var restoredPaths = new List<string>();

        foreach (string path in paths)
        {
            string extended = PathUtil.ToExtended(path);

            string? binRoot = RecycleBin.RootFor(extended);

            if (binRoot is null)
            {
                failures.Add(Lang.Pick($"{path}\r\n  该项目不在 Roboc 回收站中，无法取回。", $"{path}\r\n  This item is not in the Roboc recycle bin, so it cannot be restored."));
                continue;
            }

            string? original = RecycleBin.OriginalPathOf(extended, binRoot);

            if (original is null)
            {
                failures.Add(Lang.Pick($"{path}\r\n  无法从回收站的存放位置推算出原路径。", $"{path}\r\n  The original location cannot be worked out from where it is stored."));
                continue;
            }

            // Refused, not overwritten: the file at the original location may be a
            // newer version the user is working on, and a restore must never be the
            // thing that destroys it.
            if (DirectoryOps.Exists(original))
            {
                failures.Add(
                    $"{PathUtil.ToDisplay(original)}\r\n" +
                    Lang.Pick("  原位置已经有同名项目了。请先处理掉它，否则取回会覆盖掉它。", "  Something with that name is already in the original location. Deal with it first, or restoring will overwrite it."));
                continue;
            }

            string? parent = PathUtil.GetDirectoryName(original);

            if (!string.IsNullOrEmpty(parent))
            {
                try
                {
                    Directory.CreateDirectory(PathUtil.ToDisplay(parent));
                }
                catch (IOException ex)
                {
                    failures.Add(Lang.Pick($"{path}\r\n  无法建立原目录：{ex.Message}", $"{path}\r\n  Cannot create the original folder: {ex.Message}"));
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    failures.Add(Lang.Pick($"{path}\r\n  无法建立原目录：{ex.Message}", $"{path}\r\n  Cannot create the original folder: {ex.Message}"));
                    continue;
                }
            }

            if (Kernel32.MoveFileEx(extended, original, 0) == 0)
            {
                // Win32Exception carries the system's own message for the code, so it
                // is already in the user's language; NativeError is internal to Core
                // and would have needed an InternalsVisibleTo for one string.
                int moveError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                failures.Add(
                    Lang.Pick($"{path}\r\n  移动失败：{new System.ComponentModel.Win32Exception(moveError).Message}", $"{path}\r\n  Move failed: {new System.ComponentModel.Win32Exception(moveError).Message}"));
                continue;
            }

            // The sidecar describes an item that is no longer in the bin.
            RecycleBin.ClearRecord(binRoot, extended);

            restored++;
            restoredPaths.Add(PathUtil.ToDisplay(original));

            // The scaffolding the item sat in is now empty more often than not, and
            // leaving it behind would make the bin accumulate empty drive letters.
            RecycleBin.PruneEmpty(binRoot);
        }

        if (fromMenu)
        {
            if (failures.Count > 0)
            {
                var text = new System.Text.StringBuilder();
                text.AppendLine(Lang.Pick($"⚠️已取回 {restored} 项，{failures.Count} 项失败：", $"⚠️Restored {restored}, {failures.Count} failed:"));
                text.AppendLine();

                foreach (string failure in failures.Take(10))
                {
                    text.AppendLine(failure);
                    text.AppendLine();
                }

                ReportProblem(true, text.ToString().TrimEnd());
            }

            return failures.Count == 0 ? 0 : 3;
        }

        foreach (string failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        if (!args.Flag("--quiet"))
        {
            Console.WriteLine(Lang.Pick($"取回完成：成功 {restored} 项，失败 {failures.Count} 项", $"Restore finished: {restored} succeeded, {failures.Count} failed"));
        }

        return failures.Count == 0 ? 0 : 3;
    }

    public static int Reclaim(Args args)
    {
        // Explorer invokes a verb once per selected item even when the command takes
        // no arguments. Batching normally handles that, but it only engages when
        // paths arrive, so 回收空间 -- which takes none -- ran once per file and
        // produced a dialog and an Explorer window for each. The claim is what makes
        // it single-shot; only the first invocation does anything.
        if (args.Flag("--gui-errors") &&
            !PendingSelection.TryClaim("reclaim", string.Empty, out _))
        {
            return 0;
        }

        // A bin directory holding nothing but its index file is empty as far as the
        // user is concerned. Testing the directory instead meant this reported success
        // silently whenever a bin had been created once and then emptied -- no dialog,
        // no window, nothing at all.
        List<string> roots = [.. RecycleBin.ExistingRoots().Where(RecycleBin.HasItems)];

        if (roots.Count == 0)
        {
              // Reported only. Opening a drive window was an unasked-for side effect:
              // the user asked a question about the bin and got a file browser.
              if (args.Flag("--gui-errors"))
              {
                  ShowDialog(
                      Lang.Pick("♻️回收站是空的，没有可回收的内容。", "♻️The recycle bin is empty."),
                      Lang.Pick("Roboc 回收站", "Roboc — Recycle bin"),
                      warning: false,
                      centre: true);

                  return 0;
              }

              Console.WriteLine(Lang.Pick("♻️回收空间是空的，没有可回收的内容。", "♻️The recycle bin is empty."));
              return 0;
        }

        var items = new List<(string Root, RecycleBin.Item Item)>();

        foreach (string root in roots)
        {
            foreach (RecycleBin.Item item in RecycleBin.List(root))
            {
                items.Add((root, item));
            }
        }

        if (items.Count == 0)
        {
            foreach (string root in roots)
            {
                RecycleBin.PruneEmpty(root);
            }

            // The same report as the no-bin-at-all case, because it means the same
            // thing to the user. Reached only if a bin empties between the check and
            // the listing, but a menu invocation that says nothing at all is the
            // failure this whole branch exists to avoid.
            if (args.Flag("--gui-errors"))
            {
                ShowDialog(
                    Lang.Pick("♻️回收站是空的，没有可回收的内容。", "♻️The recycle bin is empty."),
                    Lang.Pick("Roboc 回收站", "Roboc — Recycle bin"),
                    warning: false,
                    centre: true);

                return 0;
            }

            Console.WriteLine(Lang.Pick("♻️回收空间是空的，没有可回收的内容。", "♻️The recycle bin is empty."));
            return 0;
        }

        long totalBytes = items.Sum(i => i.Item.Bytes);

        if (!args.Flag("--empty"))
        {
            Console.WriteLine(Lang.Pick($"回收站中共有 {items.Count} 项，占用 {FormatBytes(totalBytes)}：", $"{items.Count} items in the recycle bin, using {FormatBytes(totalBytes)}:"));
            Console.WriteLine();

            foreach ((string root, RecycleBin.Item item) in items.OrderByDescending(i => i.Item.Bytes))
            {
                Console.WriteLine($"  {FormatBytes(item.Bytes),10}  {item.DiscardedAt:yyyy-MM-dd HH:mm}  {PathUtil.ToDisplay(item.OriginalPath)}");
            }

            Console.WriteLine();
            // The separator belongs to the language, not to the join: Chinese uses an
            // enumeration comma and English a comma and a space, and hardcoding either
            // reads as a typo in the other.
            Console.WriteLine(Lang.Pick(
                $"存储位置：{string.Join("、", roots.Select(PathUtil.ToDisplay))}",
                $"Stored at: {string.Join(", ", roots.Select(PathUtil.ToDisplay))}"));
            Console.WriteLine(Lang.Pick("加 --empty 参数可清空并释放空间。", "Pass --empty to empty it and reclaim the space."));

            if (args.Flag("--gui-errors"))
            {
                // The menu invocation opens the bin so the user can act on what is
                // listed rather than only reading about it.
                foreach (string root in roots)
                {
                    OpenFolder(PathUtil.ToDisplay(root));
                }
            }

            return 0;
        }

        // Emptying is irreversible, so it needs an explicit --yes.
        //
        // This used to accept --gui-errors as an alternative, on the grounds that the
        // menu path would show a confirmation dialog. Nothing on the menu ever passed
        // --empty, so that dialog was unreachable and the exemption was a way to empty
        // the bin with no confirmation at all. The dialog is gone; --yes is now the
        // only way through.
        if (!args.Flag("--yes"))
        {
            Console.Error.WriteLine(Lang.Pick("roboc: 清空回收站不可恢复，需要 --yes 确认", "roboc: emptying the recycle bin cannot be undone; pass --yes to confirm"));
            return 2;
        }

        int removed = 0;
        int failed = 0;

        foreach ((string _, RecycleBin.Item item) in items)
        {
            if (FastDeleterTryDelete(item.StoredPath, out int deleteError))
            {
                removed++;
            }
            else
            {
                failed++;
                Console.Error.WriteLine(
                    Lang.Pick($"roboc: 无法删除 {PathUtil.ToDisplay(item.StoredPath)}：{DescribeDeleteError(deleteError)}", $"roboc: cannot delete {PathUtil.ToDisplay(item.StoredPath)}: {DescribeDeleteError(deleteError)}"));
            }
        }

        foreach (string root in roots)
        {
            RecycleBin.PruneEmpty(root);
        }

        if (!args.Flag("--quiet"))
        {
            Console.WriteLine(Lang.Pick($"已释放 {FormatBytes(totalBytes)}，删除 {removed} 项", $"Freed {FormatBytes(totalBytes)}, deleted {removed} items") +
                              (failed > 0 ? Lang.Pick($"，{failed} 项失败", $", {failed} failed") : ""));
        }

        return failed == 0 ? 0 : 3;
    }

    /// <summary>彻底删除 — irreversible, and therefore always confirmed.</summary>
    public static int Purge(Args args)
    {
        List<string> paths = args.Positionals();

        bool fromMenu = args.Flag("--gui-errors");

        if (paths.Count == 0)
        {
            ReportProblem(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。\r\n\r\n", "⚠️No items were received.\r\n\r\n") +
                Lang.Pick("如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。", "If this was invoked from the context menu, the selection did not reach the submenu."));
            return 2;
        }

        if (!TryCollectFromMenu(fromMenu, "purge", paths, out List<string> collected))
        {
            return 0;
        }

        paths = collected;

        if (paths.Count == 0)
        {
            ReportProblem(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."));
            return 2;
        }

        bool confirmed = args.Flag("--no-confirm");

        if (!confirmed)
        {
            // Budgeted, for the same reason the paste conflict scan is: a dialog that
            // appears after a long silence is worse than one that reports what it has
            // so far and says so. Deleting is irreversible, so the size of the
            // selection is exactly what the user needs before agreeing to it.
            confirmed = ConfirmIrreversible(paths, 400);
        }

        if (!confirmed)
        {
            return 0;
        }

        // Hand off to the ordinary delete path so this shares its guards, its
        // progress reporting and its error handling rather than growing a second
        // implementation of deletion.
        // --gui-errors must be forwarded. Without it the delete path believes it was
        // not invoked from a menu, so it creates no progress window and routes its
        // failures nowhere -- the same omission that once left 粘贴 silent, made again
        // in the neighbouring forwarder.
        var forwarded = new string[4 + paths.Count];
        forwarded[0] = "delete";
        forwarded[1] = "--direct";
        forwarded[2] = "--yes";
        forwarded[3] = "--gui-errors";

        for (int i = 0; i < paths.Count; i++)
        {
            forwarded[4 + i] = paths[i];
        }

        // The delete engine reports failures through Console.Error, which goes
        // nowhere when the verb was invoked from a menu. Capturing it here rather
        // than changing the engine keeps the fix in one place and works for any
        // command that reports the same way.
        if (!fromMenu)
        {
            return DeleteCommand.Run(new Args(forwarded));
        }

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            int exitCode = DeleteCommand.Run(new Args(forwarded));

            string text = captured.ToString().Trim();

            if (text.Length > 0)
            {
                // Wrapped, because the captured text is the delete engine's own output
                // -- it starts with "roboc:" and carries no mark of its own, so on its
                // own it was the one dialog in the program that did not look like the
                // others. The engine's words are kept verbatim underneath.
                ReportProblem(true, Lang.Pick($"⚠️彻底删除失败：\r\n\r\n{text}", $"⚠️Permanent delete failed:\r\n\r\n{text}"));
                return exitCode;
            }

            return exitCode;
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// Reports which registry locations this process may write.
    ///
    /// This exists because the failure is invisible from inside the code: the same
    /// key that this process is refused, PowerShell writes without complaint. When
    /// an operation succeeds for one program and fails for another on the same
    /// account, the difference is a policy applied to the program, and the way to
    /// find its shape is to probe several locations and compare.
    /// </summary>
    public static int Diagnose(Args args)
    {
        _ = args;

        Console.WriteLine(Lang.Pick("Roboc 环境诊断", "Roboc environment report"));
        Console.WriteLine("=================================================");
        Console.WriteLine(Lang.Pick($"程序位置   : {Environment.ProcessPath}", $"Program    : {Environment.ProcessPath}"));
        Console.WriteLine(Lang.Pick($"64 位进程  : {Environment.Is64BitProcess}", $"64-bit     : {Environment.Is64BitProcess}"));
        Console.WriteLine(Lang.Pick($"完整性级别 : {Kernel32.DescribeIntegrityLevel()}", $"Integrity  : {Kernel32.DescribeIntegrityLevel()}"));
        Console.WriteLine(Lang.Pick($"输出编码   : {Console.OutputEncoding.CodePage} ({Console.OutputEncoding.WebName})", $"Output enc : {Console.OutputEncoding.CodePage} ({Console.OutputEncoding.WebName})"));
        Console.WriteLine(Lang.Pick($"系统 ANSI CP: {Kernel32.GetACP()} / OEM CP: {Kernel32.GetOEMCP()}", $"System CPs : ANSI {Kernel32.GetACP()} / OEM {Kernel32.GetOEMCP()}"));
        Console.WriteLine(Lang.Pick($"控制台输出 CP: {Kernel32.GetConsoleOutputCP()}", $"Console CP : {Kernel32.GetConsoleOutputCP()}"));
        Console.WriteLine(Lang.Pick($"有控制台   : {Kernel32.GetConsoleMode(Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE), out _) != 0}", $"Console    : {Kernel32.GetConsoleMode(Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE), out _) != 0}"));
        string logPath = ConsoleHost.LogPath ?? Lang.Pick("（未启用）", "(not enabled)");
        Console.WriteLine(Lang.Pick($"日志文件   : {logPath}", $"Log file   : {logPath}"));
        Console.WriteLine();

        (string Path, string Why)[] probes =
        [
            (@"Software\RobocProbe", Lang.Pick("HKCU\\Software 下的普通键", "an ordinary key under HKCU\\Software")),
            (@"Software\Classes\RobocProbe", Lang.Pick("HKCU\\Software\\Classes 根下", "the root of HKCU\\Software\\Classes")),
            (@"Software\Classes\*\shell\RobocProbe", Lang.Pick("文件右键菜单区（键名含字面星号）", "the file context-menu area (key name contains a literal asterisk)")),
            (@"Software\Classes\Directory\shell\RobocProbe", Lang.Pick("文件夹右键菜单区", "the folder context-menu area")),
            (@"Software\Classes\Roboc.Menu\shell\01copy", Lang.Pick("本程序实际要写的位置", "where this program actually writes")),
        ];

        Console.WriteLine(Lang.Pick("注册表写入测试：", "Registry write test:"));
        int usable = 0;

        foreach ((string path, string why) in probes)
        {
            string verdict;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
                {
                    if (key is null)
                    {
                        verdict = Lang.Pick("失败：CreateSubKey 返回了 null", "failed: CreateSubKey returned null");
                    }
                    else
                    {
                        key.SetValue("probe", "1");
                        key.DeleteValue("probe", throwOnMissingValue: false);
                        verdict = Lang.Pick("可写", "writable");
                        usable++;
                    }
                }

                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                }
                catch (Exception)
                {
                    // Leaves a probe key behind at worst; not worth failing over.
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                verdict = Lang.Pick($"被拒绝：{ex.Message}", $"denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                verdict = $"{ex.GetType().Name}：{ex.Message}";
            }

            Console.WriteLine($"  [{(verdict == Lang.Pick("可写", "writable") ? "可以" : "拒绝")}] {path}");
            Console.WriteLine($"         {why}");
            if (verdict != Lang.Pick("可写", "writable"))
            {
                Console.WriteLine($"         {verdict}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(Lang.Pick($"结论：{usable}/{probes.Length} 个位置可写。", $"Result: {usable} of {probes.Length} locations are writable."));

        if (usable == 0)
        {
            Console.WriteLine(Lang.Pick("  所有位置都被拒绝，说明整个 HKCU 写入对本程序被拦截。", "  Every location was denied, so HKCU writes are being blocked for this program entirely."));
            Console.WriteLine(Lang.Pick("  普通用户对自己的 HKCU 有完全控制权，这不是 Windows 权限的结果。", "  An ordinary user has full control over their own HKCU, so this is not a Windows permission."));
            Console.WriteLine(Lang.Pick("  请检查安全软件（如 360、火绒、电脑管家）是否拦截了本程序写注册表。", "  Check whether security software is blocking this program from writing to the registry."));
        }
        else if (usable < probes.Length)
        {
            Console.WriteLine(Lang.Pick("  只有部分位置被拒绝。若被拒的都是右键菜单相关的键，", "  Only some locations were denied. If the denials are all context-menu related,"));
            Console.WriteLine(Lang.Pick("  则是安全软件针对 shell 扩展注册位置的定向拦截。", "  it is security software targeting shell-extension registration specifically."));
        }
        else
        {
            Console.WriteLine(Lang.Pick("  所有位置都可写，右键菜单应当可以正常安装。", "  Every location is writable, so the context menu should install normally."));
        }

        return usable > 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    internal static void ReportProblemPublic(bool fromMenu, string message) => ReportProblem(fromMenu, message);

    /// <summary>
    /// Turns the paths from one invocation into the full selection.
    ///
    /// Explorer substitutes %1 and calls the verb once per selected file; it never
    /// appends the selection. So the batch is assembled here. Returns false when
    /// another invocation has already claimed the batch, in which case this process
    /// should exit without doing anything or saying anything.
    /// </summary>
    internal static bool TryCollectFromMenu(bool fromMenu, string verb, List<string> incoming, out List<string> paths)
    {
        paths = incoming;

        if (!fromMenu || incoming.Count == 0)
        {
            return true;
        }

        // The shell is asked first, because its answer is authoritative and immediate.
        //
        // A static verb receives its selection through %1, and the shell stops
        // expanding that per item once the selection grows past about fifteen -- so
        // a two-hundred-file copy arrives as a single path. The shell itself still
        // knows the whole selection, and asking it takes about as long as starting
        // this process did. Everything below is for when that question cannot be
        // answered, and is kept rather than deleted for exactly that reason.
        List<string> live = ExplorerSelection.PreferLarger(incoming);

        if (live.Count > 0)
        {
            // Already done by a sibling that has since exited and released the lock.
            // Without this the same selection is acted on twice, and the second pass
            // reports every item as a failure because the first pass already moved it.
            if (PendingSelection.WasJustHandled(verb, live))
            {
                return false;
            }

            if (!PendingSelection.TryClaimImmediate(verb))
            {
                // A sibling is already doing this, with the same answer.
                return false;
            }

            PendingSelection.MarkHandled(verb, live);
            paths = live;
            return true;
        }

        if (!PendingSelection.TryClaim(verb, incoming[0], out List<string> collected))
        {
            return false;
        }

        paths = collected.Count > 0 ? collected : incoming;
        return true;
    }

    /// <summary>
    /// Reports a problem where the user will actually see it.
    ///
    /// Invoked from the context menu there is no console, so Console.Error goes
    /// into nothing and the verb appears to do nothing at all. That is not a
    /// cosmetic issue: it is the difference between "this failed and here is why"
    /// and silence.
    /// </summary>
    /// <summary>
    /// Display width of a string, counting a Chinese character as two columns.
    ///
    /// This is what makes the stat rows line up. Text is centred a line at a time, so
    /// two lines edge up against each other only if they are the same width -- and a
    /// label of four characters against one of five is not. Padding both to a fixed
    /// width in these units gives every line the same total, which puts them at the
    /// same x and lets the columns inside them line up.
    /// </summary>
    private static int DisplayWidth(string text)
    {
        int width = 0;

        foreach (char c in text)
        {
            width += c > 127 ? 2 : 1;
        }

        return width;
    }

    /// <summary>Right-aligns a value in a field of the given width.</summary>
    private static string PadValue(string value, int units)
    {
        int width = DisplayWidth(value);
        return width >= units ? value : new string(' ', units - width) + value;
    }

    /// <summary>
    /// One "label value" row, padded so that every row made this way is the same
    /// width. Computed at run time, not written as literal spaces: the values change
    /// length as the counts grow, and a literal would drift out of alignment the first
    /// time a folder held a thousand files.
    /// </summary>
    private static string StatRow(string label, string value)
    {
        const int LabelUnits = 12;
        const int ValueUnits = 10;

        string padded = label;

        while (DisplayWidth(padded) < LabelUnits)
        {
            padded += '　';
        }

        return padded + PadValue(value, ValueUnits);
    }

    private static void ReportProblem(bool fromMenu, string message)
    {
        if (fromMenu)
        {
            ShowDialog(message, "Roboc", warning: false, centre: true);
            return;
        }

        Console.Error.WriteLine("roboc: " + message);
    }

    /// <summary>
    /// Removes one discarded item, which may be a whole tree.
    ///
    /// FastDeleter.TryDelete removes a single entry: it issues one POSIX delete
    /// against one handle, which a non-empty directory refuses with
    /// ERROR_DIR_NOT_EMPTY. A discarded directory holds an entire tree, so it goes
    /// through DeleteTreeQuiet instead.
    /// </summary>
    private static bool FastDeleterTryDelete(string extendedPath, out int error)
    {
        error = 0;

        try
        {
            if (DirectoryOps.IsExistingDirectory(extendedPath))
            {
                Roboc.Core.Delete.FastDeleter.DeleteTreeQuiet(extendedPath);

                if (DirectoryOps.Exists(extendedPath))
                {
                    error = 145;
                    return false;
                }

                return true;
            }

            return Roboc.Core.Delete.FastDeleter.TryDelete(
                extendedPath, false, out _, out error, out _);
        }
        catch (IOException ex)
        {
            error = ex.HResult;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.HResult;
            return false;
        }
    }

    private static string DescribeDeleteError(int error) => error switch
    {
        0 => Lang.Pick("未提供错误码", "no error code given"),
        2 => Lang.Pick("找不到该项目（可能已被删除）", "not found (it may already be deleted)"),
        3 => Lang.Pick("路径不存在", "the path does not exist"),
        5 => Lang.Pick("拒绝访问：文件可能是只读的，或正被其他程序占用", "access denied: the file may be read-only, or in use by another program"),
        32 => Lang.Pick("文件正被其他程序占用", "the file is in use by another program"),
        145 => Lang.Pick("目录不是空的：其中有无法删除的项目", "the folder is not empty: something inside it could not be deleted"),
        _ => Lang.Pick($"错误码 {error}", $"error {error}"),
    };

    private static long MeasureOne(string path)
    {
        string extended = PathUtil.ToExtended(path);

        if (!DirectoryOps.Exists(extended))
        {
            return 0;
        }

        if (!DirectoryOps.IsExistingDirectory(extended))
        {
            return DirectoryOps.TryGetAttributes(extended, out WIN32_FILE_ATTRIBUTE_DATA data)
                ? ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow
                : 0;
        }

        return RecycleBin.MeasureTree(extended);
    }

    /// <summary>
    /// The warning shown before anything irreversible. Defaults to No, because a
    /// dialog that a stray Enter can confirm is not a warning.
    ///
    /// It reports files, folders and total size rather than only a count of selected
    /// items, because "6 项" says nothing about whether that is six empty folders or
    /// six folders full of work.
    /// </summary>
    private static bool ConfirmIrreversible(IReadOnlyList<string> paths, int budgetMs)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        int files = 0;
        int folders = 0;
        long bytes = 0;
        bool complete = true;

        foreach (string path in paths)
        {
            string extended = PathUtil.ToExtended(path);

            if (!DirectoryOps.Exists(extended))
            {
                continue;
            }

            if (!DirectoryOps.IsExistingDirectory(extended))
            {
                files++;

                if (DirectoryOps.TryGetAttributes(extended, out WIN32_FILE_ATTRIBUTE_DATA data))
                {
                    bytes += ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                }

                continue;
            }

            folders++;

            if (!MeasureTree(extended, deadline, budgetMs, ref files, ref folders, ref bytes))
            {
                complete = false;
                break;
            }
        }

        var text = new System.Text.StringBuilder();

        text.AppendLine(complete
            ? Lang.Pick($"⚠️即将彻底删除 {paths.Count:N0} 项：", $"⚠️About to permanently delete {paths.Count:N0} items:")
            : Lang.Pick($"⚠️即将彻底删除 {paths.Count:N0} 项（其中部分内容仍在统计）：", $"⚠️About to permanently delete {paths.Count:N0} items (still counting some of them):"));

        text.AppendLine();
        text.AppendLine(StatRow(Lang.Pick("包含文件", "Files"), Lang.Pick($"{files:N0} 个", $"{files:N0}")));
        text.AppendLine(StatRow(Lang.Pick("包含文件夹", "Folders"), Lang.Pick($"{folders:N0} 个", $"{folders:N0}")));
        text.AppendLine(StatRow(Lang.Pick("共占空间", "Total size"), FormatBytes(bytes)));
        text.AppendLine();
        text.AppendLine(Lang.Pick("⚠️此操作无法撤销。", "⚠️This cannot be undone."));
        text.AppendLine();
        text.AppendLine(Lang.Pick("确定要继续吗？", "Continue?"));

        return ShowDialog(text.ToString(), Lang.Pick("Roboc — 不可恢复的操作", "Roboc — Irreversible"), warning: true, ask: true, centre: true);
    }

    /// <summary>
    /// Counts a tree, giving up at the deadline and reporting what it has.
    /// </summary>
    private static bool MeasureTree(
        string root, System.Diagnostics.Stopwatch deadline, int budgetMs,
        ref int files, ref int folders, ref long bytes)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            if (deadline.ElapsedMilliseconds > budgetMs)
            {
                return false;
            }

            string current = stack.Pop();
            var entries = new List<FindEntry>(64);

            if (!DirectoryList.Enumerate(current, entries))
            {
                continue;
            }

            foreach (FindEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!entry.IsReparsePoint)
                    {
                        // Counted here, not only in the caller's top-level loop.
                        // Counting only the top level made a folder holding a
                        // thousand subfolders report "6 个文件夹".
                        folders++;
                        stack.Push(entry.FullPath);
                    }
                }
                else
                {
                    files++;
                    bytes += entry.Size;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Shows a folder in Explorer.
    ///
    /// 回收空间 is a management verb, so its useful output is the bin itself: the
    /// user can see what is held, how large it is, and delete from there. A dialog
    /// that only reports a total leaves them with nowhere to act.
    /// </summary>
    private static void OpenFolder(string path)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = '"' + path + '"',
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Opening a folder is a convenience; failing to is not worth an error.
        }
    }

    /// <summary>
    /// Asks what to do about files that are already at the destination.
    ///
    /// Returns 0 to replace, 1 to skip, 2 to cancel. Defaults to Cancel: a paste
    /// that silently merged into an earlier result changed which files the folder
    /// held, and that is not something to decide by pressing Enter.
    /// </summary>
    internal static int AskOverwrite(ConflictScan.Result scan)
    {
        var text = new System.Text.StringBuilder();

        if (scan.Conflicts > 0)
        {
            text.AppendLine(scan.Complete
                ? Lang.Pick($"⚠️目标位置已有 {scan.Conflicts:N0} 个同名项目：", $"⚠️The destination already has {scan.Conflicts:N0} items with the same name:")
                : Lang.Pick($"⚠️目标位置至少有 {scan.Conflicts:N0} 个同名项目（仍在统计中）：", $"⚠️The destination has at least {scan.Conflicts:N0} items with the same name (still counting):"));

            text.AppendLine();

            if (scan.Identical > 0)
            {
                // Colons rather than full-width spaces. Those spaces lined the numbers
                // up into columns when the text was left-aligned; centred, every line
                // is positioned on its own and the columns no longer line up at all.
                text.AppendLine(StatRow(Lang.Pick("内容相同", "Identical"), Lang.Pick($"{scan.Identical:N0} 个文件", $"{scan.Identical:N0} files")));
            }

            if (scan.Different > 0)
            {
                text.AppendLine(StatRow(Lang.Pick("内容不同", "Different"), Lang.Pick($"{scan.Different:N0} 个文件", $"{scan.Different:N0} files")));
            }

            if (scan.TypeMismatch > 0)
            {
                text.AppendLine(StatRow(Lang.Pick("类型不同", "Wrong type"), Lang.Pick($"{scan.TypeMismatch:N0} 个", $"{scan.TypeMismatch:N0}")));
            }

            if (scan.Bytes > 0)
            {
                text.AppendLine(StatRow(Lang.Pick("涉及大小", "Size involved"), FormatBytes(scan.Bytes)));
            }
        }
        else
        {
            text.AppendLine(Lang.Pick("目标位置没有同名项目，将全部作为新增内容粘贴。", "Nothing at the destination shares these names; everything will be pasted as new."));
        }

        // 是 and 否 answer the conflicts immediately above them; the additions are a
        // separate piece of news and must not sit between the question and its
        // answers, which is what made the wording ambiguous.
        text.AppendLine();
        text.AppendLine(Lang.Pick("是：全部替换为，剪贴板中的版本", "Yes: replace all with the clipboard versions"));
        text.AppendLine(Lang.Pick("否：保留原有项目，粘贴新增内容", "No: keep what is there and paste only what is new"));

        if (scan.NewFiles > 0 || scan.NewFolders > 0 || scan.MergedFolders > 0)
        {
            text.AppendLine();

            if (scan.NewFiles > 0)
            {
                text.AppendLine(StatRow(Lang.Pick("将新增文件", "New files"), Lang.Pick($"{scan.NewFiles:N0} 个", $"{scan.NewFiles:N0}")));
            }

            if (scan.NewFolders > 0)
            {
                text.AppendLine(StatRow(Lang.Pick("将新增文件夹", "New folders"), Lang.Pick($"{scan.NewFolders:N0} 个", $"{scan.NewFolders:N0}")));
            }

            if (scan.MergedFolders > 0)
            {
                text.AppendLine(StatRow(Lang.Pick("将合并", "Merged"), Lang.Pick($"{scan.MergedFolders:N0} 个同名文件夹", $"{scan.MergedFolders:N0} folders will be merged")));
            }
        }

        text.AppendLine();
        text.AppendLine(Lang.Pick("取消：什么都不做", "Cancel: do nothing"));

        return MessageDialog.Show(
            text.ToString(), Lang.Pick("Roboc — 目标已存在同名项目", "Roboc — Name already exists at the destination"), DialogButtons.YesNoCancel, warning: true, centre: true);
    }

    /// <summary>
    /// Shows a message.
    ///
    /// <paramref name="warning"/> only chooses the icon; <paramref name="ask"/> says
    /// whether the user actually has to decide something. The two were the same flag,
    /// so a report -- "回收了 0 项，1 项失败" -- offered 是 and 否, neither of which
    /// meant anything. Only a real question gets two buttons now.
    /// </summary>
    private static bool ShowDialog(
        string text, string caption, bool warning, bool ask = false, bool centre = false)
    {
        // Counted from zero: 0 is always the affirmative outcome, so one button set
        // and two read the same way at the call site.
        int choice = MessageDialog.Show(
            text,
            caption,
            ask ? DialogButtons.YesNo : DialogButtons.Acknowledge,
            warning,
            centre);

        return !ask || choice == 0;
    }

    private static string DescribeBinError(int error) => error switch
    {
        NativeConst.ERROR_INVALID_DRIVE =>
            Lang.Pick("该位置所在的驱动器不支持独立的 Roboc 回收站", "the drive cannot hold a separate Roboc recycle bin"),
        NativeConst.ERROR_INVALID_NAME =>
            Lang.Pick("该位置的路径无法映射到回收站中（例如网络路径）", "the path cannot be mapped into the bin (a network path, for instance)"),
        NativeConst.ERROR_INVALID_PARAMETER =>
            Lang.Pick("该项目已经在回收站里了，不能重复回收", "this item is already in the bin and cannot be recycled twice"),
            NativeConst.ERROR_ALREADY_EXISTS =>
                Lang.Pick("回收站中这个位置已经有一份了。请先把回收站里的那一份处理掉，", "The bin already holds something from that location. Deal with that copy first,") +
                Lang.Pick("否则无法判断该恢复哪一份。", "or there is no way to tell which one to restore."),
        5 => Lang.Pick("拒绝访问：无法在所在驱动器建立回收站", "access denied: the recycle bin could not be created on that drive"),
        32 => Lang.Pick("文件正被其他程序占用", "the file is in use by another program"),
        _ => Lang.Pick($"错误码 {error}", $"error {error}"),
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F2} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F1} KB";
        return $"{bytes} B";
    }
}
