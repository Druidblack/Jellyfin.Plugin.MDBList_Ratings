using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Debug helpers for manually testing rating updates on a single library item,
/// without having to run the full library scheduled task (and burn through daily API limits).
/// </summary>
[ApiController]
[Route("Plugins/MdbListRatings")]
public sealed class DebugController : ControllerBase
{
    public sealed class DebugRefreshItemResponse
    {
        [JsonPropertyName("found")]
        public bool Found { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        [JsonPropertyName("communityRating")]
        public float? CommunityRating { get; set; }

        [JsonPropertyName("criticRating")]
        public float? CriticRating { get; set; }

        [JsonPropertyName("children")]
        public List<DebugRefreshChildResult>? Children { get; set; }
    }

    public sealed class DebugRefreshChildResult
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        [JsonPropertyName("communityRating")]
        public float? CommunityRating { get; set; }

        [JsonPropertyName("criticRating")]
        public float? CriticRating { get; set; }
    }

    /// <summary>
    /// Fetches and applies ratings for a single item (Movie/Series/Season/Episode) by its Jellyfin item id.
    /// Intended for manual testing; bypasses the library-wide scheduled task loop.
    /// </summary>
    /// <param name="itemId">Jellyfin item id (guid).</param>
    /// <param name="includeSeasonsAndEpisodes">
    /// When <paramref name="itemId"/> refers to a Series, also fetch and apply ratings for all of its
    /// Seasons and Episodes (in addition to the Series itself).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("DebugRefreshItem")]
    [Produces("application/json")]
    public async Task<ActionResult<DebugRefreshItemResponse>> RefreshItem(
        [FromQuery] Guid itemId,
        [FromQuery] bool includeSeasonsAndEpisodes,
        CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new DebugRefreshItemResponse { Found = false });
        }

        if (itemId == Guid.Empty)
        {
            return BadRequest("Missing required query parameter: itemId");
        }

        var item = plugin.LibraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Ok(new DebugRefreshItemResponse { Found = false });
        }

        var outcome = await plugin.Updater.UpdateItemRatingsAsync(item, cancellationToken).ConfigureAwait(false);

        var response = new DebugRefreshItemResponse
        {
            Found = true,
            Name = item.Name,
            Outcome = outcome.ToString(),
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating
        };

        if (includeSeasonsAndEpisodes && item is Series)
        {
            var children = plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { itemId },
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Season, BaseItemKind.Episode }
            })
            .OrderBy(c => c is Episode ? 1 : 0)
            .ThenBy(c => c.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(c => c.IndexNumber ?? int.MaxValue)
            .ToList();

            var childResults = new List<DebugRefreshChildResult>(children.Count);
            foreach (var child in children)
            {
                var childOutcome = await plugin.Updater.UpdateItemRatingsAsync(child, cancellationToken).ConfigureAwait(false);
                childResults.Add(new DebugRefreshChildResult
                {
                    Type = child is Season ? "Season" : child is Episode ? "Episode" : child.GetType().Name,
                    Name = child.Name,
                    Outcome = childOutcome.ToString(),
                    CommunityRating = child.CommunityRating,
                    CriticRating = child.CriticRating
                });
            }

            response.Children = childResults;
        }

        return Ok(response);
    }
}
