using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.EventHandlers;
using Jellyfin.Plugin.AutoSubSync.Services;
using Jellyfin.Plugin.AutoSubSync.Subtitles;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoSubSync;

// Scheduled tasks are discovered via IScheduledTask and need no registration here.
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginPaths>();
        serviceCollection.AddSingleton<ISyncStore, SyncStore>();
        serviceCollection.AddSingleton<BackupVault>();

        serviceCollection.AddSingleton<PayloadStore>();
        serviceCollection.AddSingleton<PayloadFetcher>();
        serviceCollection.AddSingleton<AssyRuntime>();
        serviceCollection.AddSingleton<IAssyCliRunner, AssyCliRunner>();

        serviceCollection.AddSingleton<SeConvRuntime>();
        serviceCollection.AddSingleton<ISeConvRunner, SeConvRunner>();

        serviceCollection.AddSingleton<ISubtitleExtractor, FfmpegSubtitleExtractor>();
        serviceCollection.AddSingleton<ImageSubtitleExtractor>();
        serviceCollection.AddSingleton<SubtitleDiscoveryService>();
        serviceCollection.AddSingleton<SubtitlePlacer>();

        serviceCollection.AddSingleton<LibraryScopeResolver>();
        serviceCollection.AddSingleton<AdaptiveConcurrency>();
        serviceCollection.AddSingleton<SyncQueue>();
        serviceCollection.AddSingleton<SyncOrchestrator>();
        serviceCollection.AddSingleton<SubtitleDeduplicator>();
        serviceCollection.AddSingleton<RollbackService>();

        serviceCollection.AddHostedService<PayloadBootstrap>();
        serviceCollection.AddHostedService<LibraryEventHandler>();
    }
}
