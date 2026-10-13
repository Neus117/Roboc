using Roboc.Core.Fs;

namespace Roboc.Core.Shell;

/// <summary>
/// Where recycled items came from, kept in one file per bin instead of a text file
/// beside every item.
///
/// <b>Why not one file beside each item.</b> That worked, and it had the property
/// that matters most -- the record could not be separated from the thing it
/// described. It also meant the bin filled up with a text file per item, which is
/// clutter in a folder the user is expected to look at.
///
/// <b>Why two copies.</b> Consolidating into one file removes that clutter and
/// introduces a single point of failure: a flat bin no longer encodes the original
/// path anywhere else, so losing the index makes every item in it unrestorable --
/// present but unreachable. The index is therefore written twice: inside the bin,
/// where it stays with the data, and beside the executable, so moving the program
/// does not strand a bin on some other volume. Reading merges both, so whichever
/// survives is enough.
///
/// <b>Format.</b> Tab-separated text, one record per line, so it can be read and
/// repaired by hand. A path containing a tab would break the format; that is
/// accepted rather than guessed at, and such a record is skipped on load instead of
/// being misread.
/// </summary>
public static class BinIndex
{
    /// <summary>Name of the copy kept inside the bin, hidden from view.</summary>
    public const string BinFileName = ".roboc-index.tsv";

    private const string Header = "# Roboc 回收索引　卷\t名称\t原路径\t回收时间";

    private readonly record struct Entry(string Volume, string Name, string Original, string Time);

    /// <summary>
    /// Records that an item now lives in the bin under a given name.
    ///
    /// Returns false when neither copy could be written, which the caller treats as
    /// a failed recycle and undoes -- an item whose origin is unrecorded cannot be
    /// put back, and that state must not be allowed to exist.
    /// </summary>
    public static bool Add(string binRoot, string storedExtended, string originalExtended)
    {
        string volume = VolumeOf(binRoot);
        string name = PathUtil.GetFileName(storedExtended);
        string original = PathUtil.ToDisplay(originalExtended);

        List<Entry> entries = Load(binRoot);
        entries.RemoveAll(e => Matches(e, volume, name));
        entries.Add(new Entry(volume, name, original, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

        return Save(binRoot, entries);
    }

    /// <summary>Forgets an item, because it has left the bin.</summary>
    public static void Remove(string binRoot, string storedExtended)
    {
        string volume = VolumeOf(binRoot);
        string name = PathUtil.GetFileName(storedExtended);

        List<Entry> entries = Load(binRoot);

        if (entries.RemoveAll(e => Matches(e, volume, name)) == 0)
        {
            return;
        }

        Save(binRoot, entries);
    }

    /// <summary>
    /// The original path of a stored item, or null when nothing is recorded.
    /// </summary>
    public static string? Lookup(string binRoot, string storedExtended)
    {
        string volume = VolumeOf(binRoot);
        string name = PathUtil.GetFileName(storedExtended);

        foreach (Entry entry in Load(binRoot))
        {
            if (Matches(entry, volume, name))
            {
                return PathUtil.ToExtended(entry.Original);
            }
        }

        return null;
    }

    /// <summary>Rewrites both copies from a set of records. Used by migration.</summary>
    public static bool Replace(string binRoot, IEnumerable<(string StoredName, string OriginalPath)> items)
    {
        string volume = VolumeOf(binRoot);
        List<Entry> entries = Load(binRoot);

        foreach ((string name, string original) in items)
        {
            entries.RemoveAll(e => Matches(e, volume, name));
            entries.Add(new Entry(
                volume, name, original, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        return Save(binRoot, entries);
    }

    // ------------------------------------------------------------------ storage

    /// <summary>The copy that lives beside the executable.</summary>
    private static string? ProgramCopyPath()
    {
        try
        {
            string? directory = Path.GetDirectoryName(Environment.ProcessPath);

            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            return Path.Combine(directory, ".roboc", "bin-index.tsv");
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Reads both copies and merges them.
    ///
    /// Merged rather than chosen between: the two can legitimately differ after an
    /// interrupted write, and a record present in either one is a record that can
    /// still save an item. The later timestamp wins when both describe one name.
    /// </summary>
    private static List<Entry> Load(string binRoot)
    {
        var merged = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Copies(binRoot))
        {
            foreach (Entry entry in ReadOne(path))
            {
                string key = entry.Volume + "|" + entry.Name;

                if (!merged.TryGetValue(key, out Entry existing) ||
                    string.CompareOrdinal(entry.Time, existing.Time) >= 0)
                {
                    merged[key] = entry;
                }
            }
        }

        return [.. merged.Values];
    }

    /// <summary>
    /// Writes both copies.
    ///
    /// The copy inside a bin gets only that bin's records; the copy beside the
    /// executable gets all of them, because it is the one that has to survive the
    /// program being moved. Writing the merged set into every bin would leave each
    /// bin describing items on volumes that have nothing to do with it, growing
    /// without bound as other volumes are used.
    /// </summary>
    private static bool Save(string binRoot, List<Entry> entries)
    {
        string volume = VolumeOf(binRoot);
        bool any = false;
        bool first = true;

        foreach (string path in Copies(binRoot))
        {
            List<Entry> forThisFile = first
                ? entries
                : [.. entries.Where(e => string.Equals(
                       e.Volume, volume, StringComparison.OrdinalIgnoreCase))];

            first = false;

            if (WriteOne(path, forThisFile))
            {
                any = true;
            }
        }

        return any;
    }

    private static IEnumerable<string> Copies(string binRoot)
    {
        string? program = ProgramCopyPath();

        if (program is not null)
        {
            yield return program;
        }

        yield return PathUtil.ToDisplay(binRoot) + "\\" + BinFileName;
    }

    private static List<Entry> ReadOne(string path)
    {
        var entries = new List<Entry>();

        try
        {
            if (!File.Exists(path))
            {
                return entries;
            }

            foreach (string line in File.ReadLines(path, System.Text.Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] parts = line.Split('\t');

                // A malformed line is skipped, never guessed at: a record read wrongly
                // would send an item to the wrong place, which is worse than not
                // knowing where it goes.
                if (parts.Length >= 3 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    entries.Add(new Entry(
                        parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : string.Empty));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return entries;
    }

    private static bool WriteOne(string path, List<Entry> entries)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var text = new System.Text.StringBuilder();
            text.AppendLine(Header);

            foreach (Entry entry in entries)
            {
                text.Append(entry.Volume).Append('\t')
                    .Append(entry.Name).Append('\t')
                    .Append(entry.Original).Append('\t')
                    .AppendLine(entry.Time);
            }

            // Written whole to a temporary file and moved into place, so a reader
            // never sees a half-written index and an interrupted write cannot destroy
            // the only record of where things belong.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);

            if (path.EndsWith(BinFileName, StringComparison.OrdinalIgnoreCase))
            {
                Interop.Kernel32.SetFileAttributes(path, Interop.NativeConst.FILE_ATTRIBUTE_HIDDEN);
            }

            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // -------------------------------------------------------------------- keys

    private static string VolumeOf(string binRoot)
    {
        string display = PathUtil.ToDisplay(binRoot);
        return display.Length > 0 ? char.ToUpperInvariant(display[0]).ToString() : "?";
    }

    private static bool Matches(Entry entry, string volume, string name)
        => string.Equals(entry.Volume, volume, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase);
}
