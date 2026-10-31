using Jellyfin.Plugin.Sillon.Api;
using Jellyfin.Plugin.Sillon.Catalog;
using Jellyfin.Plugin.Sillon.Configuration;
using Jellyfin.Plugin.Sillon.Library;
using Jellyfin.Plugin.Sillon.Lidarr;
using Jellyfin.Plugin.Sillon.Push;
using Jellyfin.Plugin.Sillon.Requests;
using Jellyfin.Plugin.Sillon.Storage;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sillon;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // resolved lazily: the plugin instance exists by the time the first request or task needs a service
        serviceCollection.AddSingleton<Func<PluginConfiguration>>(_ => () => Plugin.Instance?.Configuration ?? new PluginConfiguration());
        serviceCollection.AddSingleton(_ => new RequestStore(DataFolder()));
        serviceCollection.AddSingleton(_ => new DeviceStore(DataFolder()));
        serviceCollection.AddSingleton<IJellyfinLibrary, JellyfinLibrary>();
        serviceCollection.AddSingleton<ILidarrProvider>(sp =>
            new LidarrProvider(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<Func<PluginConfiguration>>()));
        serviceCollection.AddSingleton<IPushSender>(sp => new ApnsClient(ApnsClient.CreateHttpClient(), sp.GetRequiredService<ILogger<ApnsClient>>()));
        serviceCollection.AddSingleton(_ => new ServerKeyStore(DataFolder()));
        serviceCollection.AddSingleton(_ => new RelayState(DataFolder()));
        serviceCollection.AddSingleton(sp => new RelayClient(
            RelayClient.CreateHttpClient(),
            sp.GetRequiredService<ServerKeyStore>(),
            sp.GetRequiredService<RelayState>(),
            sp.GetRequiredService<DeviceStore>(),
            sp.GetRequiredService<Func<PluginConfiguration>>(),
            sp.GetRequiredService<ILogger<RelayClient>>()));
        serviceCollection.AddSingleton(sp => new ApnsNotifier(
            sp.GetRequiredService<DeviceStore>(),
            sp.GetRequiredService<IPushSender>(),
            sp.GetRequiredService<Func<PluginConfiguration>>(),
            Environment.GetEnvironmentVariable,
            sp.GetRequiredService<ILogger<ApnsNotifier>>(),
            sp.GetRequiredService<RelayClient>()));
        serviceCollection.AddSingleton<INotifier>(sp => sp.GetRequiredService<ApnsNotifier>());
        serviceCollection.AddSingleton(sp => new AdminService(
            sp.GetRequiredService<RequestStore>(),
            sp.GetRequiredService<DeviceStore>(),
            sp.GetRequiredService<IJellyfinLibrary>(),
            sp.GetRequiredService<ILidarrProvider>(),
            sp.GetRequiredService<ApnsNotifier>(),
            sp.GetRequiredService<Func<PluginConfiguration>>(),
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty));
        serviceCollection.AddSingleton<IMusicBrainzApi>(sp => new MusicBrainzClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(NamedClient.Default),
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            sp.GetRequiredService<ILogger<MusicBrainzClient>>()));
        serviceCollection.AddSingleton<ITrackHints>(sp => new DeezerHints(
            DeezerHints.CreateHttpClient(),
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            sp.GetRequiredService<ILogger<DeezerHints>>()));
        serviceCollection.AddSingleton(sp => new CatalogService(
            sp.GetRequiredService<ILidarrProvider>(),
            sp.GetRequiredService<RequestStore>(),
            sp.GetRequiredService<IJellyfinLibrary>(),
            sp.GetRequiredService<Func<PluginConfiguration>>(),
            sp.GetRequiredService<ILogger<CatalogService>>(),
            sp.GetRequiredService<IMusicBrainzApi>(),
            hints: sp.GetRequiredService<ITrackHints>()));
        serviceCollection.AddSingleton(sp => new RequestService(
            sp.GetRequiredService<RequestStore>(),
            sp.GetRequiredService<ILidarrProvider>(),
            sp.GetRequiredService<IJellyfinLibrary>(),
            sp.GetRequiredService<INotifier>(),
            sp.GetRequiredService<Func<PluginConfiguration>>(),
            sp.GetRequiredService<ILogger<RequestService>>(),
            shutdown: sp.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? default));
        serviceCollection.AddSingleton<IScheduledTask, ReconcileTask>();
    }

    private static string DataFolder()
    {
        var folder = Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The Sillon plugin is not loaded.");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
