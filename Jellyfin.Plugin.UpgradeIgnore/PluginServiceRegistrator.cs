using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.UpgradeIgnore;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton<UpgradeDetectionService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<UpgradeDetectionService>());
    }
}
