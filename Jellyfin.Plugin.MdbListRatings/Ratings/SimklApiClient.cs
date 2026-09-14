using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class SimklApiResult
{
    public MdbListTitleResponse? Data { get; init; }
    public int StatusCode { get; init; }
    public bool IsRateLimited { get; init; }
    public bool IsCredentialRejected { get; init; }
    public int RetryAfterSeconds { get; init; }
}

/// <summary>
/// Public Simkl catalog client. Resolves Jellyfin external IDs through Simkl's lightweight
/// /redirect endpoint, then reads the Cloudflare-cached detail record.
/// </summary>
internal sealed class SimklApiClient
{
    private const string BaseUrl = "https://api.simkl.com";
    private const string AppName = "jellyfin-mdblist-ratings";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;

    public SimklApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SimklApiResult> GetTitleRatingsAsync(
        string? tmdbId,
        string? imdbId,
        string? tvdbId,
        string contentType,
        string clientId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new SimklApiResult { StatusCode = 0, IsCredentialRejected = true };
        }

        var resolveUrl = BuildResolveUrl(tmdbId, imdbId, tvdbId, contentType, clientId);
        if (resolveUrl is null)
        {
            return new SimklApiResult { StatusCode = 404 };
        }

        try
        {
            using var resolveResponse = await SendAsync(resolveUrl, clientId, cancellationToken).ConfigureAwait(false);
            var resolveStatus = (int)resolveResponse.StatusCode;

            if (resolveResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new SimklApiResult
                {
                    StatusCode = resolveStatus,
                    IsRateLimited = true,
                    RetryAfterSeconds = GetRetryAfterSeconds(resolveResponse, 5)
                };
            }

            if (resolveStatus == 412)
            {
                // Simkl uses HTTP 412 client_id_failed for an invalid Client ID.
                // This is a credential error, not a temporary rate limit.
                return new SimklApiResult
                {
                    StatusCode = resolveStatus,
                    IsCredentialRejected = true
                };
            }

            if (resolveResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new SimklApiResult { StatusCode = resolveStatus, IsCredentialRejected = true };
            }

            if (!IsRedirect(resolveResponse.StatusCode) || resolveResponse.Headers.Location is null)
            {
                return new SimklApiResult { StatusCode = resolveStatus };
            }

            var target = ParseResolvedLocation(resolveResponse.Headers.Location);
            if (target is null)
            {
                return new SimklApiResult { StatusCode = 404 };
            }

            var detailUrl = BuildDetailUrl(target.Value.Endpoint, target.Value.SimklId, clientId);
            using var detailResponse = await SendAsync(detailUrl, clientId, cancellationToken).ConfigureAwait(false);
            var detailStatus = (int)detailResponse.StatusCode;

            if (detailResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new SimklApiResult
                {
                    StatusCode = detailStatus,
                    IsRateLimited = true,
                    RetryAfterSeconds = GetRetryAfterSeconds(detailResponse, 5)
                };
            }

            if (detailStatus == 412)
            {
                return new SimklApiResult
                {
                    StatusCode = detailStatus,
                    IsCredentialRejected = true
                };
            }

            if (detailResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new SimklApiResult { StatusCode = detailStatus, IsCredentialRejected = true };
            }

            if (!detailResponse.IsSuccessStatusCode)
            {
                return new SimklApiResult { StatusCode = detailStatus };
            }

            await using var stream = await detailResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var detail = await JsonSerializer.DeserializeAsync<SimklDetailResponse>(stream, GetJsonOptions(), cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                return new SimklApiResult { StatusCode = detailStatus };
            }

            return new SimklApiResult
            {
                StatusCode = detailStatus,
                Data = MapToSharedRatings(detail, target.Value, tmdbId, imdbId)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Do not log the exception object here: Simkl requires client_id in the URL and
            // HttpRequestException can include that URL. Keep credentials out of Jellyfin logs.
            _logger.LogWarning(
                "Simkl lookup failed for {ContentType} (TMDb={TmdbId}, IMDb={ImdbId}, TVDB={TvdbId}); error type: {ErrorType}",
                contentType,
                tmdbId,
                imdbId,
                tvdbId,
                ex.GetType().Name);
            return new SimklApiResult { StatusCode = 0 };
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string clientId, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Simkl permits 10 public catalog GETs/sec per client_id. Each title may require
            // a /redirect + detail pair, so keep this client comfortably below the ceiling.
            var wait = _nextRequestUtc - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var http = _httpClientFactory.CreateClient(SecretHttpClient.SimklNoRedirectName);
            http.Timeout = TimeSpan.FromSeconds(30);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0");
            // Detail endpoints also support the header form; sending it in addition to the
            // required query parameter keeps compatibility with Simkl's documented API style.
            request.Headers.TryAddWithoutValidation("simkl-api-key", clientId);

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _nextRequestUtc = DateTimeOffset.UtcNow.AddMilliseconds(125);
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static string? BuildResolveUrl(string? tmdbId, string? imdbId, string? tvdbId, string contentType, string clientId)
    {
        var args = new List<string>
        {
            "to=simkl",
            "client_id=" + Uri.EscapeDataString(clientId),
            "app-name=" + Uri.EscapeDataString(AppName),
            "app-version=" + Uri.EscapeDataString(GetAppVersion())
        };

        var hasId = false;
        var normalizedImdb = NormalizeImdb(imdbId);
        if (!string.IsNullOrWhiteSpace(normalizedImdb))
        {
            args.Add("imdb=" + Uri.EscapeDataString(normalizedImdb));
            hasId = true;
        }

        if (TryDigits(tmdbId, out var tmdb))
        {
            args.Add("tmdb=" + Uri.EscapeDataString(tmdb));
            // type=show lets Simkl match both ordinary TV and anime.
            args.Add("type=" + (string.Equals(contentType, "show", StringComparison.OrdinalIgnoreCase) ? "show" : "movie"));
            hasId = true;
        }

        if (TryDigits(tvdbId, out var tvdb))
        {
            args.Add("tvdb=" + Uri.EscapeDataString(tvdb));
            hasId = true;
        }

        return hasId ? BaseUrl + "/redirect?" + string.Join("&", args) : null;
    }

    private static string BuildDetailUrl(string endpoint, long simklId, string clientId)
        => BaseUrl + "/" + endpoint + "/" + simklId.ToString(CultureInfo.InvariantCulture)
            + "?client_id=" + Uri.EscapeDataString(clientId)
            + "&app-name=" + Uri.EscapeDataString(AppName)
            + "&app-version=" + Uri.EscapeDataString(GetAppVersion());

    private static MdbListTitleResponse MapToSharedRatings(SimklDetailResponse detail, ResolvedTarget target, string? fallbackTmdbId, string? fallbackImdbId)
    {
        var result = new MdbListTitleResponse
        {
            Type = target.Endpoint switch
            {
                "movies" => "movie",
                "tv" => "show",
                "anime" => "anime",
                _ => detail.Type
            },
            Ids = new MdbListIds
            {
                Imdb = NormalizeImdb(detail.Ids?.Imdb) ?? NormalizeImdb(fallbackImdbId),
                Tmdb = TryDigits(detail.Ids?.Tmdb, out var tmdbFromDetail) && int.TryParse(tmdbFromDetail, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTmdb)
                    ? parsedTmdb
                    : (TryDigits(fallbackTmdbId, out var fallbackTmdb) && int.TryParse(fallbackTmdb, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedFallbackTmdb) ? parsedFallbackTmdb : null)
            }
        };

        var slug = detail.Ids?.Slug;
        var simklId = detail.Ids?.Simkl ?? (target.SimklId <= int.MaxValue ? (int?)target.SimklId : null);
        var simklUrl = BuildPublicUrl(target.Endpoint, simklId, slug) ?? target.PublicUrl;

        AddRating(result, "simkl", GetRating(detail, "simkl"), simklUrl);

        var resolvedImdb = NormalizeImdb(detail.Ids?.Imdb) ?? NormalizeImdb(fallbackImdbId);
        var imdbUrl = string.IsNullOrWhiteSpace(resolvedImdb) ? null : "https://www.imdb.com/title/" + resolvedImdb + "/";
        AddRating(result, "simkl_imdb", GetRating(detail, "imdb"), imdbUrl);

        var malId = NormalizeDigits(detail.Ids?.Mal);
        var malUrl = string.IsNullOrWhiteSpace(malId) ? null : "https://myanimelist.net/anime/" + malId;
        AddRating(result, "simkl_mal", GetRating(detail, "mal"), malUrl);

        return result;
    }

    private static SimklRatingValue? GetRating(SimklDetailResponse detail, string key)
    {
        if (detail.Ratings is null || !detail.Ratings.TryGetValue(key, out var value))
        {
            return null;
        }

        return value;
    }

    private static void AddRating(MdbListTitleResponse result, string source, SimklRatingValue? value, string? url)
    {
        if (value?.Rating is not double rating || double.IsNaN(rating) || double.IsInfinity(rating) || rating <= 0 || rating > 10)
        {
            return;
        }

        result.Ratings.Add(new MdbListRating
        {
            Source = source,
            Value = rating,
            Score = rating * 10.0,
            Votes = value.Votes,
            Url = url
        });
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static ResolvedTarget? ParseResolvedLocation(Uri location)
    {
        Uri absolute;
        if (location.IsAbsoluteUri)
        {
            absolute = location;
        }
        else
        {
            if (!Uri.TryCreate(new Uri("https://simkl.com"), location, out var resolved))
            {
                return null;
            }

            absolute = resolved;
        }

        if (!string.Equals(absolute.Host, "simkl.com", StringComparison.OrdinalIgnoreCase)
            && !absolute.Host.EndsWith(".simkl.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = absolute.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }

        var endpoint = parts[0].ToLowerInvariant() switch
        {
            "movies" => "movies",
            "movie" => "movies",
            "tv" => "tv",
            "anime" => "anime",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(endpoint) ? null : new ResolvedTarget(endpoint, id, absolute.ToString());
    }

    private static string? BuildPublicUrl(string endpoint, int? simklId, string? slug)
    {
        if (!simklId.HasValue || simklId.Value <= 0)
        {
            return null;
        }

        var path = endpoint switch
        {
            "movies" => "movies",
            "tv" => "tv",
            "anime" => "anime",
            _ => endpoint
        };

        var url = "https://simkl.com/" + path + "/" + simklId.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            url += "/" + slug.Trim().Trim('/');
        }

        return url;
    }

    private static int GetRetryAfterSeconds(HttpResponseMessage response, int fallback)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta.TotalSeconds > 0)
        {
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        }

        return fallback;
    }

    private static string? NormalizeImdb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string(v.Skip(2).Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : "tt" + digits;
        }

        var onlyDigits = new string(v.Where(char.IsDigit).ToArray());
        return onlyDigits.Length == 0 ? null : "tt" + onlyDigits;
    }

    private static bool TryDigits(string? value, out string digits)
    {
        digits = NormalizeDigits(value) ?? string.Empty;
        return digits.Length > 0;
    }

    private static string? NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static string GetAppVersion()
        => typeof(SimklApiClient).Assembly.GetName().Version?.ToString(4) ?? "1.0.0.10";

    private static JsonSerializerOptions GetJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly record struct ResolvedTarget(string Endpoint, long SimklId, string PublicUrl);
}
