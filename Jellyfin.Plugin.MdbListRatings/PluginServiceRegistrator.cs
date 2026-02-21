using Jellyfin.Plugin.MdbListRatings.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MdbListRatings;

/// <summary>
/// Registers background services for this plugin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register a hosted service that registers our Web UI transformation via
        // jellyfin-plugin-file-transformation. This avoids writing to index.html on disk.
        serviceCollection.AddHostedService<WebUiTransformationHostedService>();
    }
}
