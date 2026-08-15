namespace Jellyfin.Plugin.AutoSubSync.Services;

// Serializes every entry point onto one worker per subtitle target.
public sealed class TargetLocks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    internal int TrackedCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<IDisposable> AcquireAsync(
        Guid itemId,
        string targetKey,
        CancellationToken cancellationToken)
    {
        var key = Key(itemId, targetKey);
        var entry = Retain(key);

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry);
            throw;
        }

        return new Lease(this, key, entry);
    }

    internal static string Key(Guid itemId, string targetKey)
        => itemId.ToString("N") + '\n' + targetKey;

    private Entry Retain(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Waiters++;
            return entry;
        }
    }

    private void Release(string key, Entry entry)
    {
        lock (_lock)
        {
            if (--entry.Waiters > 0)
            {
                return;
            }

            _entries.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Waiters { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly TargetLocks _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private bool _released;

        public Lease(TargetLocks owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            // ! Signal before dropping the entry; the drop can dispose the semaphore.
            _entry.Semaphore.Release();
            _owner.Release(_key, _entry);
        }
    }
}
