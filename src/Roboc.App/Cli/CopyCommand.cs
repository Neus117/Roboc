using Roboc.Core.Control;
using Roboc.Core.Copy;
using Roboc.Core.Diagnostics;
using Roboc.Core.Fs;
using Roboc.Core.Model;
using Roboc.Core.Progress;

namespace Roboc.App.Cli;

internal static class CopyCommand
{
    public static int Run(Args args, bool move, HashSet<string>? changedRoots = null)
    {
        string? destination = args.Option("--to");

        if (string.IsNullOrWhiteSpace(destination))
        {
            Console.Error.WriteLine("roboc: --to <dir> is required");
            return 2;
        }

        List<string> sources = args.Positionals();
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("roboc: no source paths given");
            return 2;
        }

        bool quiet = args.Flag("--quiet");

        // Settings live beside the executable, so this folder can be copied elsewhere
        // and behaves identically, and deleting it leaves nothing behind.
        RobocSettings settings = RobocSettings.Load();

        var options = new CopyOptions
        {
            WorkerThreads = args.IntOption("--workers", 0),
            Overwrite = ParseOverwrite(args.Option("--overwrite", "skip")),
            CopyBufferSize = args.IntOption("--buffer", 1024 * 1024),
            // Exposed so a benchmark can compare like with like against tools that
            // never flush. Off is faster and less durable; see CopyOptions.
            FlushLargeFiles = !args.Flag("--no-flush"),
            // Measurement knobs for the publish design, not user-facing tuning.
            Publish = args.Flag("--always-atomic") ? PublishMode.AlwaysAtomic
                    : args.Flag("--always-direct") ? PublishMode.AlwaysDirect
                    : settings.AtomicPublish ? PublishMode.Split
                    : PublishMode.AlwaysDirect,
            PosixRename = !args.Flag("--no-posix-rename"),

            // Each of these is the saved setting, overridden by either flag. Both
            // directions exist because a feature switched on in the settings window
            // must still be switchable off for one run -- a setting users cannot get
            // out of temporarily is one they learn not to turn on.
            //
            // Detecting named streams costs a FindFirstStreamW per file -- 19 us
            // measured against a 16 us baseline -- and the copy this program is built
            // for is very many very small files, almost none of which carry one.
            CopyAlternateDataStreams = Resolve(
                args, "--ads", "--no-ads", settings.CopyAlternateDataStreams),

            // Verification reads both files end to end, roughly doubling the read
            // traffic of a copy. Worth it when the destination is a removable disk, a
            // network share or a drive you have reason to distrust.
            VerifyChecksum = Resolve(
                args, "--verify", "--no-verify", settings.VerifyChecksum),

            RecreateHardLinks = Resolve(
                args, "--hardlinks", "--no-hardlinks", settings.RecreateHardLinks),

            // MegaBytes between resume checkpoints. Each one is flushed before the
            // journal records it, so a small value limits how far the writer can run
            // ahead of the disk. Raising the default was attempted and reverted for
            // want of a measurement that could attribute the difference to it; see
            // CopyOptions.ResumeCheckpointInterval. The knob is exposed so that
            // measurement can be made properly.
            ResumeCheckpointInterval = args.IntOption("--checkpoint-interval", 32) * 1024L * 1024L,
        };

        using var control = new JobControl();

        // Checked before anything is created, including the progress window. The guard
        // itself lives in the engine, but by the time the engine runs the progress window
        // has already been started on its own thread -- and that thread is still asleep in
        // its show delay when the engine throws. The dialog then opens first, and its modal
        // message loop pumps the progress window's pending creation message: a blank
        // progress window appears *behind* the dialog, with 取消 and 关闭 that never
        // receive a message and cannot be clicked.
        //
        // Reported by the user on 2026-10-15, twice, with screenshots. Rejecting here means
        // the window has not been started at all, which is the only clean fix.
        //
        // Both ends are checked, and the destination matters as much as the sources:
        // pasting *to* a phone passes the device's own path as the target, and without this
        // the run got as far as CreateDirectory, which reported
        // "CreateDirectory failed for 8&1&0000#{6ac27878-...}: the system cannot find the
        // path specified" -- the same fault, reported in the same internal language.
        if (!RejectNonFileSystemPaths(sources, destination, args.Flag("--gui-errors")))
        {
            return 4;
        }

