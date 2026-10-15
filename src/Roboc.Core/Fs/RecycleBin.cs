using Roboc.Core.Diagnostics;
using Roboc.Core.Delete;
using Roboc.Core.Interop;
using Roboc.Core.Shell;
using Roboc.Core.Model;

namespace Roboc.Core.Fs;

/// <summary>
/// A recycle bin of this tool's own, kept separate from Explorer's.
///
/// Mixing into the shell's own bin means mixing into its bookkeeping, its size
/// accounting, its per-user quotas and its "empty" semantics. Keeping a separate
/// bin makes every one of those this tool's own decision and leaves the shell's
/// bin untouched.
///
/// <b>Location and naming.</b> A bin is created on the volume that holds the
/// discarded item, at <c>&lt;volume&gt;:\Roboc_bin\</c>. It is created when
/// first needed and removed once it is empty and space is reclaimed. The name is
/// plain text because the whole point is that a user can open it in Explorer,
/// understand it, and move something back out by hand.
///
/// <b>Layout.</b> Inside, the original path is mirrored with the drive colon
/// removed, so the stored location *is* the restore instruction:
///
/// <code>
///   D:\Coding\workspace\foo.txt
///     -> D:\Roboc_bin\D\Coding\workspace\foo.txt
/// </code>
///
/// No index or database is needed: the restore target can be read straight off
/// the storage path, and nothing can drift out of sync with a file the user moved
/// by hand.
///
/// <b>The ambiguity, and how it is resolved.</b> A mirrored tree cannot by itself
/// distinguish a recycled *directory* from a scaffolding directory that exists
/// only because some deeper item needed its path. A recycled directory therefore
/// carries a marker file, <see cref="MarkerFileName"/>, holding its original path
/// and the time it was discarded. Files need no marker, because scaffolding is
/// only ever made of directories, so every file in the tree is a discarded item.
/// The marker doubles as a note the user can read in Explorer.
///
/// <b>Cost.</b> Within one volume this is a single rename, so discarding a
/// million-file directory is as cheap as discarding one file. There is no copy
/// and no delete pass: the bytes never move. Across volumes a rename is impossible
/// and the caller is told so rather than silently falling back to a copy that
/// would behave nothing like the user expects.
/// </summary>
public static class RecycleBin
{
    /// <summary>Name of the bin directory at a volume root.</summary>
    public const string FolderName = "Roboc_bin";

    /// <summary>Marker written inside a discarded directory, identifying it as an
    /// item rather than as scaffolding, and telling the user where it came from.</summary>
    public const string MarkerFileName = "Roboc.回收信息.txt";

    /// <summary>
    /// The "original location" line, in both languages, because the marker is written in
    /// one language and can be read back in the other.
    ///
    /// The program lets the user switch languages at any time, and the marker files
    /// already sitting in the recycle bin do not change when they do. A reader that
    /// understood only the current language would silently fail to find the original path
    /// of everything recycled before the switch, and the item would become unrestorable
    /// with nothing to explain why. Accepting both costs one extra comparison.
    ///
    /// A static readonly field is safe here, unlike the label tables elsewhere in this
    /// project: these are constants of a file format, not user-facing wording. They must
    /// not vary with the language setting -- that is exactly what makes the format
    /// readable across a language change.
    /// </summary>
    private static readonly string[] OriginalPathPrefixes =
    [
        "原位置：",
        "Original location: ",
    ];

    /// <summary>
    /// Suffix of the file that records where an item came from.
    ///
    /// The bin used to mirror the source path, which made the path itself the record
    /// and needed no bookkeeping -- but it also meant the bin's shape was tied to the
    /// shape of the tree it came from. Scaffolding directories became indistinguishable
    /// from discarded ones, so selecting one gave nothing to restore to. The sidecar
    /// separates the two concerns: the item is stored flat, and a readable text file
    /// beside it says where it belongs.
    /// </summary>
    public const string SidecarSuffix = ".Roboc回收信息.txt";

    /// <summary>
    /// Environment variable that relocates every bin to one directory.
    ///
    /// The default is a bin per volume, which is what makes discarding an item a
    /// rename instead of a copy. This exists for the cases where that is not
    /// wanted -- a user who prefers one place to look, or a locked-down machine
    /// where nothing may be created at a volume root.
    /// </summary>
    public const string RootOverrideVariable = "ROBOC_BIN_ROOT";

