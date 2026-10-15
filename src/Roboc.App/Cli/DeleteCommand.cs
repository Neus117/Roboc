using Roboc.Core.Control;
using Roboc.Core.Delete;
using Roboc.Core.Fs;
using Roboc.Core.Model;
using Roboc.Core.Progress;

namespace Roboc.App.Cli;

internal static class DeleteCommand
{
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
    /// Feeds both the console line and the window from one snapshot.
    ///
    /// The window is only present for a menu invocation, so this returns the plain
    /// console reporter in the common case rather than wrapping it for nothing.
    /// </summary>
    private static Action<ProgressSnapshot>? BuildProgressCallback(
        bool quiet, ProgressWindow? window)
    {
        Action<ProgressSnapshot>? console = quiet ? null : new ConsoleProgress().Report;

        if (window is null)
        {
            return console;
        }

        return snapshot =>
        {
            console?.Invoke(snapshot);

            long remaining = snapshot.HasTotals
                ? Math.Max(0, snapshot.FilesTotal - snapshot.FilesDone)
                : 0;

            string phase = snapshot.CurrentItem is { Length: > 0 } item
                ? (item.Length <= 46 ? item : "…" + item[^45..])
                : Lang.Pick("正在处理", "Working");

            window.Report(
                snapshot.HasTotals ? Lang.Pick($"正在删除 {snapshot.FilesTotal:N0} 个项目", $"Deleting {snapshot.FilesTotal:N0} items") : Lang.Pick("正在删除", "Deleting"),
                phase,
                remaining,
                Math.Max(0, snapshot.BytesTotal - snapshot.BytesDone),
                (long)snapshot.BytesPerSecond,
                Fraction(snapshot),
                snapshot.Remaining?.TotalSeconds);
        };
    }

    public static int Run(Args args)
    {
        List<string> sources = args.Positionals();
        if (sources.Count == 0)
        {
            Console.Error.WriteLine(Lang.Pick("没有收到任何项目。", "No items were received."));
            return 2;
        }

        bool quiet = args.Flag("--quiet");
        bool confirmed = args.Flag("--yes");

        // Heavy confirmation. The context menu passes --yes because the user
        // picked the verb from a menu on a specific selection; an interactive
        // shell invocation has to see what it is about to lose.
        if (!confirmed && !quiet)
        {
            confirmed = ConfirmInteractively(sources);
        }

        if (!confirmed)
        {
            Console.Error.WriteLine(Lang.Pick("为防止误操作，删除需要 --yes 参数确认。", "Deletion needs --yes to confirm, so that it cannot happen by accident."));
            return 2;
        }

        var options = new DeleteOptions
        {
            Confirmed = true,
            Direct = args.Flag("--direct"),
            WorkerThreads = args.IntOption("--workers", 0),
            UndoWindow = TimeSpan.FromMinutes(args.DoubleOption("--undo-window", 10)),
        };

        if (args.Flag("--no-undo"))
        {
            options = new DeleteOptions
            {
                Confirmed = true,
                Direct = options.Direct,
                WorkerThreads = options.WorkerThreads,
                UndoWindow = TimeSpan.Zero,
            };
        }

        using var control = new JobControl();

        // Same delayed window as the copy path: a delete that finishes quickly never
        // shows anything, and a long one reports what is left and how long.
        bool fromMenu = args.Flag("--gui-errors");

        // Checked before the progress window is started, for the reason spelled out in
        // CopyCommand.RejectNonFileSystemSources: the window's thread is still inside its
        // show delay when the engine's guard throws, so the dialog opens first and its
        // modal message loop then pumps the window's pending creation message -- leaving a
        // blank, unclosable progress window *behind* the dialog. Reported with screenshots
        // on 2026-10-15.
        if (!RejectNonFileSystemSources(sources, fromMenu))
        {
            return 4;
        }

        JobResult result;

        // The progress window lives only as long as the operation does, and the finally
        // below is what enforces that. It must not outlive it: a modal dialog runs its own
        // message loop on this thread, and while it does, no message reaches the progress
        // window -- so 取消 and 关闭 are painted but dead, and the window can only be
        // dismissed through the dialog.
        //
        // Reported by the user on 2026-10-15 with a screenshot: a refused delete left the
        // progress window and the error dialog on screen together. The copy path had the
        // same shape.
        //
        // Note the explicit Dispose in a finally, rather than a using declaration.
        // A using *declaration* is disposed at the end of its enclosing block, and the
        // dialog on the failure path is shown inside that block -- so the dialog appeared
        // first and the window was still on screen. Measured, not assumed: both were
        // visible in the same sample. Disposing before anything else is what fixes it.
        ProgressWindow? progressWindow = fromMenu
            ? ProgressWindow.Start(Lang.Pick("正在删除", "Deleting"), Lang.Pick("文件不会进入任何回收站", "Files do not go to any recycle bin"), control)
            : null;

        try
        {
            var progress = new ProgressAggregator();

            var job = new DeleteJob
            {
                Sources = sources,
                Options = options,
                Control = control,
                Progress = progress,
                Stream = args.Flag("--stream"),
                OnLog = quiet ? null : message => Console.Error.WriteLine("  ! " + message),
                OnProgress = BuildProgressCallback(quiet, progressWindow),
            };

            result = DeleteEngine.Run(job);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(Lang.Pick("操作已取消。", "Cancelled."));
            return 130;
        }
        catch (Exception ex)
        {
            // Guard refusals land here. They are the intended outcome for a
            // dangerous target, not a crash.
            string message = Lang.Pick($"操作失败：{ex.Message}", $"Failed: {ex.Message}");
            Console.Error.WriteLine(message);

            // And told to the user, for the same reason the copy path now does.
            //
            // A refused delete is exactly the case where the user most needs to hear the
            // reason -- the guard exists because the target was dangerous to touch, and a menu
            // invocation that writes that reason to a console nobody has leaves the user
            // believing nothing happened, or worse, that it worked.
            if (fromMenu)
            {
                MaintenanceCommands.ReportProblemPublic(true, message);
            }

            return 4;
        }
        finally
        {
            // Before any dialog, on every path out of here -- including the two returns
            // above, because a finally runs before the catch that is returning.
            progressWindow?.Dispose();
        }

        if (!quiet)
        {
            ConsoleProgress.Finish();
            PrintSummary(result, options);
        }

        if (args.Flag("--wait-cleanup"))
        {
            TrashStore.DrainNow();
        }

        return result.FilesFailed == 0 ? 0 : 3;
    }

