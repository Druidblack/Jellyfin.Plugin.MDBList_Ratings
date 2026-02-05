using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class MdbListClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public MdbListClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<MdbListApiResult> GetByTmdbAsync(string contentType, string tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            string.IsNullOrWhiteSpace(tmdbId) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return new MdbListApiResult { Data = null };
        }

        // https://api.mdblist.com/tmdb/{type}/{tmdbId}?apikey={key}
        var url = $"https://api.mdblist.com/tmdb/{Uri.EscapeDataString(contentType)}/{Uri.EscapeDataString(tmdbId)}?apikey={Uri.EscapeDataString(apiKey)}";

        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // Read rate limit headers if present.
            var limit = TryGetIntHeader(response, "X-RateLimit-Limit");
            var remaining = TryGetIntHeader(response, "X-RateLimit-Remaining");
            var resetUtc = TryGetResetUtc(response, "X-RateLimit-Reset");

            var hardRateLimited = (int)response.StatusCode == 429;

            if (!response.IsSuccessStatusCode)
            {
                if (hardRateLimited)
                {
                    _logger.LogWarning("MDBList rate limited for {Url}. Remaining={Remaining}, ResetUtc={ResetUtc:o}", url, remaining, resetUtc);
                    return new MdbListApiResult
                    {
                        Url = url,
                        RateLimitLimit = limit,
                        RateLimitRemaining = remaining,
                        RateLimitResetUtc = resetUtc,
                        IsRateLimited = true,
                        Data = null
                    };
                }

                _logger.LogWarning("MDBList request failed: {Status} {Reason} for {Url}", (int)response.StatusCode, response.ReasonPhrase, url);
                return new MdbListApiResult
                {
                    Url = url,
                    RateLimitLimit = limit,
                    RateLimitRemaining = remaining,
                    RateLimitResetUtc = resetUtc,
                    IsRateLimited = false,
                    Data = null
                };
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<MdbListTitleResponse>(raw, JsonOptions);
            return new MdbListApiResult
            {
                Url = url,
                RateLimitLimit = limit,
                RateLimitRemaining = remaining,
                RateLimitResetUtc = resetUtc,
                IsRateLimited = false,
                Data = data,
                RawJson = raw
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MDBList request error for {Url}", url);
            return new MdbListApiResult { Url = url, Data = null };
        }
    }

    private static int? TryGetIntHeader(System.Net.Http.HttpResponseMessage response, string headerName)
    {
        try
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                var s = values.FirstOrDefault();
                return int.TryParse(s, out var i) ? i : (int?)null;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static DateTimeOffset? TryGetResetUtc(System.Net.Http.HttpResponseMessage response, string headerName)
    {
        try
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                var s = values.FirstOrDefault();
                if (long.TryParse(s, out var seconds) && seconds > 0)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
