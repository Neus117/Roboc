using System.Collections.Concurrent;
using System.Globalization;
using Roboc.Core.Diagnostics;
using Roboc.Core.Fs;
using Roboc.Core.Interop;

namespace Roboc.Core.Delete;

/// <summary>One staged item: where it is now, where it came from, when it expires.</summary>
internal sealed record StagedEntry(string TrashDirectory, string MovedPath, string OriginalPath, DateTime ExpiresUtc);

/// <summary>
/// Owns the staging directories that a reversible delete renames its targets
/// into, and reclaims them once their undo window has passed.
///
/// The manifest is durable on purpose. A one-shot CLI process exits immediately
/// and takes any in-memory queue with it, so a staged tree would otherwise be
/// stranded forever. Every entry is therefore recorded on disk with an expiry,
/// and the next run reclaims whatever has expired.
/// </summary>
internal static class TrashStore
{
    public const string DirectoryPrefix = ".roboc-trash-";

    private static readonly ConcurrentQueue<StagedEntry> s_pending = new();
    private static readonly ManualResetEventSlim s_signal = new(false);
    private static readonly object s_workerLock = new();
    private static Thread? s_worker;
    private static long s_reclaimed;

    public static long DirectoriesReclaimed => Interlocked.Read(ref s_reclaimed);

