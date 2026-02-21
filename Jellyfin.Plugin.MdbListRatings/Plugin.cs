using System;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.MdbListRatings.Configuration;
using Jellyfin.Plugin.MdbListRatings.Web;

namespace Jellyfin.Plugin.MdbListRatings;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public override string Name => "MDBList Ratings";

    public override Guid Id => Guid.Parse("ab96f8b5-45ef-44be-81d6-99bc01e26b9d");

    internal ILibraryManager LibraryManager { get; }
    internal IHttpClientFactory HttpClientFactory { get; }
    internal ILoggerFactory LoggerFactory { get; }
    internal ILogger<Plugin> Log { get; }

    internal string PluginDataPath { get; }

    internal Ratings.RatingsUpdater Updater { get; }

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        LibraryManager = libraryManager;
        HttpClientFactory = httpClientFactory;
        LoggerFactory = loggerFactory;
        Log = loggerFactory.CreateLogger<Plugin>();

        // Store persistent cache and state under PluginConfigurationsPath/MdbListRatings.
        PluginDataPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "MdbListRatings");
        Directory.CreateDirectory(PluginDataPath);

        var cacheDir = Path.Combine(PluginDataPath, "cache");
        var statePath = Path.Combine(PluginDataPath, "state.json");

        Updater = new Ratings.RatingsUpdater(httpClientFactory, cacheDir, statePath, loggerFactory.CreateLogger<Ratings.RatingsUpdater>());

        // Web UI enhancement (rating source icons, optional “all ratings” display, etc.)
        // is registered via jellyfin-plugin-file-transformation at server startup.
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // "configPage" must match the file name without extension.
        return new[]
        {
            new PluginPageInfo
            {
                Name = "configPage",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
