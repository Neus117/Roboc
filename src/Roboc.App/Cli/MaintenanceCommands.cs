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
    /// <summary>
    /// Says what is about to happen before it happens.
    ///
    /// The verb is labelled 回收 whether or not the selection is in the bin, so the
    /// user is told which of the two things this press will do and can back out. A
    /// restore that runs without warning would be indistinguishable from a discard.
    /// </summary>
    private static bool ConfirmRestore(List<string> inBin, List<string> originals, int discardCount)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine(Lang.Pick($"♻️选中的 {inBin.Count:N0} 项在 Roboc 回收站里。原路径：", $"♻️{inBin.Count:N0} of the selected items are in the Roboc recycle bin. Original location:"));
        text.AppendLine();

        // Where each item is going, not just how many there are.
        //
        // "Put them back" is not enough to decide on when the answer is a path the user
        // only half-remembers. The point of a confirmation is that it can be checked
        // against what the user expected, and a count cannot be checked against anything.
        //
        // Ten, then a summary: a dialog is not a report, and a selection of two hundred
        // files would otherwise push the buttons below the fold.
        const int shown = 10;

        for (int i = 0; i < originals.Count && i < shown; i++)
        {
            text.AppendLine("　" + originals[i]);
        }

        if (originals.Count > shown)
        {
            text.AppendLine(Lang.Pick($"　…另有 {originals.Count - shown} 项", $"  ...and {originals.Count - shown} more"));
        }

        // Reworded rather than padded so both lines are the same width on their own.
        //
        // The dialog centres its text, so two lines of different lengths sit at different
        // left edges and the pair reads as ragged. Padding the shorter one fixed that and
        // looked worse: the trailing spaces are invisible, so the fix was invisible too, and
        // all that remained was a line of source that made no sense to read. Equal wording
        // needs no explanation and survives being edited.
        text.AppendLine();
        text.AppendLine(Lang.Pick("是：取回其原位", "Yes: put them back"));
        text.AppendLine(Lang.Pick("否：什么都不做", "No: do nothing"));

        if (discardCount > 0)
        {
            text.AppendLine();
            text.AppendLine(Lang.Pick($"另有 {discardCount:N0} 项不在回收站中，将在取回之后照常回收。", $"{discardCount:N0} more are not in the bin and will be recycled as usual afterwards."));
        }

        return ShowDialog(
            text.ToString().TrimEnd(), Lang.Pick("取回确认", "Confirm Restore"),
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

    /// <summary>回收 — move the selection into this tool's own recycle bin.</summary>
    public static int Recycle(Args args)
    {
        List<string> paths = args.Positionals();

        bool fromMenuEarly = args.Flag("--gui-errors");

        if (paths.Count == 0)
        {
            ReportProblem(fromMenuEarly, Lang.Pick("⚠️没有收到任何选中的项目。\r\n\r\n", "⚠️No items were received.\r\n\r\n") +
                Lang.Pick("如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。", "If this was invoked from the context menu, the selection did not reach the submenu."),
                Lang.Pick("没有收到选中项目", "No Items Received"));
            return 2;
        }

        if (!TryCollectFromMenu(fromMenuEarly, "recycle", paths, out List<string> collected))
        {
            return 0;
        }

        paths = collected;

        if (paths.Count == 0)
        {
            ReportProblem(fromMenuEarly, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."), Lang.Pick("没有收到选中项目", "No Items Received"));
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
                // The bin's own bookkeeping is not one of its contents. Selecting the index
                // file and pressing 回收 used to produce a failure dialog in the middle of a
                // batch of real items -- and because the batch stopped there, the rest of the
                // selection was never restored and no folder was opened for the ones that had
                // been. Skipped here, before the confirm dialog, so the dialog's count is the
                // count of things that can actually be acted on.
                if (RecycleBin.IsScaffolding(PathUtil.ToExtended(path)))
                {
                    continue;
                }

                inBin.Add(path);
            }
            else
            {
                toDiscard.Add(path);
            }
        }

        if (inBin.Count > 0)
        {
            // Resolved once, before anything moves, and reused afterwards.
            //
            // Restore clears the index record as it succeeds -- the record describes an item
            // that is no longer in the bin -- and OriginalPathOf resolves the original path
            // from that index. Asking a second time after the restore therefore always fails,
            // and that is exactly what happened: the dialog named the right folder while the
            // step that opens it was handed "original location unknown" and opened nothing.
            //
            // The diagnostic that found this printed originals=[（原位置未知）] on a restore
            // that had just succeeded. Reading the source twice had not.
            List<string> originals = OriginalsOf(inBin);

            if (fromMenuEarly && !ConfirmRestore(inBin, originals, toDiscard.Count))
            {
                return 0;
            }

            int restoreExit = RunRestoreQuietly(inBin, out string restoreErrors);

            if (fromMenuEarly && restoreErrors.Length > 0)
            {
                ReportProblem(true, restoreErrors, Lang.Pick("取回失败", "Restore Failed"));
            }

            // Opened here rather than inside Restore, and the distinction matters.
            //
            // RunRestoreQuietly invokes restore with --quiet, so inside that call fromMenu
            // is false and the open-the-folder step over there never runs. The menu's own
            // path is this one: 回收 performs the restore whenever part of the selection is
            // already in the bin, and it then returns straight out of the branch below when
            // there is nothing left to discard -- which is the ordinary case. The step has
            // to live where that path can actually reach it.
            if (fromMenuEarly && restoreErrors.Length == 0)
            {
                // Revealed, not merely opened: the folder alone does not say which of the
                // things in it were just put back, and that is the only question the user
                // has at this moment.
                RevealRestored(originals);
            }

            if (toDiscard.Count == 0)
            {
                return restoreExit;
            }
        }

        // Outside the block above, and that placement is the fix for a real failure.
        //
        // The classification filters out the bin's own bookkeeping, but the result was only
        // copied back to paths from inside "if (inBin.Count > 0)". Selecting nothing but the
        // index file therefore left inBin empty, skipped that whole block, and carried the
        // unfiltered list onward -- so the index file went to the recycling path after all and
        // was refused with "already in the bin", which is the dialog that used to interrupt a
        // batch. Assigning here means the filtered list is the one that continues, whether or
        // not anything was restored first.
        paths = toDiscard;

        if (paths.Count == 0)
        {
            return 0;
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
            // Checked before ToExtended, which would treat a shell namespace item as a
            // relative path and let the failure surface much deeper -- as
            // ERROR_INVALID_DRIVE, reported to the user as "the drive cannot hold a
            // separate Roboc recycle bin". That blames the user's device for a
            // limitation of this program.
            string? notFileSystem = ExplainNonFileSystemPath(path);

            if (notFileSystem is not null)
            {
                failed++;

                // Deliberately without the path. In the dialog that path was long enough to
                // push the reason off the visible area; here it would make one failure line
                // the width of three. Nothing is lost: a namespace path identifies a device
                // to nobody, and when the selection is several items the device's own name is
                // already on screen behind the terminal.
                Console.Error.WriteLine(Lang.Pick("roboc: 无法回收该项目", "roboc: cannot recycle this item")
                    + " -- " + notFileSystem);
                continue;
            }

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
            string? notFileSystem = ExplainNonFileSystemPath(path);

            if (notFileSystem is not null)
            {
                // No path on the line, and the reason is not indented under one.
                //
                // Reported on 2026-10-15: the dialog appeared to give no reason at all.
                // The reason was there -- but the line began with the namespace path, a
                // single unbroken token long enough to fill the dialog's width, so the
                // wrapped reason landed out of sight. Leading with the label and dropping
                // the identifier keeps the whole sentence visible, and the identifier told
                // the user nothing anyway.
                failures.Add(notFileSystem);
                continue;
            }

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

        // Names the outcome, not the verb: this dialog only ever reports failures, and a
        // caption reading 回收 described what the user had already asked for.
        ShowDialog(text, Lang.Pick("回收失败", "Recycle Failed"), warning: true, centre: true);
        return 3;
    }

    /// <summary>
    /// Explains, when it applies, that this path is not an item in the file system.
    ///
    /// Returns null for anything the file APIs can be asked about.
    ///
    /// <b>Why every destructive verb needs this.</b> A shell namespace item -- a phone
    /// over MTP, a library, a search result, the contents of an archive -- exposes a
    /// <c>Path</c> property and that property is a string, so it arrives through the
    /// context menu looking exactly like a file. It is not one, and the failures further
    /// down are misleading rather than merely unhelpful: the recycle verb used to say the
    /// drive cannot hold a bin, which reports this program's limitation as a fault of the
    /// user's device.
    ///
    /// Returns <see cref="NonFileSystemNotice.Reason"/> -- the one-line form -- rather than
    /// the full explanation, because every caller puts this into a failure list where ten
    /// such lines may appear at once. The dialog form is shown by the copy and delete paths,
    /// which reject the whole operation instead of listing it.
    /// </summary>
    private static string? ExplainNonFileSystemPath(string path)
        => NonFileSystemNotice.IsUsable(path) ? null : NonFileSystemNotice.Reason;

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
            ReportProblem(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."), Lang.Pick("没有收到选中项目", "No Items Received"));
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

        // Collisions the user has to decide, collected while the loop runs and put to them
        // once afterwards. Asking per file would mean a dialog per file, and a folder that
        // overlaps in fifty places would be fifty dialogs.
        var conflicts = new List<(string Source, string Target)>();

        // Entries moved into an existing folder by a merge, counted separately from whole
        // items restored: "12 files merged into the folder" and "1 item restored" are
        // different pieces of news and the report should not conflate them.
        int mergedIntoRestore = 0;

        foreach (string path in paths)
        {
            // A bin path always is a file-system path, so this should not fire -- but every
            // verb that reaches a file API gets the same refusal rather than only the ones
            // that happened to be reported. Without it, the failure below reads "this item
            // is not in the Roboc recycle bin", which is true and tells the user nothing.
            string? notFileSystem = ExplainNonFileSystemPath(path);

            if (notFileSystem is not null)
            {
                failures.Add(notFileSystem);
                continue;
            }

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

            // Something is already at the original location, and what happens next depends
            // on what the two things are.
            //
            // Two directories merge. A user who recycles a folder, works in the original
            // location for a while and then wants the folder back is the ordinary case, and
            // refusing it makes them resolve the overlap by hand -- two hundred times, for a
            // folder of two hundred files. Everything else is a collision the user has to
            // decide, and is collected rather than resolved, because a decision made two
            // hundred levels deep is one they never see.
            if (DirectoryOps.Exists(original))
            {
                if (DirectoryOps.IsExistingDirectory(extended) &&
                    DirectoryOps.IsExistingDirectory(original))
                {
                    List<(string Source, string Target)> found =
                        RecycleBin.MergeConflicts(extended, original, out int movedInto);

                    mergedIntoRestore += movedInto;
                    conflicts.AddRange(found);

                    if (found.Count == 0)
                    {
                        // Everything moved across, so the folder in the bin is now empty but
                        // for the marker it arrived with -- MergeInto deliberately leaves that
                        // behind rather than carrying it into the merged result, where it would
                        // claim the user's own folder was a discarded item.
                        //
                        // Removing it here is what lets PruneEmpty take the empty folders
                        // underneath. Without it the bin kept a shell holding nothing but a
                        // 280-byte text file, and the listing showed that shell as a whole
                        // restored item, at the original path, at 280 bytes.
                        RecycleBin.ClearRecord(binRoot, extended);
                        RecycleBin.RemoveMarker(extended);

                        restored++;
                        restoredPaths.Add(PathUtil.ToDisplay(original));
                    }

                    // The bin is left tidy either way: a merged folder that had subfolders
                    // leaves those behind empty.
                    RecycleBin.PruneEmpty(binRoot);
                    continue;
                }

                conflicts.Add((extended, original));
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

            // Both records describe an item that is no longer in the bin. The marker is the
            // third, and it sits inside the folder that was just put back -- see RemoveMarker.
            RecycleBin.ClearRecord(binRoot, extended);
            RecycleBin.RemoveMarker(original);

            restored++;
            restoredPaths.Add(PathUtil.ToDisplay(original));

            // The scaffolding the item sat in is now empty more often than not, and
            // leaving it behind would make the bin accumulate empty drive letters.
            RecycleBin.PruneEmpty(binRoot);
        }

        // Collisions, put to the user once and applied to all of them.
        //
        // After the loop, not inside it: the dialog counts what it is asking about, and a
        // count is only worth reading when it is the whole of it. It also means one answer
        // covers a folder that overlaps in fifty places rather than fifty answers covering
        // one place each.
        if (conflicts.Count > 0)
        {
            // Two callers reach this and they want two different things.
            //
            // With --gui-errors, ask: the user is looking at the screen and the answer changes
            // what happens next.
            //
            // Without it, tell them and stop. There is nobody to answer a question, but doing
            // it silently is worse -- the files they asked for are still in the bin and
            // nothing has said so.
            //
            // An earlier version tried to distinguish a menu invocation from a typed command
            // by forwarding a marker from RunRestoreQuietly, on the reasoning that a user who
            // had just answered a dialog should not be shown a notice about their own answer.
            // The reasoning was wrong: the dialog they answered asked whether to restore, not
            // what to do about a collision, and it is exactly the menu path -- where the
            // notice was suppressed -- that left the user with no idea their files had been
            // kept back. There is nothing to tell apart. --gui-errors already does it.
            int answer;

            if (fromMenu)
            {
                answer = AskOverwrite(ConflictScan.FromPairs(conflicts));
            }
            else
            {
                answer = 1;

                ShowDialog(
                    Lang.Pick(
                        $"⚠️原路径已经存在 {conflicts.Count:N0} 个同名文件，默认保留回收站副本，\r\n你可以自行删除。",
                        $"⚠️{conflicts.Count:N0} files with the same name already exist at the original location.\r\n"
                        + "The recycle bin copies have been kept, and you can delete them yourself."),
                    Lang.Pick("原路径已有同名文件", "Name Already Exists"),
                    warning: true,
                    centre: true);
            }

            if (answer == 0)
            {
                foreach ((string source, string target) in conflicts)
                {
                    if (!RecycleBin.ReplaceWith(source, target))
                    {
                        failures.Add(
                            $"{PathUtil.ToDisplay(target)}\r\n" +
                            Lang.Pick("  替换失败：无法删除原位置的项目。", "  Replace failed: the item at the original location could not be removed."));
                        continue;
                    }

                    string? conflictRoot = RecycleBin.RootFor(source);

                    if (conflictRoot is not null)
                    {
                        RecycleBin.ClearRecord(conflictRoot, source);
                        RecycleBin.RemoveMarker(target);
                        RecycleBin.PruneEmpty(conflictRoot);
                    }

                    restored++;
                    restoredPaths.Add(PathUtil.ToDisplay(target));
                }
            }

            // answer 1 leaves every conflict in the bin -- the user asked to keep what is
            // already there, and the recycled copies are the only other place they exist.
            // answer 2 has already returned by way of AskOverwrite's own Cancel default.
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

                ReportProblem(true, text.ToString().TrimEnd(), Lang.Pick("清空回收站失败", "Could Not Empty the Bin"));
            }

            // Revealed after the event, not before it, and the item rather than the folder.
            // The question at this moment is "where did it go", and a window showing the
            // right folder still leaves the user to work out which of its contents arrived
            // just now. Selecting it answers that without any searching.
            RevealRestored(restoredPaths);

            return failures.Count == 0 ? 0 : 3;
        }

        foreach (string failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        if (!args.Flag("--quiet"))
        {
            Console.WriteLine(Lang.Pick($"取回完成：成功 {restored} 项，失败 {failures.Count} 项", $"Restore finished: {restored} succeeded, {failures.Count} failed"));

            // Reported separately, because it is a different piece of news.
            //
            // "12 entries merged into the folder" and "1 item restored" say different things:
            // the first that a folder already there now holds more, the second that something
            // lost is back. A merge that finished cleanly also counts as a restored item, but
            // a partial one does not -- its remainder is still in the bin. Without this line a
            // restore that moved fifty files across reported "0 succeeded", and until it was
            // wired up here the count was computed, incremented, and read by nothing at all.
            if (mergedIntoRestore > 0)
            {
                Console.WriteLine(Lang.Pick($"其中 {mergedIntoRestore} 项合并进了已存在的文件夹。", $"{mergedIntoRestore} of them were merged into folders that already existed."));
            }
        }

        return failures.Count == 0 ? 0 : 3;
    }

    /// <summary>回收空间 — report or reclaim what the bins are holding.</summary>
    public static int Reclaim(Args args)
    {
        // Explorer invokes a verb once per selected item even when the command takes
        // no arguments. Batching normally handles that, but it only engages when
        // paths arrive, so 回收空间 -- which takes none -- ran once per file and
        // produced a dialog and an Explorer window for each.
        //
        // TryClaimImmediate, not TryClaim. This called TryClaim with an empty path,
        // and that form returns true without ever taking the lock -- the empty-path
        // branch exists because a verb with nothing to collect must not wait out the
        // settle window. So the claim this paragraph describes was doing nothing at
        // all, and the defect it was written to fix was still there: select four
        // files, choose 回收站, and four identical dialogs open. Reported by the user
        // on 2026-10-15. TryClaimImmediate is the form that takes the lock without
        // waiting, which is exactly what a no-selection verb needs.
        if (args.Flag("--gui-errors") && !PendingSelection.TryClaimImmediate("reclaim"))
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
                    Lang.Pick("回收站为空", "Recycle Bin Empty"),
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
                    Lang.Pick("回收站为空", "Recycle Bin Empty"),
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
                Lang.Pick("如果这条是从右键菜单调用的，说明选中项没有传达到子菜单。", "If this was invoked from the context menu, the selection did not reach the submenu."),
                Lang.Pick("没有收到选中项目", "No Items Received"));
            return 2;
        }

        if (!TryCollectFromMenu(fromMenu, "purge", paths, out List<string> collected))
        {
            return 0;
        }

        paths = collected;

        if (paths.Count == 0)
        {
            ReportProblem(fromMenu, Lang.Pick("⚠️没有收到任何选中的项目。", "⚠️No items were received."), Lang.Pick("没有收到选中项目", "No Items Received"));
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
        //
        // --gui-errors is forwarded only when this invocation itself came from a menu.
        //
        // It used to be forwarded unconditionally, on the reasoning that the delete path
        // routes its failures nowhere without it -- which is true, and was the wrong cure.
        // The flag does not mean "report failures"; it means "I was invoked from a menu", and
        // three separate things read it that way: the progress window is created, the
        // failure dialog is shown, and the item is taken to be a batch to be claimed through
        // the shell. So a terminal invocation of 彻底删除 forced all three on: it opened the
        // dialog, blocked on it with no one to click, and returned 4 with an empty stderr --
        // a command-line user got a modal box and no text at all.
        //
        // Found on 2026-10-15 while covering every entry point against a device path: 回收
        // and 取回 answered on stderr as expected, 彻底删除 hung. The same override is what
        // made --gui-errors behave oddly in a terminal for this verb and no other.
        // --quiet is forwarded for the same reason the user passed it: a terminal invocation
        // that asked to be quiet should not start printing a progress line. It was simply
        // never forwarded, so `purge --quiet` was not quiet. The menu path ignores it either
        // way, since fromMenu branches off above to capture stderr into the dialog.
        bool quiet = args.Flag("--quiet");

        var forwarded = new string[3 + (fromMenu ? 1 : 0) + (quiet ? 1 : 0) + paths.Count];
        int next = 0;
        forwarded[next++] = "delete";
        forwarded[next++] = "--direct";
        forwarded[next++] = "--yes";

        if (fromMenu)
        {
            forwarded[next++] = "--gui-errors";
        }

        if (quiet)
        {
            forwarded[next++] = "--quiet";
        }

        for (int i = 0; i < paths.Count; i++)
        {
            forwarded[next + i] = paths[i];
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
                ReportProblem(
                    true,
                    Lang.Pick($"⚠️彻底删除失败：\r\n\r\n{text}", $"⚠️Permanent delete failed:\r\n\r\n{text}"),
                    Lang.Pick("彻底删除失败", "Permanent Delete Failed"));
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

    internal static void ReportProblemPublic(bool fromMenu, string message, string? caption = null)
        => ReportProblem(fromMenu, message, caption);

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

    /// <summary>
    /// Shows a problem, naming what went wrong.
    ///
    /// <b>Why the caption is a parameter.</b> Every refusal in the program surfaced
    /// through here with one caption, 出错了 / "Something Went Wrong" -- true of all of
    /// them and informative about none. The rest of the program's windows name the thing
    /// they are about (正在复制, 回收站为空, 确认卸载), so a dialog titled 出错了 stood out
    /// as the one that did not say what it was. Reported by the user on 2026-10-15.
    ///
    /// The default is kept for callers whose message is already a sentence about a
    /// failure with no better short name, and for <see cref="ReportProblemPublic"/>;
    /// every call site that can name its own case should.
    /// </summary>
    private static void ReportProblem(bool fromMenu, string message, string? caption = null)
    {
        if (fromMenu)
        {
            ShowDialog(
                message,
                caption ?? Lang.Pick("出错了", "Something Went Wrong"),
                warning: false,
                centre: true);

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

        return ShowDialog(text.ToString(), Lang.Pick("不可恢复的操作", "Irreversible Action"), warning: true, ask: true, centre: true);
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
    /// The original location of each item in the bin, in the order given.
    ///
    /// Shared by the confirmation that names them and the restore that performs them, so
    /// the dialog cannot promise one path while the move goes to another.
    /// </summary>
    private static List<string> OriginalsOf(List<string> stored)
    {
        var originals = new List<string>(stored.Count);

        foreach (string path in stored)
        {
            string extended = PathUtil.ToExtended(path);
            string? binRoot = RecycleBin.RootFor(extended);

            string? original = binRoot is null
                ? null
                : RecycleBin.OriginalPathOf(extended, binRoot);

            originals.Add(original is null
                ? Lang.Pick("（原位置未知）", "(original location unknown)")
                : PathUtil.ToDisplay(original));
        }

        return originals;
    }

    /// <summary>
    /// Opens each folder that received something and selects everything that went into it.
    ///
    /// Shell32.SelectInFolder is SHOpenFolderAndSelectItems, which takes an array of child
    /// PIDLs -- so one call selects all of them, and it is the same call Explorer makes after
    /// a paste and the same one the paste path here already used.
    ///
    /// An earlier version of this used "explorer.exe /select,path" instead, one process per
    /// item. It worked and it was worse: /select takes a single item, so restoring twenty
    /// files into one folder selected one of them, and it spawned a process per window to do
    /// it. The capability was already in the project; the mistake was not looking.
    /// </summary>
    private static void RevealRestored(List<string> restoredPaths)
    {
        if (restoredPaths.Count == 0)
        {
            return;
        }

        // Grouped by parent, because a selection belongs to a window and a window belongs
        // to a folder. Restoring into three folders opens three windows, each with its own
        // items selected -- not one window and two folders' worth of confusion.
        var byFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in restoredPaths)
        {
            string folder = Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;

            if (!byFolder.TryGetValue(folder, out List<string>? items))
            {
                items = [];
                byFolder[folder] = items;
            }

            items.Add(path);
        }

        foreach ((string folder, List<string> items) in byFolder)
        {
            try
            {
                // The window showing this folder, when there is one, gets the selection
                // set in it. SHOpenFolderAndSelectItems opens a second window on a folder
                // that is already on screen -- it reuses nothing, which is not what its
                // documentation implies and is what the paste path was changed for.
                // Restoring into a folder the user is looking at had the same defect.
                if (!ExplorerSelection.TrySelectInExistingWindow(folder, items))
                {
                    Shell32.SelectInFolder(folder, items);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Revealing is a convenience on top of a convenience.
            }
        }
    }

    /// <summary>
    /// Opens a folder in the shell, raising an existing window rather than starting a new one.</summary>
    private static void OpenFolder(string path)
    {
        try
        {
            // ShellExecute, not a spawned explorer.exe.
            //
            // Both look like they open a folder and only one of them reliably does.
            // Launching explorer.exe as an ordinary child process asks the shell to start a
            // new instance of itself; when a shell is already running -- which is every
            // interactive session -- that request is handed off and can be dropped with no
            // error anywhere. ShellExecute is the documented way to ask the shell to open
            // something, and it either raises the existing window or opens a new one.
            //
            // It also means the path is not quoted by hand, which is its own class of bug: a
            // folder whose name contains a quote cannot be made safe by adding more quotes.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
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
            text.ToString(), Lang.Pick("目标已存在同名项目", "Name Already Exists"), DialogButtons.YesNoCancel, warning: true, centre: true);
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
