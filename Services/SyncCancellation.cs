namespace Jellyfin.Plugin.AutoSubSync.Services;

// One stop for every sync, whatever started it: the scheduled task, a library event, or the API.
public sealed class SyncCancellation : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();

    public CancellationToken Token
    {
        get
        {
            lock (_lock)
            {
                return _cts.Token;
            }
        }
    }

    // ! The old source is cancelled, not disposed. A caller that read Token a moment ago is still
    //   linking against it, and disposing underneath that throws.
    public void StopAll()
    {
        CancellationTokenSource previous;

        lock (_lock)
        {
            previous = _cts;
            _cts = new CancellationTokenSource();
        }

        previous.Cancel();
    }

    // Ties a caller's own token to the shared one, so either can stop the work.
    public CancellationTokenSource LinkWith(CancellationToken cancellationToken)
        => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Token);

    public void Dispose()
    {
        lock (_lock)
        {
            _cts.Dispose();
        }
    }
}
