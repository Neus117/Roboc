using System.Text;
using Roboc.Core.Model;

namespace Roboc.Core.Journal;

/// <summary>
/// Append-only job log, so an interrupted copy can be continued instead of
/// restarted.
///
/// Format, little-endian throughout:
///
///   header : magic "TSJ1" | version | key | srcRoot | dstRoot | headerCrc
///   record : kind(1) | payloadLength(4) | payloadCrc(4) | payload
///
/// Every record carries its own CRC. A log that is truncated mid-record -- the
/// normal outcome of a power loss -- is detected at that point and everything
/// before it is still trusted, which is the only property that makes the log
/// worth having: a resume that trusts a half-written record copies the wrong
/// bytes into the right file.
///
/// Records are flushed periodically rather than per record. That is safe rather
/// than merely fast, and the reason is worth stating: a lost <c>Done</c> record
/// costs one recopied file, and a lost <c>Checkpoint</c> record can only ever
/// move the resume point *backwards*, because the copier calls FlushFileBuffers
/// on the data before it reports the offset. Losing journal records can therefore
/// cause repeated work but never corrupt output.
/// </summary>
public sealed class JobJournal : IJobJournal
{
    private const uint Magic = 0x314A5354;   // "TSJ1" little-endian
    private const int Version = 1;
    private const long FlushIntervalBytes = 256 * 1024;

    private const byte KindStarted = 1;
    private const byte KindCheckpoint = 2;
    private const byte KindDone = 3;
    private const byte KindError = 4;

    private readonly FileStream _stream;
    private readonly object _gate = new();
    private readonly Dictionary<string, ItemState> _state;
    private readonly string _path;
    private long _unflushed;
    private bool _disposed;

    private readonly record struct ItemState(ItemOutcome Outcome, long Offset);

    private JobJournal(FileStream stream, string path, Dictionary<string, ItemState> state, bool resuming)
    {
        _stream = stream;
        _path = path;
        _state = state;
        HasResumeState = resuming;
    }

    public bool HasResumeState { get; }

    public string Path => _path;

    /// <summary>Items already finished in an earlier run.</summary>
    public int CompletedCount => _state.Count;

    // ------------------------------------------------------------- open / close

    /// <summary>
    /// Opens the job log for the given source set and destination, replaying
    /// whatever an earlier run left behind.
    ///
    /// Returns null when no writable location exists. The caller then falls back
    /// to <see cref="NullJobJournal"/>: resume is a convenience, and a read-only
    /// profile must not be able to fail the copy the user actually asked for.
    /// </summary>
    public static JobJournal? TryOpen(
        string destination, IReadOnlyList<string> sources, string? jobId, out string location)
    {
        string key = ComputeKey(destination, sources, jobId);
        location = string.Empty;

        foreach (string directory in CandidateDirectories(destination))
        {
            try
            {
                Directory.CreateDirectory(directory);

                // Hidden wherever it lands, and the parent too. Hiding only the jobs
                // folder left ".roboc" itself plainly visible beside the
                // executable, which is the thing the user actually sees.
                Interop.Kernel32.SetFileAttributes(directory, Interop.NativeConst.FILE_ATTRIBUTE_HIDDEN);

                string? parent = System.IO.Path.GetDirectoryName(directory);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    Interop.Kernel32.SetFileAttributes(parent, Interop.NativeConst.FILE_ATTRIBUTE_HIDDEN);
                }

                string path = System.IO.Path.Combine(directory, key + ".tsj");
                return OpenAt(path, out location);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        return null;
    }

    private static JobJournal OpenAt(string path, out string location)
    {
        location = path;
        var state = new Dictionary<string, ItemState>(StringComparer.OrdinalIgnoreCase);
        bool resuming = false;

        if (File.Exists(path))
        {
            resuming = Replay(path, state);
        }

        // FileShare.Delete matters: the log is deleted at the end of a clean run
        // while this handle is still open, and Windows refuses to delete a file
        // whose handle did not share delete access. It fails quietly, which is how
        // a completed copy ended up leaving its job directory behind.
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024, FileOptions.None);

        if (stream.Length == 0)
        {
            WriteHeader(stream, path);
        }

        return new JobJournal(stream, path, state, resuming);
    }

