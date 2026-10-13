using System.Text;

namespace Roboc.App.Cli;

/// <summary>
/// Collects the separate invocations Explorer makes for a multi-file selection.
///
/// With <c>MultiSelectModel=Single</c>, Explorer invokes the verb once per selected
/// file, substituting <c>%1</c> each time. It never appends the selection list to
/// the command line -- which is what this program assumed for three attempts, and
/// why every selection-based verb reported receiving nothing. The two third-party
/// verbs on this machine that do work both use <c>"%1"</c> in their command, and
/// one of them is explicitly Single, so this is the mechanism that actually
/// operates here.
///
/// The cost is that N selected files mean N processes. Each one appends its path
/// and exits; the first to arrive also claims the right to process, waits briefly
/// for the others to land, and then handles the whole set in one pass. Without the
/// wait, a ten-file selection would produce ten separate operations and ten
/// confirmation dialogs.
/// </summary>
internal static class PendingSelection
{
    /// <summary>
    /// How long the claiming process waits for siblings to append.
    ///
    /// Long enough for a handful of process starts to complete, short enough not to
    /// feel like a hang. Explorer starts them within a few milliseconds of each
    /// other, so this is generous rather than tight.
    /// </summary>
    private const int SettleMilliseconds = 700;

    /// <summary>
    /// Hard cap on how long the claiming process waits.
    ///
    /// A fixed sleep was tried first and split a 31-file selection into four
    /// batches, because Explorer starts one process per file and 31 process starts
    /// take far longer than any fixed window. Waiting for the list to stop growing
    /// adapts to how many files were selected; the cap only bounds the worst case.
    /// </summary>
    private const int MaxWaitMilliseconds = 30000;

    /// <summary>
    /// A floor on how long the claiming process waits before it will believe the
    /// list has stopped growing.
    ///
    /// The quiet window alone is not enough. The claiming process is often the first
    /// to start, so it can observe 300 ms of quiet before its siblings have even
    /// launched -- and then act on a one-path batch, which is how a 255-file
    /// selection ended up pasting the single item the mouse happened to be over.
    /// </summary>
    private const int MinWaitMilliseconds = 800;

