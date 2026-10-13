using Roboc.Core.Model;

namespace Roboc.Core.Journal;

/// <summary>
/// Durable record of what a job has already achieved, so an interrupted run can be
/// continued rather than restarted. The engine only ever talks to this interface.
/// </summary>
public interface IJobJournal : IDisposable
{
    bool HasResumeState { get; }
    ItemOutcome? Lookup(string relativePath);
    long GetCheckpoint(string relativePath);
    void RecordStarted(string relativePath, long size);
    void RecordCheckpoint(string relativePath, long offset);
    void RecordCompleted(string relativePath, ItemOutcome outcome, long bytes);
    void RecordError(JobError error);
    void Flush();
}

/// <summary>Discards everything. Used until the real binary log lands, so the
/// engine never needs a null check.</summary>
public sealed class NullJobJournal : IJobJournal
{
    public static NullJobJournal Instance { get; } = new();

    public bool HasResumeState => false;
    public ItemOutcome? Lookup(string relativePath) => null;
    public long GetCheckpoint(string relativePath) => 0;
    public void RecordStarted(string relativePath, long size) { }
    public void RecordCheckpoint(string relativePath, long offset) { }
    public void RecordCompleted(string relativePath, ItemOutcome outcome, long bytes) { }
    public void RecordError(JobError error) { }
    public void Flush() { }
    public void Dispose() { }
}