    /// <summary>
    /// Refuses, before the progress window is started, when the item is not in the file
    /// system. Returns true when it is usable.
    ///
    /// The wording lives in <see cref="NonFileSystemNotice"/>: it has to fit a device used
    /// as the source *and* as the destination, and it has to be one sentence rather than
    /// several near-identical ones.
    /// </summary>
    private static bool RejectNonFileSystemSources(IReadOnlyList<string> sources, bool fromMenu)
    {
        foreach (string source in sources)
        {
            if (NonFileSystemNotice.IsUsable(source))
            {
                continue;
            }

            if (fromMenu)
            {
                MaintenanceCommands.ReportProblemPublic(true, NonFileSystemNotice.Text);
            }
            else
            {
                Console.Error.WriteLine("roboc: " + NonFileSystemNotice.Text.Replace("\r\n\r\n", " "));
            }

            return false;
        }

        return true;
    }

    private static bool ConfirmInteractively(List<string> sources)
    {
        if (!ConsoleHost.HasConsole)
        {
            return false;
        }

        Console.Error.WriteLine("the following will be removed:");
        foreach (string source in sources)
        {
            Console.Error.WriteLine("  " + source);
        }

        Console.Error.Write($"type DELETE to confirm: ");
        string? answer = Console.ReadLine();
        return string.Equals(answer, "DELETE", StringComparison.Ordinal);
    }

    private static void PrintSummary(JobResult result, DeleteOptions options)
    {
        Console.WriteLine();
        Console.WriteLine($"  state     {result.State}");
        Console.WriteLine($"  removed   {result.FilesCopied:N0}");
        Console.WriteLine($"  failed    {result.FilesFailed:N0}");
        Console.WriteLine($"  elapsed   {result.Elapsed.TotalSeconds:F2}s");

        double seconds = result.Elapsed.TotalSeconds;
        if (seconds > 0)
        {
            Console.WriteLine($"  rate      {result.FilesCopied / seconds:N0} items/s");
        }

        if (!options.Direct && options.UndoWindow > TimeSpan.Zero)
        {
            Console.WriteLine($"  undo      run 'roboc undo' within " +
                              $"{options.UndoWindow.TotalMinutes:F0} minutes to bring items back");
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  first {Math.Min(10, result.Errors.Count)} note(s):");
            foreach (JobError error in result.Errors.Take(10))
            {
                Console.WriteLine($"    [{error.Kind}] {error.Operation} {error.Path}");
                Console.WriteLine($"        {error.Message}");
            }
        }
    }
}

internal static class UndoCommand
{
    public static int Run(Args args)
    {
        List<string> directories = args.Positionals();

        if (directories.Count == 0)
        {
            Console.Error.WriteLine(
                "roboc: undo needs the directory the items were deleted from, e.g. 'roboc undo D:\\data'");
            return 2;
        }

        (int restored, int failed) = DeleteEngine.RestoreStaged(
            directories, args.Flag("--quiet") ? null : Console.Error.WriteLine);

        Console.WriteLine($"restored {restored}, could not restore {failed}");
        return failed == 0 ? 0 : 3;
    }
}
