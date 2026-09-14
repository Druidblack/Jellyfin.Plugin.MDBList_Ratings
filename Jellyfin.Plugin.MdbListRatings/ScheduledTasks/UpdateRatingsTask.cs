using System;
using System.Collections.Generic;
using System.IO;
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

    public string Description => "Fetch ratings from MDBList, WhatsOn, TVmaze, OMDb, and season/episode ratings from Trakt/TMDb/WhatsOn/TVmaze/OMDb, then write them into the standard Jellyfin rating fields.";

    public string Category => "Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var cfg = plugin.Configuration;

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

        if (string.IsNullOrWhiteSpace(cfg.WhatsOnApiKey))
        {
            plugin.Log.LogWarning("WhatsOn API key is empty. Movie/Series cache enrichment still runs through the anonymous WhatsOn tier and is subject to its lower hourly limit.");
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

        // Persist a continuation cursor for quota-limited OMDb episode backfills. Rotating the
        // deterministic item order means the next successful OMDb window starts where the
        // previous quota-limited run stopped instead of spending quota on the same early episodes.
        var progressStore = new UpdateRatingsProgressStore(Path.Combine(plugin.PluginDataPath, "update-ratings-progress.json"), plugin.Log);
        await progressStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var existingOmdbCursor = progressStore.OmdbEpisodeResumeItemId;
        if (existingOmdbCursor.HasValue)
        {
            var resumeIndex = Array.FindIndex(items.ToArray(), item => item is Episode && item.Id == existingOmdbCursor.Value);
            if (resumeIndex >= 0)
            {
                items = items.Skip(resumeIndex).Concat(items.Take(resumeIndex)).ToArray();
                plugin.Log.LogInformation("Resuming OMDb episode backfill from saved cursor at item {ItemId} ({Name}).", existingOmdbCursor.Value, items[0].Name);
            }
            else
            {
                plugin.Log.LogWarning("Saved OMDb episode continuation item {ItemId} no longer exists; clearing the cursor.", existingOmdbCursor.Value);
                await progressStore.SetOmdbEpisodeResumeAsync(null, cancellationToken).ConfigureAwait(false);
                existingOmdbCursor = null;
            }
        }

        var total = items.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        var processed = 0;
        var stoppedEarly = false;
        var omdbQuotaHitThisRun = false;
        Guid? omdbQuotaItemId = null;
        string? omdbQuotaItemName = null;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = await plugin.Updater.UpdateItemRatingsAsync(item, cancellationToken).ConfigureAwait(false);
                processed++;
                progress.Report(processed * 100.0 / total);

                if (outcome == Ratings.RatingsUpdater.UpdateOutcome.OmdbRateLimited)
                {
                    // Save only the first item where the quota was actually reached. Later items
                    // continue using configured fallbacks while OMDb is on cooldown.
                    if (!omdbQuotaHitThisRun && item is Episode)
                    {
                        omdbQuotaHitThisRun = true;
                        omdbQuotaItemId = item.Id;
                        omdbQuotaItemName = item.Name;
                        await progressStore.SetOmdbEpisodeResumeAsync(item.Id, cancellationToken).ConfigureAwait(false);
                        plugin.Log.LogWarning("OMDb episode quota reached at {Name} ({ItemId}). Saved continuation cursor; the task will continue with unrelated items and available fallback providers.", item.Name, item.Id);
                    }
                }
                else if (outcome == Ratings.RatingsUpdater.UpdateOutcome.RateLimited)
                {
                    plugin.Log.LogWarning("A provider rate limit requiring task-wide stop was reached. Task will stop now and continue on the next run.");
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

        if (omdbQuotaHitThisRun)
        {
            plugin.Log.LogWarning("Ratings task completed other available work, but OMDb episode backfill is paused by its daily quota. Resume cursor: {Name} ({ItemId}); OMDb cooldown until {Cooldown}.",
                omdbQuotaItemName,
                omdbQuotaItemId,
                plugin.Updater.OmdbCooldownUntilUtc?.ToString("o") ?? "unknown");
        }
        else if (existingOmdbCursor.HasValue)
        {
            var cooldown = plugin.Updater.OmdbCooldownUntilUtc;
            if (cooldown.HasValue && cooldown.Value > DateTimeOffset.UtcNow)
            {
                plugin.Log.LogWarning("Ratings task completed while OMDb episode cooldown is still active until {Cooldown:o}. Saved continuation cursor {ItemId} is retained.", cooldown.Value, existingOmdbCursor.Value);
            }
            else
            {
                await progressStore.SetOmdbEpisodeResumeAsync(null, cancellationToken).ConfigureAwait(false);
                plugin.Log.LogInformation("OMDb episode continuation pass completed without reaching the quota again. The saved continuation cursor has been cleared.");
            }
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