    /// <summary>
    /// A folder of this tool's own, beside the executable.
    ///
    /// The destination was tried first, on the reasoning that it is writable
    /// whenever a copy is possible at all. That was a mistake in hindsight: it put
    /// the tool's internal state inside the folder the user had just pasted into,
    /// where it appeared as an unfamiliar directory and then vanished a second
    /// later when the job finished. State belongs with the program.
    ///
    /// The destination remains as a fallback for a read-only installation, and the
    /// user profile after that.
    /// </summary>
    private static IEnumerable<string> CandidateDirectories(string destination)
    {
        string? exeDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);

        if (!string.IsNullOrEmpty(exeDirectory))
        {
            yield return System.IO.Path.Combine(exeDirectory, ".roboc", "jobs");
        }

        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roboc", "jobs");

        if (!string.IsNullOrWhiteSpace(destination))
        {
            yield return System.IO.Path.Combine(destination, ".roboc-jobs");
        }
    }

    /// <summary>
    /// Identity of the job. A re-run of the same copy must land on the same key,
    /// which is what makes resume automatic rather than something the user has to
    /// ask for.
    /// </summary>
    private static string ComputeKey(string destination, IReadOnlyList<string> sources, string? jobId)
    {
        var builder = new StringBuilder();
        builder.Append(jobId ?? string.Empty).Append('\u0001');
        builder.Append(destination).Append('\u0001');

        // Sorted so the key does not depend on selection order.
        var ordered = new List<string>(sources);
        ordered.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string source in ordered)
        {
            builder.Append(source).Append('\u0001');
        }

        byte[] bytes = Encoding.Unicode.GetBytes(builder.ToString());
        uint hash = Crc32.Compute(bytes);
        return hash.ToString("x8");
    }

    // ---------------------------------------------------------------- replaying

    /// <summary>Reads every intact record. Stops at the first damaged one, which
    /// is where a crash would have left the file.</summary>
    private static bool Replay(string path, Dictionary<string, ItemState> state)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.Unicode);

            if (stream.Length < 12 || reader.ReadUInt32() != Magic)
            {
                return false;
            }

            _ = reader.ReadInt32();                     // version
            _ = reader.ReadString();                    // key
            _ = reader.ReadString();                    // srcRoot
            _ = reader.ReadString();                    // dstRoot
            _ = reader.ReadUInt32();                    // headerCrc

            long recordsStart = stream.Position;
            int intact = CountIntactRecords(stream, recordsStart);

            if (intact == 0)
            {
                return false;
            }

            stream.Position = recordsStart;

            for (int i = 0; i < intact; i++)
            {
                byte kind = reader.ReadByte();
                int length = reader.ReadInt32();
                _ = reader.ReadUInt32();                // payloadCrc, already checked
                byte[] payload = reader.ReadBytes(length);

                Apply(kind, payload, state);
            }

            // Anything a previous run got through counts as resume state, even if
            // it was only a start record; the caller reports it either way.
            return state.Count > 0 || intact > 0;
        }
        // EndOfStreamException derives from IOException and is covered here.
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Walks the records once to find how many are intact, so the replay pass can
    /// stop before a damaged tail instead of throwing part way through.
    /// </summary>
    private static int CountIntactRecords(FileStream stream, long start)
    {
        int intact = 0;
        long position = start;

        // Hoisted out of the loop. stackalloc inside a loop allocates a fresh block on
        // every pass and none of it is released until the method returns -- so this
        // nine-byte header cost nine bytes of stack per record, and a journal with a
        // million records would have asked for nine megabytes of a one-megabyte stack.
        // The buffer is overwritten on each read, so there is nothing to gain from a new
        // one per iteration.
        Span<byte> header = stackalloc byte[9];

        while (position + 9 <= stream.Length)
        {
            stream.Position = position;

            if (stream.Read(header) != 9)
            {
                break;
            }

            byte kind = header[0];
            int length = BitConverter.ToInt32(header[1..5]);
            uint storedCrc = BitConverter.ToUInt32(header[5..9]);

            if (length < 0 || position + 9 + length > stream.Length || !IsKnownKind(kind))
            {
                break;
            }

            byte[] payload = new byte[length];
            if (stream.Read(payload) != length)
            {
                break;
            }

            // The CRC covers kind + payloadLength + payload, so it is recomputed
            // over exactly that. A record whose tail never reached the disk fails
            // here and ends the walk, which is what a crash looks like.
            var combined = new byte[5 + length];
            header[..5].CopyTo(combined);
            payload.CopyTo(combined, 5);

            if (Crc32.Compute(combined) != storedCrc)
            {
                break;
            }

            intact++;
            position += 9 + length;
        }

        return intact;
    }

    private static bool IsKnownKind(byte kind)
        => kind is KindStarted or KindCheckpoint or KindDone or KindError;

    private static void Apply(byte kind, byte[] payload, Dictionary<string, ItemState> state)
    {
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.Unicode);

        switch (kind)
        {
            case KindStarted:
            {
                string relative = reader.ReadString();
                state[relative] = new ItemState(ItemOutcome.Failed, 0);
                break;
            }

            case KindCheckpoint:
            {
                string relative = reader.ReadString();
                long offset = reader.ReadInt64();
                state[relative] = new ItemState(ItemOutcome.Resumed, offset);
                break;
            }

            case KindDone:
            {
                string relative = reader.ReadString();
                var outcome = (ItemOutcome)reader.ReadByte();
                state[relative] = new ItemState(outcome, 0);
                break;
            }

            case KindError:
                // Failures are not completion; leave whatever state exists so the
                // next run retries the item.
                break;
        }
    }

    // -------------------------------------------------------------- IJobJournal

    public ItemOutcome? Lookup(string relativePath)
    {
        lock (_gate)
        {
            return _state.TryGetValue(relativePath, out ItemState item) && item.Offset == 0
                ? item.Outcome
                : null;
        }
    }

    public long GetCheckpoint(string relativePath)
    {
        lock (_gate)
        {
            return _state.TryGetValue(relativePath, out ItemState item) ? item.Offset : 0;
        }
    }

    public void RecordStarted(string relativePath, long size)
    {
        lock (_gate)
        {
            _state[relativePath] = new ItemState(ItemOutcome.Failed, 0);

            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(relativePath);
                writer.Write(size);
            }

            Write(KindStarted, payload.ToArray());
        }
    }

    public void RecordCheckpoint(string relativePath, long offset)
    {
        lock (_gate)
        {
            _state[relativePath] = new ItemState(ItemOutcome.Resumed, offset);

            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(relativePath);
                writer.Write(offset);
            }

            Write(KindCheckpoint, payload.ToArray());
        }
    }

    public void RecordCompleted(string relativePath, ItemOutcome outcome, long bytes)
    {
        lock (_gate)
        {
            _state[relativePath] = new ItemState(outcome, 0);

            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(relativePath);
                writer.Write((byte)outcome);
                writer.Write(bytes);
            }

            Write(KindDone, payload.ToArray());
        }
    }

    public void RecordError(JobError error)
    {
        lock (_gate)
        {
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(error.Path);
                writer.Write((byte)error.Kind);
                writer.Write(error.NativeCode);
                writer.Write(error.Message);
            }

            Write(KindError, payload.ToArray());
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _stream.Flush(flushToDisk: true);
            _unflushed = 0;
        }
    }

    // ------------------------------------------------------------------ writing

    private void Write(byte kind, byte[] payload)
    {
        Span<byte> header = stackalloc byte[9];
        header[0] = kind;
        BitConverter.TryWriteBytes(header[1..5], payload.Length);

        var combined = new byte[5 + payload.Length];
        header[..5].CopyTo(combined);
        payload.CopyTo(combined, 5);
        BitConverter.TryWriteBytes(header[5..9], Crc32.Compute(combined));

        _stream.Write(header);
        _stream.Write(payload);

        _unflushed += 9 + payload.Length;

        if (_unflushed >= FlushIntervalBytes)
        {
            _stream.Flush(flushToDisk: false);
            _unflushed = 0;
        }
    }

    private static void WriteHeader(FileStream stream, string path)
    {
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(System.IO.Path.GetFileNameWithoutExtension(path));
        writer.Write(string.Empty);
        writer.Write(string.Empty);
        writer.Write(0u);
        writer.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _stream.Flush(flushToDisk: true);
            }
            catch (IOException) { }

            _stream.Dispose();
        }
    }
}

/// <summary>
/// CRC-32 (IEEE). Small enough to keep beside its only user, and it avoids a
/// package dependency for the one thing the journal needs: detecting a record
/// that a crash left half-written.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
