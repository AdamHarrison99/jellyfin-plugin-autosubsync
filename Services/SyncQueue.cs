using System.Diagnostics;
using Jellyfin.Plugin.AutoSubSync.Configuration;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// The single concurrency gate for sync work.
public class SyncQueue : IDisposable
{
    // The ceiling the setting itself is clamped to.
    internal const int HardMax = 8;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _semaphore = new(HardMax, HardMax);
    private readonly AdaptiveConcurrency _adaptive;

    // Permits held back to enforce a limit below HardMax.
    private int _ballast;
    private int _inFlight;

    public SyncQueue(AdaptiveConcurrency adaptive)
    {
        _adaptive = adaptive;
    }

    public int InFlight => Volatile.Read(ref _inFlight);

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        long referenceBytes,
        CancellationToken cancellationToken)
    {
        var level = ApplyLimit();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        // ! The count on admission, which is the concurrency this run actually saw.
        var observed = Interlocked.Increment(ref _inFlight);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await work(cancellationToken).ConfigureAwait(false);

            // ! Only a run that finished normally says anything about throughput.
            stopwatch.Stop();
            _adaptive.Report(level, stopwatch.ElapsedMilliseconds, referenceBytes, AutoCeiling(), observed);

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _semaphore.Release();
        }
    }

    // ! Never rebuild the semaphore; workers holding the old one would over-admit.
    private int ApplyLimit()
    {
        var limit = ResolveLimit();
        var desired = HardMax - limit;

        lock (_lock)
        {
            // Shrinking is best-effort: a permit in use is reclaimed once it comes back.
            while (_ballast < desired && _semaphore.Wait(0))
            {
                _ballast++;
            }

            while (_ballast > desired)
            {
                _ballast--;
                _semaphore.Release();
            }
        }

        return limit;
    }

    private int ResolveLimit()
    {
        var configured = Plugin.Instance?.Configuration.MaxConcurrentSyncs ?? PluginConfiguration.AutoConcurrency;

        // An explicit number is an instruction, not a starting point.
        return configured > 0
            ? Math.Clamp(configured, 1, HardMax)
            : _adaptive.CurrentLevel(AutoCeiling());
    }

    private static int AutoCeiling()
    {
        var configuration = Plugin.Instance?.Configuration;
        return Math.Clamp(configuration?.ResolveMaxConcurrentSyncs() ?? 1, 1, HardMax);
    }

    // The semaphore outlives every caller; disposing it would throw for anyone waiting.
    public void Dispose() => GC.SuppressFinalize(this);
}
