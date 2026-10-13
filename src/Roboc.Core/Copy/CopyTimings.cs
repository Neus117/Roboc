namespace Roboc.Core.Copy;

/// <summary>
/// Per-phase timing accumulator.
///
/// Exists because guessing which phase is slow was wrong once on this project: a
/// copy is only a handful of syscalls, so intuition about where the time goes is
/// unreliable. Overhead is a couple of timestamp reads per phase, and the whole
/// thing is off unless a caller asks for it.
/// </summary>
public sealed class CopyTimings
{
    private long _openSource, _querySource, _createDestination, _preallocate;
    private long _transfer, _metadata, _flush, _rename, _close, _files;

    public void AddOpenSource(long t) => Interlocked.Add(ref _openSource, t);
    public void AddQuerySource(long t) => Interlocked.Add(ref _querySource, t);
    public void AddCreateDestination(long t) => Interlocked.Add(ref _createDestination, t);
    public void AddPreallocate(long t) => Interlocked.Add(ref _preallocate, t);
    public void AddTransfer(long t) => Interlocked.Add(ref _transfer, t);
    public void AddMetadata(long t) => Interlocked.Add(ref _metadata, t);
    public void AddFlush(long t) => Interlocked.Add(ref _flush, t);
    public void AddRename(long t) => Interlocked.Add(ref _rename, t);
    public void AddClose(long t) => Interlocked.Add(ref _close, t);
    public void CountFile() => Interlocked.Increment(ref _files);

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public string Report()
    {
        long files = Interlocked.Read(ref _files);
        long total = Interlocked.Read(ref _openSource) + Interlocked.Read(ref _querySource)
                   + Interlocked.Read(ref _createDestination) + Interlocked.Read(ref _preallocate)
                   + Interlocked.Read(ref _transfer) + Interlocked.Read(ref _metadata)
                   + Interlocked.Read(ref _flush) + Interlocked.Read(ref _rename)
                   + Interlocked.Read(ref _close);

        var lines = new List<string>
        {
            $"  files            {files:N0}",
            $"  open source      {Ms(Interlocked.Read(ref _openSource)),10:F1} ms",
            $"  query source     {Ms(Interlocked.Read(ref _querySource)),10:F1} ms",
            $"  create dest      {Ms(Interlocked.Read(ref _createDestination)),10:F1} ms",
            $"  preallocate      {Ms(Interlocked.Read(ref _preallocate)),10:F1} ms",
            $"  transfer         {Ms(Interlocked.Read(ref _transfer)),10:F1} ms",
            $"  set metadata     {Ms(Interlocked.Read(ref _metadata)),10:F1} ms",
            $"  flush            {Ms(Interlocked.Read(ref _flush)),10:F1} ms",
            $"  rename           {Ms(Interlocked.Read(ref _rename)),10:F1} ms",
            $"  close            {Ms(Interlocked.Read(ref _close)),10:F1} ms",
            $"  ------------------------------",
            $"  accounted        {Ms(total),10:F1} ms",
        };

        if (files > 0)
        {
            lines.Add($"  per file         {Ms(total) / files,10:F3} ms");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
