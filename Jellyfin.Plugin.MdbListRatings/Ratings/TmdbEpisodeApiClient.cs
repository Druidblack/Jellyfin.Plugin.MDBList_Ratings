using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class TmdbEpisodeApiClient
{
    internal sealed class TmdbEpisodeLookupResult
    {
        public int SeriesTmdbId { get; init; }
        public double AverageRating { get; init; }
        public int? Votes { get; init; }
        public string? Url { get; init; }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TmdbEpisodeApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TmdbEpisodeLookupResult?> LookupEpisodeAsync(
        string? showTmdbId,
        string? showImdbId,
        string? showTvdbId,
        int seasonNumber,
        int episodeNumber,
        string? authValue,
        CancellationToken cancellationToken)
    {
        if (seasonNumber < 0 || episodeNumber <= 0)
        {
            return null;
        }

        var auth = (authValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(auth))
        {
            return null;
        }

        var seriesTmdbId = await ResolveSeriesTmdbIdAsync(showTmdbId, showImdbId, showTvdbId, auth, cancellationToken).ConfigureAwait(false);
        if (!seriesTmdbId.HasValue || seriesTmdbId.Value <= 0)
        {
            return null;
        }

        var details = await GetEpisodeDetailsAsync(seriesTmdbId.Value, seasonNumber, episodeNumber, auth, cancellationToken).ConfigureAwait(false);
        if (details is null || !details.VoteAverage.HasValue || details.VoteAverage.Value <= 0)
        {
            return null;
        }

        var avg = Math.Round(details.VoteAverage.Value, 1, MidpointRounding.AwayFromZero);
        return new TmdbEpisodeLookupResult
        {
            SeriesTmdbId = seriesTmdbId.Value,
            AverageRating = avg,
            Votes = details.VoteCount,
            Url = $"https://www.themoviedb.org/tv/{seriesTmdbId.Value}/season/{seasonNumber}/episode/{episodeNumber}"
        };
    }

    private async Task<int?> ResolveSeriesTmdbIdAsync(string? showTmdbId, string? showImdbId, string? showTvdbId, string auth, CancellationToken cancellationToken)
    {
        var tmdb = NormalizeDigits(showTmdbId);
        if (!string.IsNullOrWhiteSpace(tmdb) && int.TryParse(tmdb, out var tmdbParsed) && tmdbParsed > 0)
        {
            return tmdbParsed;
        }

        var imdb = NormalizeImdbId(showImdbId);
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            var found = await FindTvByExternalIdAsync(imdb, "imdb_id", auth, cancellationToken).ConfigureAwait(false);
            if (found.HasValue)
            {
                return found.Value;
            }
        }

        var tvdb = NormalizeDigits(showTvdbId);
        if (!string.IsNullOrWhiteSpace(tvdb))
        {
            var found = await FindTvByExternalIdAsync(tvdb, "tvdb_id", auth, cancellationToken).ConfigureAwait(false);
            if (found.HasValue)
            {
                return found.Value;
            }
        }

        return null;
    }

    private async Task<int?> FindTvByExternalIdAsync(string externalId, string externalSource, string auth, CancellationToken cancellationToken)
    {
        var url = $"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(externalId)}?external_source={Uri.EscapeDataString(externalSource)}";
        var response = await GetJsonAsync<FindResponse>(url, auth, cancellationToken).ConfigureAwait(false);
        if (response?.TvResults is null)
        {
            return null;
        }

        foreach (var tv in response.TvResults)
        {
            if (tv?.Id is int id && id > 0)
            {
                return id;
            }
        }

        return null;
    }

    private async Task<EpisodeDetailsResponse?> GetEpisodeDetailsAsync(int seriesTmdbId, int seasonNumber, int episodeNumber, string auth, CancellationToken cancellationToken)
    {
        var url = $"https://api.themoviedb.org/3/tv/{seriesTmdbId}/season/{seasonNumber}/episode/{episodeNumber}";
        return await GetJsonAsync<EpisodeDetailsResponse>(url, auth, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(string url, string auth, CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpClientFactory.CreateClient(SecretHttpClient.Name);
            http.Timeout = TimeSpan.FromSeconds(20);

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildAuthorizedUrl(url, auth));
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+tmdb-api)");

            if (LooksLikeBearerToken(auth))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 404)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDb episode request failed: {Status} {Reason} for {Url}", (int)response.StatusCode, response.ReasonPhrase, url);
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
            _logger.LogWarning("TMDb episode request error for {Url}. ErrorType={ErrorType}", url, ex.GetType().Name);
            return default;
        }
    }

    private static string BuildAuthorizedUrl(string url, string auth)
    {
        if (LooksLikeBearerToken(auth))
        {
            return url;
        }

        var sep = url.Contains('?') ? '&' : '?';
        return url + sep + "api_key=" + Uri.EscapeDataString(auth);
    }

    private static bool LooksLikeBearerToken(string auth)
    {
        if (string.IsNullOrWhiteSpace(auth))
        {
            return false;
        }

        var trimmed = auth.Trim();
        return trimmed.Contains('.') || trimmed.StartsWith("eyJ", StringComparison.Ordinal);
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

    private sealed class EpisodeDetailsResponse
    {
        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int? VoteCount { get; set; }
    }

    private sealed class FindResponse
    {
        [JsonPropertyName("tv_results")]
        public System.Collections.Generic.List<FindTvResult>? TvResults { get; set; }
    }

    private sealed class FindTvResult
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }
    }
}
