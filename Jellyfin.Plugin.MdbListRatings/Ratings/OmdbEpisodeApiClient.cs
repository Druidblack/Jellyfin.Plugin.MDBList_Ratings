using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class OmdbEpisodeApiClient
{
    internal sealed class OmdbEpisodeLookupResult
    {
        public double AverageRating { get; init; }
        public int? Votes { get; init; }
        public string? Url { get; init; }
    }

    internal sealed class OmdbEpisodeLookupResponse
    {
        public OmdbEpisodeLookupResult? Data { get; init; }
        public bool IsRateLimited { get; init; }
        // True only when OMDb gave a definitive per-title answer that should be negative-cached
        // (not found / unsupported / no IMDb rating). Transport and credential errors are not cached.
        public bool IsDefinitiveMiss { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private sealed class CacheBox<T>
    {
        public DateTimeOffset CachedAtUtc { get; init; }
        public T Value { get; init; } = default!;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheBox<OmdbEpisodeLookupResult?>> _episodeLookupCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly TimeSpan DefaultLookupCacheTtl = TimeSpan.FromHours(1);

    public OmdbEpisodeApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<OmdbEpisodeLookupResponse> LookupEpisodeByImdbAsync(
        string? episodeImdbId,
        TimeSpan ttl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var imdbId = NormalizeImdbId(episodeImdbId);
        var key = (apiKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(key))
        {
            return new OmdbEpisodeLookupResponse();
        }

        var effectiveTtl = ttl <= TimeSpan.Zero ? DefaultLookupCacheTtl : ttl;
        var cached = TryGetFresh(_episodeLookupCache, imdbId, effectiveTtl);
        if (cached is not null)
        {
            return new OmdbEpisodeLookupResponse { Data = cached };
        }

        var url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(imdbId)}&type=episode&apikey={Uri.EscapeDataString(key)}";

        try
        {
            var http = _httpClientFactory.CreateClient(SecretHttpClient.Name);
            http.Timeout = TimeSpan.FromSeconds(25);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+omdb)");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<OmdbEpisodeResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return new OmdbEpisodeLookupResponse();
            }

            if (IsRateLimitResponse(response.StatusCode, payload))
            {
                return new OmdbEpisodeLookupResponse
                {
                    IsRateLimited = true,
                    ErrorMessage = payload.Error
                };
            }

            if (!string.Equals(payload.Response, "True", StringComparison.OrdinalIgnoreCase))
            {
                return new OmdbEpisodeLookupResponse
                {
                    IsDefinitiveMiss = IsDefinitiveMissError(payload.Error),
                    ErrorMessage = payload.Error
                };
            }

            var rating = TryParseDouble(payload.ImdbRating);
            if (!rating.HasValue || rating.Value <= 0)
            {
                return new OmdbEpisodeLookupResponse { IsDefinitiveMiss = true };
            }

            var result = new OmdbEpisodeLookupResult
            {
                AverageRating = Math.Round(rating.Value, 1, MidpointRounding.AwayFromZero),
                Votes = TryParseVotes(payload.ImdbVotes),
                Url = !string.IsNullOrWhiteSpace(payload.ImdbID) ? $"https://www.imdb.com/title/{payload.ImdbID.Trim()}" : $"https://www.imdb.com/title/{imdbId}"
            };

            _episodeLookupCache[imdbId] = new CacheBox<OmdbEpisodeLookupResult?>
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Value = result
            };

            return new OmdbEpisodeLookupResponse { Data = result };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("OMDb episode request error for {ImdbId}. ErrorType={ErrorType}", imdbId, ex.GetType().Name);
            return new OmdbEpisodeLookupResponse();
        }
    }

    private static OmdbEpisodeLookupResult? TryGetFresh(ConcurrentDictionary<string, CacheBox<OmdbEpisodeLookupResult?>> cache, string key, TimeSpan ttl)
    {
        if (cache.TryGetValue(key, out var box) && (DateTimeOffset.UtcNow - box.CachedAtUtc) <= ttl)
        {
            return box.Value;
        }

        return default;
    }

    private static bool IsRateLimitResponse(HttpStatusCode statusCode, OmdbEpisodeResponse payload)
    {
        if ((int)statusCode == 401 || (int)statusCode == 403 || (int)statusCode == 429)
        {
            return ContainsLimitReached(payload.Error);
        }

        return ContainsLimitReached(payload.Error);
    }

    private static bool IsDefinitiveMissError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("incorrect imdb id", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("no result", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsLimitReached(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.IndexOf("limit reached", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int? TryParseVotes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || string.Equals(s.Trim(), "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var digits = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
            }
        }

        return digits.Length > 0 && int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? TryParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || string.Equals(s.Trim(), "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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

        var normalized = NormalizeDigits(trimmed);
        return string.IsNullOrWhiteSpace(normalized) ? null : "tt" + normalized;
    }

    private static string? NormalizeDigits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var buffer = new System.Text.StringBuilder();
        foreach (var ch in s.Trim())
        {
            if (char.IsDigit(ch))
            {
                buffer.Append(ch);
            }
        }

        return buffer.Length == 0 ? null : buffer.ToString();
    }

    private sealed class OmdbEpisodeResponse
    {
        [JsonPropertyName("Response")]
        public string? Response { get; set; }

        [JsonPropertyName("Error")]
        public string? Error { get; set; }

        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        [JsonPropertyName("imdbVotes")]
        public string? ImdbVotes { get; set; }

        [JsonPropertyName("imdbID")]
        public string? ImdbID { get; set; }
    }
}
