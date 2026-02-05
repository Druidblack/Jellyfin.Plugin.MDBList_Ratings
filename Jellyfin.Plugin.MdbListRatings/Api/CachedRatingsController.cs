using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Provides read-only access to the MDBList cached responses.
/// Used by the Web UI injector to display all available ratings on the Details page.
/// </summary>
[ApiController]
[Route("Plugins/MdbListRatings")]
public sealed class CachedRatingsController : ControllerBase
{
    /// <summary>
    /// Returns cached MDBList response (ratings array) for a TMDb id, if available.
    /// </summary>
    /// <param name="type">MDBList content type: movie or show.</param>
    /// <param name="tmdbId">TMDb id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("CachedByTmdb")]
    [Produces("application/json")]
    public async Task<ActionResult<CachedByTmdbResponse>> GetCachedByTmdb(
        [FromQuery] string type,
        [FromQuery] string tmdbId,
        CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new CachedByTmdbResponse { HasCache = false });
        }

        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return BadRequest("Missing required query parameters: type, tmdbId");
        }

        // Only accept known types to avoid cache key ambiguity.
        type = type.Trim().ToLowerInvariant();
        if (type != "movie" && type != "show")
        {
            return BadRequest("Invalid type. Expected: movie|show");
        }

        var env = await plugin.Updater.TryGetCacheEnvelopeAsync(type, tmdbId.Trim(), cancellationToken).ConfigureAwait(false);
        if (env is null || env.Data is null)
        {
            return Ok(new CachedByTmdbResponse { HasCache = false });
        }

        return Ok(new CachedByTmdbResponse
        {
            HasCache = true,
            CachedAtUtc = env.CachedAtUtc,
            Ids = env.Data.Ids,
            Ratings = env.Data.Ratings ?? new List<MdbListRating>()
        });
    }

    public sealed class CachedByTmdbResponse
    {
        [JsonPropertyName("hasCache")]
        public bool HasCache { get; set; }

        [JsonPropertyName("cachedAtUtc")]
        public DateTimeOffset? CachedAtUtc { get; set; }

                [JsonPropertyName("ids")]
        public MdbListIds? Ids { get; set; }

[JsonPropertyName("ratings")]
        public List<MdbListRating> Ratings { get; set; } = new();
    }
}
