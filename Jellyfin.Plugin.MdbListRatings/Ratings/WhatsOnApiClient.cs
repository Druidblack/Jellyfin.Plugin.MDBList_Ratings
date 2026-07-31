using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class WhatsOnApiClient
{
    private const string BaseUrl = "https://whatson-api.onrender.com";
    private static readonly TimeSpan ResponseCacheTtl = TimeSpan.FromMinutes(60);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, (WhatsOnSeasonResponse? Response, DateTimeOffset CachedAt)> _seasonResponseCache = new();
    private readonly ConcurrentDictionary<(int TmdbId, string ItemType), (WhatsOnTitleResponse? Response, DateTimeOffset CachedAt)> _titleResponseCache = new();

    public WhatsOnApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // whatson-api.onrender.com runs on Render's free tier
    // The episode-details endpoint also returns every episode of a show in a single response,
    // so a generous timeout is needed.
    private HttpClient CreateHttpClient()
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        return http;
    }

    private static bool TryApplyRateLimit(HttpResponseMessage response, WhatsOnApiResult result)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        result.IsRateLimited = true;
        result.RetryAfterSeconds = GetRetryAfterSeconds(response);
        return true;
    }

    private static int GetRetryAfterSeconds(HttpResponseMessage response)
    {
        return response.Headers.RetryAfter?.Delta.HasValue == true
            ? (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds
            : 3600;
    }

    private static string GetUrlWithAuth(string path, string apiKey)
    {
        var url = $"{BaseUrl}{path}";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var delimiter = url.Contains("?") ? "&" : "?";
            url += $"{delimiter}api_key={Uri.EscapeDataString(apiKey)}";
        }
        return url;
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+whatson)");
        return request;
    }

    public async Task<WhatsOnApiResult> GetTitleRatingsAsync(int? tmdbId, string? imdbId, string apiKey, string itemType, CancellationToken cancellationToken)
    {
        var result = new WhatsOnApiResult();

        var now = DateTimeOffset.UtcNow;
        if (tmdbId.HasValue && _titleResponseCache.TryGetValue((tmdbId.Value, itemType), out var cachedShow) && now - cachedShow.CachedAt <= ResponseCacheTtl)
        {
            if (cachedShow.Response is null)
            {
                // Cached as not found: don't keep retrying the API.
                return result;
            }

            _logger.LogInformation("Using cached WhatsOn title response for tmdbId {TmdbId}", tmdbId.Value);
            result.Data = MapToMdbListTitleResponse(cachedShow.Response);
            return result;
        }

        string url;
        if (tmdbId.HasValue)
        {
            _logger.LogInformation("Fetching WhatsOn title response for tmdbId {TmdbId}", tmdbId.Value);
            url = GetUrlWithAuth($"/?tmdbId={tmdbId.Value}", apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(imdbId))
        {
            _logger.LogInformation("Fetching WhatsOn title response for imdbId {ImdbId}", imdbId);
            url = GetUrlWithAuth($"/?imdbId={Uri.EscapeDataString(imdbId)}", apiKey);
        }
        else
        {
            result.StatusCode = 400;
            return result;
        }

        try
        {
            url += "&ratings_filters=all&append_to_response=episodes_details";
            using var http = CreateHttpClient();
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            result.StatusCode = (int)response.StatusCode;

            if (TryApplyRateLimit(response, result))
            {
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Cache the failure so we don't retry the same tmdbId on every episode/season.
                if (tmdbId.HasValue)
                {
                    _titleResponseCache[(tmdbId.Value, itemType)] = (null, now);
                }

                return result;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var searchResponse = JsonSerializer.Deserialize<WhatsOnSearchResponse>(content, GetJsonOptions());
            // TMDB id namespaces overlap between movies and TV shows; pick the result matching the requested item type.
            var raw = searchResponse?.Results?.FirstOrDefault(r => string.Equals(r.ItemType, itemType, StringComparison.OrdinalIgnoreCase));
            if (raw is not null)
            {
                result.Data = MapToMdbListTitleResponse(raw);

                if (tmdbId.HasValue)
                {
                    _titleResponseCache[(tmdbId.Value, itemType)] = (raw, now);
                }
            }
            else if (tmdbId.HasValue)
            {
                // Empty results (or only found as a different item type): cache as not found.
                _titleResponseCache[(tmdbId.Value, itemType)] = (null, now);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching from WhatsOn API");
        }

        return result;
    }

    public async Task<WhatsOnApiResult> GetEpisodeRatingAsync(int tmdbId, int seasonNumber, int episodeNumber, string apiKey, CancellationToken cancellationToken)
    {
        var result = new WhatsOnApiResult();

        var now = DateTimeOffset.UtcNow;
        var cacheKey = (tmdbId, "tvshow");
        if (_titleResponseCache.TryGetValue(cacheKey, out var cachedShow) && now - cachedShow.CachedAt <= ResponseCacheTtl)
        {
            _logger.LogInformation("Using cached WhatsOn episode response for tmdbId {TmdbId}", tmdbId);
        }
        else
        {
            _logger.LogInformation("Fetching WhatsOn episode response for tmdbId {TmdbId}", tmdbId);
            var show = await FetchShowWithEpisodesAsync(tmdbId, apiKey, cancellationToken).ConfigureAwait(false);
            if (show.IsRateLimited)
            {
                result.IsRateLimited = true;
                result.RetryAfterSeconds = show.RetryAfterSeconds;
                return result;
            }

            // Cache the response even when the show is not found, so we don't retry the batched endpoint for every episode.
            cachedShow = (show.Response, now);
            _titleResponseCache[cacheKey] = cachedShow;

            if (show.Response is null)
            {
                return result;
            }

            if (show.Response.EpisodesDetails is not { Length: > 0 })
            {
                _logger.LogInformation("WhatsOn response for tmdbId {TmdbId} contains no episode details. Episode ratings for this show will be skipped.", tmdbId);
            }
        }

        if (cachedShow.Response?.EpisodesDetails?.Length > 0)
        {
            var episode = cachedShow.Response.EpisodesDetails.FirstOrDefault(e => e.Season == seasonNumber && e.Episode == episodeNumber);
            if (episode?.UsersRating.HasValue == true)
            {
                result.Data.Ratings = new List<MdbListRating>
                {
                    new MdbListRating { Source = "imdb", Value = episode.UsersRating.Value, Score = episode.UsersRating.Value * 10f }
                };
            }
        }

        return result;
    }

    public async Task<WhatsOnApiResult> GetSeasonRatingAsync(int tmdbId, int seasonNumber, string apiKey, CancellationToken cancellationToken)
    {
        var result = new WhatsOnApiResult();

        var now = DateTimeOffset.UtcNow;
        if (_seasonResponseCache.TryGetValue(tmdbId, out var cachedSeasons) && now - cachedSeasons.CachedAt <= ResponseCacheTtl)
        {
            _logger.LogInformation("Using cached WhatsOn season response for tmdbId {TmdbId}", tmdbId);
        }
        else
        {
            _logger.LogInformation("Fetching WhatsOn season response for tmdbId {TmdbId}", tmdbId);
            var seasons = await FetchSeasonsAsync(tmdbId, apiKey, cancellationToken).ConfigureAwait(false);
            if (seasons.IsRateLimited)
            {
                result.IsRateLimited = true;
                result.RetryAfterSeconds = seasons.RetryAfterSeconds;
                return result;
            }

            // Cache the response even when the show is not found, so we don't retry the batched endpoint for every season.
            cachedSeasons = (seasons.Response, now);
            _seasonResponseCache[tmdbId] = cachedSeasons;

            if (seasons.Response is null)
            {
                return result;
            }
        }

        if (cachedSeasons.Response != null)
        {
            ApplySeasonRating(result, cachedSeasons.Response, seasonNumber);
        }

        return result;
    }

    private async Task<(WhatsOnSeasonResponse? Response, bool IsRateLimited, int RetryAfterSeconds)> FetchSeasonsAsync(int tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        var url = GetUrlWithAuth($"/tvshow/{tmdbId}/seasons", apiKey);

        try
        {
            using var http = CreateHttpClient();
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (null, true, GetRetryAfterSeconds(response));
            }

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var seasonsResp = JsonSerializer.Deserialize<WhatsOnSeasonResponse>(content, GetJsonOptions());
                return (seasonsResp, false, 0);
            }

            return (null, false, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching season ratings from WhatsOn API");
            return (null, false, 0);
        }
    }

    private async Task<(WhatsOnTitleResponse? Response, bool IsRateLimited, int RetryAfterSeconds)> FetchShowWithEpisodesAsync(int tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        var url = GetUrlWithAuth($"/?tmdbId={tmdbId}", apiKey);
        url += "&ratings_filters=all&append_to_response=episodes_details";

        try
        {
            using var http = CreateHttpClient();
            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (null, true, GetRetryAfterSeconds(response));
            }

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var searchResponse = JsonSerializer.Deserialize<WhatsOnSearchResponse>(content, GetJsonOptions());
                // TMDB id namespaces overlap between movies and TV shows; pick the tvshow result.
                return (searchResponse?.Results?.FirstOrDefault(r => string.Equals(r.ItemType, "tvshow", StringComparison.OrdinalIgnoreCase)), false, 0);
            }

            return (null, false, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching show episode details from WhatsOn API");
            return (null, false, 0);
        }
    }

    private static void ApplySeasonRating(WhatsOnApiResult result, WhatsOnSeasonResponse seasonsResp, int seasonNumber)
    {
        if (seasonsResp.Seasons == null)
        {
            return;
        }

        foreach (var s in seasonsResp.Seasons)
        {
            if (s.SeasonNumber == seasonNumber && s.AverageUsersRating.HasValue)
            {
                result.Data.Ratings = new List<MdbListRating>
                {
                    new MdbListRating { Source = "imdb", Value = s.AverageUsersRating.Value, Score = s.AverageUsersRating.Value * 10f }
                };
                break;
            }
        }
    }

    private static MdbListTitleResponse MapToMdbListTitleResponse(WhatsOnTitleResponse t)
    {
        var res = new MdbListTitleResponse();
        res.Ratings = new List<MdbListRating>();

        // Value must stay in the provider's native scale (matching MdbListRating.Value's contract),
        // Score is always the 0-100 normalized value. normalizeMultiplier converts native -> 0-100.
        void AddRating(string source, float? val, float normalizeMultiplier)
        {
            if (val.HasValue)
            {
                res.Ratings.Add(new MdbListRating
                {
                    Source = source,
                    Value = val.Value,
                    Score = val.Value * normalizeMultiplier
                });
            }
        }

        AddRating("imdb", t.Imdb?.UsersRating, 10f); // 0-10 scale
        AddRating("tmdb", t.Tmdb?.UsersRating, 10f); // 0-10 scale
        AddRating("trakt", t.Trakt?.UsersRating, 1f); // already 0-100
        AddRating("tomatoes", t.RottenTomatoes?.CriticsRating, 1f); // already 0-100
        AddRating("popcorn", t.RottenTomatoes?.UsersRating, 1f); // already 0-100
        AddRating("metacritic", t.Metacritic?.CriticsRating, 1f); // already 0-100
        AddRating("metacriticuser", t.Metacritic?.UsersRating, 10f); // 0-10 scale
        AddRating("letterboxd", t.Letterboxd?.UsersRating, 20f); // 0-5 scale
        AddRating("senscritique", t.SensCritique?.UsersRating, 10f); // 0-10 scale
        AddRating("allocine_critics", t.Allocine?.CriticsRating, 20f); // 0-5 scale
        AddRating("allocine_users", t.Allocine?.UsersRating, 20f); // 0-5 scale
        AddRating("betaseries", t.BetaSeries?.UsersRating, 20f); // 0-5 scale
        AddRating("tvtime", t.TvTime?.UsersRating, 20f); // 0-5 scale

        // The WhatsOn API does not expose a pre-computed aggregate rating directly
        // But on https://whatson-app.com there is an average over all rating sources
        // To match this we compute our own normalized average
        var scores = res.Ratings.Where(r => r.Score.HasValue && r.Score.Value > 0).Select(r => r.Score!.Value).ToList();
        if (scores.Count > 0)
        {
            var avgScore = scores.Average();
            res.Ratings.Add(new MdbListRating
            {
                Source = "whatson",
                Value = Math.Round(avgScore / 20.0, 2, MidpointRounding.AwayFromZero),
                Score = avgScore
            });
        }

        return res;
    }

    private static JsonSerializerOptions GetJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
