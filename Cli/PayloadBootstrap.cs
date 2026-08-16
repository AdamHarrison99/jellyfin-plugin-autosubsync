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
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<PayloadBootstrap> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task _bootstrap = Task.CompletedTask;
    private Task _fetch = Task.CompletedTask;

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
        _bootstrap = Task.Run(RunAsync, _shutdownCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        // ! Settled before Dispose takes the token out from under them. A download past its
        //   cancellation check faults on the next token access.
        try
        {
            await Task.WhenAll(_bootstrap, _fetch)
                .WaitAsync(SettleTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
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

        _fetch = Task.Run(FetchSeConvAsync, _shutdownCts.Token);
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
