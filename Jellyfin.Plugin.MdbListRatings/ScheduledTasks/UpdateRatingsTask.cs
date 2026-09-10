using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MdbListRatings.ScheduledTasks;

/// <summary>
/// Scheduled task to update ratings from MDBList.
/// </summary>
public sealed class UpdateRatingsTask : IScheduledTask
{
    public string Name => "Update MDBList ratings";

    public string Key => "MdbListRatingsUpdate";

    public string Description => "Fetch ratings from MDBList, TVmaze, OMDb, and season/episode ratings from Trakt/TMDb/TVmaze/OMDb, then write them into the standard Jellyfin rating fields.";

    public string Category => "Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var cfg = plugin.Configuration;

        if (cfg.EnableImdbTop250Icon)
        {
            try
            {
                await plugin.Updater.EnsureImdbTop250ReadyAsync(cfg, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                plugin.Log.LogWarning(ex, "Failed to refresh IMDb Top 250 cache before the ratings task.");
            }
        }

        if (string.IsNullOrWhiteSpace(cfg.MdbListApiKey))
        {
            plugin.Log.LogWarning("MDBList API key is empty. MDBList-based sources will be skipped; TVmaze-only Series/Shows/Episodes mappings and Trakt/TMDb season-episode ratings can still be updated if configured.");
        }

        var needsTraktSeason = string.Equals((cfg.SeasonCommunitySource ?? string.Empty).Trim(), "trakt", StringComparison.OrdinalIgnoreCase)
            || string.Equals((cfg.SeasonCommunityFallbackSource ?? string.Empty).Trim(), "trakt", StringComparison.OrdinalIgnoreCase);
        var needsTraktEpisode = string.Equals((cfg.EpisodeCommunitySource ?? string.Empty).Trim(), "trakt", StringComparison.OrdinalIgnoreCase)
            || string.Equals((cfg.EpisodeCommunityFallbackSource ?? string.Empty).Trim(), "trakt", StringComparison.OrdinalIgnoreCase);

        if ((needsTraktSeason || needsTraktEpisode) && string.IsNullOrWhiteSpace(cfg.TraktClientId))
        {
            plugin.Log.LogWarning("Trakt Client ID is empty. Trakt-based season/episode ratings will be skipped.");
        }

        var needsTmdbSeason = string.Equals((cfg.SeasonCommunitySource ?? string.Empty).Trim(), "tmdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals((cfg.SeasonCommunityFallbackSource ?? string.Empty).Trim(), "tmdb", StringComparison.OrdinalIgnoreCase);
        var needsTmdbEpisode = string.Equals((cfg.EpisodeCommunitySource ?? string.Empty).Trim(), "tmdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals((cfg.EpisodeCommunityFallbackSource ?? string.Empty).Trim(), "tmdb", StringComparison.OrdinalIgnoreCase);

        if ((needsTmdbSeason || needsTmdbEpisode) && string.IsNullOrWhiteSpace(cfg.TmdbApiAuth))
        {
            plugin.Log.LogWarning("TMDb API key / Read Access Token is empty. TMDb-based season/episode ratings will be skipped.");
        }

        var needsOmdbEpisode = string.Equals((cfg.EpisodeCommunitySource ?? string.Empty).Trim(), "imdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals((cfg.EpisodeCommunityFallbackSource ?? string.Empty).Trim(), "imdb", StringComparison.OrdinalIgnoreCase);

        if (needsOmdbEpisode && string.IsNullOrWhiteSpace(cfg.OmdbApiKey))
        {
            plugin.Log.LogWarning("OMDb API key is empty. IMDb-based episode ratings via OMDb will be skipped.");
        }

        // Query all Movies, Series, Seasons and Episodes.
        var items = plugin.LibraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode }
        });

        static Guid GetSeriesGroupKey(BaseItem item)
        {
            return item switch
            {
                Season season => season.Series?.Id ?? Guid.Empty,
                Episode episode => episode.Series?.Id ?? Guid.Empty,
                _ => item.Id
            };
        }

        static int GetItemKindOrder(BaseItem item)
        {
            return item switch
            {
                Series => 0,
                Season => 1,
                Episode => 2,
                _ => 3
            };
        }

        static int GetSeasonOrder(BaseItem item)
        {
            return item switch
            {
                Season season => season.IndexNumber ?? 0,
                Episode episode => episode.ParentIndexNumber ?? 0,
                _ => 0
            };
        }

        static int GetEpisodeOrder(BaseItem item)
        {
            return (item as Episode)?.IndexNumber ?? 0;
        }

        // Process each show's seasons and episodes consecutively instead of
        // Jellyfin's default interleaved order,
        // Keeps the age of ratings of Episodes of a show consistent
        // also enables API response reuse while they are still hot in the in-memory caches.
        // Grouped by the series' Guid (not its name) so shows with identical titles across
        // different libraries are never interleaved with each other.
        // Movies are standalone items and are processed last.
        items = items
            .OrderBy(item => item is Movie)
            .ThenBy(GetSeriesGroupKey)
            .ThenBy(GetItemKindOrder)
            .ThenBy(GetSeasonOrder)
            .ThenBy(GetEpisodeOrder)
            .ToArray();

        var total = items.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        var processed = 0;
        var stoppedEarly = false;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = await plugin.Updater.UpdateItemRatingsAsync(item, cancellationToken).ConfigureAwait(false);
                processed++;
                progress.Report(processed * 100.0 / total);

                if (outcome == Ratings.RatingsUpdater.UpdateOutcome.RateLimited)
                {
                    plugin.Log.LogWarning("MDBList daily limit reached (or cooldown active). Task will stop now and continue on the next run.");
                    stoppedEarly = true;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                plugin.Log.LogWarning(ex, "Failed to update ratings for item: {Name}", item.Name);
            }

            // progress is reported inside the try block.
        }

        if (!stoppedEarly)
        {
            progress.Report(100);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run daily at 04:00 by default. (User can change triggers in Dashboard -> Scheduled Tasks.)
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
