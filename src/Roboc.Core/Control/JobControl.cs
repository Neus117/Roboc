namespace Roboc.Core.Control;

/// <summary>
/// Pause / resume / cancel, shared by the engine and the UI.
///
/// Pause is a gate rather than a token: workers park on it between chunks, so a
/// pause takes effect within one chunk even mid-file. Cancellation releases the
/// gate so paused workers can observe it.
/// </summary>
public sealed class JobControl : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public CancellationToken Token => _cts.Token;
    public bool IsPaused => !_gate.IsSet;
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    public void Pause() => _gate.Reset();
    public void Resume() => _gate.Set();

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Racing with shutdown; already stopping.
        }

        _gate.Set();
    }

    public void WaitWhilePaused()
    {
        if (!_gate.IsSet)
        {
            _gate.Wait(_cts.Token);
        }
    }

    public void ThrowIfCancellationRequested() => _cts.Token.ThrowIfCancellationRequested();

    /// <summary>Cheap combined check for inner loops.</summary>
    public bool ShouldStop()
    {
        if (_cts.IsCancellationRequested)
        {
            return true;
        }

        if (!_gate.IsSet)
        {
            _gate.Wait(_cts.Token);
        }

        return _cts.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Dispose();
        _cts.Dispose();
    }
}
