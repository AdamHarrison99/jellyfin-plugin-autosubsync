using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// The alignment engine's payload.
public class AssyRuntime : PayloadRuntime
{
    public AssyRuntime(PayloadStore store, PayloadFetcher fetcher, ILogger<AssyRuntime> logger)
        : base(PayloadManifest.AssyCli, store, fetcher, logger)
    {
    }
}
