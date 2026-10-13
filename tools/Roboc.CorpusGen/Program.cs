using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;

namespace Roboc.CorpusGen;

/// <summary>
/// Builds and verifies a deterministic benchmark corpus.
///
/// Everything is reproducible from a seed, so a copy can be verified byte-for-byte
/// afterwards: the manifest records every file's size and content seed, so any
/// tool's output can be re-derived and compared. A tool that is fast but wrong
/// cannot hide behind a timing number.
/// </summary>
internal static class Program
{
    private const int BlockSize = 4096;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "create" => Create(args),
                "verify" => Verify(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("corpusgen: " + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string c)
    {
        Console.Error.WriteLine($"corpusgen: unknown command '{c}'");
        return 2;
    }

    private static void PrintHelp() => Console.WriteLine("""
        Roboc corpus generator

          CorpusGen create --root <dir> [--small-count N] [--small-size B]
                           [--files-per-dir N] [--fanout N]
                           [--large-count N] [--large-size MB] [--seed N] [--clean]
          CorpusGen verify --root <dir>
        """);

    private static int Create(string[] args)
    {
        string root = Path.GetFullPath(Require(args, "--root"));
        int smallCount = Int(args, "--small-count", 20000);
        int smallSize = Int(args, "--small-size", 4096);
        int filesPerDir = Int(args, "--files-per-dir", 256);
        int fanout = Int(args, "--fanout", 64);
        int largeCount = Int(args, "--large-count", 2);
        int largeSizeMb = Int(args, "--large-size", 256);
        long seed = Long(args, "--seed", 12345);

        // Metadata-heavy extras. Neither is free to detect on the copy side -- the
        // hard link check costs a GetFileInformationByHandleEx per candidate file and
        // the stream check costs a FindFirstStreamW per file -- so a corpus carrying
        // them is how that cost gets measured rather than assumed.
        int hardLinkGroups = Int(args, "--hardlink-groups", 0);
        int adsCount = Int(args, "--ads", 0);

        if (Directory.Exists(root))
        {
            if (!Has(args, "--clean"))
            {
                Console.Error.WriteLine($"corpusgen: '{root}' exists; pass --clean to replace");
                return 1;
            }

            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        Console.WriteLine($"corpus root : {root}");
        Console.WriteLine($"small files : {smallCount:N0} x {smallSize} B");
        Console.WriteLine($"large files : {largeCount:N0} x {largeSizeMb} MB");
        Console.WriteLine();

        var manifest = new List<(string Relative, long Size, long Seed)>(smallCount + largeCount);
        var stopwatch = Stopwatch.StartNew();
        byte[] block = new byte[Math.Min(Math.Max(smallSize, 1), BlockSize)];

        for (int i = 0; i < smallCount; i++)
        {
            long fileSeed = seed + i;
            string relative = SmallFileRelativePath(i, filesPerDir, fanout);
            string full = Path.Combine(root, relative);

            string? dir = Path.GetDirectoryName(full);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            WriteFile(full, smallSize, fileSeed, block);
            manifest.Add((relative, smallSize, fileSeed));

            if (smallCount >= 5000 && (i + 1) % 5000 == 0)
            {
                Console.Write($"\r  small: {i + 1:N0}/{smallCount:N0}   ");
            }
        }

        Console.Write($"\r  small: {smallCount:N0} files                     \n");

        if (largeCount > 0)
        {
            string largeDir = Path.Combine(root, "large");
            Directory.CreateDirectory(largeDir);
            byte[] largeBlock = new byte[BlockSize];
            long largeBytes = (long)largeSizeMb * 1024 * 1024;

            for (int i = 0; i < largeCount; i++)
            {
                long fileSeed = seed + 1_000_000 + i;
                string relative = Path.Combine("large", $"large{i:D3}.bin");
                WriteFile(Path.Combine(root, relative), largeBytes, fileSeed, largeBlock);
                manifest.Add((relative, largeBytes, fileSeed));
                Console.WriteLine($"  large: {relative} ({largeSizeMb} MB)");
            }
        }

        // Hard links: extra directory entries for files that already exist. Each group
        // is a base plus one alias, so the copy has something to notice.
        int linksMade = 0;

        if (hardLinkGroups > 0 && manifest.Count > 0)
        {
            string linkDir = Path.Combine(root, "linked");
            Directory.CreateDirectory(linkDir);

            for (int g = 0; g < hardLinkGroups && g < manifest.Count; g++)
            {
                // The alias inherits the base file's seed, because it inherits the base
                // file's contents -- they are the same file. Recording a fresh seed made
                // the verifier recompute different bytes and report every alias as
                // corrupt, which is a fault in the corpus rather than in the copy.
                (string relative, long baseSize, long baseSeed) = manifest[g];
                string source = Path.Combine(root, relative);
                string extension = Path.GetExtension(relative);
                string alias = Path.Combine(linkDir, $"link{g:D5}{(extension.Length > 0 ? extension : ".dat")}");

                if (Native.CreateHardLink(alias, source, 0) != 0)
                {
                    linksMade++;
                    manifest.Add((Path.Combine("linked", Path.GetFileName(alias)), baseSize, baseSeed));
                }
            }

            Console.WriteLine($"  hard links: {linksMade:N0} aliases over {Math.Min(hardLinkGroups, manifest.Count):N0} files");
        }

        // Alternate data streams, which are inert to every tool that does not look for
        // them and invisible to every check that only compares sizes.
        int streamsMade = 0;

        if (adsCount > 0 && manifest.Count > 0)
        {
            byte[] mark = System.Text.Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");

            for (int i = 0; i < adsCount && i < manifest.Count; i++)
            {
                string target = Path.Combine(root, manifest[i].Relative) + ":Zone.Identifier";

                try
                {
                    using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    stream.Write(mark, 0, mark.Length);
                    streamsMade++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            Console.WriteLine($"  data streams: {streamsMade:N0}");
        }

        stopwatch.Stop();
        WriteManifest(root, seed, manifest);

        Console.WriteLine();
        Console.WriteLine($"created {manifest.Count:N0} files in {stopwatch.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
    }

    private static string SmallFileRelativePath(int index, int filesPerDir, int fanout)
    {
        long leaf = index / Math.Max(1, filesPerDir);
        int fileInDir = index % Math.Max(1, filesPerDir);
        var parts = new List<string>(4);
        long remaining = leaf;

        while (remaining > 0)
        {
            parts.Add("d" + (remaining % fanout).ToString("D3", CultureInfo.InvariantCulture));
            remaining /= fanout;
        }

        if (parts.Count == 0)
        {
            parts.Add("d000");
        }

        parts.Reverse();
        parts.Insert(0, "small");
        parts.Add($"f{fileInDir:D5}.dat");
        return Path.Combine(parts.ToArray());
    }

    private static void WriteFile(string path, long size, long seed, byte[] block)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 0, FileOptions.SequentialScan);

        long remaining = size;
        long blockIndex = 0;

        while (remaining > 0)
        {
            int count = (int)Math.Min(remaining, block.Length);
            FillBlock(block.AsSpan(0, count), seed, blockIndex);
            stream.Write(block, 0, count);
            remaining -= count;
            blockIndex++;
        }
    }

    private static int Verify(string[] args)
    {
        string root = Path.GetFullPath(Require(args, "--root"));
        string manifestPath = Path.Combine(root, "corpus.manifest");

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"corpusgen: no manifest at {manifestPath}");
            return 1;
        }

        var entries = ReadManifest(manifestPath);
        Console.WriteLine($"verifying {entries.Count:N0} files");
        var stopwatch = Stopwatch.StartNew();
        byte[] expected = new byte[BlockSize];
        byte[] actual = new byte[BlockSize];
        long totalBytes = 0;
        int failures = 0;
        int checkedFiles = 0;

        foreach ((string relative, long size, long fileSeed) in entries)
        {
            string full = Path.Combine(root, relative);

            if (!File.Exists(full))
            {
                Console.Error.WriteLine($"  MISSING  {relative}");
                failures++;
                continue;
            }

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 0, FileOptions.SequentialScan);

            if (stream.Length != size)
            {
                Console.Error.WriteLine($"  SIZE     {relative}: {stream.Length} != {size}");
                failures++;
                continue;
            }

            long remaining = size;
            long blockIndex = 0;
            bool bad = false;

            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, BlockSize);
                FillBlock(expected.AsSpan(0, count), fileSeed, blockIndex);
                int read = stream.ReadAtLeast(actual.AsSpan(0, count), count, throwOnEndOfStream: false);

                if (read != count || !actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count)))
                {
                    Console.Error.WriteLine($"  CONTENT  {relative} at offset {size - remaining}");
                    failures++;
                    bad = true;
                    break;
                }

                remaining -= count;
                blockIndex++;
            }

            if (!bad)
            {
                checkedFiles++;
            }

            totalBytes += size;
        }

        stopwatch.Stop();
        Console.WriteLine($"{checkedFiles:N0} files, {totalBytes / (1024.0 * 1024):F1} MB in {stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine(failures == 0 ? "OK - corpus intact" : $"FAILED - {failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }

    private static void WriteManifest(string root, long seed, List<(string, long, long)> entries)
    {
        using var writer = new StreamWriter(Path.Combine(root, "corpus.manifest"));
        writer.WriteLine("# roboc corpus manifest v1");
        writer.WriteLine($"seed {seed}");

        foreach ((string relative, long size, long fileSeed) in entries)
        {
            writer.Write(relative);
            writer.Write('\t');
            writer.Write(size);
            writer.Write('\t');
            writer.WriteLine(fileSeed);
        }
    }

    private static List<(string, long, long)> ReadManifest(string path)
    {
        var entries = new List<(string, long, long)>();

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("seed ", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length == 3)
            {
                entries.Add((parts[0],
                    long.Parse(parts[1], CultureInfo.InvariantCulture),
                    long.Parse(parts[2], CultureInfo.InvariantCulture)));
            }
        }

        return entries;
    }

    /// <summary>SplitMix64: one multiply-xor chain per 8 bytes, no table lookups.</summary>
    private static void FillBlock(Span<byte> buffer, long fileSeed, long blockIndex)
    {
        ulong state = unchecked((ulong)fileSeed * 0x9E3779B97F4A7C15UL + (ulong)blockIndex);
        int i = 0;

        for (; i + 8 <= buffer.Length; i += 8)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[i..], z);
        }

        if (i < buffer.Length)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;

            Span<byte> tail = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tail, z);
            tail[..(buffer.Length - i)].CopyTo(buffer[i..]);
        }
    }

    private static string Require(string[] args, string name)
        => Get(args, name) ?? throw new ArgumentException($"missing required option {name}");

    private static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name)
        => Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int Int(string[] args, string name, int fallback)
        => Get(args, name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static long Long(string[] args, string name, long fallback)
        => Get(args, name) is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
