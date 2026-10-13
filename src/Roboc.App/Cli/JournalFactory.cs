using Roboc.Core.Journal;

namespace Roboc.App.Cli;

/// <summary>
/// Resolves the job log for a copy or move.
///
/// Resume is keyed by (destination, source set, optional job id), so re-running
/// the same copy continues it rather than starting over. That is the behaviour
/// users expect from an interrupted copy, and it means no --resume flag is
/// needed for the common case.
/// </summary>
internal static class JournalFactory
{
    public static IJobJournal Open(
        string destination, IReadOnlyList<string> sources, string? jobId, bool disabled, out bool resuming)
    {
        resuming = false;

        if (disabled)
        {
            return NullJobJournal.Instance;
        }

        JobJournal? journal = JobJournal.TryOpen(destination, sources, jobId, out string location);

        if (journal is null)
        {
            // No writable location. Resume is a convenience; a locked-down
            // profile must not be able to fail a copy that would otherwise work.
            return NullJobJournal.Instance;
        }

        resuming = journal.HasResumeState;
        JournalPath = location;
        return journal;
    }

    /// <summary>Where the current job's log lives, for reporting and cleanup.</summary>
    public static string? JournalPath { get; private set; }

    /// <summary>Removes the log once a job has finished cleanly. Leaving it would
    /// make the next run skip files it should re-examine after a source change.</summary>
    public static void Discard()
    {
        string? path = JournalPath;
        JournalPath = null;

        if (path is null)
        {
            return;
        }

        // The log lives in a per-destination directory; remove the directory too
        // when it was ours alone, so a completed copy leaves no trace.
        try
        {
            File.Delete(path);

            if (File.Exists(path))
            {
                // Should be unreachable now that the log is opened with
                // FileShare.Delete; kept because a silent failure here is exactly
                // what made a finished copy leave its state behind.
                return;
            }

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null &&
                Path.GetFileName(directory).Equals(".roboc-jobs", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(directory) &&
                Directory.GetFileSystemEntries(directory).Length == 0)
            {
                Kernel32RemoveDirectory(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Removes an empty staging directory without pulling in the interop
    /// layer for one call.</summary>
    private static void Kernel32RemoveDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
