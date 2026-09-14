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
        // API credentials can be carried in query strings (MDBList, OMDb, TMDb v3 and WhatsOn).
        // The default IHttpClientFactory logger records request URIs, so requests that may contain
        // credentials use a dedicated client with the default logging handlers removed.
        serviceCollection
            .AddHttpClient(SecretHttpClient.Name)
            .RemoveAllLoggers();

        // Simkl /redirect returns the catalog id only in a 301 Location header. Do not
        // follow it to the human-facing HTML page. Logging is also disabled because Simkl
        // requires client_id as a URL parameter on the redirect/detail requests.
        serviceCollection
            .AddHttpClient(SecretHttpClient.SimklNoRedirectName)
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            .RemoveAllLoggers();

        // Register a hosted service that registers our Web UI transformation via
        // jellyfin-plugin-file-transformation. This avoids writing to index.html on disk.
        serviceCollection.AddHostedService<WebUiTransformationHostedService>();
    }
}
