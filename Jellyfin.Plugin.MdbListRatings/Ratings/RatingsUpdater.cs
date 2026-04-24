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

    private readonly ILogger _logger;
    private readonly MdbListClient _client;

    private readonly ImdbRatingsDataset _imdbFallback;
    private readonly ImdbTop250Dataset _imdbTop250;
    private readonly FilmwebClient _filmweb;
    private readonly TvMazeClient _tvMaze;
    private readonly TraktSeasonApiClient _traktSeason;
    private readonly TraktEpisodeApiClient _traktEpisode;
    private readonly TmdbSeasonApiClient _tmdbSeason;
    private readonly TmdbEpisodeApiClient _tmdbEpisode;
    private readonly OmdbEpisodeApiClient _omdbEpisode;

    private readonly MdbListCacheStore _cacheStore;
    private readonly RateLimitStateStore _rateLimit;
    private readonly RateLimitStateStore _omdbRateLimit;
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
        _imdbFallback = new ImdbRatingsDataset(httpClientFactory, cacheDir, logger);
        _imdbTop250 = new ImdbTop250Dataset(httpClientFactory, cacheDir, logger);
        _filmweb = new FilmwebClient(httpClientFactory, logger);
        _tvMaze = new TvMazeClient(httpClientFactory, logger);
        _traktSeason = new TraktSeasonApiClient(httpClientFactory, logger);
        _traktEpisode = new TraktEpisodeApiClient(httpClientFactory, logger);
        _tmdbSeason = new TmdbSeasonApiClient(httpClientFactory, logger);
        _tmdbEpisode = new TmdbEpisodeApiClient(httpClientFactory, logger);
        _omdbEpisode = new OmdbEpisodeApiClient(httpClientFactory, logger);
        _cacheStore = new MdbListCacheStore(cacheDir, logger);
        _rateLimit = new RateLimitStateStore(statePath, logger);
        _omdbRateLimit = new RateLimitStateStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(statePath) ?? string.Empty, "omdb-episode-state.json"), logger);
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

        string? contentType = null;
        bool isMovie = false;
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

        var effectiveShowPrimary = NormalizeSource(effectiveForTransport.ShowCommunitySource);
        var effectiveShowFallback = NormalizeSource(effectiveForTransport.ShowCommunityFallbackSource);
        var effectiveSeasonPrimary = NormalizeSource(effectiveForTransport.SeasonCommunitySource);
        var effectiveSeasonFallback = NormalizeSource(effectiveForTransport.SeasonCommunityFallbackSource);
        var effectiveEpisodePrimary = NormalizeSource(effectiveForTransport.EpisodeCommunitySource);
        var effectiveEpisodeFallback = NormalizeSource(effectiveForTransport.EpisodeCommunityFallbackSource);
        var effectiveMovieCommunityPrimary = NormalizeSource(effectiveForTransport.MovieCommunitySource);
        var effectiveMovieCommunityFallback = NormalizeSource(effectiveForTransport.MovieCommunityFallbackSource);
        var effectiveMovieCriticPrimary = NormalizeSource(effectiveForTransport.MovieCriticSource);
        var effectiveMovieCriticFallback = NormalizeSource(effectiveForTransport.MovieCriticFallbackSource);
        // Always try to keep TVmaze cached for series/shows when an external id is available,
        // so the Web "all ratings from cache" panel can display it even if TVmaze is not the
        // selected primary/fallback source for metadata writing.
        var needsTvMaze = !isMovie && !isSeason && !isEpisode && (!string.IsNullOrWhiteSpace(imdbId) || !string.IsNullOrWhiteSpace(tvdbId));
        var needsSeasonTrakt = isSeason && seasonNumber.HasValue && SeasonRequiresTrakt(effectiveSeasonPrimary, effectiveSeasonFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsSeasonTmdb = isSeason && seasonNumber.HasValue && SeasonRequiresTmdb(effectiveSeasonPrimary, effectiveSeasonFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowTmdbId) || !string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeTmdb = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresTmdb(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowTmdbId) || !string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeTrakt = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresTrakt(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeTvMaze = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresTvMaze(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && (!string.IsNullOrWhiteSpace(seasonShowImdbId) || !string.IsNullOrWhiteSpace(seasonShowTvdbId));
        var needsEpisodeOmdb = isEpisode && seasonNumber.HasValue && episodeNumber.HasValue && EpisodeRequiresOmdb(effectiveEpisodePrimary, effectiveEpisodeFallback)
            && !string.IsNullOrWhiteSpace(imdbId);
        var needsMovieMdbList = isMovie && MovieRequiresMdbList(effectiveMovieCommunityPrimary, effectiveMovieCommunityFallback, effectiveMovieCriticPrimary, effectiveMovieCriticFallback);
        var needsMdbList = !isSeason && !isEpisode && (needsMovieMdbList || (!isMovie && ShowRequiresMdbList(effectiveShowPrimary, effectiveShowFallback)));
        var needsFilmwebForWebPanel = cfg.EnableWebAllRatingsFromCache && !isSeason && !isEpisode;
        var needsFilmweb = !isSeason && !isEpisode
            && ((isMovie && MovieRequiresFilmweb(effectiveMovieCommunityPrimary, effectiveMovieCommunityFallback, effectiveMovieCriticPrimary, effectiveMovieCriticFallback))
                || (!isMovie && ShowRequiresFilmweb(effectiveShowPrimary, effectiveShowFallback))
                || needsFilmwebForWebPanel);

        if (!needsMdbList && !needsTvMaze && !needsFilmweb && !needsSeasonTrakt && !needsSeasonTmdb && !needsEpisodeTmdb && !needsEpisodeTrakt && !needsEpisodeTvMaze && !needsEpisodeOmdb)
        {
            return UpdateOutcome.Skipped;
        }

        if (needsMdbList && string.IsNullOrWhiteSpace(cfg.MdbListApiKey))
        {
            return UpdateOutcome.Skipped;
        }

        if ((needsSeasonTrakt || needsEpisodeTrakt) && string.IsNullOrWhiteSpace(cfg.TraktClientId))
        {
            return UpdateOutcome.Skipped;
        }

        if ((needsSeasonTmdb || needsEpisodeTmdb) && string.IsNullOrWhiteSpace(cfg.TmdbApiAuth))
        {
            return UpdateOutcome.Skipped;
        }

        if (needsEpisodeOmdb && string.IsNullOrWhiteSpace(cfg.OmdbApiKey))
        {
            return UpdateOutcome.Skipped;
        }

        if (needsMdbList && string.IsNullOrWhiteSpace(tmdbId))
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
                // Still allow cache augmentation for web-only providers (Filmweb) even when
                // metadata fields are already filled and won't be overwritten.
                if (!allowUpdateCommunity && !allowUpdateCritic && !needsFilmweb)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else if (isSeason)
            {
                if (!allowUpdateCommunity && !needsSeasonTrakt && !needsSeasonTmdb)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else if (isEpisode)
            {
                if (!allowUpdateCommunity && !needsEpisodeTmdb && !needsEpisodeTrakt && !needsEpisodeTvMaze && !needsEpisodeOmdb)
                {
                    return UpdateOutcome.Skipped;
                }
            }
            else
            {
                // Series: still allow fetching/augmenting the cache (e.g. TVmaze/Filmweb) even when we
                // are not going to overwrite the saved CommunityRating field.
                if (!allowUpdateCommunity && !needsTvMaze && !needsFilmweb)
                {
                    return UpdateOutcome.Skipped;
                }
            }
        }

        var fetchResult = isSeason
            ? await GetCachedOrFetchSeasonAsync(item, seasonShowTmdbId, seasonShowImdbId, seasonShowTvdbId, seasonNumber!.Value, cfg, needsSeasonTrakt, needsSeasonTmdb, cancellationToken).ConfigureAwait(false)
            : isEpisode
                ? await GetCachedOrFetchEpisodeAsync(item, imdbId, seasonShowTmdbId, seasonShowImdbId, seasonShowTvdbId, seasonNumber!.Value, episodeNumber!.Value, cfg, effectiveEpisodePrimary, effectiveEpisodeFallback, needsEpisodeTmdb, needsEpisodeTrakt, needsEpisodeTvMaze, needsEpisodeOmdb, cancellationToken).ConfigureAwait(false)
                : await GetCachedOrFetchAsync(contentType, tmdbId, imdbId, tvdbId, cfg, needsMdbList, needsTvMaze, needsFilmweb, item.Name, item.ProductionYear, cancellationToken).ConfigureAwait(false);
        if (fetchResult.Outcome == UpdateOutcome.RateLimited)
        {
            return UpdateOutcome.RateLimited;
        }

        // If MDBList returned 404 and we successfully used IMDb fallback, apply it directly.
        if (fetchResult.ImdbFallbackCommunityRating.HasValue)
        {
            if (!allowUpdateCommunity)
            {
                // Respect UpdateOnlyWhenEmpty.
                return UpdateOutcome.Skipped;
            }

            var imdbFallbackCommunity = fetchResult.ImdbFallbackCommunityRating.Value;
            const string imdbFallbackSource = "imdb";

            var imdbFallbackChanged = false;

            var ratingChanged = !item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - imdbFallbackCommunity) > 0.01f;
            if (ratingChanged)
            {
                item.CommunityRating = imdbFallbackCommunity;
            }

            var sourceChanged = SetProviderId(item, ProviderIdCommunitySource, imdbFallbackSource);
            imdbFallbackChanged = ratingChanged || sourceChanged;

            if (!imdbFallbackChanged)
            {
                return UpdateOutcome.Skipped;
            }

            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated ratings from IMDb fallback: {Name} (IMDb {ImdbId})", item.Name, imdbId);
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Updated;
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
            return fetchResult.Outcome == UpdateOutcome.Failed ? UpdateOutcome.Failed : UpdateOutcome.Skipped;
        }

        if (isSeason)
        {
            var seasonEffective = GetEffectiveMapping(item, cfg);
            var seasonPrimary = NormalizeSource(seasonEffective.SeasonCommunitySource);
            var seasonFallback = NormalizeSource(seasonEffective.SeasonCommunityFallbackSource);
            var seasonResolved = ResolveScoreWithSource(data, seasonPrimary, seasonFallback);
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
                _logger.LogInformation("Updated season ratings from configured source: {Name} (IMDb {ImdbId}, TVDB {TvdbId}, Season {SeasonNumber}, Source {Source})", item.Name, seasonShowImdbId, seasonShowTvdbId, seasonNumber, seasonResolved.UsedSource);
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
            var episodeResolved = ResolveScoreWithSource(data, episodePrimary, episodeFallback);
            if (!episodeResolved.Score0To100.HasValue)
            {
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Skipped;
            }

            var score = Clamp(episodeResolved.Score0To100.Value, 0, 100);
            var episodeCommunity = (float)Math.Round(score / 10.0, 1, MidpointRounding.AwayFromZero);
            if (episodeCommunity <= 0)
            {
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Skipped;
            }

            if (!allowUpdateCommunity)
            {
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Skipped;
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
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Skipped;
            }

            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Updated episode ratings from configured source: {Name} (IMDb {ImdbId}, TVDB {TvdbId}, S{SeasonNumber}E{EpisodeNumber}, Source {Source})", item.Name, seasonShowImdbId, seasonShowTvdbId, seasonNumber, episodeNumber, episodeResolved.UsedSource);
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Updated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save episode item after episode rating update: {Name}", item.Name);
                return fetchResult.StopAfterThis ? UpdateOutcome.RateLimited : UpdateOutcome.Failed;
            }
        }

        // Resolve mapping, considering optional per-library overrides.
        var effective = GetEffectiveMapping(item, cfg);

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

        // Special-case Letterboxd for CommunityRating: keep the provider native 0-5 value
        // instead of converting the normalized 0-100 score back to Jellyfin's 0-10 scale.
        // This preserves the exact number the user selected in settings.
        if (string.Equals(resolved.UsedSource, "letterboxd", StringComparison.OrdinalIgnoreCase))
        {
            var letterboxdRating = data.Ratings.FirstOrDefault(r => string.Equals(r.Source, resolved.UsedSource, StringComparison.OrdinalIgnoreCase));
            var nativeValue = letterboxdRating?.Value;
            if (nativeValue.HasValue && !double.IsNaN(nativeValue.Value) && !double.IsInfinity(nativeValue.Value) && nativeValue.Value > 0)
            {
                var nativeCommunity = (float)Math.Round(nativeValue.Value, 1, MidpointRounding.AwayFromZero);
                return (nativeCommunity > 0 ? nativeCommunity : null, resolved.UsedSource);
            }
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

    private static bool ShowRequiresMdbList(string primary, string fallback)
    {
        return RequiresMdbListSource(primary) || RequiresMdbListSource(fallback);
    }

    private static bool ShowRequiresFilmweb(string primary, string fallback)
    {
        return RequiresFilmwebSource(primary) || RequiresFilmwebSource(fallback);
    }

    private static bool MovieRequiresMdbList(string communityPrimary, string communityFallback, string criticPrimary, string criticFallback)
    {
        return RequiresMdbListSource(communityPrimary)
            || RequiresMdbListSource(communityFallback)
            || RequiresMdbListSource(criticPrimary)
            || RequiresMdbListSource(criticFallback);
    }

    private static bool MovieRequiresFilmweb(string communityPrimary, string communityFallback, string criticPrimary, string criticFallback)
    {
        return RequiresFilmwebSource(communityPrimary)
            || RequiresFilmwebSource(communityFallback)
            || RequiresFilmwebSource(criticPrimary)
            || RequiresFilmwebSource(criticFallback);
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

    private static bool RequiresMdbListSource(string? source)
    {
        var s = NormalizeSource(source);
        return !string.IsNullOrWhiteSpace(s) && s != "none" && s != "tvmaze" && s != "filmweb";
    }

    private static bool RequiresFilmwebSource(string? source)
    {
        return string.Equals(NormalizeSource(source), "filmweb", StringComparison.OrdinalIgnoreCase);
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

    private static void EnsureIds(MdbListTitleResponse data, string? tmdbId, string? imdbId, string? filmwebId = null)
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

        if (string.IsNullOrWhiteSpace(data.Ids.Filmweb))
        {
            data.Ids.Filmweb = NormalizeDigits(filmwebId);
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

    private async Task<List<MdbListRating>> TryFetchFilmwebRatingsAsync(string contentType, string? imdbId, string? title, int? year, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var lookup = await _filmweb.LookupAsync(contentType, imdbId, title, year, ttl, cancellationToken).ConfigureAwait(false);
        if (lookup is null || lookup.AverageRating <= 0)
        {
            return new List<MdbListRating>();
        }

        var results = new List<MdbListRating>
        {
            new MdbListRating
            {
                Source = "filmweb",
                Value = Math.Round(lookup.AverageRating, 1, MidpointRounding.AwayFromZero),
                Score = Math.Round(lookup.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                Votes = lookup.Votes,
                Url = lookup.Url
            }
        };

        if (lookup.CriticRating.HasValue && lookup.CriticRating.Value > 0)
        {
            results.Add(new MdbListRating
            {
                Source = "filmweb_critic",
                Value = Math.Round(lookup.CriticRating.Value, 1, MidpointRounding.AwayFromZero),
                Score = Math.Round(lookup.CriticRating.Value * 10.0, 1, MidpointRounding.AwayFromZero),
                Votes = lookup.CriticVotes,
                Url = lookup.Url
            });
        }

        return results;
    }

    private async Task<bool> TryMergeFilmwebRatingsAsync(
        MdbListTitleResponse data,
        string contentType,
        string? tmdbId,
        string? imdbId,
        string? title,
        int? year,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        var filmwebRatings = await TryFetchFilmwebRatingsAsync(contentType, imdbId, title, year, ttl, cancellationToken).ConfigureAwait(false);
        if (filmwebRatings.Count == 0)
        {
            return false;
        }

        EnsureIds(data, tmdbId, imdbId, TryExtractFilmwebIdFromUrl(filmwebRatings[0].Url));
        foreach (var rating in filmwebRatings)
        {
            UpsertRating(data, rating);
        }

        return true;
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

    private static string? TryExtractFilmwebIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var normalized = url.Trim();
            var queryIdIndex = normalized.IndexOf("?id=", StringComparison.OrdinalIgnoreCase);
            if (queryIdIndex >= 0)
            {
                var fromQuery = normalized[(queryIdIndex + 4)..];
                var stopIdx = fromQuery.IndexOfAny(new[] { '&', '#', '/' });
                if (stopIdx >= 0)
                {
                    fromQuery = fromQuery[..stopIdx];
                }

                var qid = NormalizeDigits(fromQuery);
                if (!string.IsNullOrWhiteSpace(qid))
                {
                    return qid;
                }
            }

            var dash = normalized.LastIndexOf('-');
            if (dash < 0 || dash + 1 >= normalized.Length)
            {
                return null;
            }

            var candidate = normalized[(dash + 1)..];
            var stop = candidate.IndexOfAny(new[] { '?', '#', '/' });
            if (stop >= 0)
            {
                candidate = candidate[..stop];
            }

            return NormalizeDigits(candidate);
        }
        catch
        {
            return null;
        }
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

        public float? ImdbFallbackCommunityRating { get; init; }
    }

    internal async Task EnsureImdbTop250ReadyAsync(PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (!cfg.EnableImdbTop250Icon)
        {
            return;
        }

        await _imdbTop250.EnsureReadyAsync(GetTtl(cfg), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ImdbTop250Snapshot?> TryGetImdbTop250SnapshotAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _imdbTop250.TryGetSnapshotAsync(cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildEpisodeCacheKey(showTmdbId, showImdbId, showTvdbId, seasonNumber, episodeNumber);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped };
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = GetTtl(cfg);
        var normalizedEpisodePrimary = NormalizeSource(episodePrimarySource);
        var omdbPrimary = string.Equals(normalizedEpisodePrimary, "imdb", StringComparison.OrdinalIgnoreCase);
        var omdbCooldownActive = _omdbRateLimit.NotBeforeUtc.HasValue && _omdbRateLimit.NotBeforeUtc.Value > now;

        async Task<FetchResult> ReturnEpisodeCacheAsync(MdbListCacheStore.CacheEnvelope env)
        {
            var changed = false;
            EnsureIds(env.Data, showTmdbId, showImdbId);

            if (needsEpisodeOmdb && !HasRatingSource(env.Data, "imdb"))
            {
                if (omdbCooldownActive)
                {
                    if (omdbPrimary)
                    {
                        _logger.LogWarning("OMDb daily request limit cooldown is active until {NotBeforeUtc:o}. Episode processing will continue on the next run.", _omdbRateLimit.NotBeforeUtc!.Value);
                        return new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
                    }
                }
                else
                {
                    var omdbLookup = await TryFetchOmdbEpisodeRatingAsync(episodeImdbId, ttl, cfg.OmdbApiKey, cancellationToken).ConfigureAwait(false);
                    if (omdbLookup.IsRateLimited)
                    {
                        await _omdbRateLimit.UpdateAsync(null, 0, null, true, cancellationToken).ConfigureAwait(false);
                        if (omdbPrimary)
                        {
                            _logger.LogWarning("OMDb daily request limit reached while updating episode ratings. Episode processing will stop until the quota resets.");
                            return HasRatingSource(env.Data, "imdb")
                                ? new FetchResult { Data = env.Data, Outcome = UpdateOutcome.Skipped, StopAfterThis = true }
                                : new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
                        }
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
                            changed = true;
                        }
                    }
                }
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
            return await ReturnEpisodeCacheAsync(cached).ConfigureAwait(false);
        }

        if (omdbPrimary && omdbCooldownActive)
        {
            if (cached is not null && HasRatingSource(cached.Data, "imdb"))
            {
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped, StopAfterThis = true };
            }

            _logger.LogWarning("OMDb daily request limit cooldown is active until {NotBeforeUtc:o}. Episode processing will continue on the next run.", _omdbRateLimit.NotBeforeUtc!.Value);
            return new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
        }

        var data = cached?.Data ?? new MdbListTitleResponse
        {
            Type = "episode",
            Ids = new MdbListIds
            {
                Imdb = NormalizeImdbId(showImdbId),
                Tmdb = int.TryParse(NormalizeDigits(showTmdbId), out var parsedTmdb) ? parsedTmdb : null
            }
        };
        EnsureIds(data, showTmdbId, showImdbId);

        if (needsEpisodeOmdb && !HasRatingSource(data, "imdb") && !omdbCooldownActive)
        {
            var omdbLookup = await TryFetchOmdbEpisodeRatingAsync(episodeImdbId, ttl, cfg.OmdbApiKey, cancellationToken).ConfigureAwait(false);
            if (omdbLookup.IsRateLimited)
            {
                await _omdbRateLimit.UpdateAsync(null, 0, null, true, cancellationToken).ConfigureAwait(false);
                if (omdbPrimary)
                {
                    _logger.LogWarning("OMDb daily request limit reached while updating episode ratings. Episode processing will stop until the quota resets.");
                    if (cached is not null && HasRatingSource(cached.Data, "imdb"))
                    {
                        return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped, StopAfterThis = true };
                    }

                    return new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
                }
            }
            else
            {
                await _omdbRateLimit.UpdateAsync(null, 1, null, false, cancellationToken).ConfigureAwait(false);
                if (omdbLookup.Data is not null)
                {
                    UpsertRating(data, new MdbListRating
                    {
                        Source = "imdb",
                        Value = omdbLookup.Data.AverageRating,
                        Score = Math.Round(omdbLookup.Data.AverageRating * 10.0, 1, MidpointRounding.AwayFromZero),
                        Votes = omdbLookup.Data.Votes,
                        Url = omdbLookup.Data.Url
                    });
                }
            }
        }

        if (needsEpisodeTmdb && !HasRatingSource(data, "tmdb"))
        {
            var lookup = await _tmdbEpisode.LookupEpisodeAsync(showTmdbId, showImdbId, showTvdbId, seasonNumber, episodeNumber, cfg.TmdbApiAuth, cancellationToken).ConfigureAwait(false);
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

        if (needsEpisodeTrakt && !HasRatingSource(data, "trakt"))
        {
            var lookup = await TryFetchTraktEpisodeRatingAsync(showImdbId, showTvdbId, seasonNumber, episodeNumber, ttl, cfg.TraktClientId, cancellationToken).ConfigureAwait(false);
            if (lookup is not null)
            {
                UpsertRating(data, lookup);
            }
        }

        if (needsEpisodeTvMaze && !HasRatingSource(data, "tvmaze"))
        {
            var lookup = await TryFetchTvMazeEpisodeRatingAsync(showImdbId, showTvdbId, seasonNumber, episodeNumber, ttl, cancellationToken).ConfigureAwait(false);
            if (lookup is not null)
            {
                UpsertRating(data, lookup);
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

        var episodeSourceCount = (needsEpisodeTmdb ? 1 : 0) + (needsEpisodeTrakt ? 1 : 0) + (needsEpisodeTvMaze ? 1 : 0) + (needsEpisodeOmdb ? 1 : 0);
        var episodeSourceTag = episodeSourceCount > 1
            ? "episode-multi"
            : needsEpisodeOmdb
                ? "omdb-episode"
                : needsEpisodeTrakt
                    ? "trakt-episode"
                    : needsEpisodeTvMaze
                        ? "tvmaze-episode"
                        : "tmdb-episode";

        var env = new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = data,
            RawJson = $"{{\"source\":\"{episodeSourceTag}\"}}"
        };

        await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
        return new FetchResult { Data = data, Outcome = UpdateOutcome.Skipped };
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

        if (data.Ratings.Count == 0)
        {
            if (cached is not null)
            {
                return new FetchResult { Data = cached.Data, Outcome = UpdateOutcome.Skipped };
            }

            return new FetchResult { Data = null, Outcome = UpdateOutcome.Failed };
        }

        var env = new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = data,
            RawJson = needsSeasonTmdb && needsSeasonTrakt
                ? "{\"source\":\"season-multi\"}"
                : needsSeasonTmdb
                    ? "{\"source\":\"tmdb-season\"}"
                    : "{\"source\":\"trakt-season\"}"
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
        bool needsTvMaze,
        bool needsFilmweb,
        string? title,
        int? year,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(contentType, tmdbId, imdbId, tvdbId);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped };
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = GetTtl(cfg);

        async Task<FetchResult> ReturnFreshOrAugmentedCacheAsync(MdbListCacheStore.CacheEnvelope env)
        {
            var changed = false;
            if (needsTvMaze && !HasRatingSource(env.Data, "tvmaze"))
            {
                try
                {
                    var tvmazeRating = await TryFetchTvMazeRatingAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
                    if (tvmazeRating is not null)
                    {
                        EnsureIds(env.Data, tmdbId, imdbId);
                        UpsertRating(env.Data, tvmazeRating);
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TVMaze augmentation failed for {Key}", cacheKey);
                }
            }

            if (needsFilmweb && !HasRatingSource(env.Data, "filmweb"))
            {
                try
                {
                    if (await TryMergeFilmwebRatingsAsync(env.Data, contentType, tmdbId, imdbId, title, year, ttl, cancellationToken).ConfigureAwait(false))
                    {
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Filmweb augmentation failed for {Key}", cacheKey);
                }
            }

            if (changed)
            {
                env.CachedAtUtc = now;
                await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
            }

            var imdbFallback = TryExtractImdbFallbackCommunityRating(env);
            if (imdbFallback.HasValue)
            {
                return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped, ImdbFallbackCommunityRating = imdbFallback.Value };
            }

            return new FetchResult { Data = env.Data, Outcome = UpdateOutcome.Skipped };
        }

        var cached = await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var age = now - cached.CachedAtUtc;
            if (age <= ttl)
            {
                return await ReturnFreshOrAugmentedCacheAsync(cached).ConfigureAwait(false);
            }
        }

        if (_rateLimit.NotBeforeUtc.HasValue && _rateLimit.NotBeforeUtc.Value > now)
        {
            if (cached is not null)
            {
                return await ReturnFreshOrAugmentedCacheAsync(cached).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "MDBList rate limit cooldown is active until {NotBeforeUtc:o}, but no cache is available for {Key}. Revalidating with a live request.",
                _rateLimit.NotBeforeUtc.Value,
                cacheKey);
        }

        MdbListTitleResponse? data = null;
        string? rawJson = cached?.RawJson;
        bool stopAfterThis = false;

        if (needsMdbList)
        {
            if (cfg.RequestDelayMs > 0)
            {
                await Task.Delay(cfg.RequestDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var api = await _client.GetByTmdbAsync(contentType, tmdbId ?? string.Empty, cfg.MdbListApiKey, cancellationToken).ConfigureAwait(false);
            rawJson = api.RawJson;
            var quotaExhausted = api.RateLimitRemaining.HasValue && api.RateLimitRemaining.Value <= 0;

            await _rateLimit.UpdateAsync(api.RateLimitLimit, api.RateLimitRemaining, api.RateLimitResetUtc, api.IsRateLimited || quotaExhausted, cancellationToken)
                .ConfigureAwait(false);

            if (api.IsRateLimited)
            {
                if (cached is not null)
                {
                    _logger.LogWarning("MDBList rate limit reached. Using stale cache for {Key}.", cacheKey);
                    return await ReturnFreshOrAugmentedCacheAsync(cached).ConfigureAwait(false);
                }

                _logger.LogWarning(
                    "MDBList rate limit reached. Will continue after {ResetUtc:o}.",
                    api.RateLimitResetUtc ?? (_rateLimit.NotBeforeUtc ?? now.AddHours(24)));
                return new FetchResult { Data = null, Outcome = UpdateOutcome.RateLimited };
            }

            stopAfterThis = quotaExhausted;

            if (api.Data is null)
            {
                if (needsTvMaze)
                {
                    try
                    {
                        var tvmazeRating = await TryFetchTvMazeRatingAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
                        if (tvmazeRating is not null)
                        {
                            data = new MdbListTitleResponse
                            {
                                Type = contentType,
                                Ids = new MdbListIds { Imdb = NormalizeImdbId(imdbId), Tmdb = int.TryParse(tmdbId, out var tmdbParsed) ? tmdbParsed : null },
                                Ratings = new System.Collections.Generic.List<MdbListRating> { tvmazeRating }
                            };

                            var tvEnv = new MdbListCacheStore.CacheEnvelope
                            {
                                CachedAtUtc = now,
                                Data = data,
                                RawJson = "{\"source\":\"tvmaze-only\"}"
                            };
                            await _cacheStore.SaveAsync(cacheKey, tvEnv, cancellationToken).ConfigureAwait(false);
                            return new FetchResult { Data = data, Outcome = UpdateOutcome.Skipped, StopAfterThis = stopAfterThis };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TVMaze fallback failed for {Key}", cacheKey);
                    }
                }

                if (needsFilmweb)
                {
                    try
                    {
                        data ??= new MdbListTitleResponse
                        {
                            Type = contentType,
                            Ids = new MdbListIds
                            {
                                Imdb = NormalizeImdbId(imdbId),
                                Tmdb = int.TryParse(tmdbId, out var filmwebTmdbParsed) ? filmwebTmdbParsed : null
                            }
                        };

                        if (await TryMergeFilmwebRatingsAsync(data, contentType, tmdbId, imdbId, title, year, ttl, cancellationToken).ConfigureAwait(false))
                        {
                            var filmwebOnlyEnv = new MdbListCacheStore.CacheEnvelope
                            {
                                CachedAtUtc = now,
                                Data = data,
                                RawJson = "{\"source\":\"filmweb-only\"}"
                            };
                            await _cacheStore.SaveAsync(cacheKey, filmwebOnlyEnv, cancellationToken).ConfigureAwait(false);
                            return new FetchResult { Data = data, Outcome = UpdateOutcome.Skipped, StopAfterThis = stopAfterThis };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Filmweb fallback failed for {Key}", cacheKey);
                    }
                }

                if (api.StatusCode == 404 && !string.IsNullOrWhiteSpace(imdbId))
                {
                    try
                    {
                        var imdbFallback = await _imdbFallback.TryGetRatingInfoAsync(imdbId, ttl, cancellationToken).ConfigureAwait(false);
                        if (imdbFallback.HasValue && imdbFallback.Value.AverageRating > 0)
                        {
                            var synthetic = new MdbListTitleResponse
                            {
                                Type = contentType,
                                Ids = new MdbListIds { Imdb = imdbId, Tmdb = int.TryParse(tmdbId, out var t) ? t : null },
                                Ratings = new System.Collections.Generic.List<MdbListRating>
                                {
                                    new MdbListRating { Source = "imdb", Value = imdbFallback.Value.AverageRating, Votes = imdbFallback.Value.Votes, Url = "https://www.imdb.com/title/" + imdbId.Trim() }
                                }
                            };

                            var env404 = new MdbListCacheStore.CacheEnvelope
                            {
                                CachedAtUtc = now,
                                Data = synthetic,
                                RawJson = "{\"source\":\"imdb-fallback\",\"reason\":\"mdblist-404\"}"
                            };
                            await _cacheStore.SaveAsync(cacheKey, env404, cancellationToken).ConfigureAwait(false);

                            return new FetchResult { Data = null, Outcome = UpdateOutcome.Skipped, ImdbFallbackCommunityRating = imdbFallback.Value.AverageRating };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IMDb fallback failed for {ImdbId} (TMDb {TmdbId})", imdbId, tmdbId);
                    }
                }

                if (cached is not null)
                {
                    return await ReturnFreshOrAugmentedCacheAsync(cached).ConfigureAwait(false);
                }

                return new FetchResult { Data = null, Outcome = UpdateOutcome.Failed };
            }

            data = api.Data;
        }
        else
        {
            data = cached?.Data ?? new MdbListTitleResponse
            {
                Type = contentType,
                Ids = new MdbListIds { Imdb = NormalizeImdbId(imdbId), Tmdb = int.TryParse(tmdbId, out var onlyTvMazeTmdb) ? onlyTvMazeTmdb : null }
            };
        }

        EnsureIds(data, tmdbId, imdbId);

        if (needsTvMaze && !HasRatingSource(data, "tvmaze"))
        {
            try
            {
                var tvmazeRating = await TryFetchTvMazeRatingAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
                if (tvmazeRating is not null)
                {
                    UpsertRating(data, tvmazeRating);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TVMaze request failed for {Key}", cacheKey);
            }
        }

        if (needsFilmweb && !HasRatingSource(data, "filmweb"))
        {
            try
            {
                await TryMergeFilmwebRatingsAsync(data, contentType, tmdbId, imdbId, title, year, ttl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Filmweb request failed for {Key}", cacheKey);
            }
        }

        var env = new MdbListCacheStore.CacheEnvelope
        {
            CachedAtUtc = now,
            Data = data,
            RawJson = needsMdbList
                ? rawJson
                : needsTvMaze && needsFilmweb
                    ? "{\"source\":\"tvmaze-filmweb-only\"}"
                    : needsFilmweb
                        ? "{\"source\":\"filmweb-only\"}"
                        : "{\"source\":\"tvmaze-only\"}"
        };

        await _cacheStore.SaveAsync(cacheKey, env, cancellationToken).ConfigureAwait(false);
        return new FetchResult { Data = data, Outcome = UpdateOutcome.Skipped, StopAfterThis = stopAfterThis };
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
