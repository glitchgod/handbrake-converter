using Jellyfin.Plugin.HandBrakeConverter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.HandBrakeConverter;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton<JobStore>();
        serviceCollection.AddSingleton<HandBrakeLocator>();
        serviceCollection.AddSingleton<TrashStore>();
        serviceCollection.AddSingleton<MediaProbe>();
        serviceCollection.AddSingleton<HistoryStore>();
        serviceCollection.AddSingleton<HandBrakeCapabilities>();
        serviceCollection.AddSingleton<TranscodeStore>();
        serviceCollection.AddHostedService<TranscodeLogWatcher>();
        serviceCollection.AddSingleton<PlaybackWatcher>();
        serviceCollection.AddSingleton<BacklogScanner>();
        serviceCollection.AddSingleton<ConversionService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ConversionService>());
    }
}