        // The destination is then actually created, here, before the window exists.
        //
        // Being a file-system path is not the same as being a path this machine can
        // make. A destination whose parent does not exist -- a folder deleted while the
        // menu was open, or a mistyped one -- passes the check above and then fails in
        // the engine at CreateDirectory. By then the progress window has been started,
        // and the same ordering defect above returns: the dialog opens first and the
        // blank window appears behind it.
        //
        // Reported by the user on 2026-10-15 after the multi-select verbs were fixed:
        // pasting to a target that had been deleted left the error dialog and an empty
        // 正在复制 window on screen together.
        //
        // Creating it here is idempotent -- reaching a directory that already exists
        // reports success -- so the engine finds it already there and does not repeat
        // the work. Doing the real operation rather than a probe has the same advantage
        // the paste path already relies on for sources: it costs one syscall and it
        // cannot disagree with what the engine will do next.
        if (!RejectUnusableDestination(destination, args.Flag("--gui-errors")))
        {
            return 4;
        }

        using var journal = JournalFactory.Open(
            destination, sources, args.Option("--job"),
            disabled: !settings.ResumeEnabled || args.Flag("--no-resume"), out bool resuming);
        var progress = new ProgressAggregator();
        CopyTimings? timings = args.Flag("--timing") ? new CopyTimings() : null;

        if (!quiet)
        {
            if (resuming)
            {
                Console.WriteLine("resuming a previously interrupted job; finished items will be skipped");
            }

            if (args.Flag("--show-journal"))
            {
                Console.WriteLine($"journal    {JournalFactory.JournalPath ?? "(none)"}");
            }
        }

        // Shown only when invoked from a menu, and only after the delay has passed --
        // a terminal run already has a live progress line, and a quick operation
        // should not interrupt anything.
        bool fromMenu = args.Flag("--gui-errors");

        JobResult result;

        // The progress window lives only as long as the operation does, and the finally
        // below is what enforces that. It must not outlive it: a modal dialog runs its own
        // message loop on this thread, and while it does, no message reaches the progress
        // window -- so 取消 and 关闭 are painted but dead, and the window can only be
        // dismissed through the dialog.
        //
        // Reported by the user on 2026-10-15 with a screenshot: a paste the guard refused
        // left the progress window and the error dialog on screen together.
        //
        // Note the explicit Dispose in a finally, rather than a using declaration.
        // A using *declaration* is disposed at the end of its enclosing block, and the
        // dialog on the failure path is shown inside that block -- so the dialog appeared
        // first and the window was still on screen. A finally runs before the catch that
        // follows it, which is exactly the order needed.
        ProgressWindow? progressWindow = fromMenu
            ? ProgressWindow.Start(Lang.Pick("正在复制", "Copying"), destination, control)
            : null;

