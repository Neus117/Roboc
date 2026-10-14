using Roboc.Core.Control;
using Roboc.Core.Copy;
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

        using ProgressWindow? progressWindow = fromMenu
            ? ProgressWindow.Start(Lang.Pick("正在复制", "Copying"), destination, control)
            : null;

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

        JobResult result;
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
