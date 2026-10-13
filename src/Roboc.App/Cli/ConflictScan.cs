using Roboc.Core.Fs;
using Roboc.Core.Interop;

namespace Roboc.App.Cli;

/// <summary>
/// Compares a selection against what is already at the destination, with a time
/// budget.
///
/// <b>Why not check each file in turn.</b> Asking whether a path exists costs about
/// 44 µs, so 20,000 of them cost 0.88 s. Enumerating each destination directory once
/// and comparing in memory costs 0.033 s for the same 20,000, and every lookup after
/// that is a hash probe at 1.8 µs. The same work, thirteen times faster, because the
/// expensive part is the per-call overhead rather than the reading.
///
/// <b>Why the cost is usually nothing.</b> Only the destination directories the
/// sources map onto are examined. Pasting a small folder into a folder holding a
/// million unrelated files looks at one directory. When the destination has no
/// same-named entry at all -- the common case -- a single existence check ends the
/// whole scan.
///
/// <b>Why a budget.</b> A large paste onto a large overlapping tree cannot be summed
/// up instantly, and a dialog that appears after a long pause is worse than one that
/// says "at least N". The scan stops at the deadline and reports what it has, marked
/// as incomplete so the wording can say so rather than implying precision it does not
/// have.
///
/// The scan is read-only, so unlike the copy itself it costs the volume nothing.
/// </summary>
internal static class ConflictScan
{
    /// <summary>What the scan found. <paramref name="Complete"/> is false when the
    /// budget ran out, in which case every count is a lower bound.</summary>
    internal readonly record struct Result(
        int Identical, int Different, int TypeMismatch, long Bytes, int Examined, bool Complete,
        int NewFiles, int NewFolders, int MergedFolders)
    {
        internal int Conflicts => Identical + Different + TypeMismatch;
    }

    private enum Kind
    {
        File,
        Directory,
    }

    private readonly record struct Entry(long Size, long LastWriteTime, Kind Kind);

    /// <summary>
    /// Walks the sources against the destination within <paramref name="budgetMs"/>.
    /// </summary>
    internal static Result Run(string destination, IReadOnlyList<string> sources, int budgetMs)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();

        int identical = 0;
        int different = 0;
        int typeMismatch = 0;
        int examined = 0;
        long bytes = 0;
        int newFiles = 0;
        int newFolders = 0;
        int mergedFolders = 0;
        bool complete = true;