        try
        {
            Action<ProgressSnapshot>? onProgress = quiet ? null : new ConsoleProgress().Report;

            if (progressWindow is not null)
            {
                Action<ProgressSnapshot>? console = quiet ? null : onProgress;

                onProgress = snapshot =>
                {
                    console?.Invoke(snapshot);

                    long remaining = snapshot.HasTotals
                        ? Math.Max(0, snapshot.FilesTotal - snapshot.FilesDone)
                        : 0;

                    progressWindow.Report(
                        BuildHeadline(snapshot, move, destination),
                        snapshot.CurrentItem is { Length: > 0 } item ? Shorten(item) : Lang.Pick("正在准备…", "Preparing..."),
                        remaining,
                        Math.Max(0, snapshot.BytesTotal - snapshot.BytesDone),
                        (long)snapshot.BytesPerSecond,
                        Fraction(snapshot),
                        snapshot.Remaining?.TotalSeconds);
                };
            }

            var job = new CopyJob
            {
                Sources = sources,
                Destination = destination,
                Options = options,
                Control = control,
                Journal = journal,
                Progress = progress,
                Move = move,
                Stream = args.Flag("--stream"),
                Timings = timings,
                OnLog = quiet ? null : message => Console.Error.WriteLine("  ! " + message),
                OnProgress = onProgress,
                OnItemWritten = changedRoots is null
                    ? null
                    : relative =>
                    {
                        int separator = relative.IndexOf('\\');

                        // A file at the top level is its own root; anything deeper
                        // belongs to the folder that contains it.
                        string root = separator < 0 ? relative : relative[..separator];

                        if (root.Length > 0)
                        {
                            lock (changedRoots)
                            {
                                changedRoots.Add(root);
                            }
                        }
                    },
            };

            try
            {
                result = CopyEngine.Run(job);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("cancelled");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"roboc: {ex.Message}");

                // Told to the user as well, when there is a user to tell.
                //
                // This is where every guard refusal lands -- a destination inside its own source, a
                // folder into itself -- and it used to write to stderr and return. Invoked from the
                // context menu there is no console, so a refused paste said nothing anywhere: not a
                // message the user could act on, not even an indication that the program had run.
                // Reported as "paste does nothing".
                //
                // The text is the guard's own, and it names both paths involved -- which is what
                // makes it actionable rather than merely informative.
                if (args.Flag("--gui-errors"))
                {
                    MaintenanceCommands.ReportProblemPublic(true, ex.Message);
                }

                return 1;
            }

            if (!quiet)
            {
                ConsoleProgress.Finish();
                PrintSummary(result, options);
            }

