using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Jellyfin.Plugin.MdbListRatings.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class RatingsUpdater
{
    // ProviderIds keys used to store which rating source was actually applied.
    // These are consumed by the optional Jellyfin Web UI injector to replace the star icon.
    internal const string ProviderIdCommunitySource = "MdbListCommunitySource";
    internal const string ProviderIdCriticSource = "MdbListCriticSource";

    private readonly ILogger _logger;
    private readonly MdbListClient _client;

    private readonly MdbListCacheStore _cacheStore;
    private readonly RateLimitStateStore _rateLimit;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public enum UpdateOutcome
    {
        Skipped = 0,
        Updated = 1,
        RateLimited = 2,
        Failed = 3
    }

    public RatingsUpdater(IHttpClientFactory httpClientFactory, string cacheDir, string statePath, ILogger<RatingsUpdater> logger)
    {
        _logger = logger;
        _client = new MdbListClient(httpClientFactory, logger);
        _cacheStore = new MdbListCacheStore(cacheDir, logger);
        _rateLimit = new RateLimitStateStore(statePath, logger);
    }

    public async Task<UpdateOutcome> UpdateItemRatingsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return UpdateOutcome.Skipped;
        }

        var cfg = plugin.Configuration;

        if (string.IsNullOrWhiteSpace(cfg.MdbListApiKey))
        {
            return UpdateOutcome.Skipped;
        }

        string? contentType = null;
        bool isMovie = false;

        if (item is Movie)
        {
            contentType = "movie";
            isMovie = true;
        }
        else if (item is Series)
        {
            // MDBList expects "show" (not "tv").
            contentType = "show";
        }
        else
        {
            return UpdateOutcome.Skipped;
        }

        var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return UpdateOutcome.Skipped;
        }

        // Optional: only update when empty.
        var communityAlready = item.CommunityRating.HasValue && item.CommunityRating.Value > 0;
        var criticAlready = item.CriticRating.HasValue && item.CriticRating.Value > 0;
        var allowUpdateCommunity = true;
        var allowUpdateCritic = true;

        if (cfg.UpdateOnlyWhenEmpty)
        {
            allowUpdateCommunity = !communityAlready;
            allowUpdateCritic = !criticAlready;

            if (isMovie)
            {
                // Movie: community + critic
                if (!allowUpdateCommunity && !allowUpdateCritic)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else
            {
                // Series: only community
                if (!allowUpdateCommunity)
                {
                    return UpdateOutcome.Skipped;
                }
            }
        }

        var fetchResult = await GetCachedOrFetchAsync(contentType, tmdbId, cfg, cancellationToken).ConfigureAwait(false);
        if (fetchResult.Outcome == UpdateOutcome.RateLimited)
        {
            return UpdateOutcome.RateLimited;
        }

        var data = fetchResult.Data;
        if (data is null || data.Ratings.Count == 0)
        {
            return fetchResult.Outcome == UpdateOutcome.Failed ? UpdateOutcome.Failed : UpdateOutcome.Skipped;
        }

        // Resolve mapping, considering optional per-library overrides.
        var effective = GetEffectiveMapping(item, cfg);

        var movieCommunitySource = NormalizeSource(effective.MovieCommunitySource);
        var movieCommunityFallback = NormalizeSource(effective.MovieCommunityFallbackSource);
        var movieCriticSource = NormalizeSource(effective.MovieCriticSource);
        var movieCriticFallback = NormalizeSource(effective.MovieCriticFallbackSource);
        var showCommunitySource = NormalizeSource(effective.ShowCommunitySource);
        var showCommunityFallback = NormalizeSource(effective.ShowCommunityFallbackSource);

        float? newCommunity = null;
        int? newCritic = null;

        string? usedCommunitySource = null;
        string? usedCriticSource = null;

        if (isMovie)
        {
            (newCommunity, usedCommunitySource) = ExtractCommunityRatingWithSource(data, movieCommunitySource, movieCommunityFallback);
            (newCritic, usedCriticSource) = ExtractCriticRatingWithSource(data, movieCriticSource, movieCriticFallback);
        }
        else
        {
            (newCommunity, usedCommunitySource) = ExtractCommunityRatingWithSource(data, showCommunitySource, showCommunityFallback);
        }

        var changed = false;

        if (allowUpdateCommunity && newCommunity.HasValue)
        {
            var ratingChanged = !item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - newCommunity.Value) > 0.01f;
            if (ratingChanged)
            {
                item.CommunityRating = newCommunity.Value;
            }

            // Store the *actual* used source (primary or fallback) so the web UI can show the right icon.
            var sourceChanged = SetProviderId(item, ProviderIdCommunitySource, usedCommunitySource);

            changed = changed || ratingChanged || sourceChanged;
        }

        if (isMovie && allowUpdateCritic && newCritic.HasValue)
        {
            var ratingChanged = !item.CriticRating.HasValue || item.CriticRating.Value != newCritic.Value;
            if (ratingChanged)
            {
                item.CriticRating = newCritic.Value;
            }

            var sourceChanged = SetProviderId(item, ProviderIdCriticSource, usedCriticSource);
            changed = changed || ratingChanged || sourceChanged;
        }

        if (!changed)
        {
            return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Skipped;
        }

        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated ratings from MDBList: {Name} (TMDb {TmdbId})", item.Name, tmdbId);
            return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save item after rating update: {Name} (TMDb {TmdbId})", item.Name, tmdbId);
            return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Failed;
        }
    }

    private static string NormalizeSource(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private sealed class EffectiveMapping
    {
        public string MovieCommunitySource { get; init; } = "imdb";
        public string MovieCommunityFallbackSource { get; init; } = "none";
        public string MovieCriticSource { get; init; } = "metacritic";
        public string MovieCriticFallbackSource { get; init; } = "none";
        public string ShowCommunitySource { get; init; } = "tmdb";
        public string ShowCommunityFallbackSource { get; init; } = "none";
    }

    private EffectiveMapping GetEffectiveMapping(BaseItem item, PluginConfiguration cfg)
    {
        var mapping = new EffectiveMapping
        {
            MovieCommunitySource = cfg.MovieCommunitySource,
            MovieCommunityFallbackSource = cfg.MovieCommunityFallbackSource,
            MovieCriticSource = cfg.MovieCriticSource,
            MovieCriticFallbackSource = cfg.MovieCriticFallbackSource,
            ShowCommunitySource = cfg.ShowCommunitySource,
            ShowCommunityFallbackSource = cfg.ShowCommunityFallbackSource
        };

        var ov = FindOverrideForItem(item, cfg);
        if (ov is null)
        {
            return mapping;
        }

        // Apply per-library overrides only when the override field is non-empty.
        // This allows partial overrides (e.g., only Series mapping) without duplicating global settings.
        return new EffectiveMapping
        {
            MovieCommunitySource = string.IsNullOrWhiteSpace(ov.MovieCommunitySource) ? mapping.MovieCommunitySource : ov.MovieCommunitySource,
            MovieCommunityFallbackSource = string.IsNullOrWhiteSpace(ov.MovieCommunityFallbackSource) ? mapping.MovieCommunityFallbackSource : ov.MovieCommunityFallbackSource,
            MovieCriticSource = string.IsNullOrWhiteSpace(ov.MovieCriticSource) ? mapping.MovieCriticSource : ov.MovieCriticSource,
            MovieCriticFallbackSource = string.IsNullOrWhiteSpace(ov.MovieCriticFallbackSource) ? mapping.MovieCriticFallbackSource : ov.MovieCriticFallbackSource,
            ShowCommunitySource = string.IsNullOrWhiteSpace(ov.ShowCommunitySource) ? mapping.ShowCommunitySource : ov.ShowCommunitySource,
            ShowCommunityFallbackSource = string.IsNullOrWhiteSpace(ov.ShowCommunityFallbackSource) ? mapping.ShowCommunityFallbackSource : ov.ShowCommunityFallbackSource
        };
    }

    private PluginConfiguration.LibraryRatingOverride? FindOverrideForItem(BaseItem item, PluginConfiguration cfg)
    {
        try
        {
            if (cfg.LibraryOverrides is null || cfg.LibraryOverrides.Count == 0)
            {
                return null;
            }

            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                return null;
            }

            // An item may belong to multiple collection folders.
            // We allow matching overrides either by library GUID or by library name (case-insensitive).
            var folders = plugin.LibraryManager.GetCollectionFolders(item)?.ToList();
            if (folders is null || folders.Count == 0)
            {
                return null;
            }

            foreach (var ov in cfg.LibraryOverrides)
            {
                if (ov is null || !ov.Enabled)
                {
                    continue;
                }

                // LibraryId acts as a "match key".
                // It may contain a GUID (preferred) or the library name.
                var key = (ov.LibraryId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = (ov.LibraryName ?? string.Empty).Trim();
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                // GUID match.
                if (Guid.TryParse(key, out var ovId))
                {
                    foreach (var folder in folders)
                    {
                        if (folder.Id == ovId)
                        {
                            return ov;
                        }
                    }

                    continue;
                }

                // String match against folder ID or folder name.
                foreach (var folder in folders)
                {
                    var idStr = folder.Id.ToString();
                    var idStrN = folder.Id.ToString("N");
                    var name = folder.Name ?? string.Empty;

                    if (string.Equals(key, idStr, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, idStrN, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(name) && string.Equals(key, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return ov;
                    }
                }
            }

            return null;
        }
        catch
        {
            // Never fail rating updates due to override resolution issues.
            return null;
        }
    }

    private (float? Rating, string? UsedSource) ExtractCommunityRatingWithSource(MdbListTitleResponse data, string primarySource, string fallbackSource)
    {
        var resolved = ResolveScoreWithSource(data, primarySource, fallbackSource);
        if (!resolved.Score0To100.HasValue)
        {
            return (null, null);
        }

        // Jellyfin CommunityRating is 0-10; MDBList score is 0-100.
        var s = Clamp(resolved.Score0To100.Value, 0, 100);
        var value = (float)Math.Round(s / 10.0, 1, MidpointRounding.AwayFromZero);
        return (value > 0 ? value : null, resolved.UsedSource);
    }

    private (int? Rating, string? UsedSource) ExtractCriticRatingWithSource(MdbListTitleResponse data, string primarySource, string fallbackSource)
    {
        var resolved = ResolveScoreWithSource(data, primarySource, fallbackSource);
        if (!resolved.Score0To100.HasValue)
        {
            return (null, null);
        }

        // Jellyfin CriticRating is 0-100.
        var s = Clamp(resolved.Score0To100.Value, 0, 100);
        var i = (int)Math.Round(s, MidpointRounding.AwayFromZero);
        return (i > 0 ? i : null, resolved.UsedSource);
    }

    private sealed class ResolvedScore
    {
        public double? Score0To100 { get; init; }
        public string? UsedSource { get; init; }
    }

    private ResolvedScore ResolveScoreWithSource(MdbListTitleResponse data, string primarySource, string fallbackSource)
    {
        var p = NormalizeSource(primarySource);
        var f = NormalizeSource(fallbackSource);

        var pScore = TryGetScore0To100(data, p);
        if (pScore.HasValue)
        {
            return new ResolvedScore { Score0To100 = pScore.Value, UsedSource = p };
        }

        if (!string.IsNullOrWhiteSpace(f) && f != "none" && !string.Equals(p, f, StringComparison.OrdinalIgnoreCase))
        {
            var fScore = TryGetScore0To100(data, f);
            if (fScore.HasValue)
            {
                return new ResolvedScore { Score0To100 = fScore.Value, UsedSource = f };
            }
        }

        return new ResolvedScore { Score0To100 = null, UsedSource = null };
    }

    private static bool SetProviderId(BaseItem item, string key, string? value)
    {
        try
        {
            // Only store meaningful keys.
            var v = NormalizeSource(value);
            if (string.IsNullOrWhiteSpace(v) || v == "none")
            {
                return item.ProviderIds.Remove(key);
            }

            if (item.ProviderIds.TryGetValue(key, out var existing) && string.Equals(existing, v, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            item.ProviderIds[key] = v;
            return true;
        }
        catch
        {
            // Never fail rating updates due to ProviderIds storage.
            return false;
        }
    }

    private double? TryGetScore0To100(MdbListTitleResponse data, string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source == "none")
        {
            return null;
        }

        var rating = data.Ratings.FirstOrDefault(r => string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase));
        var score = rating?.Score ?? NormalizeScoreFromValue(rating?.Value);

        if (!score.HasValue)
        {
            return null;
        }

        var s = score.Value;
        if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0)
        {
            return null;
        }

        return s;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : (value > max ? max : value);

    private static double? NormalizeScoreFromValue(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        // MDBList "value" может быть 0-10 (IMDb) или 0-100 (TMDb/Metacritic).
        // Приводим к 0-100.
        var v = value.Value;

        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        if (v <= 0)
        {
            return null;
        }

        if (v <= 10.0)
        {
            return v * 10.0;
        }

        if (v <= 100.0)
        {
            return v;
        }

        return null;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _rateLimit.LoadAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Returns the cached envelope (including timestamp) for a given TMDb id if present.
    /// This does not perform network requests and may return stale cache.
    /// </summary>
    internal async Task<MdbListCacheStore.CacheEnvelope?> TryGetCacheEnvelopeAsync(
        string contentType,
        string tmdbId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(contentType) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return null;
        }

        var cacheKey = $"{contentType}:{tmdbId}";
        return await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
    }

    private sealed class FetchResult
    {
        public MdbListTitleResponse? Data { get; init; }
        public UpdateOutcome Outcome { get; init; } = UpdateOutcome.Skipped;
        public bool StopAfterThis { get; init; }
    }

    private async Task<FetchResult> GetCachedOrFetchAsync(
        string contentType,
        string tmdbId,
        PluginConfiguration cfg,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{contentType}:{tmdbId}";
        var now = DateTimeOffset.UtcNow;
        var ttl = GetTtl(cfg);

        var cached = await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var age = now - cached.CachedAtUtc;
            if (age <= ttl)
            {
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped };
            }
        }

        // If we are currently in cooldown and have no usable cache, stop the task.
        if (_rateLimit.NotBeforeUtc.HasValue && _rateLimit.NotBeforeUtc.Value > now)
        {
            if (cached is not null)
            {
                // Use stale cache if available.
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped };
            }

            _logger.LogWarning(
                "MDBList rate limit cooldown is active until {NotBeforeUtc:o}. Stopping the task.",
                _rateLimit.NotBeforeUtc.Value);
            return new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
        }

        // Delay only when we are about to make a network request.
        if (cfg.RequestDelayMs > 0)
        {
            await Task.Delay(cfg.RequestDelayMs, cancellationToken).ConfigureAwait(false);
        }

        var api = await _client.GetByTmdbAsync(contentType, tmdbId, cfg.MdbListApiKey, cancellationToken).ConfigureAwait(false);

        var quotaExhausted = api.RateLimitRemaining.HasValue && api.RateLimitRemaining.Value <= 0;

        // Update rate-limit state based on headers.
        // - api.IsRateLimited: hard limit (e.g., HTTP 429)
        // - quotaExhausted: this request succeeded, but remaining is 0, so we should stop until reset.
        await _rateLimit.UpdateAsync(api.RateLimitLimit, api.RateLimitRemaining, api.RateLimitResetUtc, api.IsRateLimited || quotaExhausted, cancellationToken)
            .ConfigureAwait(false);

        if (api.IsRateLimited)
        {
            // If we have stale cache, we can still proceed with it; otherwise stop.
            if (cached is not null)
            {
                _logger.LogWarning("MDBList rate limit reached. Using stale cache for {Key}.", cacheKey);
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped };
            }

            _logger.LogWarning(
                "MDBList rate limit reached. Will continue after {ResetUtc:o}.",
                api.RateLimitResetUtc ?? (_rateLimit.NotBeforeUtc ?? now.AddHours(24)));
            return new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
        }

        if (api.Data is null)
        {
            // Network error or parse error. If we have old cache, use it; otherwise fail.
            if (cached is not null)
            {
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped };
            }

            return new FetchResult { Data = null, Outcome = UpdateOutcome.Failed };
        }

        // Save to cache.
        var env = new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = api.Data,
            RawJson = api.RawJson
        };

        await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);

        // If quota is exhausted, we still return data for this item, but signal the task to stop afterwards.
        return new FetchResult { Data = api.Data, Outcome = UpdateOutcome.Skipped, StopAfterThis = quotaExhausted };
    }

    private static TimeSpan GetTtl(PluginConfiguration cfg)
    {
        // Prefer new preset.
        if (cfg.CacheInterval != PluginConfiguration.CacheIntervalPreset.Unset)
        {
            return cfg.CacheInterval switch
            {
                PluginConfiguration.CacheIntervalPreset.Week => TimeSpan.FromDays(7),
                PluginConfiguration.CacheIntervalPreset.Month => TimeSpan.FromDays(30),
                _ => TimeSpan.FromDays(1)
            };
        }

        // Legacy configs: derive from hours.
        var h = cfg.CacheHours <= 0 ? 24 : cfg.CacheHours;
        if (h >= 24 * 30)
        {
            return TimeSpan.FromDays(30);
        }

        if (h >= 24 * 7)
        {
            return TimeSpan.FromDays(7);
        }

        return TimeSpan.FromDays(1);
    }
}