    /// <summary>
    /// Adds a path to the pending set for a verb, and reports whether this process
    /// should act on it.
    ///
    /// Exactly one process acts: it takes an exclusive lock, waits for the list to
    /// stop growing, and handles everything in it. The rest append and leave, and
    /// their paths are picked up by the holder because appending happens before the
    /// claim is attempted.
    ///
    /// <b>Why the loser must not retry.</b> Letting a process that failed to claim
    /// try again looks harmless and is not: each retry eventually succeeds, so a
    /// 255-file selection becomes dozens of batches instead of one, each writing the
    /// clipboard in turn. The last write wins, and the last batch to form is
    /// typically a single late path -- which is how a large selection came to paste
    /// one file. One batch, or none.
    ///
    /// <b>Why the wait has a floor.</b> The holder is usually the first process to
    /// start, so it can satisfy a quiet window before its siblings have launched and
    /// then act on a one-path batch. The floor makes that impossible.
    /// </summary>
    public static bool TryClaim(string verb, string path, out List<string> paths)
    {
        paths = [];

        string directory = QueueDirectory();
        string listPath = Path.Combine(directory, verb + ".list");
        string lockPath = Path.Combine(directory, verb + ".lock");

        try
        {
            Directory.CreateDirectory(directory);

            if (path.Length > 0)
            {
                using var stream = new FileStream(
                    listPath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(path);
            }

            FileStream lockStream;

            try
            {
                lockStream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Someone else is collecting, and this path was appended before the
                // claim was attempted, so their batch includes it.
                return false;
            }

            using (lockStream)
            {
                // A verb that passes no path is claiming only to be single-shot.
                //
                // 粘贴 and 回收空间 take no selection -- Explorer still invokes them
                // once per selected item -- so there is nothing to collect and
                // nothing to wait for. Sending them through the batch path made them
                // wait out the settle window and then report an empty batch, which
                // the caller read as "not the claimer" and exited on. The verb then
                // did nothing at all, every time.
                if (path.Length == 0)
                {
                    return true;
                }

                WaitForBatchToSettle(listPath);
                paths = ReadBatch(listPath, clear: true);

                // True whenever the claim succeeded. An empty batch is not a failure:
                // the caller decides what to do with no paths, and reporting it as a
                // lost claim would silently drop the work.
                return true;
            }
        }
        catch (IOException)
        {
            paths = [path];
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            paths = [path];
            return true;
        }
    }
    /// <summary>
    /// Claims the right to act, without waiting for anything.
    ///
    /// The waiting in <see cref="TryClaim"/> exists for one reason: to reassemble a
    /// selection that Explorer delivered as separate invocations. When the shell has
    /// already been asked what is selected, there is nothing left to reassemble, and
    /// every millisecond of that wait is time the user spends looking at a menu item
    /// that has not done anything yet. The lock is still taken, because one process
    /// acting is still the requirement -- two hundred processes each writing the
    /// clipboard is what turned a large copy into a stalled one.
    ///
    /// The handle is deliberately held for the life of the process: releasing it
    /// early would let a sibling claim while this one is still working.
    /// </summary>
    public static bool TryClaimImmediate(string verb)
    {
        string directory = QueueDirectory();
        string lockPath = Path.Combine(directory, verb + ".lock");

        try
        {
            Directory.CreateDirectory(directory);

            HeldLock = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // No writable place for the lock. Acting anyway risks every process doing
            // the work, but refusing would mean the verb does nothing at all, which is
            // worse than doing it twice.
            return true;
        }
    }

    /// <summary>The claim's lock file, kept open until the process ends.</summary>
    private static FileStream? HeldLock;
    /// <summary>
    /// Waits until the pending list stops growing, or the cap is reached.
    ///
    /// The list is a file that the sibling processes append to, so its size is a
    /// direct measure of whether more of the selection is still on its way. A fixed
    /// delay cannot know that, and guessing low splits the batch.
    /// </summary>
    private static void WaitForBatchToSettle(string listPath)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var quiet = System.Diagnostics.Stopwatch.StartNew();
        long lastLength = -1;

        while (total.ElapsedMilliseconds < MaxWaitMilliseconds)
        {
            long length = 0;

            try
            {
                var info = new FileInfo(listPath);
                length = info.Exists ? info.Length : 0;
            }
            catch (IOException) { }

            if (length != lastLength)
            {
                lastLength = length;
                quiet.Restart();
            }
            else if (quiet.ElapsedMilliseconds >= SettleMilliseconds &&
                     total.ElapsedMilliseconds >= MinWaitMilliseconds)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Reads the batch, de-duplicating paths that arrived twice.
    ///
    /// The list is deleted only when it is provably unchanged since it was read.
    ///
    /// Deleting it unconditionally destroyed the paths of every process that had
    /// already opened it for append: on Windows a handle keeps writing to the file
    /// it was opened on, so their writes went into a directory entry that no longer
    /// existed and vanished without an error anywhere. Those processes then failed
    /// to claim the lock -- the reader still held it -- and exited, so the selection
    /// silently shrank to whatever had arrived first. A list that grew while it was
    /// being read is left in place for the next batch to pick up.
    /// </summary>
    private static List<string> ReadBatch(string listPath, bool clear)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(listPath))
            {
                return paths;
            }

            long before = new FileInfo(listPath).Length;

            foreach (string line in File.ReadAllLines(listPath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    paths.Add(trimmed);
                }
            }

            long after = new FileInfo(listPath).Length;

            if (clear && after == before)
            {
                File.Delete(listPath);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return paths;
    }

    /// <summary>
    /// Where the pending list lives. Next to the executable first, so a portable
    /// copy keeps its own state; the user profile as a fallback, for the case where
    /// the executable sits somewhere read-only.
    /// </summary>
    private static string QueueDirectory()
    {
        string? exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);

        if (!string.IsNullOrEmpty(exeDirectory))
        {
            string candidate = Path.Combine(exeDirectory, ".roboc-pending");

            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roboc", "pending");
    }
    /// <summary>
    /// Remembers that a selection has just been acted on.
    ///
    /// <b>Why this is needed.</b> Every process in a batch reads the same selection
    /// from the shell. The first one takes the lock and acts; the lock is released when
    /// that process exits, so a later arrival takes it too and acts on the same list a
    /// second time. For 回收 that produced a dialog reading "已回收 0 项，4 项失败" --
    /// every failure real, every item already recycled, and the user told the operation
    /// had failed when it had succeeded.
    ///
    /// The mark is a hash of the selection plus the moment it was handled. A process
    /// that sees the same selection within <see cref="RecentlyHandledMs"/> exits
    /// silently, which is what it should have done in the first place.
    /// </summary>
    private const int RecentlyHandledMs = 15000;

    private static string MarkPath(string verb) => Path.Combine(QueueDirectory(), verb + ".handled");

    /// <summary>True when this exact selection was handled moments ago.</summary>
    public static bool WasJustHandled(string verb, IReadOnlyList<string> paths)
    {
        try
        {
            string mark = MarkPath(verb);

            if (!File.Exists(mark))
            {
                return false;
            }

            string[] lines = File.ReadAllLines(mark);

            if (lines.Length < 2)
            {
                return false;
            }

            if (!long.TryParse(lines[1], out long ticks) ||
                DateTime.UtcNow.Ticks - ticks > TimeSpan.FromMilliseconds(RecentlyHandledMs).Ticks)
            {
                return false;
            }

            return string.Equals(lines[0], Fingerprint(paths), StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Records that this selection has been handled.</summary>
    public static void MarkHandled(string verb, IReadOnlyList<string> paths)
    {
        try
        {
            string mark = MarkPath(verb);
            Directory.CreateDirectory(Path.GetDirectoryName(mark)!);

            File.WriteAllLines(
                mark,
                [Fingerprint(paths), DateTime.UtcNow.Ticks.ToString()]);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A stable identity for a selection. Sorted, so the same set of paths in a
    /// different order is recognised as the same work.
    /// </summary>
    private static string Fingerprint(IReadOnlyList<string> paths)
    {
        var sorted = new List<string>(paths);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', sorted)));

        return Convert.ToHexString(hash);
    }
}