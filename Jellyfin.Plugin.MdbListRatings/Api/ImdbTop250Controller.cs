using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

[ApiController]
[Authorize]
[Route("Plugins/MdbListRatings")]
public sealed class ImdbTop250Controller : ControllerBase
{
    [HttpGet("ImdbTop250Index")]
    [Produces("application/json")]
    public async Task<ActionResult<ImdbTop250IndexResponse>> GetImdbTop250Index(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new ImdbTop250IndexResponse { Enabled = false, HasCache = false });
        }

        var cfg = plugin.Configuration;
        if (!cfg.EnableImdbTop250Icon)
        {
            return Ok(new ImdbTop250IndexResponse { Enabled = false, HasCache = false });
        }

        var snapshot = await plugin.Updater.TryGetImdbTop250SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Ok(new ImdbTop250IndexResponse { Enabled = true, HasCache = false });
        }

        return Ok(new ImdbTop250IndexResponse
        {
            Enabled = true,
            HasCache = true,
            CachedAtUtc = snapshot.CachedAtUtc,
            Ids = snapshot.Ids ?? new List<string>()
        });
    }

    public sealed class ImdbTop250IndexResponse
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("hasCache")]
        public bool HasCache { get; set; }

        [JsonPropertyName("cachedAtUtc")]
        public DateTimeOffset? CachedAtUtc { get; set; }

        [JsonPropertyName("ids")]
        public List<string> Ids { get; set; } = new();
    }
}
