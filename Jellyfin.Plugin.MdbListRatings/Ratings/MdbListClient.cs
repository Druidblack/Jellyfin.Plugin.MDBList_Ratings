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

        // Keep the credential-bearing URL strictly local to the HTTP request.
        // Never put it into plugin logs or result objects.
        var safeUrl = $"https://api.mdblist.com/tmdb/{Uri.EscapeDataString(contentType)}/{Uri.EscapeDataString(tmdbId)}";
        var requestUrl = safeUrl + "?apikey=" + Uri.EscapeDataString(apiKey);

        try
        {
            // This named client has the default IHttpClientFactory request loggers removed.
            // Otherwise HttpClient logging could expose the API key through the request URI.
            var http = _httpClientFactory.CreateClient(SecretHttpClient.Name);
            http.Timeout = TimeSpan.FromSeconds(15);

            using var response = await http.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

            // Read rate limit headers if present.
            var limit = TryGetIntHeader(response, "X-RateLimit-Limit");
            var remaining = TryGetIntHeader(response, "X-RateLimit-Remaining");
            var resetUtc = TryGetResetUtc(response, "X-RateLimit-Reset");

            var hardRateLimited = (int)response.StatusCode == 429;

            if (!response.IsSuccessStatusCode)
            {
                if (hardRateLimited)
                {
                    _logger.LogWarning("MDBList rate limited for {Url}. Remaining={Remaining}, ResetUtc={ResetUtc:o}", safeUrl, remaining, resetUtc);
                    return new MdbListApiResult
                    {
                        Url = safeUrl,
                        StatusCode = (int)response.StatusCode,
                        ReasonPhrase = response.ReasonPhrase,
                        RateLimitLimit = limit,
                        RateLimitRemaining = remaining,
                        RateLimitResetUtc = resetUtc,
                        IsRateLimited = true,
                        Data = null
                    };
                }

                _logger.LogWarning("MDBList request failed: {Status} {Reason} for {Url}", (int)response.StatusCode, response.ReasonPhrase, safeUrl);
                return new MdbListApiResult
                {
                    Url = safeUrl,
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    RateLimitLimit = limit,
                    RateLimitRemaining = remaining,
                    RateLimitResetUtc = resetUtc,
                    IsRateLimited = false,
                    Data = null
                };
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<MdbListTitleResponse>(raw, JsonOptions);
            AddMdbListScoreAverage(data);
            return new MdbListApiResult
            {
                Url = safeUrl,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
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
            _logger.LogWarning("MDBList request error for {Url}. ErrorType={ErrorType}", safeUrl, ex.GetType().Name);
            return new MdbListApiResult { Url = safeUrl, Data = null };
        }
    }

    private static void AddMdbListScoreAverage(MdbListTitleResponse? data)
    {
        if (data is null || !data.ScoreAverage.HasValue)
        {
            return;
        }

        var score = data.ScoreAverage.Value;
        if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0 || score > 100)
        {
            return;
        }

        data.Ratings ??= new System.Collections.Generic.List<MdbListRating>();
        var existing = data.Ratings.FirstOrDefault(r => string.Equals(r.Source, "mdblist", StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            data.Ratings.Add(new MdbListRating
            {
                Source = "mdblist",
                Value = score,
                Score = score
            });
            return;
        }

        // Keep the top-level aggregate authoritative if a future API response also happens
        // to contain an entry with the same source key in the ratings array.
        existing.Value = score;
        existing.Score = score;
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
