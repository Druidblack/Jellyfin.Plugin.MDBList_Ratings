using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class FilmwebClient
{
    public sealed class FilmwebLookupResult
    {
        public double AverageRating { get; init; }
        public int? Votes { get; init; }
        public double? CriticRating { get; init; }
        public int? CriticVotes { get; init; }
        public string? Url { get; init; }
        public string? FilmwebId { get; init; }
    }

    public sealed class CacheBox
    {
        public DateTimeOffset CachedAtUtc { get; init; }
        public FilmwebLookupResult? Value { get; init; }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheBox> _lookupCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(8);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public FilmwebClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FilmwebLookupResult?> LookupAsync(string contentType, string? imdbId, string? title, int? year, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var normalizedContentType = NormalizeContentType(contentType);
        if (normalizedContentType is null)
        {
            return null;
        }

        imdbId = NormalizeImdbId(imdbId);
        title = NormalizeTitle(title);
        var cacheKey = $"{normalizedContentType}:{imdbId ?? "-"}:{title ?? "-"}:{(year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : "-")}";
        var effectiveTtl = ttl > TimeSpan.Zero ? ttl : DefaultCacheTtl;
        if (_lookupCache.TryGetValue(cacheKey, out var cached) && (DateTimeOffset.UtcNow - cached.CachedAtUtc) <= effectiveTtl)
        {
            return cached.Value;
        }

        var result = await LookupCoreAsync(normalizedContentType, imdbId, title, year, cancellationToken).ConfigureAwait(false);
        _lookupCache[cacheKey] = new CacheBox
        {
            CachedAtUtc = DateTimeOffset.UtcNow,
            Value = result
        };
        return result;
    }

    private async Task<FilmwebLookupResult?> LookupCoreAsync(string contentType, string? imdbId, string? title, int? year, CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(imdbId, title, year);
        foreach (var query in queries)
        {
            var hit = await SearchBestHitAsync(contentType, query, title, cancellationToken).ConfigureAwait(false);
            if (hit is null)
            {
                continue;
            }

            var rating = await GetRatingAsync(hit.Id, cancellationToken).ConfigureAwait(false);
            if (rating is not null)
            {
                var criticRating = await GetCriticRatingAsync(hit.Id, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Filmweb API found item '{Title}' ({Id}) with rating {Rate} ({Count} votes) and critic rating {CriticRate} ({CriticCount} votes)", hit.MatchedTitle, hit.Id, rating.Rate, rating.Count, criticRating?.Rate, criticRating?.Count);
                return new FilmwebLookupResult
                {
                    AverageRating = rating.Rate,
                    Votes = rating.Count > 0 ? rating.Count : null,
                    CriticRating = criticRating?.Rate > 0 ? criticRating.Rate : null,
                    CriticVotes = criticRating?.Count > 0 ? criticRating.Count : null,
                    // Stable API-only URL form. Filmweb resolves it to canonical film/serial URL.
                    Url = $"https://www.filmweb.pl/film?id={hit.Id.ToString(CultureInfo.InvariantCulture)}",
                    FilmwebId = hit.Id.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        return null;
    }

    private async Task<FilmwebRating?> GetRatingAsync(int filmwebId, CancellationToken cancellationToken)
    {
        if (filmwebId <= 0)
        {
            return null;
        }

        var ratingUrl = $"https://www.filmweb.pl/api/v1/film/{filmwebId.ToString(CultureInfo.InvariantCulture)}/rating";
        var json = await GetStringWithThrottleAsync(ratingUrl, cancellationToken, true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<FilmwebRating>(json, JsonOptions);
            if (payload is null || payload.Rate <= 0)
            {
                _logger.LogWarning("Filmweb rating API returned empty or zero rate for id {FilmwebId}", filmwebId);
                return null;
            }

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filmweb rating JSON parse failed for id {FilmwebId}", filmwebId);
            return null;
        }
    }

    private async Task<FilmwebRating?> GetCriticRatingAsync(int filmwebId, CancellationToken cancellationToken)
    {
        if (filmwebId <= 0)
        {
            return null;
        }

        var ratingUrl = $"https://www.filmweb.pl/api/v1/film/{filmwebId.ToString(CultureInfo.InvariantCulture)}/critics/rating";
        var json = await GetStringWithThrottleAsync(ratingUrl, cancellationToken, true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<FilmwebRating>(json, JsonOptions);
            if (payload is null || payload.Rate <= 0)
            {
                _logger.LogDebug("Filmweb critic rating API returned empty or zero rate for id {FilmwebId}", filmwebId);
                return null;
            }

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filmweb critic rating JSON parse failed for id {FilmwebId}", filmwebId);
            return null;
        }
    }

    private async Task<string?> GetStringWithThrottleAsync(string url, CancellationToken cancellationToken, bool expectJson)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _nextAllowedUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(20);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+filmweb)");
                if (expectJson)
                {
                    request.Headers.Accept.ParseAdd("application/json,text/plain,*/*");
                }
                else
                {
                    request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                }

                request.Headers.AcceptLanguage.ParseAdd("pl-PL,pl;q=0.9,en-US;q=0.7,en;q=0.5");

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _nextAllowedUtc = DateTimeOffset.UtcNow.Add(MinSpacing);

                if ((int)response.StatusCode == 404)
                {
                    return null;
                }

                if ((int)response.StatusCode == 429)
                {
                    var delay = TimeSpan.FromSeconds(3 + (attempt * 2));
                    _nextAllowedUtc = DateTimeOffset.UtcNow.Add(delay);
                    _logger.LogWarning("Filmweb request rate limited for {Url}. Backing off for {Delay}.", url, delay);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Filmweb request failed: {Status} {Reason} for {Url}", (int)response.StatusCode, response.ReasonPhrase, url);
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Filmweb request error for {Url}", url);
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        return null;
    }

    private async Task<SearchHit?> SearchBestHitAsync(string contentType, string query, string? expectedTitle, CancellationToken cancellationToken)
    {
        var searchType = contentType == "movie" ? "film" : "serial";
        var searchUrl = $"https://www.filmweb.pl/api/v1/search?q={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(searchType)}";
        var json = await GetStringWithThrottleAsync(searchUrl, cancellationToken, true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SearchResponse>(json, JsonOptions);
            if (payload?.SearchHits is null || payload.SearchHits.Count == 0)
            {
                _logger.LogDebug("Filmweb search API returned no hits for query '{Query}'", query);
                return null;
            }

            var candidates = payload.SearchHits
                .Where(h => h is not null && h.Id > 0 && string.Equals(h.Type, searchType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.LogDebug("Filmweb search API returned no candidates of type '{Type}' for query '{Query}'", searchType, query);
                return null;
            }

            var normalizedExpected = NormalizeTitle(expectedTitle);
            if (string.IsNullOrWhiteSpace(normalizedExpected))
            {
                return candidates[0];
            }

            SearchHit? best = null;
            var bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                var score = ScoreTitleMatch(normalizedExpected!, candidate.MatchedTitle);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best ?? candidates[0];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filmweb search JSON parse failed for query '{Query}'", query);
            return null;
        }
    }

    private static int ScoreTitleMatch(string expectedTitle, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return 0;
        }

        var expected = NormalizeForMatch(expectedTitle);
        var candidate = NormalizeForMatch(candidateTitle);
        if (expected.Length == 0 || candidate.Length == 0)
        {
            return 0;
        }

        if (string.Equals(expected, candidate, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (candidate.Contains(expected, StringComparison.Ordinal))
        {
            return 600;
        }

        if (expected.Contains(candidate, StringComparison.Ordinal))
        {
            return 500;
        }

        var commonPrefix = 0;
        var len = Math.Min(expected.Length, candidate.Length);
        while (commonPrefix < len && expected[commonPrefix] == candidate[commonPrefix])
        {
            commonPrefix++;
        }

        return commonPrefix;
    }

    private static List<string> BuildSearchQueries(string? imdbId, string? title, int? year)
    {
        var queries = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            queries.Add(imdbId!);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleOnly = title!;
            var titleWithYear = year.HasValue && year.Value > 1800
                ? $"{titleOnly} {year.Value.ToString(CultureInfo.InvariantCulture)}"
                : null;

            if (!string.IsNullOrWhiteSpace(titleWithYear))
            {
                queries.Add(titleWithYear!);
            }

            queries.Add(titleOnly);
        }

        return queries
            .Select(q => (q ?? string.Empty).Trim())
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var v = value.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(v.Length);
        foreach (var ch in v)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Replace("  ", " ", StringComparison.Ordinal).Trim();
    }

    public sealed class SearchResponse
    {
        public List<SearchHit> SearchHits { get; set; } = new();
    }

    public sealed class SearchHit
    {
        public int Id { get; set; }
        public string? Type { get; set; }
        public string? MatchedTitle { get; set; }
    }

    private static string? NormalizeContentType(string? contentType)
    {
        var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return ct is "movie" or "show" ? ct : null;
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
            return "tt" + NormalizeDigits(trimmed[2..]);
        }

        var digits = NormalizeDigits(trimmed);
        return string.IsNullOrWhiteSpace(digits) ? null : "tt" + digits;
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var t = title.Trim();
        return t.Length == 0 ? null : t;
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

    public sealed class FilmwebRating
    {
        public int Count { get; set; }
        public double Rate { get; set; }
    }
}