            // Invoked from a menu there is no console, so the summary and every error
            // would be written into nothing. That is how a paste whose sources no longer
            // existed came to look like it had done nothing at all.
            if (args.Flag("--gui-errors"))
            {
                ReportToDialog(result, options);
            }
        }
        finally
        {
            // Before any dialog, on every path out of here -- including the two returns
            // in the catches above, because a finally runs before the catch that is
            // returning.
            progressWindow?.Dispose();
        }

        // A clean run has nothing left to resume, and keeping the log would make
        // the next run skip files it should re-examine.
        if (result.State == JobState.Completed && result.FilesFailed == 0)
        {
            JournalFactory.Discard();
        }

        if (timings is not null)
        {
            Console.WriteLine();
            Console.WriteLine("  phase breakdown (summed across workers):");
            Console.WriteLine(timings.Report());
        }

        return result.FilesFailed == 0 ? 0 : 3;
    }

    /// <summary>
    /// Progress as a fraction, using whichever measure is actually moving.
    ///
    /// Bytes are right for a copy: they advance as data is written, and the file
    /// count would sit at zero for the whole of a large-file transfer. They are
    /// useless for a delete, which reports whole items and passes zero bytes for
    /// each -- so preferring bytes unconditionally pinned a delete at 0% from start
    /// to finish. The same rule cannot be right for both, so the rule asks which
    /// counter has actually advanced.
    /// </summary>
    private static double Fraction(ProgressSnapshot snapshot)
    {
        if (!snapshot.HasTotals)
        {
            return -1;
        }

        if (snapshot.BytesTotal > 0 && snapshot.BytesDone > 0)
        {
            return snapshot.BytesFraction;
        }

        if (snapshot.FilesTotal > 0)
        {
            return snapshot.FilesFraction;
        }

        return snapshot.BytesTotal > 0 ? snapshot.BytesFraction : 0;
    }

    /// <summary>
    /// The line Explorer puts first: how many items, and between which folders. It
    /// answers "what is happening" before the numbers answer "how far along".
    ///
    /// <b>Two whole sentences, not one sentence with a shared verb.</b> This used to take
    /// a verb argument and slot it into both languages, on the assumption that the verb
    /// sits in the same place in each. It does not: Chinese puts it between the source and
    /// the destination with a trailing 到, while English puts "to" in that position and
    /// needs no verb at all. Sharing the verb produced
    /// "Copying 8 items from send-test Copyto receive-test" -- which is what a sentence
    /// looks like when it has been assembled rather than written.
    /// </summary>
    private static string BuildHeadline(ProgressSnapshot snapshot, bool moving, string destination)
    {
        string from = snapshot.CurrentItem is { Length: > 0 } item
            ? Path.GetFileName(Path.GetDirectoryName(item) ?? item)
            : "…";

        string to = Path.GetFileName(destination.TrimEnd('\\'));

        string count = snapshot.HasTotals
            ? Lang.Pick($"{snapshot.FilesTotal:N0} 个项目", $"{snapshot.FilesTotal:N0} items")
            : Lang.Pick("所选项目", "the selection");

        return moving
            ? Lang.Pick($"正在将 {count}从 {from} 移动到 {to}", $"Moving {count} from {from} to {to}")
            : Lang.Pick($"正在将 {count}从 {from} 复制到 {to}", $"Copying {count} from {from} to {to}");
    }
    /// <summary>
    /// Trims a path to something that fits a small window, keeping the end -- the
    /// file name is what identifies it; the drive and the first few folders are not.
    /// </summary>
    private static string Shorten(string path)
    {
        const int Limit = 46;

        return path.Length <= Limit ? path : "…" + path[^(Limit - 1)..];
    }

    /// <summary>
    /// Refuses, before anything is created, when either end of the operation is not an
    /// item in the file system. Returns true when both ends are usable.
    ///
    /// <b>Why here and not only in the engine.</b> The engine's guard fires after the
    /// progress window has been started, and its thread is still inside the show delay at
    /// that moment. The error dialog then opens first, and because a modal dialog runs its
    /// own message loop, it pumps the pending creation message for the progress window:
    /// a blank window appears behind the dialog with 取消 and 关闭 that can never be
    /// clicked. Rejecting before the window exists removes the possibility rather than
    /// trying to sequence around it.
    ///
    /// <b>Both directions.</b> A device path can arrive as the source (pasting *from* a
    /// phone) or as the destination (pasting *to* one). Only the source was checked at
    /// first, and the destination then failed much deeper, with
    /// <c>CreateDirectory failed for 8&amp;1&amp;0000#{6ac27878-...}</c> -- which names
    /// neither the problem nor what to do about it.
    ///
    /// The wording itself lives in <see cref="NonFileSystemNotice"/>, because the same
    /// refusal is given by the delete path, the recycle verb and the engines, and four
    /// near-identical copies of it had already drifted apart once.
    /// </summary>
    private static bool RejectNonFileSystemPaths(
        IReadOnlyList<string> sources, string destination, bool fromMenu)
    {
        foreach (string source in sources)
        {
            if (!NonFileSystemNotice.IsUsable(source))
            {
                ReportNotFileSystem(fromMenu);
                return false;
            }
        }

        if (!NonFileSystemNotice.IsUsable(destination))
        {
            ReportNotFileSystem(fromMenu);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates the destination before the progress window exists, and reports failure
    /// with a sentence that names the cause.
    ///
    /// Returns true when the destination is ready.
    ///
    /// <b>Why the real creation and not a probe.</b> The engine creates the destination
    /// as its first act, so doing it here means the engine finds it already there
    /// instead of doing the work twice. A separate "can I create this?" test would be a
    /// second implementation of the same decision, free to disagree with the one that
    /// matters -- the same reasoning that keeps the paste path from re-deriving its
    /// sources.
    ///
    /// <b>Why the message names the cause.</b> The engine's version is
    /// <c>CreateDirectory failed for &lt;path&gt;: 系统找不到指定的路径。(0x00000003)</c>
    /// -- the API call's name, in English, followed by a Windows error string. What a
    /// user needs is that the folder above the target does not exist and that nothing
    /// was copied. The system's own description is still appended, because it is the
    /// part that distinguishes a missing parent from a denied one, and because a report
    /// is worth more when it carries the original words.
    /// </summary>
    private static bool RejectUnusableDestination(string destination, bool fromMenu)
    {
        string extended = PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(destination));

        if (DirectoryOps.CreateIfMissing(extended, out int error) || DirectoryOps.Exists(extended))
        {
            return true;
        }

        // Two different faults, and telling them apart is worth one sentence: a missing
        // drive has no parent to go looking for.
        string drive = Path.GetPathRoot(PathUtil.ToDisplay(extended)) ?? string.Empty;
        bool driveExists = drive.Length > 0 && DirectoryOps.Exists(PathUtil.ToExtended(drive));

        string message = driveExists
            ? Lang.Pick(
                $"⚠️目标位置的上层文件夹不存在，无法在其中创建：\r\n\r\n{destination}\r\n\r\n" +
                $"请先在资源管理器中创建它，或改粘贴到已有的文件夹。\r\n\r\n（{NativeError.DescribeWin32(error)}）",
                $"⚠️The folder above the destination does not exist, so nothing can be created in it:\r\n\r\n{destination}\r\n\r\n" +
                $"Create it in Explorer first, or paste into a folder that exists.\r\n\r\n({NativeError.DescribeWin32(error)})")
            : Lang.Pick(
                $"⚠️目标位置的驱动器不存在或未就绪：\r\n\r\n{destination}\r\n\r\n" +
                $"请确认该驱动器已连接。\r\n\r\n（{NativeError.DescribeWin32(error)}）",
                $"⚠️The drive for the destination does not exist or is not ready:\r\n\r\n{destination}\r\n\r\n" +
                $"Check that it is connected.\r\n\r\n({NativeError.DescribeWin32(error)})");

        if (fromMenu)
        {
            MaintenanceCommands.ReportProblemPublic(true, message);
        }
        else
        {
            Console.Error.WriteLine("roboc: " + message.Replace("\r\n\r\n", " "));
        }

        return false;
    }

    /// <summary>Shows the explanation: a dialog from the menu, stderr from a terminal.</summary>
    private static void ReportNotFileSystem(bool fromMenu)
    {
        if (fromMenu)
        {
            MaintenanceCommands.ReportProblemPublic(true, NonFileSystemNotice.Text);
        }
        else
        {
            Console.Error.WriteLine("roboc: " + NonFileSystemNotice.Text.Replace("\r\n\r\n", " "));
        }
    }

    /// <summary>
    /// Shows the outcome in a dialog.
    ///
    /// Only the failure case carries information the user cannot otherwise get: a
    /// success is already visible as the files that appeared, whereas a failure with
    /// no console is indistinguishable from nothing happening.
    /// </summary>
    private static void ReportToDialog(JobResult result, CopyOptions options)
    {
        _ = options;

        if (result.FilesFailed == 0 && result.Errors.Count == 0)
        {
            // Silence on success, as Explorer is silent: the files appearing is the report.
            // Only a failure carries information the user cannot otherwise get.
            //
            // With one exception, and it is the one that produced a bug report. A paste where
            // nothing was copied because everything was already there is not a success the user
            // can see: the files were there before and they are there after, so the screen does
            // not change and nothing was said. From the outside that is indistinguishable from
            // the program failing to run at all -- and it is what "the paste does nothing" and
            // "it finishes instantly, is it cheating?" both turned out to be.
            //
            // Only when nothing at all was copied. A paste that added one file among nine
            // thousand skipped ones has shown its work, and interrupting that with a dialog
            // would be noise.
            if (result.FilesCopied == 0 && result.FilesSkipped > 0)
            {
                MaintenanceCommands.ReportProblemPublic(
                    true,
                    Lang.Pick(
                        $"ℹ️没有需要复制的项目：目标位置的 {result.FilesSkipped:N0} 项与来源完全相同。",
                        $"ℹ️Nothing needed copying: the {result.FilesSkipped:N0} items at the destination are already identical to the source."));
            }

            return;
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine(Lang.Pick($"⚠️复制 {result.FilesCopied:N0} 项成功，{result.FilesFailed:N0} 项失败。", $"⚠️{result.FilesCopied:N0} copied, {result.FilesFailed:N0} failed."));
        text.AppendLine();

        foreach (JobError error in result.Errors.Take(8))
        {
            text.AppendLine($"· {error.Path}");
            text.AppendLine($"    {error.Message}");
        }

        if (result.Errors.Count > 8)
        {
            text.AppendLine(Lang.Pick($"…另有 {result.Errors.Count - 8} 条错误未显示", $"...{result.Errors.Count - 8} more errors not shown"));
        }

        if (result.Errors.Count == 0 && result.FilesFailed > 0)
        {
            text.AppendLine(Lang.Pick("（未记录具体错误，请用 --show-journal 查看日志位置）", "(no specific errors were recorded; use --show-journal to find the log)"));
        }

        MaintenanceCommands.ReportProblemPublic(true, text.ToString());
    }

    /// <summary>
    /// The saved setting, overridden by either flag.
    ///
    /// Both directions are needed. With only the enabling flag, a feature switched on in
    /// the settings window could not be switched off for a single run -- and a setting
    /// users cannot get out of temporarily is one they learn not to turn on.
    /// </summary>
    private static bool Resolve(Args args, string on, string off, bool saved)
        => args.Flag(on) || (saved && !args.Flag(off));

    private static OverwriteMode ParseOverwrite(string value) => value.ToLowerInvariant() switch
    {
        "skip" or "same" => OverwriteMode.SkipIfSame,
        "skip-existing" or "leave" => OverwriteMode.SkipExisting,
        "newer" => OverwriteMode.OverwriteIfNewer,
        "all" or "force" => OverwriteMode.OverwriteAll,
        "fail" => OverwriteMode.FailIfExists,
        "rename" => OverwriteMode.RenameIfExists,
        _ => OverwriteMode.SkipIfSame,
    };

    private static void PrintSummary(JobResult result, CopyOptions options)
    {
        Console.WriteLine();
        Console.WriteLine($"  state     {result.State}");
        Console.WriteLine($"  copied    {result.FilesCopied:N0}");
        Console.WriteLine($"  skipped   {result.FilesSkipped:N0}");
        Console.WriteLine($"  failed    {result.FilesFailed:N0}");
        Console.WriteLine($"  bytes     {ConsoleProgress.FormatBytes(result.BytesTransferred)}");
        Console.WriteLine($"  elapsed   {result.Elapsed.TotalSeconds:F2}s");

        double seconds = result.Elapsed.TotalSeconds;
        if (seconds > 0)
        {
            Console.WriteLine($"  rate      {result.FilesCopied / seconds:N0} files/s, " +
                              $"{ConsoleProgress.FormatBytes((long)(result.BytesTransferred / seconds))}/s");
        }

        Console.WriteLine($"  workers   {(options.WorkerThreads > 0 ? options.WorkerThreads : CopyEngine.DefaultWorkerCount)}");

        if (result.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  first {Math.Min(10, result.Errors.Count)} error(s):");
            foreach (JobError error in result.Errors.Take(10))
            {
                Console.WriteLine($"    [{error.Kind}] {error.Operation} {error.Path}");
                Console.WriteLine($"        {error.Message}");
            }
        }
    }
}
