using System;
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

internal sealed class TraktSeasonApiClient
{
    internal sealed class TraktSeasonLookupResult
    {
        public double AverageRating { get; init; }
        public int? Votes { get; init; }
        public string? Url { get; init; }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(350);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TraktSeasonApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TraktSeasonLookupResult?> LookupSeasonAsync(
        string? showImdbId,
        string? showTvdbId,
        int seasonNumber,
        string? clientId,
        CancellationToken cancellationToken)
    {
        var apiKey = (clientId ?? string.Empty).Trim();
        var imdbId = NormalizeImdbId(showImdbId);
        var tvdbId = NormalizeDigits(showTvdbId);
        if (seasonNumber < 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var show = await LookupShowAsync(imdbId, tvdbId, apiKey, cancellationToken).ConfigureAwait(false);
        if (show?.Ids?.Trakt is null)
        {
            return null;
        }

        var ratings = await GetSeasonRatingsAsync(show.Ids.Trakt.Value, seasonNumber, apiKey, cancellationToken).ConfigureAwait(false);
        if (ratings?.Rating is null || ratings.Rating.Value <= 0)
        {
            return null;
        }

        var webShowId = !string.IsNullOrWhiteSpace(show.Ids.Slug)
            ? show.Ids.Slug!.Trim()
            : (!string.IsNullOrWhiteSpace(imdbId) ? imdbId : show.Ids.Trakt.Value.ToString());

        return new TraktSeasonLookupResult
        {
            AverageRating = Math.Round(ratings.Rating.Value, 1, MidpointRounding.AwayFromZero),
            Votes = ratings.Votes,
            Url = $"https://trakt.tv/shows/{Uri.EscapeDataString(webShowId)}/seasons/{seasonNumber}"
        };
    }

    private async Task<TraktSearchResult?> LookupShowAsync(string? imdbId, string? tvdbId, string clientId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var byImdb = await SearchShowByExternalIdAsync("imdb", imdbId, clientId, cancellationToken).ConfigureAwait(false);
            if (byImdb is not null)
            {
                return byImdb;
            }
        }

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            var byTvdb = await SearchShowByExternalIdAsync("tvdb", tvdbId, clientId, cancellationToken).ConfigureAwait(false);
            if (byTvdb is not null)
            {
                return byTvdb;
            }
        }

        return null;
    }

    private async Task<TraktSearchResult?> SearchShowByExternalIdAsync(string idType, string idValue, string clientId, CancellationToken cancellationToken)
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
                return item;
            }
        }

        return null;
    }

    private async Task<TraktSeasonRatingsResponse?> GetSeasonRatingsAsync(int traktShowId, int seasonNumber, string clientId, CancellationToken cancellationToken)
    {
        var url = $"https://api.trakt.tv/shows/{traktShowId}/seasons/{seasonNumber}/ratings";
        return await GetJsonWithRetryAsync<TraktSeasonRatingsResponse>(url, clientId, cancellationToken).ConfigureAwait(false);
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
                http.Timeout = TimeSpan.FromSeconds(20);

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
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("show")]
        public TraktShow? Show { get; set; }

        [JsonIgnore]
        public TraktShowIds? Ids => Show?.Ids;
    }

    private sealed class TraktShow
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

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

    private sealed class TraktSeasonRatingsResponse
    {
        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("votes")]
        public int? Votes { get; set; }
    }
}
