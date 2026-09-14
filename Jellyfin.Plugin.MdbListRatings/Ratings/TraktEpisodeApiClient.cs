using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class TraktEpisodeApiClient
{
    internal sealed class TraktEpisodeLookupResult
    {
        public double AverageRating { get; init; }
        public int? Votes { get; init; }
        public string? Url { get; init; }
    }

    private sealed class CacheBox<T>
    {
        public DateTimeOffset CachedAtUtc { get; init; }
        public T Value { get; init; } = default!;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, CacheBox<TraktShowLookupResult?>> _showLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, CacheBox<IReadOnlyList<TraktSeasonWithEpisodesResponse>?>> _showSeasonsCache = new();

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DefaultLookupCacheTtl = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public TraktEpisodeApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TraktEpisodeLookupResult?> LookupEpisodeAsync(
        string? showImdbId,
        string? showTvdbId,
        int seasonNumber,
        int episodeNumber,
        TimeSpan ttl,
        string? clientId,
        CancellationToken cancellationToken)
    {
        var apiKey = (clientId ?? string.Empty).Trim();
        var imdbId = NormalizeImdbId(showImdbId);
        var tvdbId = NormalizeDigits(showTvdbId);
        if (seasonNumber < 0 || episodeNumber <= 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var show = await LookupShowAsync(imdbId, tvdbId, apiKey, cancellationToken).ConfigureAwait(false);
        if (show?.Ids?.Trakt is not int traktShowId || traktShowId <= 0)
        {
            return null;
        }

        var seasons = await GetShowSeasonsAsync(traktShowId, ttl, apiKey, cancellationToken).ConfigureAwait(false);
        if (seasons is null || seasons.Count == 0)
        {
            return null;
        }

        foreach (var season in seasons)
        {
            if (season is null || season.Number != seasonNumber || season.Episodes is null || season.Episodes.Count == 0)
            {
                continue;
            }

            foreach (var episode in season.Episodes)
            {
                if (episode is null || episode.Number != episodeNumber)
                {
                    continue;
                }

                var rating = episode.Rating;
                if (!rating.HasValue || double.IsNaN(rating.Value) || double.IsInfinity(rating.Value) || rating.Value <= 0)
                {
                    return null;
                }

                var webShowId = !string.IsNullOrWhiteSpace(show.Ids.Slug)
                    ? show.Ids.Slug!.Trim()
                    : (!string.IsNullOrWhiteSpace(imdbId) ? imdbId : traktShowId.ToString());

                return new TraktEpisodeLookupResult
                {
                    AverageRating = Math.Round(rating.Value, 1, MidpointRounding.AwayFromZero),
                    Votes = episode.Votes,
                    Url = $"https://trakt.tv/shows/{Uri.EscapeDataString(webShowId)}/seasons/{seasonNumber}/episodes/{episodeNumber}"
                };
            }
        }

        return null;
    }

    private async Task<TraktShowLookupResult?> LookupShowAsync(string? imdbId, string? tvdbId, string clientId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var cacheKey = $"imdb:{imdbId}";
            var cached = TryGetFresh(_showLookupCache, cacheKey, DefaultLookupCacheTtl);
            if (cached is not null)
            {
                return cached;
            }

            var fetched = await SearchShowByExternalIdAsync("imdb", imdbId, clientId, cancellationToken).ConfigureAwait(false);
            _showLookupCache[cacheKey] = new CacheBox<TraktShowLookupResult?>
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Value = fetched
            };
            if (fetched is not null)
            {
                if (fetched.Ids?.Tvdb is int fetchedTvdb)
                {
                    _showLookupCache.TryAdd($"tvdb:{fetchedTvdb}", new CacheBox<TraktShowLookupResult?>
                    {
                        CachedAtUtc = DateTimeOffset.UtcNow,
                        Value = fetched
                    });
                }

                return fetched;
            }
        }

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            var cacheKey = $"tvdb:{tvdbId}";
            var cached = TryGetFresh(_showLookupCache, cacheKey, DefaultLookupCacheTtl);
            if (cached is not null)
            {
                return cached;
            }

            var fetched = await SearchShowByExternalIdAsync("tvdb", tvdbId, clientId, cancellationToken).ConfigureAwait(false);
            _showLookupCache[cacheKey] = new CacheBox<TraktShowLookupResult?>
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Value = fetched
            };
            if (fetched is not null)
            {
                if (!string.IsNullOrWhiteSpace(fetched.Ids?.Imdb))
                {
                    _showLookupCache.TryAdd($"imdb:{fetched.Ids.Imdb}", new CacheBox<TraktShowLookupResult?>
                    {
                        CachedAtUtc = DateTimeOffset.UtcNow,
                        Value = fetched
                    });
                }

                return fetched;
            }
        }

        return null;
    }

    private async Task<TraktShowLookupResult?> SearchShowByExternalIdAsync(string idType, string idValue, string clientId, CancellationToken cancellationToken)
    {
        var url = $"https://api.trakt.tv/search/{Uri.EscapeDataString(idType)}/{Uri.EscapeDataString(idValue)}?type=show";
        var results = await GetJsonWithRetryAsync<List<TraktSearchResult>>(url, clientId, cancellationToken).ConfigureAwait(false);
        if (results is null || results.Count == 0)
        {
            return null;
        }

        foreach (var item in results)
        {
            if (item?.Show?.Ids?.Trakt is not null)
            {
                return new TraktShowLookupResult
                {
                    Ids = item.Show.Ids
                };
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<TraktSeasonWithEpisodesResponse>?> GetShowSeasonsAsync(int traktShowId, TimeSpan ttl, string clientId, CancellationToken cancellationToken)
    {
        var effectiveTtl = ttl <= TimeSpan.Zero ? DefaultLookupCacheTtl : ttl;
        var cached = TryGetFresh(_showSeasonsCache, traktShowId, effectiveTtl);
        if (cached is not null)
        {
            return cached;
        }

        var url = $"https://api.trakt.tv/shows/{traktShowId}/seasons?extended=episodes,full";
        var fetched = await GetJsonWithRetryAsync<List<TraktSeasonWithEpisodesResponse>>(url, clientId, cancellationToken).ConfigureAwait(false);
        _showSeasonsCache[traktShowId] = new CacheBox<IReadOnlyList<TraktSeasonWithEpisodesResponse>?>
        {
            CachedAtUtc = DateTimeOffset.UtcNow,
            Value = fetched
        };

        return fetched;
    }

    private async Task<T?> GetJsonWithRetryAsync<T>(string url, string clientId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitTurnAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var http = _httpClientFactory.CreateClient(SecretHttpClient.Name);
                http.Timeout = TimeSpan.FromSeconds(25);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
                request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
                request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+trakt-api)");

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                MarkRequestCompleted();

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    var delay = GetRetryDelay(response, attempt);
                    _logger.LogWarning("Trakt API rate limited for {Url}. Backing off for {Delay}.", url, delay);
                    DelayUntil(delay);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return default;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt API request failed: {Status} {Reason} for {Url}", (int)response.StatusCode, response.ReasonPhrase, url);
                    return default;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt API request error for {Url}", url);
                return default;
            }
        }

        return default;
    }

    private static T? TryGetFresh<TKey, T>(ConcurrentDictionary<TKey, CacheBox<T>> cache, TKey key, TimeSpan ttl)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var box) && (DateTimeOffset.UtcNow - box.CachedAtUtc) <= ttl)
        {
            return box.Value;
        }

        return default;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var raw in values)
            {
                if (int.TryParse(raw, out var seconds) && seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
        }

        return TimeSpan.FromSeconds(3 + (attempt * 2));
    }

    private static async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_nextAllowedUtc > now)
            {
                var delay = _nextAllowedUtc - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            _nextAllowedUtc = DateTimeOffset.UtcNow + MinSpacing;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void MarkRequestCompleted()
    {
        var candidate = DateTimeOffset.UtcNow + MinSpacing;
        if (candidate > _nextAllowedUtc)
        {
            _nextAllowedUtc = candidate;
        }
    }

    private static void DelayUntil(TimeSpan delay)
    {
        var candidate = DateTimeOffset.UtcNow + delay;
        if (candidate > _nextAllowedUtc)
        {
            _nextAllowedUtc = candidate;
        }
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

    private static string? NormalizeDigits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var chars = s.Trim();
        foreach (var ch in chars)
        {
            if (ch < '0' || ch > '9')
            {
                return null;
            }
        }

        return chars;
    }

    private sealed class TraktSearchResult
    {
        [JsonPropertyName("show")]
        public TraktShow? Show { get; set; }
    }

    private sealed class TraktShowLookupResult
    {
        public TraktShowIds? Ids { get; init; }
    }

    private sealed class TraktShow
    {
        [JsonPropertyName("ids")]
        public TraktShowIds? Ids { get; set; }
    }

    private sealed class TraktShowIds
    {
        [JsonPropertyName("trakt")]
        public int? Trakt { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }

        [JsonPropertyName("tvdb")]
        public int? Tvdb { get; set; }
    }

    private sealed class TraktSeasonWithEpisodesResponse
    {
        [JsonPropertyName("number")]
        public int? Number { get; set; }

        [JsonPropertyName("episodes")]
        public List<TraktEpisodeResponse>? Episodes { get; set; }
    }

    private sealed class TraktEpisodeResponse
    {
        [JsonPropertyName("number")]
        public int? Number { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("votes")]
        public int? Votes { get; set; }
    }
}
