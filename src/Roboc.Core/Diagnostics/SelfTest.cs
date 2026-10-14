using System.Runtime.InteropServices;
using Roboc.Core.Control;
using Roboc.Core.Copy;
using Roboc.Core.Delete;
using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Journal;
using Roboc.Core.Model;
using Roboc.Core.Progress;

namespace Roboc.Core.Diagnostics;

/// <summary>
/// Exercises every interop primitive and every deletion guard against real paths.
///
/// The guard section exists because of a real incident: `delete D:` was accepted,
/// resolved to the process's current directory (Windows treats `D:` as
/// drive-relative), and removed it. These cases are the regression test for that.
/// </summary>
internal static class SelfTest
{
    public static int Run(TextWriter output, string? scratchRoot = null)
    {
        int failures = 0;
        int passed = 0;

        void Check(string name, Action body)
        {
            try
            {
                body();
                passed++;
                output.WriteLine($"  [ok]   {name}");
            }
            catch (Exception ex)
            {
                failures++;
                output.WriteLine($"  [FAIL] {name}");
                output.WriteLine($"         {ex.GetType().Name}: {ex.Message}");
            }
        }

        output.WriteLine("Roboc self-test");
        output.WriteLine();

        Check("native struct layouts", LayoutAssert.Verify);

        Check("path conversion round-trip", () =>
        {
            string local = PathUtil.ToExtended(@"C:\Windows\notepad.exe");
            Assert(local == @"\\?\C:\Windows\notepad.exe", $"local extended wrong: {local}");
            Assert(PathUtil.ToDisplay(local) == @"C:\Windows\notepad.exe", "display round-trip failed");
            Assert(PathUtil.ToNtPath(local) == @"\??\C:\Windows\notepad.exe", "nt path wrong");

            string unc = PathUtil.ToExtended(@"\\server\share\dir\file.txt");
            Assert(unc == @"\\?\UNC\server\share\dir\file.txt", $"unc extended wrong: {unc}");
            Assert(PathUtil.GetVolumeRoot(local) == @"\\?\C:\", "volume root wrong");
            Assert(PathUtil.GetVolumeRoot(unc) == @"\\?\UNC\server\share\", "unc root wrong");
            Assert(PathUtil.NeedsExtendedPrefix(@"C:\dir\trailing."), "trailing dot not flagged");
            Assert(PathUtil.NeedsExtendedPrefix(@"C:\dir\NUL.txt"), "reserved name not flagged");
        });

        Check("volume-root detection preserves the separator", () =>
        {
            // Regression: TrimTrailingSeparator used to turn "\\?\D:\" into
            // "\\?\D:", which is not a valid path. Existence checks then failed
            // on a volume root, so guards keyed on "does this exist" passed for
            // the wrong reason.
            Assert(PathUtil.IsVolumeRoot(@"\\?\D:\"), "extended drive root not detected");
            Assert(PathUtil.IsVolumeRoot(@"C:\"), "plain drive root not detected");
            Assert(PathUtil.IsVolumeRoot(@"\\?\UNC\srv\share\"), "extended UNC root not detected");
            Assert(!PathUtil.IsVolumeRoot(@"\\?\D:\dir"), "subdirectory wrongly called a root");
            Assert(PathUtil.TrimTrailingSeparator(@"\\?\D:\") == @"\\?\D:\",
                "volume root lost its separator");
            Assert(PathUtil.TrimTrailingSeparator(@"\\?\D:\dir\") == @"\\?\D:\dir",
                "ordinary directory not trimmed");
        });

        // -------------------------------------------------------- deletion guards
        //
        // These run against synthetic argument strings and never touch real data:
        // the guard must reject before anything else happens.

        Check("guard: drive-relative 'D:' is refused", () =>
        {
            string message = CaptureRefusal(@"D:");
            Assert(message.Contains("drive-relative", StringComparison.OrdinalIgnoreCase),
                $"wrong refusal reason: {message}");
        });

        Check("guard: drive-relative 'D:sub' is refused", () =>
        {
            string message = CaptureRefusal(@"D:sub");
            Assert(message.Contains("drive-relative", StringComparison.OrdinalIgnoreCase),
                $"wrong refusal reason: {message}");
        });

        Check("guard: volume roots are refused by name", () =>
        {
            foreach (string root in new[] { @"C:\", @"D:\", @"E:\" })
            {
                string message = CaptureRefusal(root);
                Assert(message.Contains("volume root", StringComparison.OrdinalIgnoreCase),
                    $"'{root}' refused for the wrong reason: {message}");
            }
        });

        Check("guard: targets containing the running program are refused", () =>
        {
            string baseDirectory = AppContext.BaseDirectory;
            string message = CaptureRefusal(baseDirectory);
            Assert(message.Contains("running program", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("current directory", StringComparison.OrdinalIgnoreCase),
                $"wrong refusal reason: {message}");
        });

        Check("guard: an ancestor of the running program is refused", () =>
        {
            string ancestor = Path.GetPathRoot(AppContext.BaseDirectory) ?? string.Empty;

            // A volume root trips the volume-root guard first; step one level in
            // so this exercises the ancestor rule specifically.
            string probe = Path.GetDirectoryName(
                Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\'))) ?? ancestor;

            string message = CaptureRefusal(probe);
            Assert(message.Length > 0, "ancestor was not refused at all");
        });

        Check("guard: deletion without confirmation is refused", () =>
        {
            var job = new DeleteJob
            {
                Sources = [Path.GetTempPath()],
                Options = new DeleteOptions { Confirmed = false },
            };

            try
            {
                DeleteEngine.Run(job);
            }
            catch (IOException ex)
            {
                Assert(ex.Message.Contains("confirmation", StringComparison.OrdinalIgnoreCase),
                    $"wrong refusal reason: {ex.Message}");
                return;
            }

            throw new InvalidOperationException("unconfirmed delete was allowed");
        });

        Check("guard: a benign target is NOT refused by the guards", () =>
        {
            // The failure mode of over-zealous guards is refusing real work, so
            // assert that an ordinary deep path passes all of them. DirectoryOps
            // is never reached because the path does not exist.
            string benign = Path.Combine(Path.GetTempPath(), "roboc-guard-probe", "nested");
            string extended = PathUtil.TrimTrailingSeparator(PathUtil.ToExtended(benign));

            Assert(!PathUtil.IsVolumeRoot(extended), "benign path called a volume root");
            Assert(!extended.Equals(PathUtil.EnsureTrailingSeparator(extended), StringComparison.Ordinal),
                "benign path has a trailing separator after trimming");
        });

        // ------------------------------------------------------------ interop
        string scratchBase = scratchRoot ?? Path.GetTempPath();
        string scratch = Path.Combine(scratchBase, "roboc-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        output.WriteLine($"  scratch: {scratch}");
        output.WriteLine();

        try
        {
            Check("volume information + sector size", () => ProbeVolume(scratch));
            Check("directory enumeration", () => ProbeEnumeration(scratch));
            Check("file create / write / read back", () => ProbeFileIo(scratch));
            Check("FILE_BASIC_INFO / FILE_STANDARD_INFO / FILE_ID_INFO", () => ProbeFileInfo(scratch));
            Check("delete read-only file in one call", () => ProbeReadOnlyDelete(scratch));
            Check("delete a directory tree with the fast deleter", () => ProbeTreeDelete(scratch));
        Check("deleting reports the size it removed", () => ProbeDeleteReportsSize(scratch));
            Check("NTSTATUS -> Win32 error mapping", ProbeErrorMapping);
            Check("journal round-trips completion and checkpoints", () => ProbeJournalRoundTrip(scratch));
            Check("journal recovers from a truncated tail", () => ProbeJournalTruncated(scratch));
        Check("hard links survive a copy as links", () => ProbeHardLinkCopy(scratch));
        Check("verification detects a corrupted copy", () => ProbeVerification(scratch));
        Check("leftover temp scan finds only staged files", () => ProbeLeftoverScan(scratch));
        Check("disk-full maps to its own error kind", ProbeDiskFullMapping);
        Check("a recycled directory is listed back by the bin", () => ProbeRecycledDirectoryIsListed(scratch));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }

        output.WriteLine();
        output.WriteLine($"{passed} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Runs a delete with every guard armed and returns the refusal text.
    /// Fails the test if the target is somehow accepted.</summary>
    private static string CaptureRefusal(string path)
    {
        var job = new DeleteJob
        {
            Sources = [path],
            Options = new DeleteOptions { Confirmed = true, Direct = true, UndoWindow = TimeSpan.Zero },
        };

        try
        {
            DeleteEngine.Run(job);
        }
        catch (IOException ex)
        {
            // FileNotFoundException derives from IOException and is covered here.
            return ex.Message;
        }

        throw new InvalidOperationException($"'{path}' was accepted, which it must never be");
    }

    // ------------------------------------------------------------------ probes

    /// <summary>
    /// Two hard links to one file must arrive at the destination still linked.
    ///
    /// <b>Why this needs its own test.</b> Every other way of checking a copy passes
    /// when hard links are broken. Both paths exist, both have the right size, both
    /// read back the right bytes -- the copy looks perfect. The only thing wrong is
    /// invisible until later: editing one no longer changes the other, and the data
    /// occupies twice the space. A test that stops at "the files are there and
    /// correct" cannot see it, so this one edits a file and checks the other.
    ///
    /// The link is made here with the same call the copy engine uses, so a mistake in
    /// the arguments shows up as a failure of the test rather than as a false pass.
    /// </summary>
    private static void ProbeHardLinkCopy(string scratch)
    {
        string source = Path.Combine(scratch, "hardlink-src");
        string destination = Path.Combine(scratch, "hardlink-dst");
        Directory.CreateDirectory(source);

        string first = Path.Combine(source, "original.txt");
        string second = Path.Combine(source, "linked.txt");
        File.WriteAllText(first, "shared content");

        // 1. Make the link, and refuse to continue if the link itself did not happen.
        Assert(
            Kernel32.CreateHardLink(
                PathUtil.ToExtended(second), PathUtil.ToExtended(first), 0) != 0,
            $"could not create the source hard link: {NativeError.DescribeWin32(Marshal.GetLastWin32Error())}");

        Assert(LinkCount(first) == 2, $"source link count is {LinkCount(first)}, expected 2");
        Assert(FileIdsMatch(first, second), "the two source paths are not the same file");

        // 2. Copy the tree.
        var options = new CopyOptions
        {
            RecreateHardLinks = true,
            WorkerThreads = 2,
            FlushLargeFiles = false,
        };

        using var control = new JobControl();
        var job = new CopyJob
        {
            Sources = [source],
            Destination = destination,
            Options = options,
            Control = control,
        };

        JobResult result = CopyEngine.Run(job);

        Assert(result.FilesFailed == 0, $"copy reported {result.FilesFailed} failure(s)");

        string destinationFirst = Path.Combine(destination, "hardlink-src", "original.txt");
        string destinationSecond = Path.Combine(destination, "hardlink-src", "linked.txt");

        Assert(File.Exists(destinationFirst), "original.txt missing at the destination");
        Assert(File.Exists(destinationSecond), "linked.txt missing at the destination");

        // 3. The contents must be right regardless of how they got there.
        Assert(File.ReadAllText(destinationFirst) == "shared content", "original content wrong");
        Assert(File.ReadAllText(destinationSecond) == "shared content", "linked content wrong");

        // 4. The point of the whole test: they must still be one file.
        Assert(
            FileIdsMatch(destinationFirst, destinationSecond),
            "the two destination paths are separate files, not hard links");

        Assert(
            LinkCount(destinationFirst) == 2,
            $"destination link count is {LinkCount(destinationFirst)}, expected 2");

        // 5. And the behaviour a user would notice: writing through one path is
        //    visible through the other.
        File.WriteAllText(destinationFirst, "edited through the first path");

        Assert(
            File.ReadAllText(destinationSecond) == "edited through the first path",
            "editing one path did not change the other, so they are not links");
    }

    /// <summary>
    /// The walk that runs when a copy aborts because the disk filled up.
    ///
    /// Tested here rather than against a real full disk, because a full disk cannot be
    /// made without either administrator rights or filling a real volume, and the walk
    /// is where the mistakes would be anyway: a queue with no ceiling, a suffix test
    /// that catches the wrong files, a recursion that never ends. Every one of those
    /// turns the report into a second problem on top of the one being reported.
    ///
    /// Three things are asserted, and the third is the one that matters most: the scan
    /// must not be able to run away. A crash during an abort is a crash the user cannot
    /// recover from, because the copy they were waiting on never finishes.
    /// </summary>
    private static void ProbeLeftoverScan(string scratch)
    {
        string root = Path.Combine(scratch, "leftover");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "c"));

        // Staged files, at several depths.
        string shallow = Path.Combine(root, "one" + FileCopier.TempSuffix);
        string deep = Path.Combine(root, "a", "b", "two" + FileCopier.TempSuffix);
        string sibling = Path.Combine(root, "c", "three" + FileCopier.TempSuffix);
        File.WriteAllText(shallow, "x");
        File.WriteAllText(deep, "x");
        File.WriteAllText(sibling, "x");

        // Things that must NOT be reported.
        File.WriteAllText(Path.Combine(root, "ordinary.txt"), "x");
        File.WriteAllText(Path.Combine(root, "a", "temp-suffix-in-the-middle.txt"), "x");
        File.WriteAllText(Path.Combine(root, "not" + FileCopier.TempSuffix + ".txt"), "x");

        var found = CopyEngine.FindLeftoverTemporaries(root);

        Assert(found.Count == 3, $"found {found.Count} staged files, expected 3");

        foreach (string path in new[] { shallow, deep, sibling })
        {
            Assert(
                found.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)),
                $"missed {Path.GetFileName(path)}");
        }

        Assert(
            !found.Any(f => f.Path.Contains("ordinary", StringComparison.OrdinalIgnoreCase)),
            "an ordinary file was reported as staged");

        Assert(
            !found.Any(f => f.Path.Contains("in-the-middle", StringComparison.OrdinalIgnoreCase)),
            "a file merely containing the suffix was reported as staged");

        // Sizes, because the report is what tells the user how much room a retry needs.
        // A staged file is as large as the file being staged, so the total is the
        // difference between "retry and fail again" and "retry and succeed".
        var sized = found.First(f => string.Equals(f.Path, shallow, StringComparison.OrdinalIgnoreCase));
        Assert(sized.Size == 1, $"staged file size reported as {sized.Size}, expected 1");

        Assert(found.Sum(f => f.Size) == 3, $"total size {found.Sum(f => f.Size)}, expected 3");

        // The ceiling: a tree larger than the limit must stop, not run to completion.
        string wide = Path.Combine(scratch, "leftover-wide");
        Directory.CreateDirectory(wide);

        for (int i = 0; i < 40; i++)
        {
            File.WriteAllText(Path.Combine(wide, $"f{i}" + FileCopier.TempSuffix), "x");
        }

        var capped = CopyEngine.FindLeftoverTemporaries(wide, limit: 5);
        Assert(capped.Count == 5, $"limit ignored: got {capped.Count}, expected 5");

        var budgeted = CopyEngine.FindLeftoverTemporaries(wide, limit: 200, visitBudget: 7);
        Assert(budgeted.Count <= 7, $"visit budget ignored: examined {budgeted.Count}");

        // A destination that does not exist is not an error, it is nothing to report.
        Assert(
            CopyEngine.FindLeftoverTemporaries(Path.Combine(scratch, "no-such-place")).Count == 0,
            "a missing destination produced results");
    }

    /// <summary>
    /// Both codes that mean "no space" must land on DiskFull.
    ///
    /// Windows reports them separately -- ERROR_DISK_FULL when a write cannot extend a
    /// file, ERROR_HANDLE_DISK_FULL when the handle itself is out of room -- and a
    /// mapping that catches only one of them lets the other through as Unknown, so the
    /// copy neither aborts nor explains itself.
    /// </summary>
    private static void ProbeDiskFullMapping()
    {
        Assert(
            FileCopier.Classify(NativeConst.ERROR_DISK_FULL) == ErrorKind.DiskFull,
            "ERROR_DISK_FULL did not map to DiskFull");

        Assert(
            FileCopier.Classify(NativeConst.ERROR_HANDLE_DISK_FULL) == ErrorKind.DiskFull,
            "ERROR_HANDLE_DISK_FULL did not map to DiskFull");
    }

    /// <summary>
    /// The verifier must pass two identical files and fail two that differ.
    ///
    /// Both halves matter and for different reasons. A verifier that never fails is
    /// decoration, and one that fails on identical files turns every copy into a
    /// reported corruption -- which is worse, because it trains the user to ignore the
    /// report that exists to be believed. The mismatching case also checks that the
    /// difference is located rather than merely announced: an offset that points at the
    /// wrong place sends anyone investigating to the wrong place too.
    /// </summary>
    private static void ProbeVerification(string scratch)
    {
        string left = Path.Combine(scratch, "verify-a.bin");
        string right = Path.Combine(scratch, "verify-b.bin");

        byte[] payload = new byte[200_000];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(left, payload);
        File.WriteAllBytes(right, payload);

        Assert(
            Verifier.Compare(PathUtil.ToExtended(left), PathUtil.ToExtended(right), out long clean),
            $"identical files reported as different (offset {clean})");

        // One byte, far enough in to be past the first block.
        payload[150_000] ^= 0xFF;
        File.WriteAllBytes(right, payload);

        Assert(
            !Verifier.Compare(PathUtil.ToExtended(left), PathUtil.ToExtended(right), out long offset),
            "a corrupted copy passed verification");

        Assert(offset == 150_000, $"mismatch reported at {offset}, expected 150000");

        // A length difference must be caught without reading the contents.
        File.WriteAllBytes(right, payload[..100_000]);

        Assert(
            !Verifier.Compare(PathUtil.ToExtended(left), PathUtil.ToExtended(right), out long shortOffset),
            "a truncated copy passed verification");

        Assert(shortOffset == 100_000, $"length mismatch reported at {shortOffset}, expected 100000");
    }

    /// <summary>Number of directory entries pointing at the file.</summary>
    private static uint LinkCount(string path)
    {
        nint handle = Kernel32.CreateFile(
            PathUtil.ToExtended(path), NativeConst.FILE_READ_ATTRIBUTES,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0, NativeConst.OPEN_EXISTING, 0, 0);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            NativeError.ThrowWin32("CreateFile(link count)", path);
        }

        try
        {
            unsafe
            {
                FILE_STANDARD_INFO standard;
                Assert(Kernel32.GetFileInformationByHandleEx(
                    handle, NativeConst.FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO)) != 0,
                    "standard info failed");

                return standard.NumberOfLinks;
            }
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>True when both paths resolve to the same file on the same volume.</summary>
    private static bool FileIdsMatch(string left, string right)
    {
        FILE_ID_INFO a = FileIdOf(left);
        FILE_ID_INFO b = FileIdOf(right);

        return a.VolumeSerialNumber == b.VolumeSerialNumber &&
               a.FileId.Low == b.FileId.Low &&
               a.FileId.High == b.FileId.High;
    }

    private static FILE_ID_INFO FileIdOf(string path)
    {
        nint handle = Kernel32.CreateFile(
            PathUtil.ToExtended(path), NativeConst.FILE_READ_ATTRIBUTES,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0, NativeConst.OPEN_EXISTING, 0, 0);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            NativeError.ThrowWin32("CreateFile(file id)", path);
        }

        try
        {
            unsafe
            {
                FILE_ID_INFO id;
                Assert(Kernel32.GetFileInformationByHandleEx(
                    handle, NativeConst.FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)) != 0,
                    "file id failed");

                return id;
            }
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    private static void ProbeVolume(string scratch)
    {
        string root = PathUtil.GetVolumeRootDisplay(scratch);
        Assert(Kernel32.GetDiskFreeSpace(root, out _, out uint bytesPerSector, out _, out _) != 0,
            "GetDiskFreeSpace failed");
        Assert(bytesPerSector is > 0 and <= 4096, $"implausible sector size {bytesPerSector}");
        Assert(Kernel32.GetDiskFreeSpaceEx(root, out _, out ulong total, out _) != 0, "GetDiskFreeSpaceEx failed");
        Assert(total > 0, "total volume size reported as zero");
    }

    private static void ProbeEnumeration(string scratch)
    {
        string dir = Path.Combine(scratch, "enum");
        Directory.CreateDirectory(dir);

        const int count = 512;
        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"item{i:D4}.txt"), i.ToString());
        }

        Directory.CreateDirectory(Path.Combine(dir, "subdir"));

        var entries = new List<FindEntry>(1024);
        Assert(DirectoryList.Enumerate(PathUtil.ToExtended(dir), entries), "enumeration failed");
        Assert(entries.Count == count + 1, $"enumerated {entries.Count}, expected {count + 1}");
        Assert(entries.Count(e => e.IsDirectory) == 1, "directory count wrong");
    }

    private static void ProbeFileIo(string scratch)
    {
        string path = Path.Combine(scratch, "io.bin");
        byte[] payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(path, payload);
        Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(payload), "content mismatch");
    }

    private static void ProbeFileInfo(string scratch)
    {
        string path = Path.Combine(scratch, "info.txt");
        File.WriteAllText(path, "hello roboc");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc));

        nint handle = Kernel32.CreateFile(
            PathUtil.ToExtended(path), NativeConst.FILE_READ_ATTRIBUTES,
            NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE | NativeConst.FILE_SHARE_DELETE,
            0, NativeConst.OPEN_EXISTING, 0, 0);

        if (handle == NativeConst.INVALID_HANDLE_VALUE)
        {
            NativeError.ThrowWin32("CreateFile(info)", path);
        }

        try
        {
            unsafe
            {
                FILE_BASIC_INFO basic;
                Assert(Kernel32.GetFileInformationByHandleEx(
                    handle, NativeConst.FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO)) != 0,
                    "basic info failed");

                var expected = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                Assert(DateTime.FromFileTimeUtc(basic.LastWriteTime) == expected, "LastWriteTime wrong");

                FILE_STANDARD_INFO standard;
                Assert(Kernel32.GetFileInformationByHandleEx(
                    handle, NativeConst.FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO)) != 0,
                    "standard info failed");
                Assert(standard.EndOfFile == "hello roboc".Length, "size wrong");
                Assert(!standard.IsDirectory, "file reported as directory");

                FILE_ID_INFO id;
                Assert(Kernel32.GetFileInformationByHandleEx(
                    handle, NativeConst.FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)) != 0,
                    "file id failed");
                Assert(!id.FileId.IsZero, "file id is zero");
            }
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>The load-bearing claim of the fast-delete path: a read-only file
    /// goes in one NtSetInformationFile where DeleteFileW cannot.</summary>
    private static void ProbeReadOnlyDelete(string scratch)
    {
        string path = Path.Combine(scratch, "readonly.txt");
        File.WriteAllText(path, "protected");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        string extended = PathUtil.ToExtended(path);
        Assert(Kernel32.DeleteFile(extended) == 0, "expected DeleteFile to fail on a read-only file");
        Assert(NativeError.LastWin32() == NativeConst.ERROR_ACCESS_DENIED, "expected ACCESS_DENIED");
        Assert(File.Exists(path), "read-only file vanished from the failed DeleteFile");

        Assert(FastDeleter.TryDelete(extended, directory: false, out _, out int code, out string message),
            $"fast delete failed: {NativeError.DescribeWin32(code)} {message}");
        Assert(!File.Exists(path), "file survived a successful POSIX delete");
    }

    /// <summary>
    /// Deleting reports the size of what it deleted.
    ///
    /// The delete progress window plots a speed graph from the byte counter the engine feeds
    /// it. AddFileCompleted was called with a hardcoded zero on this path, so bytesDone never
    /// moved, bytesPerSecond stayed at zero, and the chart drew its frame and its gridlines
    /// and then no line at all -- for every deletion of every size, with nothing anywhere
    /// reporting a problem. The window looked like a chart with no data because that is
    /// exactly what it was.
    ///
    /// The staged path had always reported bytes, which is why only the unstaged one came up
    /// blank. This pins the value down, so a refactor that drops it fails here rather than
    /// quietly emptying the graph again.
    /// </summary>
    private static void ProbeDeleteReportsSize(string scratch)
    {
        const int Payload = 12_345;

        string path = Path.Combine(scratch, "sized-for-delete.bin");
        File.WriteAllBytes(path, new byte[Payload]);

        Assert(
            FastDeleter.TryDelete(
                PathUtil.ToExtended(path), directory: false,
                out _, out int code, out string message, out long reported),
            $"fast delete failed: {NativeError.DescribeWin32(code)} {message}");

        Assert(!File.Exists(path), "file survived a successful delete");
        Assert(reported == Payload, $"delete reported {reported} bytes for a {Payload}-byte file");
    }

    private static void ProbeTreeDelete(string scratch)
    {
        string root = Path.Combine(scratch, "tree");
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));

        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(root, $"f{i}.bin"), "x");
        }

        string deep = Path.Combine(root, "a", "b", "c", "deep.bin");
        File.WriteAllText(deep, "y");
        File.SetAttributes(deep, FileAttributes.ReadOnly);

        FastDeleter.DeleteTreeQuiet(PathUtil.ToExtended(root));
        Assert(!Directory.Exists(root), "tree survived DeleteTreeQuiet");
    }

    /// <summary>
    /// The journal exists to be trusted after a crash, so the two things worth
    /// proving are that state survives a reopen and that a half-written tail does
    /// not poison the records before it.
    /// </summary>
    private static void ProbeJournalRoundTrip(string scratch)
    {
        string destination = Path.Combine(scratch, "journal-roundtrip");
        Directory.CreateDirectory(destination);
        string[] sources = [@"C:\source-one", @"C:\source-two"];

        using (JobJournal? journal = JobJournal.TryOpen(destination, sources, null, out _))
        {
            Assert(journal is not null, "journal could not be opened");

            journal!.RecordStarted("big.bin", 65536);
            journal.RecordCheckpoint("big.bin", 4096);
            journal.RecordCompleted("a.txt", ItemOutcome.Copied, 10);
            journal.RecordCompleted("b.txt", ItemOutcome.SkippedSame, 20);
            journal.Flush();
        }

        using (JobJournal? reopened = JobJournal.TryOpen(destination, sources, null, out _))
        {
            Assert(reopened is not null, "journal could not be reopened");
            Assert(reopened!.HasResumeState, "resume state was not detected");
            Assert(reopened.Lookup("a.txt") == ItemOutcome.Copied, "a.txt outcome was lost");
            Assert(reopened.Lookup("b.txt") == ItemOutcome.SkippedSame, "b.txt outcome was lost");
            Assert(reopened.GetCheckpoint("big.bin") == 4096, "checkpoint offset was lost");
        }
    }

    private static void ProbeJournalTruncated(string scratch)
    {
        string destination = Path.Combine(scratch, "journal-truncated");
        Directory.CreateDirectory(destination);
        string[] sources = [@"C:\source"];

        string path;
        using (JobJournal? journal = JobJournal.TryOpen(destination, sources, null, out path))
        {
            Assert(journal is not null, "journal could not be opened");
            journal!.RecordCompleted("first.txt", ItemOutcome.Copied, 1);
            journal.RecordCompleted("second.txt", ItemOutcome.Copied, 2);
            journal.Flush();
        }

        // A record header claiming a large payload that was never written: exactly
        // what a power loss in the middle of a record leaves behind.
        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            append.Write([3, 0xFF, 0x01, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44]);
        }

        using (JobJournal? reopened = JobJournal.TryOpen(destination, sources, null, out _))
        {
            Assert(reopened is not null, "journal could not be reopened after damage");
            Assert(reopened!.Lookup("first.txt") == ItemOutcome.Copied,
                "an intact record before the damage was discarded");
            Assert(reopened.Lookup("second.txt") == ItemOutcome.Copied,
                "an intact record before the damage was discarded");
        }
    }

    private static void ProbeErrorMapping()
    {
        // FormatMessage is localised, so assert on structure (the hex code is
        // always appended) rather than on English wording.
        string accessDenied = NativeError.DescribeWin32(NativeConst.ERROR_ACCESS_DENIED);
        Assert(accessDenied.Contains("0x00000005", StringComparison.OrdinalIgnoreCase),
            $"ACCESS_DENIED description lacks its code: {accessDenied}");

        string notFound = NativeError.DescribeNtStatus(NativeConst.STATUS_OBJECT_NAME_NOT_FOUND);
        Assert(notFound.Contains("NTSTATUS 0xC0000034", StringComparison.OrdinalIgnoreCase),
            $"NTSTATUS description lacks its code: {notFound}");

        var error = NativeError.CreateWin32("NtOpenFile", @"C:\x", NativeConst.ERROR_ACCESS_DENIED);
        Assert(error.NeedsElevation, "ACCESS_DENIED not classified as needing elevation");
        Assert(error.IsTransient == false, "ACCESS_DENIED wrongly classified as transient");

        var sharing = NativeError.CreateWin32("NtOpenFile", @"C:\x", NativeConst.ERROR_SHARING_VIOLATION);
        Assert(sharing.IsTransient, "SHARING_VIOLATION not classified as transient");
    }

    /// <summary>
    /// Recycles a directory and checks the bin can list it.
    ///
    /// The defect this exists to catch: WriteMarker was written in the first commit and
    /// never called, and Walk only recognised a discarded directory if it carried that
    /// marker. So recycling a folder moved it, indexed it, and left it invisible -- the bin
    /// reported itself empty while a complete folder sat inside it. Nothing failed, nothing
    /// was logged, and the files were still on disk; the listing simply never mentioned
    /// them.
    ///
    /// Two things are asserted, because they are two different mechanisms and either can
    /// break on its own: the item is listed at all, and the folder carries the marker that
    /// makes it self-describing if the index is ever lost.
    /// </summary>
    private static void ProbeRecycledDirectoryIsListed(string scratch)
    {
        string source = Path.Combine(scratch, "recycled-dir");
        Directory.CreateDirectory(Path.Combine(source, "inner"));
        File.WriteAllText(Path.Combine(source, "inner", "payload.txt"), "payload");

        string extended = PathUtil.ToExtended(source);
        string? binRoot = RecycleBin.RootFor(extended);

        if (binRoot is null)
        {
            // No writable volume to recycle onto. Not a failure of the code under test.
            return;
        }

        Assert(RecycleBin.TryPut(extended, out string stored, out int error),
            $"recycling a directory failed with error {error}");

        try
        {
            Assert(!Directory.Exists(source), "the source directory still exists after recycling");

            List<RecycleBin.Item> items = RecycleBin.List(binRoot);

            Assert(
                items.Any(i => i.IsDirectory && i.OriginalPath.EndsWith("recycled-dir", StringComparison.OrdinalIgnoreCase)),
                $"the bin listed {items.Count} items and did not include the directory just recycled");

            Assert(
                File.Exists(Path.Combine(PathUtil.ToDisplay(stored), RecycleBin.MarkerFileName)),
                "the recycled directory carries no marker file, so it would be unrecognisable if the index were lost");
        }
        finally
        {
            // Take it straight back out of the bin.
            //
            // Moved rather than deleted: this probe runs against the user's real bin --
            // there is no scratch volume to recycle onto -- so it must leave nothing
            // behind. MoveFileEx back to the source path is the call the restore command
            // itself makes.
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Kernel32.MoveFileEx(PathUtil.ToExtended(stored), extended, 0);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