    private static string? StateDirectory
    {
        get
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roboc");

                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }
    }

    private static string? ManifestPath
    {
        get
        {
            string? directory = StateDirectory;
            return directory is null ? null : Path.Combine(directory, "staged.tsv");
        }
    }

    public static string NewTrashDirectory(string parentExtended) =>
        PathUtil.CombineExtended(parentExtended, DirectoryPrefix + Guid.NewGuid().ToString("N")[..8]);

    public const string MarkerFileName = ".staged.tsv";

    public static void Stage(string trashDirectory, string movedPath, string originalPath, TimeSpan window)
    {
        var entry = new StagedEntry(trashDirectory, movedPath, originalPath, DateTime.UtcNow + window);

        // The authoritative record lives inside the staging directory. Writing it
        // to %LOCALAPPDATA% instead looked fine and was wrong: a locked-down or
        // redirected profile denies that write, the staging succeeded anyway, and
        // the undo record was silently lost. The staging directory's own parent is
        // writable by construction -- we just created a directory in it.
        WriteMarker(entry);

        // Best-effort index so an argument-less 'undo' can find the parents.
        Append(entry);

        s_pending.Enqueue(entry);
        EnsureWorker();
        s_signal.Set();
    }

    private static void WriteMarker(StagedEntry entry)
    {
        try
        {
            string line = string.Join('\t',
                entry.ExpiresUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                entry.MovedPath,
                entry.OriginalPath);

            File.WriteAllText(Path.Combine(entry.TrashDirectory, MarkerFileName), line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reads one staging directory's own marker.</summary>
    private static StagedEntry? ReadMarker(string trashDirectory)
    {
        try
        {
            string path = Path.Combine(trashDirectory, MarkerFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            string[] parts = File.ReadAllText(path).Trim().Split('\t');
            if (parts.Length != 3 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out long ticks))
            {
                return null;
            }

            return new StagedEntry(trashDirectory, parts[1], parts[2], new DateTime(ticks, DateTimeKind.Utc));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Finds every staging directory directly inside the given directories.
    /// This is what makes undo independent of any writable profile location.
    /// </summary>
    public static List<StagedEntry> FindStagedIn(IEnumerable<string> parentDirectories)
    {
        var found = new List<StagedEntry>();

        foreach (string parent in parentDirectories)
        {
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }

            string extended = PathUtil.ToExtended(parent);
            var entries = new List<FindEntry>(32);

            if (!DirectoryList.Enumerate(extended, entries))
            {
                continue;
            }

            foreach (FindEntry entry in entries)
            {
                if (!entry.IsDirectory || !entry.Name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                StagedEntry? staged = ReadMarker(entry.FullPath);
                if (staged is not null)
                {
                    found.Add(staged);
                }
            }
        }

        return found;
    }

    private static void Append(StagedEntry entry)
    {
        string? path = ManifestPath;
        if (path is null)
        {
            return;
        }

        try
        {
            string line = string.Join('\t',
                entry.ExpiresUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                entry.TrashDirectory,
                entry.MovedPath,
                entry.OriginalPath);

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static List<StagedEntry> ReadManifest()
    {
        var entries = new List<StagedEntry>();
        string? path = ManifestPath;

        if (path is null || !File.Exists(path))
        {
            return entries;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException) { return entries; }
        catch (UnauthorizedAccessException) { return entries; }

        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 4 ||
                !long.TryParse(parts[0], CultureInfo.InvariantCulture, out long ticks))
            {
                continue;
            }

            entries.Add(new StagedEntry(parts[1], parts[2], parts[3], new DateTime(ticks, DateTimeKind.Utc)));
        }

        return entries;
    }

    private static void WriteManifest(IEnumerable<StagedEntry> entries)
    {
        string? path = ManifestPath;
        if (path is null)
        {
            return;
        }

        try
        {
            var lines = new List<string>();
            foreach (StagedEntry entry in entries)
            {
                lines.Add(string.Join('\t',
                    entry.ExpiresUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    entry.TrashDirectory,
                    entry.MovedPath,
                    entry.OriginalPath));
            }

            File.WriteAllLines(path, lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Picks up everything an earlier run staged, and reclaims whatever
    /// has already expired.</summary>
    public static List<StagedEntry> LoadAndSweep()
    {
        List<StagedEntry> entries = ReadManifest();
        var live = new List<StagedEntry>(entries.Count);

        foreach (StagedEntry entry in entries)
        {
            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                FastDeleter.DeleteTreeQuiet(entry.TrashDirectory);
                Kernel32.RemoveDirectory(entry.TrashDirectory);
                Interlocked.Increment(ref s_reclaimed);
            }
            else
            {
                live.Add(entry);
                s_pending.Enqueue(entry);
            }
        }

        if (live.Count != entries.Count)
        {
            WriteManifest(live);
        }

        if (!s_pending.IsEmpty)
        {
            EnsureWorker();
            s_signal.Set();
        }

        return live;
    }

    /// <summary>
    /// Moves staged items back to their original locations.
    ///
    /// Searches the given directories for staging directories and reads each one's
    /// own marker. When no directory is given it falls back to the best-effort
    /// index, which may be unavailable.
    /// </summary>
    public static (int Restored, int Failed) RestoreAll(
        IReadOnlyList<string> parentDirectories, Action<string>? log = null)
    {
        List<StagedEntry> entries = parentDirectories.Count > 0
            ? FindStagedIn(parentDirectories)
            : ReadManifest();

        var remaining = new List<StagedEntry>(entries.Count);
        int restored = 0;
        int failed = 0;

        foreach (StagedEntry entry in entries)
        {
            if (!DirectoryOps.Exists(entry.MovedPath))
            {
                // Already reclaimed. Drop the record rather than pretend.
                failed++;
                continue;
            }

            if (FastDeleter.TryRestore(entry.MovedPath, entry.OriginalPath, out int code))
            {
                restored++;
                try { File.Delete(Path.Combine(entry.TrashDirectory, MarkerFileName)); } catch { }
                Kernel32.RemoveDirectory(entry.TrashDirectory);
                log?.Invoke($"restored {PathUtil.ToDisplay(entry.OriginalPath)}");
            }
            else
            {
                failed++;
                remaining.Add(entry);
                log?.Invoke($"could not restore {PathUtil.ToDisplay(entry.OriginalPath)}: " +
                            NativeError.DescribeWin32(code));
            }
        }

        WriteManifest(remaining);
        return (restored, failed);
    }
    /// <summary>Reclaims any staging directory left in the given directory by an
    /// earlier run whose record was lost.</summary>
    public static void SweepSiblings(string parentExtended)
    {
        var entries = new List<FindEntry>(32);
        if (!DirectoryList.Enumerate(parentExtended, entries))
        {
            return;
        }

        foreach (FindEntry entry in entries)
        {
            if (!entry.IsDirectory || !entry.Name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // Never reclaim a staging directory whose undo window is still open;
            // that would silently destroy the very thing undo promises.
            StagedEntry? staged = ReadMarker(entry.FullPath);
            if (staged is not null && staged.ExpiresUtc > DateTime.UtcNow)
            {
                continue;
            }

            FastDeleter.DeleteTreeQuiet(entry.FullPath);
            Kernel32.RemoveDirectory(entry.FullPath);
            Interlocked.Increment(ref s_reclaimed);
        }
    }

    private static void EnsureWorker()
    {
        if (s_worker is not null)
        {
            return;
        }

        lock (s_workerLock)
        {
            if (s_worker is not null)
            {
                return;
            }

            s_worker = new Thread(Loop)
            {
                IsBackground = true,
                Name = "roboc-trash-sweeper",
            };

            s_worker.Start();
        }
    }

    private static void Loop()
    {
        // Background mode is the point: this runs after the user has already been
        // told the delete finished.
        Kernel32.SetThreadPriority(Kernel32.GetCurrentThread(), NativeConst.THREAD_MODE_BACKGROUND_BEGIN);

        try
        {
            while (true)
            {
                if (!s_signal.Wait(5000))
                {
                    continue;
                }

                s_signal.Reset();
                Drain();
            }
        }
        finally
        {
            Kernel32.SetThreadPriority(Kernel32.GetCurrentThread(), NativeConst.THREAD_MODE_BACKGROUND_END);
        }
    }

    private static void Drain()
    {
        if (s_pending.IsEmpty)
        {
            return;
        }

        // Re-read the manifest so entries staged by our own process are included
        // with correct expiries rather than being reclaimed immediately.
        List<StagedEntry> entries = ReadManifest();
        var live = new List<StagedEntry>(entries.Count);

        foreach (StagedEntry entry in entries)
        {
            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                FastDeleter.DeleteTreeQuiet(entry.TrashDirectory);
                Kernel32.RemoveDirectory(entry.TrashDirectory);
                Interlocked.Increment(ref s_reclaimed);
            }
            else
            {
                live.Add(entry);
            }
        }

        while (s_pending.TryDequeue(out _))
        {
            // The manifest is the source of truth; the queue is only a wake-up.
        }

        if (live.Count != entries.Count)
        {
            WriteManifest(live);
        }

        if (live.Count > 0)
        {
            s_signal.Set();
        }
    }

    /// <summary>Blocks until every expired entry has been reclaimed.</summary>
    public static void DrainNow()
    {
        EnsureWorker();
        Drain();
    }
}