    /// <summary>Extended path of the bin that serves the volume holding <paramref name="extendedPath"/>.</summary>
    public static string? RootFor(string extendedPath)
    {
        string? overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);

        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return PathUtil.ToExtended(overridden);
        }

        string volumeRoot;

        try
        {
            volumeRoot = PathUtil.GetVolumeRoot(extendedPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // A UNC volume has no single place to put a per-volume bin that the user
        // could find, so this refuses rather than inventing one.
        if (volumeRoot.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            volumeRoot.Length < 6 || volumeRoot[5] != ':')
        {
            return null;
        }

        return volumeRoot + FolderName;
    }

    /// <summary>
    /// Whether a path already sits inside a Roboc bin, on this drive or any
    /// other. Checked by name rather than by comparing against a computed root,
    /// because the bin can be relocated by ROBOC_BIN_ROOT and an item could
    /// have been discarded before that changed.
    /// </summary>
    public static bool IsInsideAnyBin(string extendedPath)
    {
        // By name, which catches a bin on a drive the path no longer resolves to.
        foreach (string part in extendedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(FolderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // And by the root actually in force, which is what catches a relocated bin.
        // The name check alone missed that case entirely: with the root overridden
        // to a folder called something else, an item already inside it was accepted
        // for recycling a second time.
        string? root = RootFor(extendedPath);

        return root is not null &&
               extendedPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where an item would be stored, before any collision suffix.</summary>
    public static string? TargetFor(string extendedSource, string binRoot)
    {
        string volumeRoot = PathUtil.GetVolumeRoot(extendedSource);

        if (volumeRoot.Length < 6 || volumeRoot[5] != ':')
        {
            return null;
        }

        char driveLetter = volumeRoot[4];
        string relative = extendedSource[volumeRoot.Length..].TrimStart('\\');

        if (relative.Length == 0)
        {
            return null;
        }

        return $@"{binRoot}\{driveLetter}\{relative}";
    }

    /// <summary>Where an item's sidecar lives, given the item's own stored path.</summary>
    public static string SidecarFor(string storedExtended)
        => storedExtended + SidecarSuffix;

    /// <summary>
    /// Records where a stored item came from.
    ///
    /// Readable on purpose: opening it in Explorer answers "where did this come from"
    /// without this program, which matters because the bin is also maintained by hand.
    /// </summary>
    public static bool TryWriteSidecar(string storedExtended, string originalSource)
    {
        try
        {
            string text =
                Lang.Pick("此项目由 Roboc 回收。\r\n", "Recycled by Roboc.\r\n") +
                Lang.Pick("原位置：", "Original location: ") + PathUtil.ToDisplay(originalSource) + "\r\n" +
                Lang.Pick("回收时间：", "Recycled at:      ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                "\r\n" +
                Lang.Pick("取回方法：在资源管理器中选中此项目，右键 Roboc → 回收。\r\n", "To restore: select this item in Explorer and choose Roboc -> Recycle.\r\n") +
                Lang.Pick("也可以直接把这个项目移回上面的原位置。\r\n", "You can also move it back to the original location shown above.\r\n") +
                Lang.Pick("删除此文件会导致回收站无法判断该项目应还原到哪里。\r\n", "Deleting this file leaves the bin unable to tell where the item belongs.\r\n");

            File.WriteAllText(
                PathUtil.ToDisplay(SidecarFor(storedExtended)), text, System.Text.Encoding.UTF8);

            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Removes an item's sidecar.
    ///
    /// Called once the item has left the bin. Leaving it behind produced a file that
    /// describes an item which is no longer there -- harmless to read, but it makes
    /// the bin accumulate litter and makes "which of these are real" harder to answer
    /// by looking.
    /// </summary>
    public static void ClearRecord(string binRoot, string storedExtended)
    {
        BinIndex.Remove(binRoot, storedExtended);
        RemoveSidecar(storedExtended);
    }

    /// <summary>
    /// True for the files a bin keeps for itself rather than for the user.
    ///
    /// The index and the markers are how a bin describes its contents; they are not
    /// contents. Selecting one and pressing 回收 asks to restore something that was never
    /// recycled, and the restore correctly refuses -- but the refusal arrives as a dialog
    /// in the middle of a batch, and a batch that stops to report a non-item is a batch the
    /// user has to dismiss and start again.
    ///
    /// Reported rather than hidden, because a file that silently ignores a command is
    /// worse than one that says it cannot.
    /// </summary>
    public static bool IsScaffolding(string extendedPath)
    {
        // EndsWith on the whole path, not GetFileName.
        //
        // The paths reaching here are extended (\\?\D:\...) and GetFileName was returning
        // something that matched neither name, so the index file fell through to the
        // recycling path and was refused with "already in the bin". EndsWith is what the rest
        // of this file already uses to recognise these two names, and agreement with the code
        // that writes them matters more than elegance here.
        string trimmed = PathUtil.TrimTrailingSeparator(extendedPath);

        return trimmed.EndsWith(BinIndex.BinFileName, StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(MarkerFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the marker a discarded directory carries.
    ///
    /// Called once the directory is back where it came from. The marker says "this folder is
    /// in the recycle bin, and here is where it belongs" -- true while it is in the bin, and
    /// false the moment it is not. Left behind, it is a small text file appearing inside a
    /// folder the user has just restored, explaining that the folder is in a recycle bin it
    /// is no longer in.
    ///
    /// Failures are swallowed: a marker that cannot be deleted is untidy, and refusing to
    /// complete a restore over one would be a poor trade.
    /// </summary>
    public static void RemoveMarker(string restoredExtended)
    {
        string target = PathUtil.ToDisplay(restoredExtended + "\\" + MarkerFileName);

        if (!DirectoryOps.IsExistingDirectory(restoredExtended))
        {
            return;
        }

        try
        {
            File.Delete(target);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void RemoveSidecar(string storedExtended)
    {
        try
        {
            string path = PathUtil.ToDisplay(SidecarFor(storedExtended));

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    /// <summary>Reads the original path out of a sidecar, or null when there is none.</summary>
    public static string? ReadSidecar(string storedExtended)
    {
        try
        {
            string path = PathUtil.ToDisplay(SidecarFor(storedExtended));

            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadLines(path))
            {
                // Both languages, always.
                //
                // The sidecar is written in whichever language was in force when the item
                // was recycled, and read back later -- possibly after the user has
                // switched languages, which this program now lets them do at any time. A
                // reader that only understood the current language would fail to find the
                // original path of everything recycled before the switch, and the item
                // would become unrestorable without any error to explain why.
                //
                // Reading both costs one extra comparison and removes the ordering
                // dependency completely.
                string? value = null;

                foreach (string prefix in OriginalPathPrefixes)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        value = line[prefix.Length..].Trim();
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(value))
                {
                    return PathUtil.ToExtended(value);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    /// <summary>
    /// Recovers the original path of a stored item.
    ///
    /// The sidecar is authoritative. The mirrored layout is still read as a fallback,
    /// and deliberately so: a sidecar that failed to write, or an item recycled before
    /// the flat layout existed, would otherwise be an item that can never be put back
    /// -- a file that is present but unreachable, which is the one outcome a bin must
    /// never produce.
    /// </summary>
    public static string? OriginalPathOf(string storedExtended, string binRoot)
    {
        // The index is authoritative. Every other source below is a fallback for
        // records written before it existed, or for a bin whose index was lost -- an
        // item that is present but unreachable is the one outcome a bin must never
        // produce, so each older mechanism stays readable rather than being removed.
        string? fromIndex = BinIndex.Lookup(binRoot, storedExtended);

        if (fromIndex is not null)
        {
            return fromIndex;
        }

        string? fromSidecar = ReadSidecar(storedExtended);

        if (fromSidecar is not null)
        {
            return fromSidecar;
        }

        // A directory recycled by an older build carries the marker inside it.
        if (DirectoryOps.IsExistingDirectory(storedExtended))
        {
            string? fromMarker = ReadMarkerOriginal(storedExtended);

            if (fromMarker is not null && fromMarker.Length > 0)
            {
                return PathUtil.ToExtended(fromMarker);
            }
        }

        string prefix = binRoot + "\\";

        if (!storedExtended.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = storedExtended[prefix.Length..];

        if (rest.Length < 3 || rest[1] != '\\')
        {
            return null;
        }

        return $@"\\?\{rest[0]}:{rest[1..]}";
    }

    // ------------------------------------------------------------------ storing

    /// <summary>
    /// Picks the name an item will be stored under.
    ///
    /// <paramref name="alreadyStored"/> is true when this exact original path is
    /// already in the bin, which the caller refuses: the point of the bin is that an
    /// item can be put back, and two copies of one path cannot both go home.
    /// </summary>
    private static string? FreeTargetFor(string source, string binRoot, out bool alreadyStored)
    {
        alreadyStored = false;

        string leaf = PathUtil.GetFileName(source);

        if (leaf.Length == 0)
        {
            return null;
        }

        string display = PathUtil.ToDisplay(source);

        for (int n = 1; n < 10_000; n++)
        {
            string name = n == 1 ? leaf : $"{StripExtension(leaf)} ({n}){ExtensionOf(leaf)}";
            string candidate = binRoot + "\\" + name;

            if (!DirectoryOps.Exists(candidate))
            {
                return candidate;
            }

            // Occupied. Whose is it?
            string? owner = BinIndex.Lookup(binRoot, candidate) ?? ReadSidecar(candidate);

            if (owner is not null && string.Equals(
                    PathUtil.ToDisplay(owner), display, StringComparison.OrdinalIgnoreCase))
            {
                alreadyStored = true;
                return null;
            }
        }

        return null;
    }

    private static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : string.Empty;
    }

    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// Moves one item into the bin. The item may be a file or a directory, and a
    /// directory of any size costs the same as a file: it is a rename.
    /// </summary>
    public static bool TryPut(string extendedSource, out string storedPath, out int error)
    {
        storedPath = string.Empty;
        error = 0;

        string source = PathUtil.TrimTrailingSeparator(extendedSource);

        // An item that is already in a bin must not be binned again: the mirror
        // path would gain a level every time, producing 回收站\D\回收站\D\... and a
        // tree that restoration can no longer read.
        if (IsInsideAnyBin(source))
        {
            error = NativeConst.ERROR_INVALID_PARAMETER;
            return false;
        }

        string? binRoot = RootFor(source);
        if (binRoot is null)
        {
            error = NativeConst.ERROR_INVALID_DRIVE;
            return false;
        }

        if (!CreateDeep(binRoot, out error))
        {
            return false;
        }

        // Flat, under a name derived from the item itself. A name already in use is
        // only a conflict when it belongs to the same original path -- that is the
        // case the bin refuses, because two copies of one path make "put it back"
        // ambiguous. Two different paths that happen to share a name are ordinary and
        // simply get a numbered name, with the sidecar keeping them apart.
        string? target = FreeTargetFor(source, binRoot, out bool alreadyStored);

        if (alreadyStored)
        {
            error = NativeConst.ERROR_ALREADY_EXISTS;
            return false;
        }

        if (target is null)
        {
            error = NativeConst.ERROR_INVALID_NAME;
            return false;
        }

        if (Kernel32.MoveFileEx(source, target, 0) == 0)
        {
            error = NativeError.LastWin32();
            return false;
        }

        // Recorded after the move, and the move is undone if it fails.
        //
        // The index is now the only thing that says where the item belongs -- the
        // flat layout does not encode it anywhere else -- so an item missing from it
        // can never be restored. Rolling back keeps that state from ever existing.
        if (!BinIndex.Add(binRoot, target, source))
        {
            Kernel32.MoveFileEx(target, source, 0);
            error = NativeConst.ERROR_ALREADY_EXISTS;
            return false;
        }

        // A recycled directory also gets a marker written inside it.
        //
        // WriteMarker existed from the first commit but was never called, which is how a
        // folder could sit in the bin, complete and correctly indexed, that the bin then
        // reported as empty. The listing no longer depends on the marker -- the index is
        // authoritative and Walk now consults it -- so this is no longer load-bearing for
        // correctness. It is written for two other reasons.
        //
        // It is the fallback if the index is ever lost: OriginalPathOf still reads it.
        // And it is written for the person who opens the folder to find out what it is
        // doing in their recycle bin, which the index cannot tell them.
        //
        // Not rolled back on failure, unlike the index above: the item is already
        // restorable through the index, so a missing marker is a loss of convenience
        // rather than of the item.
        if (DirectoryOps.IsExistingDirectory(target))
        {
            WriteMarker(target, source);
        }

        storedPath = target;
        return true;
    }

    /// <summary>
    /// Creates every level of a path.
    ///
    /// CreateDirectoryW makes exactly one level, so the mirrored path -- which is
    /// as deep as the original was -- fails with ERROR_PATH_NOT_FOUND the moment
    /// more than the last component is missing.
    /// </summary>
    private static bool CreateDeep(string extendedDirectory, out int error)
    {
        error = 0;

        if (DirectoryOps.Exists(extendedDirectory))
        {
            return true;
        }

        string volumeRoot = PathUtil.GetVolumeRoot(extendedDirectory);
        string rest = extendedDirectory[volumeRoot.Length..];
        string current = volumeRoot.TrimEnd('\\');

        foreach (string part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current + "\\" + part;

            if (!DirectoryOps.CreateIfMissing(current, out error))
            {
                return false;
            }
        }

        return true;
    }



    private static void WriteMarker(string storedDirectory, string originalSource)
    {
        try
        {
            string marker = storedDirectory + "\\" + MarkerFileName;
            string text =
                Lang.Pick("此目录由 Roboc 回收。\r\n", "Recycled by Roboc.\r\n") +
                Lang.Pick("原位置：", "Original location: ") + PathUtil.ToDisplay(originalSource) + "\r\n" +
                Lang.Pick("回收时间：", "Recycled at:      ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                "\r\n" +
                Lang.Pick("还原方法：把此目录移回上面的原位置即可。\r\n", "To restore: move this folder back to the original location shown above.\r\n") +
                Lang.Pick("此文件是识别标记，删除它会导致回收站无法识别此目录。\r\n", "This file is the marker. Deleting it leaves the bin unable to recognise the folder.\r\n");

            File.WriteAllText(PathUtil.ToDisplay(marker), text, System.Text.Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // --------------------------------------------------------------- inspecting

    /// <summary>A discarded item found in a bin.</summary>
    public readonly record struct Item(string StoredPath, string OriginalPath, bool IsDirectory, long Bytes, DateTime DiscardedAt);

    /// <summary>
    /// Converts a bin from the mirrored layout to the flat one. Returns how many
    /// items were moved.
    ///
    /// <b>Why it is safe to run repeatedly.</b> Every step is a same-volume rename
    /// plus a sidecar, and an item that already has a sidecar is left alone. Running
    /// it twice, or interrupting it, leaves a bin that is partly flat and partly
    /// mirrored -- which reads correctly either way, because the original path of an
    /// item is looked up in the sidecar first and reconstructed from the mirrored
    /// path only when there is none.
    ///
    /// <b>Why it only ever moves.</b> Nothing is deleted except scaffolding
    /// directories once they are empty, and an item is moved only after its sidecar
    /// has been accepted -- if the write fails the move is undone, so the bin can
    /// never hold an item that cannot be put back.
    /// </summary>
    public static int Migrate(string binRoot)
    {
        if (!DirectoryOps.Exists(binRoot))
        {
            return 0;
        }

        int moved = 0;

        try
        {
            foreach (string entry in Directory.GetFileSystemEntries(PathUtil.ToDisplay(binRoot)))
            {
                string extended = PathUtil.ToExtended(entry);

                if (File.Exists(entry))
                {
                    continue;   // Already flat, or a sidecar.
                }

                // A flat item has a sidecar beside it. A directory without one is
                // either scaffolding from the mirrored layout or a directory recycled
                // before sidecars existed, and the marker tells the two apart.
                if (File.Exists(PathUtil.ToDisplay(SidecarFor(extended))))
                {
                    continue;
                }

                if (HasMarker(extended))
                {
                    moved += Adopt(binRoot, extended, ReadMarkerOriginal(extended));
                    continue;
                }

                moved += MigrateTree(binRoot, extended);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        moved += AbsorbSidecars(binRoot);
        RemoveOrphanSidecars(binRoot);
        PruneEmpty(binRoot);
        return moved;
    }

    /// <summary>Walks mirrored scaffolding, adopting every discarded item it holds.</summary>
    private static int MigrateTree(string binRoot, string extendedDirectory)
    {
        int moved = 0;

        try
        {
            foreach (string entry in Directory.GetFileSystemEntries(PathUtil.ToDisplay(extendedDirectory)))
            {
                string extended = PathUtil.ToExtended(entry);

                if (File.Exists(entry))
                {
                    // In the mirrored layout every file is a discarded item: the
                    // scaffolding is built only out of directories.
                    moved += Adopt(binRoot, extended, MirrorArithmetic(extended, binRoot));
                    continue;
                }

                if (HasMarker(extended))
                {
                    moved += Adopt(binRoot, extended, ReadMarkerOriginal(extended));
                    continue;
                }

                moved += MigrateTree(binRoot, extended);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return moved;
    }

    /// <summary>The old way of recovering an original path, kept for migration.</summary>
    private static string? MirrorArithmetic(string storedExtended, string binRoot)
    {
        string prefix = binRoot + "\\";

        if (!storedExtended.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = storedExtended[prefix.Length..];

        if (rest.Length < 3 || rest[1] != '\\')
        {
            return null;
        }

        return $@"\\?\{rest[0]}:{rest[1..]}";
    }

    /// <summary>
    /// Gives a mirrored item a flat home and a sidecar. The item stays where it is if
    /// either step fails, so a failed migration costs nothing but a retry.
    /// </summary>
    private static int Adopt(string binRoot, string extended, string? originalSource)
    {
        if (originalSource is null || originalSource.Length == 0)
        {
            return 0;
        }

        string? target = FreeTargetFor(originalSource, binRoot, out bool alreadyStored);

        // Already in the bin under a different name: this is a second copy of one
        // path, which the bin refuses rather than keeping. It is left where it is,
        // readable by the fallback, instead of being destroyed.
        if (target is null || alreadyStored)
        {
            return 0;
        }

        if (Kernel32.MoveFileEx(extended, target, 0) == 0)
        {
            return 0;
        }

        if (!TryWriteSidecar(target, originalSource))
        {
            Kernel32.MoveFileEx(target, extended, 0);
            return 0;
        }

        return 1;
    }

    /// <summary>
    /// Folds sidecar files into the index and removes them.
    ///
    /// This is what stops the bin filling up with a text file per item. The record is
    /// added to the index before the sidecar is deleted, so an interruption leaves the
    /// information in at least one place rather than in neither.
    /// </summary>
    private static int AbsorbSidecars(string binRoot)
    {
        int absorbed = 0;
        string display = PathUtil.ToDisplay(binRoot);

        try
        {
            foreach (string file in Directory.GetFiles(display))
            {
                if (!file.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string owner = file[..^SidecarSuffix.Length];

                if (!File.Exists(owner) && !Directory.Exists(owner))
                {
                    continue;   // Orphan; RemoveOrphanSidecars deals with it.
                }

                string? original = ReadSidecar(PathUtil.ToExtended(owner));

                if (original is null)
                {
                    continue;
                }

                if (BinIndex.Add(binRoot, PathUtil.ToExtended(owner), original))
                {
                    File.Delete(file);
                    absorbed++;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return absorbed;
    }

    /// <summary>Deletes sidecars whose item is gone.</summary>
    private static void RemoveOrphanSidecars(string binRoot)
    {
        try
        {
            foreach (string entry in Directory.GetFiles(PathUtil.ToDisplay(binRoot)))
            {
                if (!entry.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string owner = entry[..^SidecarSuffix.Length];

                if (!File.Exists(owner) && !Directory.Exists(owner))
                {
                    File.Delete(entry);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Moves everything under <paramref name="source"/> into <paramref name="target"/>,
    /// merging where the two overlap.
    ///
    /// <b>Why merging rather than refusing.</b> Restoring a folder used to be refused
    /// outright whenever anything with that name was already at the original location. That
    /// is the wrong answer for the common case: a user recycles a folder, works in the
    /// original location for a while, and wants the folder back. Refusing makes them
    /// resolve the overlap by hand -- for a folder with two hundred files, by hand two
    /// hundred times.
    ///
    /// <b>Where the recursion stops.</b> An entry whose destination does not exist is moved
    /// straight across. Where both sides are directories, the pair recurses. Everything else
    /// is a conflict and is collected rather than resolved, because the decision belongs to
    /// the user and a decision made two hundred levels deep is one they never see.
    ///
    /// <b>Returns having moved what it could.</b> A conflict part way through does not undo
    /// the moves already made, and must not: those files are not in conflict with anything.
    /// </summary>
    private static void MergeInto(
        string source, string target, List<(string Source, string Target)> conflicts, ref int moved)
    {
        var entries = new List<FindEntry>(64);

        if (!DirectoryList.Enumerate(source, entries))
        {
            return;
        }

        foreach (FindEntry entry in entries)
        {
            if (entry.IsReparsePoint)
            {
                continue;
            }

            // The marker travels with the folder it describes, but only while the folder is
            // whole. Once its contents are being merged into an existing folder, the marker
            // would claim the merged result is a discarded item -- and the bin would then
            // offer to take back a folder that is half the user's own work.
            if (!entry.IsDirectory && string.Equals(entry.Name, MarkerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = target + "\\" + entry.Name;

            if (!DirectoryOps.Exists(destination))
            {
                if (Kernel32.MoveFileEx(entry.FullPath, destination, 0) != 0)
                {
                    moved++;
                }

                continue;
            }

            if (entry.IsDirectory && DirectoryOps.IsExistingDirectory(destination))
            {
                MergeInto(entry.FullPath, destination, conflicts, ref moved);
                continue;
            }

            conflicts.Add((entry.FullPath, destination));
        }
    }

    /// <summary>
    /// The entries a merge found in conflict, as (source, destination) pairs.
    ///
    /// Exposed so the caller can put the decision to the user with the same dialog the
    /// paste path uses, rather than inventing a second vocabulary for the same question.
    /// </summary>
    public static List<(string Source, string Target)> MergeConflicts(
        string source, string target, out int moved)
    {
        var conflicts = new List<(string Source, string Target)>();
        int count = 0;

        MergeInto(source, target, conflicts, ref count);
        moved = count;

        return conflicts;
    }

    /// <summary>
    /// Moves one more item into place, replacing whatever is there.
    ///
    /// Used only after the user has said to replace. The destination is removed first
    /// because MoveFileEx onto an existing file fails -- and it is removed with the fast
    /// deleter so that replacing a directory with a file, or the other way round, works the
    /// same as replacing like with like.
    /// </summary>
    public static bool ReplaceWith(string source, string target)
    {
        if (DirectoryOps.Exists(target))
        {
            if (!FastDeleter.TryDelete(
                    target, DirectoryOps.IsExistingDirectory(target), out _, out _, out _))
            {
                return false;
            }
        }

        return Kernel32.MoveFileEx(source, target, 0) != 0;
    }
    public static bool HasItems(string binRoot)
    {
        try
        {
            string display = PathUtil.ToDisplay(binRoot);

            if (!Directory.Exists(display))
            {
                return false;
            }

            foreach (string entry in Directory.GetFileSystemEntries(display))
            {
                if (!entry.EndsWith(BinIndex.BinFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    /// <summary>
    /// Lists every item in one bin, with its size.
    ///
    /// A file is an item; a directory is an item if it carries the marker. Anything
    /// else in the tree is scaffolding. Sizes are computed by walking, which is the
    /// only way to know what reclaiming would actually free.
    /// </summary>
    public static List<Item> List(string binRoot)
    {
        var items = new List<Item>();

        if (!DirectoryOps.Exists(binRoot))
        {
            return items;
        }

        Walk(binRoot, binRoot, items);
        return items;
    }

    private static void Walk(string directory, string binRoot, List<Item> items)
    {
        var entries = new List<FindEntry>(64);

        if (!DirectoryList.Enumerate(directory, entries))
        {
            return;
        }

        // The bin root is the container, never one of its contents.
        //
        // Every discarded directory carries its marker inside itself, so a marker sitting
        // at the top of the bin belongs to no item -- it is a marker file that was itself
        // recycled, like any other file. Read as a marker it made the whole bin look like a
        // single discarded folder: the listing offered the entire recycle bin back as one
        // item, at the original path of a text file inside it. One stray file was enough.
        //
        // Below the root the check stands, because that is exactly what it is for.
        bool atRoot = string.Equals(directory, binRoot, StringComparison.OrdinalIgnoreCase);

        foreach (FindEntry entry in entries)
        {
            if (entry.IsReparsePoint)
            {
                continue;
            }

            if (!entry.IsDirectory)
            {
                string? original = OriginalPathOf(entry.FullPath, binRoot);
                if (original is not null)
                {
                    items.Add(new Item(
                        entry.FullPath, original, false, entry.Size, ToLocalTime(entry.LastWriteTime)));
                }

                continue;
            }

            if (!atRoot && HasMarker(entry.FullPath))
            {
                // A discarded directory: one item, not a container to descend into.
                items.Add(new Item(
                    entry.FullPath,
                    ReadMarkerOriginal(entry.FullPath) ?? OriginalPathOf(entry.FullPath, binRoot) ?? entry.FullPath,
                    true,
                    MeasureTree(entry.FullPath),
                    ToLocalTime(entry.LastWriteTime)));

                continue;
            }

            // A directory the index knows about is an item even without a marker.
            //
            // This is the defect that made "recycle a folder, then open the bin" report an
            // empty bin. OriginalPathOf treats the index as authoritative and the marker as
            // a fallback for records written before it existed -- but Walk only ever asked
            // about the marker, so a recycled directory without one fell through to the
            // recursion below, was treated as a container, and its contents were then tested
            // one by one against the index, where none of them appear. The folder sat in the
            // bin with every file intact while the program said there was nothing there.
            //
            // Asking OriginalPathOf is safe for any directory, because its last fallback
            // matches only the mirrored layout, where the first component under the bin root
            // is a drive letter followed by a separator -- which no ordinary subdirectory of
            // a recycled folder is.
            if (OriginalPathOf(entry.FullPath, binRoot) is { } recorded)
            {
                items.Add(new Item(
                    entry.FullPath,
                    recorded,
                    true,
                    MeasureTree(entry.FullPath),
                    ToLocalTime(entry.LastWriteTime)));

                continue;
            }

            Walk(entry.FullPath, binRoot, items);
        }
    }

    /// <summary>
    /// FILETIME comes out of the directory enumeration as a raw long. A zero or
    /// nonsensical value appears on some file systems and would throw, and a
    /// listing is not worth failing over a timestamp.
    /// </summary>
    private static DateTime ToLocalTime(long fileTime)
    {
        if (fileTime <= 0)
        {
            return DateTime.MinValue;
        }

        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool HasMarker(string directory)
        => DirectoryOps.Exists(directory + "\\" + MarkerFileName);

    private static string? ReadMarkerOriginal(string directory)
    {
        try
        {
            string path = PathUtil.ToDisplay(directory + "\\" + MarkerFileName);

            foreach (string line in File.ReadLines(path))
            {
                // Both languages, always -- see OriginalPathPrefixes.
                foreach (string prefix in OriginalPathPrefixes)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return line[prefix.Length..].Trim();
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    /// <summary>Total bytes in a tree, which is what reclaiming it would free.</summary>
    public static long MeasureTree(string extendedDirectory)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(extendedDirectory);

        while (stack.Count > 0)
        {
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
                        stack.Push(entry.FullPath);
                    }
                }
                else
                {
                    total += entry.Size;
                }
            }
        }

        return total;
    }

    /// <summary>Bins that currently exist, one per volume that has been used.</summary>
    public static List<string> ExistingRoots()
    {
        var roots = new List<string>();

        string? overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);

        if (!string.IsNullOrWhiteSpace(overridden))
        {
            string extendedOverride = PathUtil.ToExtended(overridden);

            if (DirectoryOps.Exists(extendedOverride))
            {
                roots.Add(extendedOverride);
            }

            return roots;
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            string candidate = drive.RootDirectory.FullName.TrimEnd('\\') + "\\" + FolderName;
            string extended = PathUtil.ToExtended(candidate);

            if (DirectoryOps.Exists(extended))
            {
                roots.Add(extended);
            }
        }

        return roots;
    }

    /// <summary>
    /// Removes the bin's scaffolding once it is empty, so a drive that no longer
    /// holds anything discarded also no longer shows the folder.
    /// </summary>
    public static void PruneEmpty(string binRoot)
    {
        try
        {
            string display = PathUtil.ToDisplay(binRoot);
            if (!Directory.Exists(display))
            {
                return;
            }

            // RemoveDirectory on an empty directory fails, which is the check.
            PruneSubdirectories(binRoot);

            if (Directory.GetFileSystemEntries(display).Length == 0)
            {
                Directory.Delete(display, recursive: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void PruneSubdirectories(string directory)
    {
        var entries = new List<FindEntry>(64);
        if (!DirectoryList.Enumerate(directory, entries))
        {
            return;
        }

        foreach (FindEntry entry in entries)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            PruneSubdirectories(entry.FullPath);

            try
            {
                string display = PathUtil.ToDisplay(entry.FullPath);
                if (Directory.Exists(display) && Directory.GetFileSystemEntries(display).Length == 0)
                {
                    Directory.Delete(display, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
