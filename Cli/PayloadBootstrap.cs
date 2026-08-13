using Jellyfin.Plugin.AutoSubSync.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Installs the pinned payloads at startup when they are not already on disk.
public class PayloadBootstrap : IHostedService, IDisposable
{
    private readonly AssyRuntime _assy;
    private readonly SeConvRuntime _seConv;
    private readonly ILogger<PayloadBootstrap> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

    public PayloadBootstrap(
        AssyRuntime assy,
        SeConvRuntime seConv,
        ILogger<PayloadBootstrap> logger)
    {
        _assy = assy;
        _seConv = seConv;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        // ! Never block startup on a download that runs to hundreds of megabytes.
        _ = Task.Run(RunAsync, _shutdownCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        _shutdownCts.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        try
        {
            await _assy.EnsureReadyAsync(_shutdownCts.Token).ConfigureAwait(false);

            // The OCR payload is only worth its download to a server that has OCR turned on.
            if (NeedsOcr(Plugin.Instance?.Configuration))
            {
                await _seConv.EnsureReadyAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payload bootstrap failed");
        }
    }

    // ! Turning the setting on is the trigger. Waiting for the first file that needs it strands
    //   the admin on "not downloaded yet" with nothing to do about it.
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration config || !NeedsOcr(config))
        {
            return;
        }

        _ = Task.Run(FetchSeConvAsync, _shutdownCts.Token);
    }

    private async Task FetchSeConvAsync()
    {
        try
        {
            await _seConv.EnsureReadyAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not install the OCR payload after a settings change");
        }
    }

    private static bool NeedsOcr(PluginConfiguration? config)
        => config is not null && (config.ConvertImageSubtitles || config.RemoveHearingImpairedTags);

    public void Dispose()
    {
        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
