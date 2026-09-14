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
using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class RatingsUpdater
{
    // ProviderIds keys used to store which rating source was actually applied.
    // These are consumed by the optional Jellyfin Web UI injector to replace the star icon.
    internal const string ProviderIdCommunitySource = "MdbListCommunitySource";
    internal const string ProviderIdCriticSource = "MdbListCriticSource";
    internal const string ProviderIdImdbTopRanking = "MdbListImdbTopRanking";

    private readonly ILogger _logger;
    private readonly MdbListClient _client;

    private readonly ImdbRatingsDataset _imdbFallback;
    private readonly TvMazeClient _tvMaze;
    private readonly TraktSeasonApiClient _traktSeason;
    private readonly TraktEpisodeApiClient _traktEpisode;
    private readonly TmdbSeasonApiClient _tmdbSeason;
    private readonly TmdbEpisodeApiClient _tmdbEpisode;
    private readonly OmdbEpisodeApiClient _omdbEpisode;
    private readonly WhatsOnApiClient _whatsOn;
    private readonly SimklApiClient _simkl;

    private readonly MdbListCacheStore _cacheStore;
    private readonly RateLimitStateStore _rateLimit;
    private readonly RateLimitStateStore _omdbRateLimit;
    private readonly RateLimitStateStore _whatsOnRateLimit;
    private readonly RateLimitStateStore _simklRateLimit;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public enum UpdateOutcome
    {
        Skipped = 0,
        Updated = 1,
        RateLimited = 2,
        Failed = 3,
        // OMDb is provider-specific: hitting its episode quota must not stop unrelated work.
        OmdbRateLimited = 4
    }

    public RatingsUpdater(IHttpClientFactory httpClientFactory, string cacheDir, string statePath, ILogger<RatingsUpdater> logger)
    {
        _logger = logger;
        _client = new MdbListClient(httpClientFactory, logger);
        _imdbFallback = new ImdbRatingsDataset(httpClientFactory, cacheDir, logger);
        _tvMaze = new TvMazeClient(httpClientFactory, logger);
        _traktSeason = new TraktSeasonApiClient(httpClientFactory, logger);
        _traktEpisode = new TraktEpisodeApiClient(httpClientFactory, logger);
        _tmdbSeason = new TmdbSeasonApiClient(httpClientFactory, logger);
        _tmdbEpisode = new TmdbEpisodeApiClient(httpClientFactory, logger);
        _omdbEpisode = new OmdbEpisodeApiClient(httpClientFactory, logger);
        _whatsOn = new WhatsOnApiClient(httpClientFactory, logger);
        _simkl = new SimklApiClient(httpClientFactory, logger);
        _cacheStore = new MdbListCacheStore(cacheDir, logger);
        _rateLimit = new RateLimitStateStore(statePath, logger);
        _omdbRateLimit = new RateLimitStateStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(statePath) ?? string.Empty, "omdb-episode-state.json"), logger);
        _whatsOnRateLimit = new RateLimitStateStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(statePath) ?? string.Empty, "whatson-state.json"), logger);
        _simklRateLimit = new RateLimitStateStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(statePath) ?? string.Empty, "simkl-state.json"), logger);
    }

    internal DateTimeOffset? OmdbCooldownUntilUtc => _omdbRateLimit.NotBeforeUtc;

    public async Task<UpdateOutcome> UpdateItemRatingsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return UpdateOutcome.Skipped;
        }

        var cfg = plugin.Configuration;

        string? contentType = null;
        bool isMovie = false;
        bool isShow = false;
        bool isSeason = false;
        bool isEpisode = false;
        int? seasonNumber = null;
        int? episodeNumber = null;
        string? seasonShowTmdbId = null;
        string? seasonShowImdbId = null;
        string? seasonShowTvdbId = null;

        if (item is Movie)
        {
            contentType = "movie";
            isMovie = true;
        }
        else if (item is Series)
        {
            // MDBList expects "show" (not "tv").
            contentType = "show";
            isShow = true;
        }
        else if (item is Season seasonItem)
        {
            contentType = "season";
            isSeason = true;
            var seasonLookup = ResolveSeasonLookup(item, seasonItem);
            seasonNumber = seasonLookup.SeasonNumber;
            seasonShowTmdbId = seasonLookup.ShowTmdbId;
            seasonShowImdbId = seasonLookup.ShowImdbId;
            seasonShowTvdbId = seasonLookup.ShowTvdbId;
            if (!seasonNumber.HasValue || seasonNumber.Value < 0 || (string.IsNullOrWhiteSpace(seasonShowTmdbId) && string.IsNullOrWhiteSpace(seasonShowImdbId) && string.IsNullOrWhiteSpace(seasonShowTvdbId)))
            {
                return UpdateOutcome.Skipped;
            }
        }
        else if (item is Episode episodeItem)
        {
            contentType = "episode";
            isEpisode = true;
            var episodeLookup = ResolveEpisodeLookup(item, episodeItem);
            seasonNumber = episodeLookup.SeasonNumber;
            episodeNumber = episodeLookup.EpisodeNumber;
            seasonShowTmdbId = episodeLookup.ShowTmdbId;
            seasonShowImdbId = episodeLookup.ShowImdbId;
            seasonShowTvdbId = episodeLookup.ShowTvdbId;
            if (!seasonNumber.HasValue || seasonNumber.Value < 0 || !episodeNumber.HasValue || episodeNumber.Value <= 0 || (string.IsNullOrWhiteSpace(seasonShowTmdbId) && string.IsNullOrWhiteSpace(seasonShowImdbId) && string.IsNullOrWhiteSpace(seasonShowTvdbId)))
            {
                _logger.LogDebug("Skipping episode rating update for {Name}: could not resolve SERIES ids for lookup (Episode TMDb={EpisodeTmdbId}, IMDb={EpisodeImdbId}, TVDB={EpisodeTvdbId}, Resolved show TMDb={ShowTmdbId}, IMDb={ShowImdbId}, TVDB={ShowTvdbId}, Season={SeasonNumber}, Episode={EpisodeNumber})",
                    item.Name,
                    item.GetProviderId(MetadataProvider.Tmdb),
                    item.GetProviderId(MetadataProvider.Imdb),
                    item.GetProviderId(MetadataProvider.Tvdb),
                    seasonShowTmdbId,
                    seasonShowImdbId,
                    seasonShowTvdbId,
                    seasonNumber,
                    episodeNumber);
                return UpdateOutcome.Skipped;
            }
        }
        else
        {
            return UpdateOutcome.Skipped;
        }

        var effectiveForTransport = GetEffectiveMapping(item, cfg);

        var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        var tvdbId = item.GetProviderId(MetadataProvider.Tvdb);

        var effectiveMoviePrimary = NormalizeSource(effectiveForTransport.MovieCommunitySource);
        var effectiveMovieFallback = NormalizeSource(effectiveForTransport.MovieCommunityFallbackSource);
        var effectiveMovieCritic = NormalizeSource(effectiveForTransport.MovieCriticSource);
        var effectiveMovieCriticFallback = NormalizeSource(effectiveForTransport.MovieCriticFallbackSource);
        var effectiveShowPrimary = NormalizeSource(effectiveForTransport.ShowCommunitySource);
        var effectiveShowFallback = NormalizeSource(effectiveForTransport.ShowCommunityFallbackSource);
        var effectiveSeasonPrimary = NormalizeSource(effectiveForTransport.SeasonCommunitySource);
        var effectiveSeasonFallback = NormalizeSource(effectiveForTransport.SeasonCommunityFallbackSource);
        var effectiveEpisodePrimary = NormalizeSource(effectiveForTransport.EpisodeCommunitySource);
        var effectiveEpisodeFallback = NormalizeSource(effectiveForTransport.EpisodeCommunityFallbackSource);
        // Always try to keep TVmaze cached for series/shows when an external id is available,
        // so the Web "all ratings from cache" panel can display it even if TVmaze is not the
        // selected primary/fallback source for metadata writing.
        var needsTvMaze = !isMovie && !isSeason && !isEpisode && (!string.IsNullOrWhiteSpace(imdbId) || !string.IsNullOrWhiteSpace(tvdbId));
        var needsSeasonTrakt = isSeason && seasonNumber.HasValue && SeasonRequiresTrakt(effectiveSeasonPrimary, effectiveSeasonFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsSeasonTmdb = isSeason && seasonNumber.HasValue && SeasonRequiresTmdb(effectiveSeasonPrimary, effectiveSeasonFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowTmdbId) || !string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsSeasonWhatsOn = isSeason && seasonNumber.HasValue && SeasonRequiresWhatsOn(effectiveSeasonPrimary, effectiveSeasonFallback)
            && !string.IsNullOrWhiteSpace(seasonShowTmdbId);
        var needsEpisodeTmdb = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresTmdb(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowTmdbId) || !string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeTrakt = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresTrakt(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeTvMaze = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresTvMaze(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeOmdb = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresOmdb(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && !string.IsNullOrWhiteSpace(imdbId);
        var needsEpisodeWhatsOn = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresWhatsOn(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && !string.IsNullOrWhiteSpace(seasonShowTmdbId);
        // Movie/Series cache population is deliberately independent from the selected rating source:
        // always try MDBList, WhatsOn and Simkl. The configured primary/fallback mapping only decides
        // which cached value is written to Jellyfin's CommunityRating/CriticRating fields.
        var needsWhatsOn = isMovie || isShow;
        var needsMdbList = isMovie || isShow;
        var needsSimkl = isMovie || isShow;

        if (!needsMdbList && !needsWhatsOn && !needsSimkl && !needsTvMaze && !needsSeasonTrakt && !needsSeasonTmdb && !needsSeasonWhatsOn && !needsEpisodeTmdb && !needsEpisodeTrakt && !needsEpisodeTvMaze && !needsEpisodeOmdb && !needsEpisodeWhatsOn)
        {
            return UpdateOutcome.Skipped;
        }


        // Missing credentials must disable only the unavailable provider, not the whole
        // primary/fallback chain. This is especially important when the missing credential
        // belongs to the fallback source: a valid primary source must still be fetched and
        // applied. The inverse also works: when the primary provider is unavailable, an
        // available fallback provider can still resolve the rating.
        if (string.IsNullOrWhiteSpace(cfg.TraktClientId))
        {
            if (needsSeasonTrakt || needsEpisodeTrakt)
            {
                _logger.LogDebug("Trakt Client ID is empty; skipping Trakt for {Name} while continuing with other configured rating sources.", item.Name);
            }

            needsSeasonTrakt = false;
            needsEpisodeTrakt = false;
        }

        if (string.IsNullOrWhiteSpace(cfg.TmdbApiAuth))
        {
            if (needsSeasonTmdb || needsEpisodeTmdb)
            {
                _logger.LogDebug("TMDb API credential is empty; skipping TMDb for {Name} while continuing with other configured rating sources.", item.Name);
            }

            needsSeasonTmdb = false;
            needsEpisodeTmdb = false;
        }

        if (string.IsNullOrWhiteSpace(cfg.OmdbApiKey))
        {
            if (needsEpisodeOmdb)
            {
                _logger.LogDebug("OMDb API key is empty; skipping OMDb for {Name} while continuing with other configured rating sources.", item.Name);
            }

            needsEpisodeOmdb = false;
        }

        // Re-evaluate after credential filtering. If every requested transport is unavailable,
        // there is genuinely nothing left to fetch for this item.
        if (!needsMdbList && !needsWhatsOn && !needsSimkl && !needsTvMaze && !needsSeasonTrakt && !needsSeasonTmdb && !needsSeasonWhatsOn && !needsEpisodeTmdb && !needsEpisodeTrakt && !needsEpisodeTvMaze && !needsEpisodeOmdb && !needsEpisodeWhatsOn)
        {
            return UpdateOutcome.Skipped;
        }

        if (needsTvMaze && string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tvdbId))
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
                if (!allowUpdateCommunity && !allowUpdateCritic && !needsMdbList && !needsWhatsOn && !needsSimkl)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else if (isSeason)
            {
                if (!allowUpdateCommunity && !needsSeasonTrakt && !needsSeasonTmdb && !needsSeasonWhatsOn)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else if (isEpisode)
            {
                if (!allowUpdateCommunity && !needsEpisodeTmdb && !needsEpisodeTrakt && !needsEpisodeTvMaze && !needsEpisodeOmdb && !needsEpisodeWhatsOn)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else
            {
                // Series: still allow fetching/augmenting the cache (e.g. TVmaze, WhatsOn, Simkl) even when we
                // are not going to overwrite the saved CommunityRating field.
                if (!allowUpdateCommunity && !needsTvMaze && !needsWhatsOn && !needsSimkl)
                {
                    return UpdateOutcome.Skipped;
                }
            }
        }

        var fetchResult = isSeason
            ? await GetCachedOrFetchSeasonAsync(item, seasonShowTmdbId, seasonShowImdbId, seasonShowTvdbId, seasonNumber!.Value, cfg, needsSeasonTrakt, needsSeasonTmdb, needsSeasonWhatsOn, cancellationToken).ConfigureAwait(false)
            : isEpisode
                ? await GetCachedOrFetchEpisodeAsync(item, imdbId, seasonShowTmdbId, seasonShowImdbId, seasonShowTvdbId, seasonNumber!.Value, episodeNumber!.Value, cfg, effectiveEpisodePrimary, effectiveEpisodeFallback, needsEpisodeTmdb, needsEpisodeTrakt, needsEpisodeTvMaze, needsEpisodeOmdb, needsEpisodeWhatsOn, cancellationToken).ConfigureAwait(false)
                : await GetCachedOrFetchAsync(contentType, tmdbId, imdbId, tvdbId, cfg, needsMdbList, needsWhatsOn, needsSimkl, needsTvMaze, cancellationToken).ConfigureAwait(false);
        if (fetchResult.Outcome == UpdateOutcome.RateLimited)
        {
            return UpdateOutcome.RateLimited;
        }

        // If MDBList returned 404 and we successfully used IMDb fallback, preserve the historical
        // direct-fallback behavior. WhatsOn was still fetched first, so its Top 250/status metadata
        // can be persisted even in this path.
        if (fetchResult.ImdbFallbackCommunityRating.HasValue)
        {
            var specialChanged = false;
            if ((isMovie || isShow) && fetchResult.Data?.WhatsOnFeatures is not null)
            {
                var rank = fetchResult.Data.WhatsOnFeatures.ImdbTopRanking;
                var rankValue = rank.HasValue && rank.Value >= 1 && rank.Value <= 250
                    ? rank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                specialChanged = SetProviderId(item, ProviderIdImdbTopRanking, rankValue);
            }

            if (!allowUpdateCommunity)
            {
                if (!specialChanged)
                {
                    return UpdateOutcome.Skipped;
                }

                try
                {
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    return UpdateOutcome.Updated;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save WhatsOn status metadata for {Name}", item.Name);
                    return UpdateOutcome.Failed;
                }
            }

            var imdbFallbackCommunity = fetchResult.ImdbFallbackCommunityRating.Value;
            const string imdbFallbackSource = "imdb";
            var ratingChanged = !item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - imdbFallbackCommunity) > 0.01f;
            if (ratingChanged)
            {
                item.CommunityRating = imdbFallbackCommunity;
            }

            var sourceChanged = SetProviderId(item, ProviderIdCommunitySource, imdbFallbackSource);
            var imdbFallbackChanged = ratingChanged || sourceChanged || specialChanged;

            if (!imdbFallbackChanged)
            {
                return UpdateOutcome.Skipped;
            }

            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated ratings from IMDb fallback: {Name} (IMDb {ImdbId})", item.Name, imdbId);
                return UpdateOutcome.Updated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save item after IMDb fallback rating update: {Name} (IMDb {ImdbId})", item.Name, imdbId);
                return UpdateOutcome.Failed;
            }
        }

        var data = fetchResult.Data;
        if (data is null || data.Ratings.Count == 0)
        {
            if (isEpisode && fetchResult.OmdbRateLimitHit)
            {
                return UpdateOutcome.OmdbRateLimited;
            }

            return fetchResult.Outcome == UpdateOutcome.Failed ? UpdateOutcome.Failed : UpdateOutcome.Skipped;
        }

        if (isSeason)
        {
            var seasonEffective = GetEffectiveMapping(item, cfg);
            var seasonPrimary = NormalizeSource(seasonEffective.SeasonCommunitySource);
            var seasonFallback = NormalizeSource(seasonEffective.SeasonCommunityFallbackSource);

            // WhatsOn season data is sourced from IMDb; report the original source when applying the rating.
            seasonPrimary = MapWhatsOnToImdb(seasonPrimary);
            seasonFallback = MapWhatsOnToImdb(seasonFallback);

            var seasonResolved = ResolveScoreWithSource(data, seasonPrimary, seasonFallback, seasonEffective.SeasonCommunitySource, seasonEffective.SeasonCommunityFallbackSource);
            if (!seasonResolved.Score0To100.HasValue)
            {
                return UpdateOutcome.Skipped;
            }

            var score = Clamp(seasonResolved.Score0To100.Value, 0, 100);
            var seasonCommunity = (float)Math.Round(score / 10.0, 1, MidpointRounding.AwayFromZero);
            if (seasonCommunity <= 0)
            {
                return UpdateOutcome.Skipped;
            }

            if (!allowUpdateCommunity)
            {
                return UpdateOutcome.Skipped;
            }

            var seasonRatingChanged = !item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - seasonCommunity) > 0.01f;
            if (seasonRatingChanged)
            {
                item.CommunityRating = seasonCommunity;
            }

            var seasonSourceChanged = SetProviderId(item, ProviderIdCommunitySource, seasonResolved.UsedSource);
            var seasonChanged = seasonRatingChanged || seasonSourceChanged;
            if (!seasonChanged)
            {
                return UpdateOutcome.Skipped;
            }

            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated season ratings from configured source: {Name} (IMDb {ImdbId}, TVDB {TvdbId}, Season {SeasonNumber}, Source {ConfiguredSource} -> {ResolvedSource})", item.Name, seasonShowImdbId, seasonShowTvdbId, seasonNumber, seasonResolved.ConfiguredSource, seasonResolved.UsedSource);
                return UpdateOutcome.Updated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save season item after season rating update: {Name}", item.Name);
                return UpdateOutcome.Failed;
            }
        }

        if (isEpisode)
        {
            var episodeEffective = GetEffectiveMapping(item, cfg);
            var episodePrimary = NormalizeSource(episodeEffective.EpisodeCommunitySource);
            var episodeFallback = NormalizeSource(episodeEffective.EpisodeCommunityFallbackSource);

            // WhatsOn episode data is sourced from IMDb; report the original source when applying the rating.
            episodePrimary = MapWhatsOnToImdb(episodePrimary);
            episodeFallback = MapWhatsOnToImdb(episodeFallback);

            var episodeResolved = ResolveScoreWithSource(data, episodePrimary, episodeFallback, episodeEffective.EpisodeCommunitySource, episodeEffective.EpisodeCommunityFallbackSource);
            if (!episodeResolved.Score0To100.HasValue)
            {
                return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Skipped;
            }

            var score = Clamp(episodeResolved.Score0To100.Value, 0, 100);
            var episodeCommunity = (float)Math.Round(score / 10.0, 1, MidpointRounding.AwayFromZero);
            if (episodeCommunity <= 0)
            {
                return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Skipped;
            }

            if (!allowUpdateCommunity)
            {
                return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Skipped;
            }

            var episodeRatingChanged = !item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - episodeCommunity) > 0.01f;
            if (episodeRatingChanged)
            {
                item.CommunityRating = episodeCommunity;
            }

            var episodeSourceChanged = SetProviderId(item, ProviderIdCommunitySource, episodeResolved.UsedSource);
            var episodeChanged = episodeRatingChanged || episodeSourceChanged;
            if (!episodeChanged)
            {
                return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Skipped;
            }

            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated episode ratings from configured source: {Name} (IMDb {ImdbId}, TVDB {TvdbId}, S{SeasonNumber}E{EpisodeNumber}, Source {ConfiguredSource} -> {ResolvedSource})", item.Name, seasonShowImdbId, seasonShowTvdbId, seasonNumber, episodeNumber, episodeResolved.ConfiguredSource, episodeResolved.UsedSource);
                return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Updated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save episode item after episode rating update: {Name}", item.Name);
                return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Failed;
            }
        }

        // Resolve mapping, considering optional per-library overrides.
        var effective = GetEffectiveMapping(item, cfg);

        // Keep "(WhatsOn)" aliases distinct while resolving values so a cached MDBList value cannot
        // satisfy an explicitly selected WhatsOn source. We only map the used source back to the
        // native provider key when storing Jellyfin's icon/source ProviderId.
        var movieCommunitySource = NormalizeSource(effective.MovieCommunitySource);
        var movieCommunityFallback = NormalizeSource(effective.MovieCommunityFallbackSource);
        var movieCriticSource = NormalizeSource(effective.MovieCriticSource);
        var movieCriticFallback = NormalizeSource(effective.MovieCriticFallbackSource);
        var showCommunitySource = NormalizeSource(effective.ShowCommunitySource);
        var showCommunityFallback = NormalizeSource(effective.ShowCommunityFallbackSource);
        var seasonCommunitySource = NormalizeSource(effective.SeasonCommunitySource);
        var seasonCommunityFallback = NormalizeSource(effective.SeasonCommunityFallbackSource);

        float? newCommunity = null;
        int? newCritic = null;

        string? usedCommunitySource = null;
        string? usedCriticSource = null;

        if (isMovie)
        {
            (newCommunity, usedCommunitySource) = ExtractCommunityRatingWithSource(data, movieCommunitySource, movieCommunityFallback);
            (newCritic, usedCriticSource) = ExtractCriticRatingWithSource(data, movieCriticSource, movieCriticFallback);
        }
        else if (isSeason)
        {
            (newCommunity, usedCommunitySource) = ExtractCommunityRatingWithSource(data, seasonCommunitySource, seasonCommunityFallback);
        }
        else
        {
            (newCommunity, usedCommunitySource) = ExtractCommunityRatingWithSource(data, showCommunitySource, showCommunityFallback);
        }

        var changed = false;

        // Persist IMDb Top ranking supplied by WhatsOn on the item itself. The Web UI already fetches
        // ProviderIds in batches, so this replaces the old separately downloaded Top 250 JSON index.
        if ((isMovie || isShow) && data.WhatsOnFeatures is not null)
        {
            var rank = data.WhatsOnFeatures.ImdbTopRanking;
            var rankValue = rank.HasValue && rank.Value >= 1 && rank.Value <= 250
                ? rank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            changed = SetProviderId(item, ProviderIdImdbTopRanking, rankValue) || changed;
        }

        if (allowUpdateCommunity && newCommunity.HasValue)
        {
            var ratingChanged = !item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - newCommunity.Value) > 0.01f;
            if (ratingChanged)
            {
                item.CommunityRating = newCommunity.Value;
            }

            // Store the *actual* used source (primary or fallback) so the web UI can show the right icon.
            var sourceChanged = SetProviderId(item, ProviderIdCommunitySource, MapProviderAliasToNative(usedCommunitySource ?? string.Empty));

            changed = changed || ratingChanged || sourceChanged;
        }

        if (isMovie && allowUpdateCritic && newCritic.HasValue)
        {
            var ratingChanged = !item.CriticRating.HasValue || item.CriticRating.Value != newCritic.Value;
            if (ratingChanged)
            {
                item.CriticRating = newCritic.Value;
            }

            var sourceChanged = SetProviderId(item, ProviderIdCriticSource, MapProviderAliasToNative(usedCriticSource ?? string.Empty));
            changed = changed || ratingChanged || sourceChanged;
        }

        if (!changed)
        {
            return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Skipped;
        }

        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated ratings from MDBList: {Name} (TMDb {TmdbId})", item.Name, tmdbId);
            return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save item after rating update: {Name} (TMDb {TmdbId})", item.Name, tmdbId);
            return fetchResult.OmdbRateLimitHit ? UpdateOutcome.OmdbRateLimited : UpdateOutcome.Failed;
        }
    }

    private sealed class SeasonLookup
    {
        public string? ShowTmdbId { get; init; }
        public string? ShowImdbId { get; init; }
        public string? ShowTvdbId { get; init; }
        public int? SeasonNumber { get; init; }
    }

    private SeasonLookup ResolveSeasonLookup(BaseItem item, Season season)
    {
        var seasonNo = season.IndexNumber ?? item.IndexNumber ?? item.ParentIndexNumber;

        // Important: for season/episode TMDb lookup we need SERIES identifiers, not the season's own external ids.
        // Some libraries store season-level provider ids on the Season item, which can poison TMDb series resolution.
        // Therefore, prefer parent Series ids first and only fall back to the Season item ids when no series ids are available.
        string? itemTmdb = item.GetProviderId(MetadataProvider.Tmdb);
        string? itemImdb = item.GetProviderId(MetadataProvider.Imdb);
        string? itemTvdb = item.GetProviderId(MetadataProvider.Tvdb);
        string? showTmdbId = null;
        string? showImdbId = null;
        string? showTvdbId = null;

        BaseItem? parent = item.DisplayParent;
        if (parent is null && item.ParentId != Guid.Empty)
        {
            try { parent = Plugin.Instance?.LibraryManager.GetItemById(item.ParentId); } catch { parent = null; }
        }

        while (parent is not null)
        {
            if (parent is Series parentSeries)
            {
                showTmdbId ??= parentSeries.GetProviderId(MetadataProvider.Tmdb);
                showImdbId ??= parentSeries.GetProviderId(MetadataProvider.Imdb);
                showTvdbId ??= parentSeries.GetProviderId(MetadataProvider.Tvdb);
            }

            BaseItem? nextParent = parent.DisplayParent;
            if (nextParent is null && parent.ParentId != Guid.Empty)
            {
                try { nextParent = Plugin.Instance?.LibraryManager.GetItemById(parent.ParentId); } catch { nextParent = null; }
            }

            parent = nextParent;
        }

        showTmdbId ??= itemTmdb;
        showImdbId ??= itemImdb;
        showTvdbId ??= itemTvdb;

        return new SeasonLookup
        {
            ShowTmdbId = NormalizeDigits(showTmdbId),
            ShowImdbId = NormalizeImdbId(showImdbId),
            ShowTvdbId = NormalizeDigits(showTvdbId),
            SeasonNumber = seasonNo
        };
    }

    private static string BuildSeasonCacheKey(string? showTmdbId, string? showImdbId, string? showTvdbId, int seasonNumber)
    {
        if (seasonNumber < 0)
        {
            return string.Empty;
        }

        var imdb = NormalizeImdbId(showImdbId);
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            return $"season:imdb:{imdb}:season:{seasonNumber}";
        }

        var tmdb = NormalizeDigits(showTmdbId);
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            return $"season:tmdb:{tmdb}:season:{seasonNumber}";
        }

        var tvdb = NormalizeDigits(showTvdbId);
        if (!string.IsNullOrWhiteSpace(tvdb))
        {
            return $"season:tvdb:{tvdb}:season:{seasonNumber}";
        }

        return string.Empty;
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
        public string SeasonCommunitySource { get; init; } = "trakt";
        public string SeasonCommunityFallbackSource { get; init; } = "none";
        public string EpisodeCommunitySource { get; init; } = "tmdb";
        public string EpisodeCommunityFallbackSource { get; init; } = "none";
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
            ShowCommunityFallbackSource = cfg.ShowCommunityFallbackSource,
            SeasonCommunitySource = cfg.SeasonCommunitySource,
            SeasonCommunityFallbackSource = cfg.SeasonCommunityFallbackSource,
            EpisodeCommunitySource = cfg.EpisodeCommunitySource,
            EpisodeCommunityFallbackSource = cfg.EpisodeCommunityFallbackSource
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
            ShowCommunityFallbackSource = string.IsNullOrWhiteSpace(ov.ShowCommunityFallbackSource) ? mapping.ShowCommunityFallbackSource : ov.ShowCommunityFallbackSource,
            SeasonCommunitySource = string.IsNullOrWhiteSpace(ov.SeasonCommunitySource) ? mapping.SeasonCommunitySource : ov.SeasonCommunitySource,
            SeasonCommunityFallbackSource = string.IsNullOrWhiteSpace(ov.SeasonCommunityFallbackSource) ? mapping.SeasonCommunityFallbackSource : ov.SeasonCommunityFallbackSource,
            EpisodeCommunitySource = string.IsNullOrWhiteSpace(ov.EpisodeCommunitySource) ? mapping.EpisodeCommunitySource : ov.EpisodeCommunitySource,
            EpisodeCommunityFallbackSource = string.IsNullOrWhiteSpace(ov.EpisodeCommunityFallbackSource) ? mapping.EpisodeCommunityFallbackSource : ov.EpisodeCommunityFallbackSource
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
        public string? ConfiguredSource { get; init; }
    }

    private ResolvedScore ResolveScoreWithSource(MdbListTitleResponse data, string primarySource, string fallbackSource, string? configuredPrimary = null, string? configuredFallback = null)
    {
        var p = NormalizeSource(primarySource);
        var f = NormalizeSource(fallbackSource);

        var pScore = TryGetScore0To100(data, p);
        if (pScore.HasValue)
        {
            return new ResolvedScore { Score0To100 = pScore.Value, UsedSource = p, ConfiguredSource = configuredPrimary ?? p };
        }

        if (!string.IsNullOrWhiteSpace(f) && f != "none" && !string.Equals(p, f, StringComparison.OrdinalIgnoreCase))
        {
            var fScore = TryGetScore0To100(data, f);
            if (fScore.HasValue)
            {
                return new ResolvedScore { Score0To100 = fScore.Value, UsedSource = f, ConfiguredSource = configuredFallback ?? f };
            }
        }

        return new ResolvedScore { Score0To100 = null, UsedSource = null, ConfiguredSource = null };
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

        // Letterboxd uses a native 0-5 scale. Normalize it explicitly to 0-100
        // so Jellyfin CommunityRating (0-10) becomes nativeValue * 2.
        // Prefer the native value here instead of MDBList's generic score field to keep
        // the conversion consistent for both MDBList and WhatsOn Letterboxd sources.
        double? score;
        if ((string.Equals(source, "letterboxd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "whatson_letterboxd", StringComparison.OrdinalIgnoreCase))
            && rating?.Value is double letterboxdValue
            && !double.IsNaN(letterboxdValue)
            && !double.IsInfinity(letterboxdValue)
            && letterboxdValue > 0
            && letterboxdValue <= 5.0)
        {
            score = letterboxdValue * 20.0;
        }
        else
        {
            score = rating?.Score ?? NormalizeScoreFromValue(rating?.Value);
        }

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

    private static bool ShowRequiresMdbList(string primary, string fallback)
    {
        return RequiresMdbListSource(primary) || RequiresMdbListSource(fallback);
    }

    private static bool SeasonRequiresTrakt(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "trakt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "trakt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SeasonRequiresTmdb(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "tmdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "tmdb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EpisodeRequiresTmdb(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "tmdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "tmdb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EpisodeRequiresTrakt(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "trakt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "trakt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EpisodeRequiresTvMaze(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "tvmaze", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "tvmaze", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EpisodeRequiresOmdb(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "imdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "imdb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EpisodeRequiresWhatsOn(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "whatson", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "whatson", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SeasonRequiresWhatsOn(string primary, string fallback)
    {
        return string.Equals(NormalizeSource(primary), "whatson", StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeSource(fallback), "whatson", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> WhatsOnOnlySources = new(StringComparer.OrdinalIgnoreCase)
    {
        "whatson", "senscritique", "allocine_critics", "allocine_users", "betaseries",
        "whatson_imdb", "whatson_tmdb", "whatson_trakt", "whatson_tomatoes", "whatson_popcorn",
        "whatson_metacritic", "whatson_metacriticuser", "whatson_letterboxd"
    };

    internal static bool IsWhatsOnOnlySource(string? source)
    {
        return WhatsOnOnlySources.Contains(NormalizeSource(source));
    }


    private static readonly HashSet<string> SimklOnlySources = new(StringComparer.OrdinalIgnoreCase)
    {
        "simkl", "simkl_imdb", "simkl_mal"
    };

    internal static bool IsSimklOnlySource(string? source)
    {
        return SimklOnlySources.Contains(NormalizeSource(source));
    }

    // Movie/Show community and critic sources can select "<Provider> (WhatsOn)" as an
    // alternative to fetching the same data from MDBList (which has a daily rate limit, vs
    // WhatsOn's hourly limit). These aliases resolve to the same native rating key so the
    // resulting icon/label matches selecting the native source (e.g. "imdb") directly.
    private static string MapProviderAliasToNative(string source)
    {
        return source switch
        {
            "whatson_imdb" => "imdb",
            "whatson_tmdb" => "tmdb",
            "whatson_trakt" => "trakt",
            "whatson_tomatoes" => "tomatoes",
            "whatson_popcorn" => "popcorn",
            "whatson_metacritic" => "metacritic",
            "whatson_metacriticuser" => "metacriticuser",
            "whatson_letterboxd" => "letterboxd",
            "simkl_imdb" => "imdb",
            "simkl_mal" => "myanimelist",
            _ => source
        };
    }

    // Returns the distinct WhatsOn-backed source keys (e.g. "senscritique", "whatson") that are
    // actually configured among the given primary/fallback sources, so cache checks can look for
    // the specific rating(s) needed instead of the generic "whatson" aggregate key.
    private static IReadOnlyCollection<string> GetWhatsOnSources(params string?[] sources)
    {
        return sources
            .Select(NormalizeSource)
            .Where(IsWhatsOnOnlySource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool RequiresMdbListSource(string? source)
    {
        var s = NormalizeSource(source);
        return !string.IsNullOrWhiteSpace(s) && s != "none" && s != "tvmaze" && !IsWhatsOnOnlySource(s) && !IsSimklOnlySource(s);
    }

    private async Task ApplyWhatsOnRateLimitAsync(WhatsOnApiResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!result.IsRateLimited)
        {
            return;
        }

        var retryAfter = result.RetryAfterSeconds > 0 ? result.RetryAfterSeconds : 3600;
        await _whatsOnRateLimit.UpdateAsync(null, 0, now.AddSeconds(retryAfter), true, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("WhatsOn API rate limit reached. Retry after {Seconds} seconds.", retryAfter);
    }

    private bool IsWhatsOnRateLimitActive(DateTimeOffset now)
    {
        return _whatsOnRateLimit.NotBeforeUtc.HasValue && _whatsOnRateLimit.NotBeforeUtc.Value > now;
    }


    private async Task ApplySimklRateLimitAsync(SimklApiResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!result.IsRateLimited)
        {
            return;
        }

        var retryAfter = result.RetryAfterSeconds > 0 ? result.RetryAfterSeconds : 60;
        await _simklRateLimit.UpdateAsync(null, 0, now.AddSeconds(retryAfter), true, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Simkl API rate limit reached. Retry after {Seconds} seconds.", retryAfter);
    }

    private bool IsSimklRateLimitActive(DateTimeOffset now)
    {
        return _simklRateLimit.NotBeforeUtc.HasValue && _simklRateLimit.NotBeforeUtc.Value > now;
    }

    private static string MapWhatsOnToImdb(string source)
    {
        return string.Equals(source, "whatson", StringComparison.OrdinalIgnoreCase) ? "imdb" : source;
    }

    private async Task<IReadOnlyList<MdbListRating>?> TryFetchWhatsOnEpisodeRatingAsync(string? showTmdbId, int seasonNumber, int episodeNumber, PluginConfiguration cfg, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (IsWhatsOnRateLimitActive(now) || !int.TryParse(showTmdbId, out var tmdbId))
        {
            return null;
        }

        var lookup = await _whatsOn.GetEpisodeRatingAsync(tmdbId, seasonNumber, episodeNumber, cfg.WhatsOnApiKey, cancellationToken).ConfigureAwait(false);
        if (lookup.IsRateLimited)
        {
            await ApplyWhatsOnRateLimitAsync(lookup, now, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return lookup.Data?.Ratings is { Count: > 0 } ratings ? ratings : null;
    }

    private async Task<IReadOnlyList<MdbListRating>?> TryFetchWhatsOnSeasonRatingAsync(string? showTmdbId, int seasonNumber, PluginConfiguration cfg, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (IsWhatsOnRateLimitActive(now) || !int.TryParse(showTmdbId, out var tmdbId))
        {
            return null;
        }

        var lookup = await _whatsOn.GetSeasonRatingAsync(tmdbId, seasonNumber, cfg.WhatsOnApiKey, cancellationToken).ConfigureAwait(false);
        if (lookup.IsRateLimited)
        {
            await ApplyWhatsOnRateLimitAsync(lookup, now, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return lookup.Data?.Ratings is { Count: > 0 } ratings ? ratings : null;
    }

    private static string? BuildCacheKey(string contentType, string? tmdbId, string? imdbId, string? tvdbId)
    {
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            return $"{contentType}:{tmdbId.Trim()}";
        }

        var normalizedImdb = NormalizeImdbId(imdbId);
        if (!string.IsNullOrWhiteSpace(normalizedImdb))
        {
            return $"{contentType}:imdb:{normalizedImdb}";
        }

        var normalizedTvdb = NormalizeDigits(tvdbId);
        if (!string.IsNullOrWhiteSpace(normalizedTvdb))
        {
            return $"{contentType}:tvdb:{normalizedTvdb}";
        }

        return null;
    }

    private static bool EnsureMdbListScoreAverageFromRawJson(MdbListCacheStore.CacheEnvelope env)
    {
        if (env.Data is null || HasRatingSource(env.Data, "mdblist") || string.IsNullOrWhiteSpace(env.RawJson))
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(env.RawJson);
            if (!doc.RootElement.TryGetProperty("score_average", out var scoreElement))
            {
                return false;
            }

            double score;
            if (scoreElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (!scoreElement.TryGetDouble(out score))
                {
                    return false;
                }
            }
            else if (scoreElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var raw = scoreElement.GetString();
                if (!double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out score))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0 || score > 100)
            {
                return false;
            }

            UpsertRating(env.Data, new MdbListRating
            {
                Source = "mdblist",
                Value = score,
                Score = score
            });
            env.Data.ScoreAverage = score;
            return true;
        }
        catch
        {
            // Old cache files may contain non-MDBList RawJson markers (for example the IMDb
            // fallback marker). Those are intentionally ignored.
            return false;
        }
    }

    private static bool HasRatingSource(MdbListTitleResponse? data, string source)
    {
        if (data?.Ratings is null || data.Ratings.Count == 0)
        {
            return false;
        }

        return data.Ratings.Any(r => string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase));
    }

    private static void UpsertRating(MdbListTitleResponse data, MdbListRating rating)
    {
        var existing = data.Ratings.FirstOrDefault(r => string.Equals(r.Source, rating.Source, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            data.Ratings.Add(rating);
            return;
        }

        existing.Value = rating.Value;
        existing.Score = rating.Score;
        existing.Votes = rating.Votes;
        existing.Url = rating.Url;
    }

    private static void EnsureIds(MdbListTitleResponse data, string? tmdbId, string? imdbId)
    {
        data.Ids ??= new MdbListIds();

        if (string.IsNullOrWhiteSpace(data.Ids.Imdb))
        {
            data.Ids.Imdb = NormalizeImdbId(imdbId);
        }

        if (!data.Ids.Tmdb.HasValue && int.TryParse(tmdbId, out var tmdb))
        {
            data.Ids.Tmdb = tmdb;
        }
    }

    private async Task<MdbListRating?> TryFetchTvMazeRatingAsync(string? imdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        var lookup = await _tvMaze.LookupShowAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
        if (lookup is null || lookup.AverageRating <= 0)
        {
            return null;
        }

        var avg = lookup.AverageRating;
        return new MdbListRating
        {
            Source = "tvmaze",
            Value = Math.Round(avg, 1, MidpointRounding.AwayFromZero),
            Score = Math.Round(avg * 10.0, 1, MidpointRounding.AwayFromZero),
            Url = lookup.Url
        };
    }

    private async Task<MdbListRating?> TryFetchTvMazeEpisodeRatingAsync(string? imdbId, string? tvdbId, int seasonNumber, int episodeNumber, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var lookup = await _tvMaze.LookupEpisodeAsync(imdbId, tvdbId, seasonNumber, episodeNumber, ttl, cancellationToken).ConfigureAwait(false);
        if (lookup is null || lookup.AverageRating <= 0)
        {
            return null;
        }

        var avg = lookup.AverageRating;
        return new MdbListRating
        {
            Source = "tvmaze",
            Value = Math.Round(avg, 1, MidpointRounding.AwayFromZero),
            Score = Math.Round(avg * 10.0, 1, MidpointRounding.AwayFromZero),
            Url = lookup.Url
        };
    }

    private async Task<MdbListRating?> TryFetchTraktEpisodeRatingAsync(string? imdbId, string? tvdbId, int seasonNumber, int episodeNumber, TimeSpan ttl, string? clientId, CancellationToken cancellationToken)
    {
        var lookup = await _traktEpisode.LookupEpisodeAsync(imdbId, tvdbId, seasonNumber, episodeNumber, ttl, clientId, cancellationToken).ConfigureAwait(false);
        if (lookup is null || lookup.AverageRating <= 0)
        {
            return null;
        }

        var avg = lookup.AverageRating;
        return new MdbListRating
        {
            Source = "trakt",
            Value = Math.Round(avg, 1, MidpointRounding.AwayFromZero),
            Score = Math.Round(avg * 10.0, 1, MidpointRounding.AwayFromZero),
            Votes = lookup.Votes,
            Url = lookup.Url
        };
    }

    private async Task<OmdbEpisodeApiClient.OmdbEpisodeLookupResponse> TryFetchOmdbEpisodeRatingAsync(string? episodeImdbId, TimeSpan ttl, string? apiKey, CancellationToken cancellationToken)
    {
        return await _omdbEpisode.LookupEpisodeByImdbAsync(episodeImdbId, ttl, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeImdbId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var trimmed = imdbId.Trim();
        if (trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            var digits = NormalizeDigits(trimmed[2..]);
            return string.IsNullOrWhiteSpace(digits) ? null : "tt" + digits;
        }

        var onlyDigits = NormalizeDigits(trimmed);
        return string.IsNullOrWhiteSpace(onlyDigits) ? null : "tt" + onlyDigits;
    }

    private static string? NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static float? TryExtractImdbFallbackCommunityRating(MdbListCacheStore.CacheEnvelope env)
    {
        try
        {
            if (env is null || env.Data is null || env.Data.Ratings is null || env.Data.Ratings.Count == 0)
            {
                return null;
            }

            // We only treat this as a hard IMDb fallback when we explicitly marked it.
            if (string.IsNullOrWhiteSpace(env.RawJson) || env.RawJson.IndexOf("imdb-fallback", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            var rating = env.Data.Ratings.FirstOrDefault(r => string.Equals(r.Source, "imdb", StringComparison.OrdinalIgnoreCase));
            if (rating?.Value is null)
            {
                return null;
            }

            var v = rating.Value.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
            {
                return null;
            }

            // IMDb averageRating is 0-10.
            return (float)v;
        }
        catch
        {
            return null;
        }
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
            await _omdbRateLimit.LoadAsync(cancellationToken).ConfigureAwait(false);
            await _simklRateLimit.LoadAsync(cancellationToken).ConfigureAwait(false);
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
        public bool OmdbRateLimitHit { get; init; }

        public float? ImdbFallbackCommunityRating { get; init; }
    }

    internal async Task<MdbListCacheStore.CacheEnvelope?> TryGetCacheEnvelopeByItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        string? key = null;
        if (item is Movie)
        {
            key = BuildCacheKey("movie", item.GetProviderId(MetadataProvider.Tmdb), item.GetProviderId(MetadataProvider.Imdb), item.GetProviderId(MetadataProvider.Tvdb));
        }
        else if (item is Series)
        {
            key = BuildCacheKey("show", item.GetProviderId(MetadataProvider.Tmdb), item.GetProviderId(MetadataProvider.Imdb), item.GetProviderId(MetadataProvider.Tvdb));
        }
        else if (item is Season seasonItem)
        {
            var seasonLookup = ResolveSeasonLookup(item, seasonItem);
            if (seasonLookup.SeasonNumber.HasValue)
            {
                key = BuildSeasonCacheKey(seasonLookup.ShowTmdbId, seasonLookup.ShowImdbId, seasonLookup.ShowTvdbId, seasonLookup.SeasonNumber.Value);
            }
        }
        else if (item is Episode episodeItem)
        {
            var episodeLookup = ResolveEpisodeLookup(item, episodeItem);
            if (episodeLookup.SeasonNumber.HasValue && episodeLookup.EpisodeNumber.HasValue)
            {
                key = BuildEpisodeCacheKey(episodeLookup.ShowTmdbId, episodeLookup.ShowImdbId, episodeLookup.ShowTvdbId, episodeLookup.SeasonNumber.Value, episodeLookup.EpisodeNumber.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return await _cacheStore.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private sealed class EpisodeLookup
    {
        public string? ShowTmdbId { get; init; }
        public string? ShowImdbId { get; init; }
        public string? ShowTvdbId { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
    }

    private static string BuildEpisodeCacheKey(string? showTmdbId, string? showImdbId, string? showTvdbId, int seasonNumber, int episodeNumber)
    {
        if (seasonNumber < 0 || episodeNumber <= 0)
        {
            return string.Empty;
        }

        var tmdb = NormalizeDigits(showTmdbId);
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            return $"episode:tmdb:{tmdb}:season:{seasonNumber}:episode:{episodeNumber}";
        }

        var imdb = NormalizeImdbId(showImdbId);
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            return $"episode:imdb:{imdb}:season:{seasonNumber}:episode:{episodeNumber}";
        }

        var tvdb = NormalizeDigits(showTvdbId);
        if (!string.IsNullOrWhiteSpace(tvdb))
        {
            return $"episode:tvdb:{tvdb}:season:{seasonNumber}:episode:{episodeNumber}";
        }

        return string.Empty;
    }

    private EpisodeLookup ResolveEpisodeLookup(BaseItem item, Episode episodeItem)
    {
        // Important: for TMDb episode lookup we need SERIES identifiers, not the episode's own external ids.
        // Some libraries store episode-level TVDB/IMDb ids on Episode items. If we prefer those ids, TMDb /find can fail
        // or resolve incorrectly. Therefore, resolve parent Series ids first and only fall back to item ids if needed.
        string? itemTmdb = item.GetProviderId(MetadataProvider.Tmdb);
        string? itemImdb = item.GetProviderId(MetadataProvider.Imdb);
        string? itemTvdb = item.GetProviderId(MetadataProvider.Tvdb);
        string? showTmdbId = null;
        string? showImdbId = null;
        string? showTvdbId = null;
        int? resolvedSeason = episodeItem.ParentIndexNumber;
        int? resolvedEpisode = episodeItem.IndexNumber;

        BaseItem? parent = item.DisplayParent;
        if (parent is null && item.ParentId != Guid.Empty)
        {
            try { parent = Plugin.Instance?.LibraryManager.GetItemById(item.ParentId); } catch { parent = null; }
        }

        while (parent is not null)
        {
            if (parent is Season parentSeason)
            {
                resolvedSeason ??= parentSeason.IndexNumber;
                showTmdbId ??= parentSeason.Series?.GetProviderId(MetadataProvider.Tmdb);
                showImdbId ??= parentSeason.Series?.GetProviderId(MetadataProvider.Imdb);
                showTvdbId ??= parentSeason.Series?.GetProviderId(MetadataProvider.Tvdb);
            }
            else if (parent is Series parentSeries)
            {
                showTmdbId ??= parentSeries.GetProviderId(MetadataProvider.Tmdb);
                showImdbId ??= parentSeries.GetProviderId(MetadataProvider.Imdb);
                showTvdbId ??= parentSeries.GetProviderId(MetadataProvider.Tvdb);
            }

            BaseItem? nextParent = parent.DisplayParent;
            if (nextParent is null && parent.ParentId != Guid.Empty)
            {
                try { nextParent = Plugin.Instance?.LibraryManager.GetItemById(parent.ParentId); } catch { nextParent = null; }
            }

            parent = nextParent;
        }

        showTmdbId ??= itemTmdb;
        showImdbId ??= itemImdb;
        showTvdbId ??= itemTvdb;

        return new EpisodeLookup
        {
            ShowTmdbId = NormalizeDigits(showTmdbId),
            ShowImdbId = NormalizeImdbId(showImdbId),
            ShowTvdbId = NormalizeDigits(showTvdbId),
            SeasonNumber = resolvedSeason,
            EpisodeNumber = resolvedEpisode
        };
    }

    private async Task<FetchResult> GetCachedOrFetchEpisodeAsync(
        BaseItem item,
        string? episodeImdbId,
        string? showTmdbId,
        string? showImdbId,
        string? showTvdbId,
        int seasonNumber,
        int episodeNumber,
        PluginConfiguration cfg,
        string episodePrimarySource,
        string episodeFallbackSource,
        bool needsEpisodeTmdb,
        bool needsEpisodeTrakt,
        bool needsEpisodeTvMaze,
        bool needsEpisodeOmdb,
        bool needsEpisodeWhatsOn,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildEpisodeCacheKey(showTmdbId, showImdbId, showTvdbId, seasonNumber, episodeNumber);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped };
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = GetTtl(cfg);
        var negativeTtl = ttl < TimeSpan.FromDays(7) ? ttl : TimeSpan.FromDays(7);
        if (negativeTtl <= TimeSpan.Zero)
        {
            negativeTtl = TimeSpan.FromDays(1);
        }

        var normalizedEpisodePrimary = NormalizeSource(episodePrimarySource);
        var whatsOnPrimary = string.Equals(normalizedEpisodePrimary, "whatson", StringComparison.OrdinalIgnoreCase);
        var omdbCooldownActive = _omdbRateLimit.NotBeforeUtc.HasValue && _omdbRateLimit.NotBeforeUtc.Value > now;
        var omdbRateLimitHit = false;

        var cached = await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var env = cached ?? new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = new MdbListTitleResponse
            {
                Type = "episode",
                Ids = new MdbListIds
                {
                    Imdb = NormalizeImdbId(showImdbId),
                    Tmdb = int.TryParse(NormalizeDigits(showTmdbId), out var parsedTmdb) ? parsedTmdb : null
                }
            }
        };

        env.Data ??= new MdbListTitleResponse { Type = "episode" };
        env.Data.Ratings ??= new List<MdbListRating>();
        EnsureIds(env.Data, showTmdbId, showImdbId);
        var changed = false;

        bool IsNegativeFresh(string provider)
        {
            return env.ProviderMissesUtc is not null
                && env.ProviderMissesUtc.TryGetValue(provider, out var missedAt)
                && now - missedAt <= negativeTtl;
        }

        void MarkNegative(string provider)
        {
            env.ProviderMissesUtc ??= new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            env.ProviderMissesUtc[provider] = now;
            changed = true;
        }

        void ClearNegative(string provider)
        {
            if (env.ProviderMissesUtc is not null && env.ProviderMissesUtc.Remove(provider))
            {
                changed = true;
            }
        }

        // WhatsOn and OMDb episode ratings are both IMDb-sourced. Respect the configured
        // order, but an OMDb cooldown/quota hit must never prevent fallback providers below.
        async Task FetchWhatsOnEpisodeAsync()
        {
            if (needsEpisodeWhatsOn && !HasRatingSource(env.Data, "imdb"))
            {
                try
                {
                    var ratings = await TryFetchWhatsOnEpisodeRatingAsync(showTmdbId, seasonNumber, episodeNumber, cfg, now, cancellationToken).ConfigureAwait(false);
                    if (ratings is not null)
                    {
                        foreach (var r in ratings)
                        {
                            UpsertRating(env.Data, r);
                        }

                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WhatsOn episode augmentation failed for {Key}", cacheKey);
                }
            }
        }

        if (whatsOnPrimary)
        {
            await FetchWhatsOnEpisodeAsync().ConfigureAwait(false);
        }

        if (needsEpisodeOmdb && !HasRatingSource(env.Data, "imdb") && !IsNegativeFresh("omdb-episode"))
        {
            if (omdbCooldownActive)
            {
                // Provider-specific cooldown: skip OMDb only. TMDb/Trakt/TVMaze/WhatsOn
                // fallback work below is still allowed to run.
                _logger.LogDebug("OMDb episode cooldown active until {NotBeforeUtc:o}; skipping OMDb for {Key} and continuing with other providers.", _omdbRateLimit.NotBeforeUtc!.Value, cacheKey);
            }
            else
            {
                var omdbLookup = await TryFetchOmdbEpisodeRatingAsync(episodeImdbId, ttl, cfg.OmdbApiKey, cancellationToken).ConfigureAwait(false);
                if (omdbLookup.IsRateLimited)
                {
                    await _omdbRateLimit.UpdateAsync(null, 0, null, true, cancellationToken).ConfigureAwait(false);
                    omdbRateLimitHit = true;
                    _logger.LogWarning("OMDb daily request limit reached while updating episode ratings at {Key}. OMDb will pause, but other providers and fallback ratings will continue.", cacheKey);
                }
                else
                {
                    await _omdbRateLimit.UpdateAsync(null, 1, null, false, cancellationToken).ConfigureAwait(false);
                    if (omdbLookup.Data is not null)
                    {
                        UpsertRating(env.Data, new MdbListRating
                        {
                            Source = "imdb",
                            Value = omdbLookup.Data.AverageRating,
                            Score = Math.Round(omdbLookup.Data.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                            Votes = omdbLookup.Data.Votes,
                            Url = omdbLookup.Data.Url
                        });
                        ClearNegative("omdb-episode");
                        changed = true;
                    }
                    else if (omdbLookup.IsDefinitiveMiss)
                    {
                        // Persist known OMDb misses for up to seven days (or the configured cache
                        // interval when shorter) so no-rating episodes do not consume quota daily.
                        MarkNegative("omdb-episode");
                        _logger.LogDebug("Negative-cached OMDb episode miss for {Key} until approximately {Expires:o}.", cacheKey, now.Add(negativeTtl));
                    }
                }
            }
        }

        if (!whatsOnPrimary)
        {
            await FetchWhatsOnEpisodeAsync().ConfigureAwait(false);
        }

        if (needsEpisodeTmdb && !HasRatingSource(env.Data, "tmdb"))
        {
            try
            {
                var tmdb = await _tmdbEpisode.LookupEpisodeAsync(showTmdbId, showImdbId, showTvdbId, seasonNumber, episodeNumber, cfg.TmdbApiAuth, cancellationToken).ConfigureAwait(false);
                if (tmdb is not null && tmdb.AverageRating > 0)
                {
                    UpsertRating(env.Data, new MdbListRating
                    {
                        Source = "tmdb",
                        Value = Math.Round(tmdb.AverageRating, 1, MidpointRounding.AwayFromZero),
                        Score = Math.Round(tmdb.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                        Votes = tmdb.Votes,
                        Url = tmdb.Url
                    });
                    if (env.Data.Ids is null || !env.Data.Ids.Tmdb.HasValue)
                    {
                        EnsureIds(env.Data, tmdb.SeriesTmdbId.ToString(), showImdbId);
                    }

                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TMDb episode augmentation failed for {Key}", cacheKey);
            }
        }

        if (needsEpisodeTrakt && !HasRatingSource(env.Data, "trakt"))
        {
            try
            {
                var trakt = await TryFetchTraktEpisodeRatingAsync(showImdbId, showTvdbId, seasonNumber, episodeNumber, ttl, cfg.TraktClientId, cancellationToken).ConfigureAwait(false);
                if (trakt is not null)
                {
                    UpsertRating(env.Data, trakt);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt episode augmentation failed for {Key}", cacheKey);
            }
        }

        if (needsEpisodeTvMaze && !HasRatingSource(env.Data, "tvmaze"))
        {
            try
            {
                var tvmaze = await TryFetchTvMazeEpisodeRatingAsync(showImdbId, showTvdbId, seasonNumber, episodeNumber, ttl, cancellationToken).ConfigureAwait(false);
                if (tvmaze is not null)
                {
                    UpsertRating(env.Data, tvmaze);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVMaze episode augmentation failed for {Key}", cacheKey);
            }
        }

        var episodeSourceCount = (needsEpisodeTmdb ? 1 : 0) + (needsEpisodeTrakt ? 1 : 0) + (needsEpisodeTvMaze ? 1 : 0) + (needsEpisodeOmdb ? 1 : 0) + (needsEpisodeWhatsOn ? 1 : 0);
        var episodeSourceTag = episodeSourceCount > 1
            ? "episode-multi"
            : needsEpisodeOmdb
                ? "omdb-episode"
                : needsEpisodeTrakt
                    ? "trakt-episode"
                    : needsEpisodeTvMaze
                        ? "tvmaze-episode"
                        : needsEpisodeWhatsOn
                            ? "whatson-episode"
                            : "tmdb-episode";

        if (changed || cached is null)
        {
            env.CachedAtUtc = now;
            env.RawJson = $"{{\"source\":\"{episodeSourceTag}\"}}";
            await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
        }

        if (env.Data.Ratings.Count == 0)
        {
            return new FetchResult
            {
                Data = env.Data,
                Outcome = cached is null && !changed ? UpdateOutcome.Failed : UpdateOutcome.Skipped,
                OmdbRateLimitHit = omdbRateLimitHit
            };
        }

        return new FetchResult
        {
            Data = env.Data,
            Outcome = UpdateOutcome.Skipped,
            OmdbRateLimitHit = omdbRateLimitHit
        };
    }

    private async Task<FetchResult> GetCachedOrFetchSeasonAsync(
        BaseItem item,
        string? showTmdbId,
        string? showImdbId,
        string? showTvdbId,
        int seasonNumber,
        PluginConfiguration cfg,
        bool needsSeasonTrakt,
        bool needsSeasonTmdb,
        bool needsSeasonWhatsOn,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildSeasonCacheKey(showTmdbId, showImdbId, showTvdbId, seasonNumber);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped };
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = GetTtl(cfg);

        async Task<FetchResult> ReturnSeasonCacheAsync(MdbListCacheStore.CacheEnvelope env)
        {
            var changed = false;
            EnsureIds(env.Data, showTmdbId, showImdbId);

            if (needsSeasonTrakt && !HasRatingSource(env.Data, "trakt"))
            {
                try
                {
                    var trakt = await _traktSeason.LookupSeasonAsync(showImdbId, showTvdbId, seasonNumber, cfg.TraktClientId, cancellationToken).ConfigureAwait(false);
                    if (trakt is not null && trakt.AverageRating > 0)
                    {
                        UpsertRating(env.Data, new MdbListRating
                        {
                            Source = "trakt",
                            Value = Math.Round(trakt.AverageRating, 1, MidpointRounding.AwayFromZero),
                            Score = Math.Round(trakt.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                            Votes = trakt.Votes,
                            Url = trakt.Url
                        });
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Trakt season augmentation failed for {Key}", cacheKey);
                }
            }

            if (needsSeasonTmdb && !HasRatingSource(env.Data, "tmdb"))
            {
                try
                {
                    var tmdb = await _tmdbSeason.LookupSeasonAsync(showTmdbId, showImdbId, showTvdbId, seasonNumber, cfg.TmdbApiAuth, cancellationToken).ConfigureAwait(false);
                    if (tmdb is not null && tmdb.AverageRating > 0)
                    {
                        UpsertRating(env.Data, new MdbListRating
                        {
                            Source = "tmdb",
                            Value = Math.Round(tmdb.AverageRating, 1, MidpointRounding.AwayFromZero),
                            Score = Math.Round(tmdb.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                            Votes = tmdb.Votes,
                            Url = tmdb.Url
                        });
                        if (env.Data.Ids is null || !env.Data.Ids.Tmdb.HasValue)
                        {
                            EnsureIds(env.Data, tmdb.SeriesTmdbId.ToString(), showImdbId);
                        }
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TMDb season augmentation failed for {Key}", cacheKey);
                }
            }

            if (needsSeasonWhatsOn && !HasRatingSource(env.Data, "imdb"))
            {
                try
                {
                    var ratings = await TryFetchWhatsOnSeasonRatingAsync(showTmdbId, seasonNumber, cfg, now, cancellationToken).ConfigureAwait(false);
                    if (ratings is not null)
                    {
                        foreach (var r in ratings)
                        {
                            UpsertRating(env.Data, r);
                        }
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WhatsOn season augmentation failed for {Key}", cacheKey);
                }
            }

            if (changed)
            {
                env.CachedAtUtc = now;
                await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
            }

            return new FetchResult { Data = env.Data, Outcome = UpdateOutcome.Skipped };
        }

        var cached = await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null && (now - cached.CachedAtUtc) <= ttl)
        {
            return await ReturnSeasonCacheAsync(cached).ConfigureAwait(false);
        }

        var data = cached?.Data ?? new MdbListTitleResponse
        {
            Type = "season",
            Ids = new MdbListIds
            {
                Imdb = NormalizeImdbId(showImdbId),
                Tmdb = int.TryParse(NormalizeDigits(showTmdbId), out var parsedTmdb) ? parsedTmdb : null
            }
        };
        EnsureIds(data, showTmdbId, showImdbId);

        if (needsSeasonTrakt && !HasRatingSource(data, "trakt"))
        {
            var lookup = await _traktSeason.LookupSeasonAsync(showImdbId, showTvdbId, seasonNumber, cfg.TraktClientId, cancellationToken).ConfigureAwait(false);
            if (lookup is not null && lookup.AverageRating > 0)
            {
                UpsertRating(data, new MdbListRating
                {
                    Source = "trakt",
                    Value = Math.Round(lookup.AverageRating, 1, MidpointRounding.AwayFromZero),
                    Score = Math.Round(lookup.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                    Votes = lookup.Votes,
                    Url = lookup.Url
                });
            }
        }

        if (needsSeasonTmdb && !HasRatingSource(data, "tmdb"))
        {
            var lookup = await _tmdbSeason.LookupSeasonAsync(showTmdbId, showImdbId, showTvdbId, seasonNumber, cfg.TmdbApiAuth, cancellationToken).ConfigureAwait(false);
            if (lookup is not null && lookup.AverageRating > 0)
            {
                UpsertRating(data, new MdbListRating
                {
                    Source = "tmdb",
                    Value = Math.Round(lookup.AverageRating, 1, MidpointRounding.AwayFromZero),
                    Score = Math.Round(lookup.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                    Votes = lookup.Votes,
                    Url = lookup.Url
                });
                EnsureIds(data, lookup.SeriesTmdbId.ToString(), showImdbId);
            }
        }

        if (needsSeasonWhatsOn && !HasRatingSource(data, "imdb"))
        {
            var ratings = await TryFetchWhatsOnSeasonRatingAsync(showTmdbId, seasonNumber, cfg, now, cancellationToken).ConfigureAwait(false);
            if (ratings is not null)
            {
                foreach (var r in ratings)
                {
                    UpsertRating(data, r);
                }
            }
        }

        if (data.Ratings.Count == 0)
        {
            if (cached is not null)
            {
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped };
            }

            return new FetchResult { Data = null, Outcome = UpdateOutcome.Failed };
        }

        var seasonSourceTag = (needsSeasonTmdb, needsSeasonTrakt, needsSeasonWhatsOn) switch
        {
            (true, true, _) => "season-multi",
            (true, false, false) => "tmdb-season",
            (false, true, false) => "trakt-season",
            (false, false, true) => "whatson-season",
            _ => "season-multi"
        };

        var env = new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = data,
            RawJson = $"{{\"source\":\"{seasonSourceTag}\"}}"
        };

        await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
        return new FetchResult { Data = data, Outcome = UpdateOutcome.Skipped };
    }

    private async Task<FetchResult> GetCachedOrFetchAsync(
        string contentType,
        string? tmdbId,
        string? imdbId,
        string? tvdbId,
        PluginConfiguration cfg,
        bool needsMdbList,
        bool needsWhatsOn,
        bool needsSimkl,
        bool needsTvMaze,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(contentType, tmdbId, imdbId, tvdbId);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped };
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = GetTtl(cfg);
        var cached = await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var env = cached ?? new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = new MdbListTitleResponse
            {
                Type = contentType,
                Ids = new MdbListIds
                {
                    Imdb = NormalizeImdbId(imdbId),
                    Tmdb = int.TryParse(tmdbId, out var initialTmdb) ? initialTmdb : null
                }
            }
        };

        env.Data ??= new MdbListTitleResponse { Type = contentType };
        env.Data.Ratings ??= new List<MdbListRating>();
        EnsureIds(env.Data, tmdbId, imdbId);

        // Upgrade existing cache files created before score_average was represented as a
        // normal rating entry. RawJson contains the original MDBList response, so this can
        // be done locally without spending another MDBList API request.
        var cacheChanged = EnsureMdbListScoreAverageFromRawJson(env);
        float? imdbFallbackCommunityRating = null;

        static bool IsFresh(DateTimeOffset? fetchedAt, DateTimeOffset current, TimeSpan maxAge)
            => fetchedAt.HasValue && current - fetchedAt.Value <= maxAge;

        var canFetchMdbList = needsMdbList
            && !string.IsNullOrWhiteSpace(cfg.MdbListApiKey)
            && !string.IsNullOrWhiteSpace(tmdbId);
        var canFetchWhatsOn = needsWhatsOn
            && (!string.IsNullOrWhiteSpace(tmdbId) || !string.IsNullOrWhiteSpace(imdbId));
        var canFetchSimkl = needsSimkl
            && !string.IsNullOrWhiteSpace(cfg.SimklClientId)
            && (!string.IsNullOrWhiteSpace(tmdbId) || !string.IsNullOrWhiteSpace(imdbId) || !string.IsNullOrWhiteSpace(tvdbId));

        // ---- MDBList -------------------------------------------------------
        // Fetch independently from the configured primary/fallback source. This keeps the
        // shared cache populated even when the user currently selects a WhatsOn-only source.
        if (canFetchMdbList && !IsFresh(env.MdbListFetchedAtUtc, now, ttl))
        {
            if (!_rateLimit.NotBeforeUtc.HasValue || _rateLimit.NotBeforeUtc.Value <= now)
            {
                if (cfg.RequestDelayMs > 0)
                {
                    await Task.Delay(cfg.RequestDelayMs, cancellationToken).ConfigureAwait(false);
                }

                var api = await _client.GetByTmdbAsync(contentType, tmdbId!, cfg.MdbListApiKey, cancellationToken).ConfigureAwait(false);
                var quotaExhausted = api.RateLimitRemaining.HasValue && api.RateLimitRemaining.Value <= 0;
                await _rateLimit.UpdateAsync(
                        api.RateLimitLimit,
                        api.RateLimitRemaining,
                        api.RateLimitResetUtc,
                        api.IsRateLimited || quotaExhausted,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!api.IsRateLimited)
                {
                    // A completed request (including 404) counts as a provider refresh. This avoids
                    // hammering MDBList for titles that legitimately have no entry.
                    env.MdbListFetchedAtUtc = now;
                    cacheChanged = true;

                    if (api.Data is not null)
                    {
                        // MDBList is authoritative for its own source keys. Preserve data that only
                        // WhatsOn/TVMaze can supply, then replace the MDBList portion with fresh data.
                        var preservedRatings = env.Data.Ratings
                            .Where(r => IsWhatsOnOnlySource(r.Source)
                                || IsSimklOnlySource(r.Source)
                                || string.Equals(r.Source, "tvmaze", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var preservedFeatures = env.Data.WhatsOnFeatures;

                        env.Data = api.Data;
                        env.Data.Ratings ??= new List<MdbListRating>();
                        EnsureIds(env.Data, tmdbId, imdbId);
                        foreach (var r in preservedRatings)
                        {
                            UpsertRating(env.Data, r);
                        }
                        env.Data.WhatsOnFeatures = preservedFeatures;
                        env.RawJson = api.RawJson;
                    }
                    else if (api.StatusCode == 404 && !string.IsNullOrWhiteSpace(imdbId))
                    {
                        // Preserve the existing IMDb dataset fallback behavior for missing MDBList
                        // titles, but do not return early: WhatsOn must still be fetched below.
                        try
                        {
                            var imdbFallback = await _imdbFallback.TryGetRatingInfoAsync(imdbId, ttl, cancellationToken).ConfigureAwait(false);
                            if (imdbFallback.HasValue && imdbFallback.Value.AverageRating > 0)
                            {
                                UpsertRating(env.Data, new MdbListRating
                                {
                                    Source = "imdb",
                                    Value = imdbFallback.Value.AverageRating,
                                    Score = imdbFallback.Value.AverageRating * 10.0,
                                    Votes = imdbFallback.Value.Votes,
                                    Url = "https://www.imdb.com/title/" + imdbId.Trim()
                                });
                                imdbFallbackCommunityRating = imdbFallback.Value.AverageRating;
                                env.RawJson = "{\"source\":\"imdb-fallback\",\"reason\":\"mdblist-404\"}";
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "IMDb fallback failed for {ImdbId} (TMDb {TmdbId})", imdbId, tmdbId);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("MDBList rate limit reached for {Key}; continuing with cached MDBList data and still attempting other providers.", cacheKey);
                }
            }
        }

        // ---- WhatsOn -------------------------------------------------------
        // WhatsOn is also fetched independently from the selected source. Besides ratings it now
        // supplies IMDb top_ranking, Metacritic must_see and Rotten Tomatoes certification flags.
        if (canFetchWhatsOn
            && !IsFresh(env.WhatsOnFetchedAtUtc, now, ttl)
            && !IsWhatsOnRateLimitActive(now))
        {
            try
            {
                var tmdbParsed = int.TryParse(tmdbId, out var tid) ? (int?)tid : null;
                var whatsOnItemType = string.Equals(contentType, "show", StringComparison.OrdinalIgnoreCase) ? "tvshow" : "movie";
                var lookup = await _whatsOn.GetTitleRatingsAsync(tmdbParsed, imdbId, cfg.WhatsOnApiKey, whatsOnItemType, cancellationToken).ConfigureAwait(false);

                if (lookup.IsRateLimited)
                {
                    await ApplyWhatsOnRateLimitAsync(lookup, now, cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("WhatsOn rate limit reached for {Key}; continuing with cached WhatsOn data.", cacheKey);
                }
                else if ((lookup.StatusCode >= 200 && lookup.StatusCode < 300) || lookup.StatusCode == 404)
                {
                    // A successful or genuine not-found response is a completed provider refresh.
                    env.WhatsOnFetchedAtUtc = now;
                    cacheChanged = true;

                    if (lookup.Data is not null)
                    {
                        EnsureIds(env.Data, tmdbId, imdbId);
                        if (lookup.Data.Ratings is not null)
                        {
                            foreach (var r in lookup.Data.Ratings)
                            {
                                UpsertRating(env.Data, r);
                            }
                        }

                        if (lookup.Data.WhatsOnFeatures is not null)
                        {
                            env.Data.WhatsOnFeatures = lookup.Data.WhatsOnFeatures;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WhatsOn augmentation failed for {Key}", cacheKey);
            }
        }

        // ---- Simkl ---------------------------------------------------------
        // Simkl is populated independently from the selected rating source, like MDBList and
        // WhatsOn. Its IMDb/MAL ratings carry vote counts and participate in Web UI dedupe.
        if (canFetchSimkl
            && !IsFresh(env.SimklFetchedAtUtc, now, ttl)
            && !IsSimklRateLimitActive(now))
        {
            try
            {
                var lookup = await _simkl.GetTitleRatingsAsync(tmdbId, imdbId, tvdbId, contentType, cfg.SimklClientId, cancellationToken).ConfigureAwait(false);

                if (lookup.IsRateLimited)
                {
                    await ApplySimklRateLimitAsync(lookup, now, cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("Simkl rate limit/cooldown reached for {Key}; continuing with cached Simkl data and other providers.", cacheKey);
                }
                else if (lookup.IsCredentialRejected)
                {
                    _logger.LogWarning("Simkl rejected the configured Client ID for {Key}; continuing with other providers.", cacheKey);
                }
                else if ((lookup.StatusCode >= 200 && lookup.StatusCode < 300) || lookup.StatusCode == 404)
                {
                    env.SimklFetchedAtUtc = now;
                    cacheChanged = true;

                    // A completed Simkl refresh is authoritative for Simkl-owned keys.
                    env.Data.Ratings.RemoveAll(r => IsSimklOnlySource(r.Source));
                    if (lookup.Data?.Ratings is not null)
                    {
                        EnsureIds(env.Data, tmdbId, imdbId);
                        foreach (var r in lookup.Data.Ratings)
                        {
                            UpsertRating(env.Data, r);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Simkl augmentation failed for {Key}", cacheKey);
            }
        }

        // ---- TVMaze --------------------------------------------------------
        if (needsTvMaze && !HasRatingSource(env.Data, "tvmaze"))
        {
            try
            {
                var tvmazeRating = await TryFetchTvMazeRatingAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
                if (tvmazeRating is not null)
                {
                    UpsertRating(env.Data, tvmazeRating);
                    cacheChanged = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVMaze augmentation failed for {Key}", cacheKey);
            }
        }

        EnsureIds(env.Data, tmdbId, imdbId);

        if (cacheChanged || cached is null)
        {
            env.CachedAtUtc = now;
            await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
        }

        if (imdbFallbackCommunityRating.HasValue)
        {
            return new FetchResult
            {
                Data = env.Data,
                Outcome = UpdateOutcome.Skipped,
                ImdbFallbackCommunityRating = imdbFallbackCommunityRating.Value
            };
        }

        if (env.Data.Ratings is null || env.Data.Ratings.Count == 0)
        {
            return new FetchResult { Data = cached?.Data, Outcome = cached is null ? UpdateOutcome.Failed : UpdateOutcome.Skipped };
        }

        return new FetchResult { Data = env.Data, Outcome = UpdateOutcome.Skipped };
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
                PluginConfiguration.CacheIntervalPreset.Custom => TimeSpan.FromDays(Math.Max(1, cfg.CacheCustomDays)),
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