        // Destination directories are enumerated once each and kept, because a source
        // tree revisits the same parent directory for every file it holds.
        var listing = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);

        foreach (string source in sources)
        {
            string leaf = Path.GetFileName(source.TrimEnd('\\', '/'));

            if (leaf.Length == 0)
            {
                continue;
            }

            string target = Path.Combine(destination, leaf);

            // The fast path, and by far the most common one: nothing of this name is
            // there, so nothing inside it can conflict either.
            if (!DirectoryOps.Exists(PathUtil.ToExtended(target)))
            {
                // Nothing of this name is there, so all of it is new.
                //
                // A source that is a file must be counted as one file. Sending it to
                // CountAll enumerated it as a directory, which fails and returns
                // success, so every new file was invisible -- the same mistake that
                // had just been fixed in Walk, made again in the branch written
                // beside it.
                if (!DirectoryOps.IsExistingDirectory(PathUtil.ToExtended(source)))
                {
                    newFiles++;
                    continue;
                }

                if (!CountAll(source, deadline, budgetMs, ref newFiles, ref newFolders))
                {
                    complete = false;
                    break;
                }

                continue;
            }

            if (!DirectoryOps.IsExistingDirectory(PathUtil.ToExtended(source)))
            {
                // A source that is a file. Walking it as a directory enumerated
                // nothing and returned success, so every top-level file was silently
                // absent from the count -- only files nested inside folders were
                // ever reported.
                if (!DirectoryOps.TryGetAttributes(
                        PathUtil.ToExtended(target), out WIN32_FILE_ATTRIBUTE_DATA existing))
                {
                    continue;
                }

                examined++;

                if (existing.IsDirectory)
                {
                    typeMismatch++;
                    continue;
                }

                if (!DirectoryOps.TryGetAttributes(
                        PathUtil.ToExtended(source), out WIN32_FILE_ATTRIBUTE_DATA wanted))
                {
                    continue;
                }

                long sourceSize = ((long)wanted.nFileSizeHigh << 32) | wanted.nFileSizeLow;
                long targetSize = ((long)existing.nFileSizeHigh << 32) | existing.nFileSizeLow;

                bytes += sourceSize;

                if (sourceSize == targetSize &&
                    wanted.ftLastWriteTime.ToLong() == existing.ftLastWriteTime.ToLong())
                {
                    identical++;
                }
                else
                {
                    different++;
                }

                continue;
            }

            mergedFolders++;

            if (!Walk(source, target, listing, ref examined, ref identical, ref different,
                      ref typeMismatch, ref newFiles, ref newFolders, ref bytes, deadline, budgetMs))
            {
                complete = false;
                break;
            }
        }

        return new Result(
            identical, different, typeMismatch, bytes, examined, complete,
            newFiles, newFolders, mergedFolders);
    }

    private static bool Walk(
        string source, string target,
        Dictionary<string, Dictionary<string, Entry>> listing,
        ref int examined, ref int identical, ref int different, ref int typeMismatch,
        ref int newFiles, ref int newFolders,
        ref long bytes, System.Diagnostics.Stopwatch deadline, int budgetMs)
    {
        var entries = new List<FindEntry>(64);

        if (!DirectoryList.Enumerate(source, entries))
        {
            return true;
        }

        Dictionary<string, Entry>? existing = List(target, listing);

        foreach (FindEntry entry in entries)
        {
            // Checked per item rather than once per directory, because a single
            // directory can hold tens of thousands and the budget must be able to
            // interrupt inside it.
            if (deadline.ElapsedMilliseconds > budgetMs)
            {
                return false;
            }

            examined++;
            string childTarget = target + "\\" + entry.Name;

            if (existing is null || !existing.TryGetValue(entry.Name, out Entry found))
            {
                if (entry.IsDirectory)
                {
                    newFolders++;
                }
                else
                {
                    newFiles++;
                }

                continue;
            }

            if (entry.IsDirectory != (found.Kind == Kind.Directory))
            {
                typeMismatch++;
                continue;
            }

            if (entry.IsDirectory)
            {
                if (!Walk(entry.FullPath, childTarget, listing, ref examined, ref identical,
                          ref different, ref typeMismatch, ref newFiles, ref newFolders,
                          ref bytes, deadline, budgetMs))
                {
                    return false;
                }

                continue;
            }

            bytes += entry.Size;

            if (found.Size == entry.Size && found.LastWriteTime == entry.LastWriteTime)
            {
                identical++;
            }
            else
            {
                different++;
            }
        }

        return true;
    }

    /// <summary>Counts a whole subtree as new, stopping at the same deadline.</summary>
    private static bool CountAll(
        string source, System.Diagnostics.Stopwatch deadline, int budgetMs,
        ref int files, ref int folders)
    {
        var stack = new Stack<string>();
        stack.Push(source);

        while (stack.Count > 0)
        {
            if (deadline.ElapsedMilliseconds > budgetMs)
            {
                return false;
            }

            var entries = new List<FindEntry>(64);

            if (!DirectoryList.Enumerate(stack.Pop(), entries))
            {
                continue;
            }

            foreach (FindEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    folders++;
                    stack.Push(entry.FullPath);
                }
                else
                {
                    files++;
                }
            }
        }

        return true;
    }

    /// <summary>Names in a directory, read once and remembered.</summary>
    private static Dictionary<string, Entry>? List(
        string directory, Dictionary<string, Dictionary<string, Entry>> listing)
    {
        if (listing.TryGetValue(directory, out Dictionary<string, Entry>? cached))
        {
            return cached;
        }

        var entries = new List<FindEntry>(64);

        if (!DirectoryList.Enumerate(PathUtil.ToExtended(directory), entries))
        {
            listing[directory] = null!;
            return null;
        }

        var map = new Dictionary<string, Entry>(entries.Count, StringComparer.OrdinalIgnoreCase);

        foreach (FindEntry entry in entries)
        {
            map[entry.Name] = new Entry(
                entry.Size, entry.LastWriteTime,
                entry.IsDirectory ? Kind.Directory : Kind.File);
        }

        listing[directory] = map;
        return map;
    }
}
