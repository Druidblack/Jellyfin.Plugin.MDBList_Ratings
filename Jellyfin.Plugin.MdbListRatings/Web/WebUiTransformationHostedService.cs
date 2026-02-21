using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Web;

/// <summary>
/// Registers Web UI transformations once the Jellyfin host has started.
/// We intentionally do this via <see cref="IHostedService"/> instead of touching
/// files on disk (e.g. /usr/share/jellyfin/web/index.html).
/// </summary>
internal sealed class WebUiTransformationHostedService : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebUiTransformationHostedService> _logger;
    private bool _registered;
    private bool _disposed;

    public WebUiTransformationHostedService(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<WebUiTransformationHostedService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                _logger.LogDebug("MDBListRatings: plugin instance not available; skipping Web UI transformation registration.");
                return Task.CompletedTask;
            }

            _registered = FileTransformationIntegration.TryRegisterIndexHtmlTransformation(plugin.Id, _serviceProvider, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MDBListRatings: failed to register Web UI transformations.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_registered)
            {
                return Task.CompletedTask;
            }

            var plugin = Plugin.Instance;
            if (plugin is not null)
            {
                FileTransformationIntegration.TryUnregisterIndexHtmlTransformation(plugin.Id, _serviceProvider, _logger);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal; server shutdown.
            _logger.LogDebug(ex, "MDBListRatings: error while unregistering Web UI transformation (non-fatal).");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Best-effort cleanup.
        if (_registered)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin is not null)
                {
                    FileTransformationIntegration.TryUnregisterIndexHtmlTransformation(plugin.Id, _serviceProvider, _logger);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
