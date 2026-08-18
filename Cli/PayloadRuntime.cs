using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

public enum PayloadReadiness
{
    Ready,
    Fetching,
    Unavailable
}

public record RuntimeStatus(PayloadReadiness Readiness, string? ExecutablePath, string Message)
{
    public bool IsReady => Readiness == PayloadReadiness.Ready;
}

// Resolves one vendored tool from the payload cache, and installs it when it is missing.
public class PayloadRuntime
{
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(15);

    private readonly PayloadTool _tool;
    private readonly PayloadStore _store;
    private readonly PayloadFetcher _fetcher;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private string? _resolved;
    private PayloadReadiness? _reported;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public PayloadRuntime(PayloadTool tool, PayloadStore store, PayloadFetcher fetcher, ILogger logger)
    {
        _tool = tool;
        _store = store;
        _fetcher = fetcher;
        _logger = logger;
    }

    public PayloadTool Tool => _tool;

    public string ToolVersion => _tool.ToolVersion;

    public string? ExecutablePath => GetStatus().ExecutablePath;

    public bool IsAvailable => GetStatus().IsReady;

    // ! Re-resolves. A payload can arrive at any time, and the cache survives no update.
    public RuntimeStatus GetStatus()
    {
        lock (_gate)
        {
            var status = Resolve();

            if (_reported != status.Readiness)
            {
                _reported = status.Readiness;
                Report(status);
            }

            return status;
        }
    }

    // Resolves, and installs the payload if a previous attempt left it missing.
    public async Task<RuntimeStatus> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var status = GetStatus();

        if (status.Readiness != PayloadReadiness.Unavailable
            || _tool.For(PlatformRid.Current) is null
            || !ClaimAttempt())
        {
            return status;
        }

        var result = await _fetcher.EnsureAsync(_tool, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Could not install the {Tool} payload: {Message}", _tool.Name, result.Message);
        }

        return GetStatus();
    }

    // ! One attempt per cooldown. Item-added events retry on every new file.
    private bool ClaimAttempt()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastAttemptUtc < RetryCooldown)
            {
                return false;
            }

            _lastAttemptUtc = now;
            return true;
        }
    }

    private RuntimeStatus Resolve()
    {
        var rid = PlatformRid.Current;

        if (_resolved is not null && File.Exists(_resolved))
        {
            return Ready(_resolved);
        }

        _resolved = _store.ResolveExecutable(_tool, rid);
        if (_resolved is not null)
        {
            return Ready(_resolved);
        }

        if (_fetcher.IsRunning(_tool))
        {
            return new RuntimeStatus(
                PayloadReadiness.Fetching, null, $"Downloading {_tool.Name} {_tool.ToolVersion}.");
        }

        if (_tool.For(rid) is null)
        {
            return new RuntimeStatus(
                PayloadReadiness.Unavailable,
                null,
                $"No {_tool.Name} payload is published for {PlatformRid.Describe()}.");
        }

        return new RuntimeStatus(
            PayloadReadiness.Unavailable,
            null,
            $"The {_tool.Name} {_tool.ToolVersion} payload has not been downloaded yet.");
    }

    private RuntimeStatus Ready(string path)
        => new(PayloadReadiness.Ready, path, $"{_tool.Name} {_tool.ToolVersion} is ready.");

    private void Report(RuntimeStatus status)
    {
        if (status.IsReady)
        {
            _logger.LogInformation(
                "Using {Tool} {Version} at {Path}", _tool.Name, _tool.ToolVersion, status.ExecutablePath);
            return;
        }

        _logger.LogWarning("{Tool} is not usable: {Message}", _tool.Name, status.Message);
    }
}
