using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MdbListRatings.ScheduledTasks;

/// <summary>
/// Scheduled task to update ratings from MDBList.
/// </summary>
public sealed class UpdateRatingsTask : IScheduledTask
{
    public string Name => "Update MDBList ratings";

    public string Key => "MdbListRatingsUpdate";

    public string Description => "Fetch ratings from mdblist.com (by TMDb id) and write them into the standard Jellyfin rating fields.";

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
            plugin.Log.LogWarning("MDBList API key is empty. Configure it in Dashboard -> Plugins -> MDBList Ratings.");
            return;
        }

        // Query all Movies and Series.
        var items = plugin.LibraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series }
        });

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
