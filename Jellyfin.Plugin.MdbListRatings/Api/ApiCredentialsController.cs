using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Tests API credentials entered on the plugin configuration page without saving them.
/// Credentials are accepted only in a POST body and are never returned or logged.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/MdbListRatings")]
public sealed class ApiCredentialsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ApiCredentialsController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("TestApiCredential")]
    [Produces("application/json")]
    public async Task<ActionResult<ApiCredentialTestResponse>> TestApiCredential(
        [FromBody] ApiCredentialTestRequest request,
        CancellationToken cancellationToken)
    {
        var provider = (request.Provider ?? string.Empty).Trim().ToLowerInvariant();
        var credential = (request.Credential ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(credential))
        {
            return Ok(Result(provider, false, "error", "Enter a credential first."));
        }

        return provider switch
        {
            "mdblist" => Ok(await TestMdbListAsync(credential, cancellationToken).ConfigureAwait(false)),
            "trakt" => Ok(await TestTraktAsync(credential, cancellationToken).ConfigureAwait(false)),
            "tmdb" => Ok(await TestTmdbAsync(credential, cancellationToken).ConfigureAwait(false)),
            "omdb" => Ok(await TestOmdbAsync(credential, cancellationToken).ConfigureAwait(false)),
            "whatson" => Ok(await TestWhatsOnAsync(credential, cancellationToken).ConfigureAwait(false)),
            "simkl" => Ok(await TestSimklAsync(credential, cancellationToken).ConfigureAwait(false)),
            _ => BadRequest(Result(provider, false, "error", "Unknown API provider."))
        };
    }

    private async Task<ApiCredentialTestResponse> TestMdbListAsync(string apiKey, CancellationToken cancellationToken)
    {
        const string provider = "mdblist";
        const string safeUrl = "https://api.mdblist.com/tmdb/movie/550";
        var requestUrl = safeUrl + "?apikey=" + Uri.EscapeDataString(apiKey);

        try
        {
            var http = CreateSecretClient(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Result(provider, true, "success", "MDBList API key is valid.", (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(provider, false, "error", "MDBList rejected the API key.", (int)response.StatusCode);
            }

            if ((int)response.StatusCode == 429)
            {
                return Result(provider, true, "warning", "MDBList accepted the request, but the API rate limit has been reached.", (int)response.StatusCode);
            }

            return Result(provider, null, "warning", $"MDBList returned HTTP {(int)response.StatusCode}; the key could not be confirmed.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(provider, null, "warning", "MDBList API check timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(provider, null, "warning", "Could not connect to MDBList to verify the API key.");
        }
    }

    private async Task<ApiCredentialTestResponse> TestTraktAsync(string clientId, CancellationToken cancellationToken)
    {
        const string provider = "trakt";
        const string url = "https://api.trakt.tv/movies/trending?limit=1";

        try
        {
            var http = CreateSecretClient(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
            request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+credential-test)");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Result(provider, true, "success", "Trakt API Client ID is valid.", (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(provider, false, "error", "Trakt rejected the Client ID.", (int)response.StatusCode);
            }

            if ((int)response.StatusCode == 429)
            {
                return Result(provider, true, "warning", "Trakt accepted the request, but the API rate limit has been reached.", (int)response.StatusCode);
            }

            return Result(provider, null, "warning", $"Trakt returned HTTP {(int)response.StatusCode}; the Client ID could not be confirmed.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(provider, null, "warning", "Trakt API check timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(provider, null, "warning", "Could not connect to Trakt to verify the Client ID.");
        }
    }

    private async Task<ApiCredentialTestResponse> TestTmdbAsync(string auth, CancellationToken cancellationToken)
    {
        const string provider = "tmdb";
        const string baseUrl = "https://api.themoviedb.org/3/configuration";
        var isBearer = LooksLikeBearerToken(auth);
        var requestUrl = isBearer ? baseUrl : baseUrl + "?api_key=" + Uri.EscapeDataString(auth);

        try
        {
            var http = CreateSecretClient(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+credential-test)");
            if (isBearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Result(provider, true, "success", isBearer ? "TMDb API key is valid." : "TMDb API key is valid.", (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(provider, false, "error", "TMDb rejected the API credential.", (int)response.StatusCode);
            }

            if ((int)response.StatusCode == 429)
            {
                return Result(provider, true, "warning", "TMDb accepted the request, but the API rate limit has been reached.", (int)response.StatusCode);
            }

            return Result(provider, null, "warning", $"TMDb returned HTTP {(int)response.StatusCode}; the credential could not be confirmed.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(provider, null, "warning", "TMDb API check timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(provider, null, "warning", "Could not connect to TMDb to verify the API credential.");
        }
    }

    private async Task<ApiCredentialTestResponse> TestOmdbAsync(string apiKey, CancellationToken cancellationToken)
    {
        const string provider = "omdb";
        var requestUrl = "https://www.omdbapi.com/?i=tt0111161&apikey=" + Uri.EscapeDataString(apiKey);

        try
        {
            var http = CreateSecretClient(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            OmdbTestPayload? payload = null;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<OmdbTestPayload>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Handled below using the HTTP status only.
            }

            if (payload is not null && string.Equals(payload.Response, "True", StringComparison.OrdinalIgnoreCase))
            {
                return Result(provider, true, "success", "OMDb API key is valid.", (int)response.StatusCode);
            }

            var error = payload?.Error ?? string.Empty;
            if (Contains(error, "limit reached") || Contains(error, "request limit"))
            {
                return Result(provider, true, "warning", "OMDb recognized the API key, but its request limit has been reached.", (int)response.StatusCode);
            }

            if (Contains(error, "invalid api key") || Contains(error, "no api key") || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(provider, false, "error", "OMDb rejected the API key.", (int)response.StatusCode);
            }

            if (response.IsSuccessStatusCode)
            {
                return Result(provider, null, "warning", "OMDb responded, but the API key could not be confirmed.", (int)response.StatusCode);
            }

            return Result(provider, null, "warning", $"OMDb returned HTTP {(int)response.StatusCode}; the API key could not be confirmed.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(provider, null, "warning", "OMDb API check timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(provider, null, "warning", "Could not connect to OMDb to verify the API key.");
        }
    }

    private async Task<ApiCredentialTestResponse> TestWhatsOnAsync(string apiKey, CancellationToken cancellationToken)
    {
        const string provider = "whatson";
        const string safeUrl = "https://whatson-api.onrender.com/?tmdbId=550&ratings_filters=all";
        var requestUrl = safeUrl + "&api_key=" + Uri.EscapeDataString(apiKey);

        try
        {
            var http = CreateSecretClient(TimeSpan.FromSeconds(30));

            // WhatsOn deliberately falls back to the anonymous tier when an unknown api_key is
            // supplied. Therefore HTTP 200 alone does not prove that the key is valid. Compare
            // the rate-limit tier returned with the supplied key against an anonymous request.
            using var keyedRequest = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            keyedRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            keyedRequest.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+credential-test)");
            using var keyedResponse = await http.SendAsync(keyedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (keyedResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(provider, false, "error", "WhatsOn rejected the API key.", (int)keyedResponse.StatusCode);
            }

            if ((int)keyedResponse.StatusCode == 429)
            {
                return Result(provider, null, "warning", "WhatsOn rate-limited the test request, so the API key could not be confirmed.", (int)keyedResponse.StatusCode);
            }

            if (!keyedResponse.IsSuccessStatusCode)
            {
                return Result(provider, null, "warning", $"WhatsOn returned HTTP {(int)keyedResponse.StatusCode}; the API key could not be confirmed.", (int)keyedResponse.StatusCode);
            }

            var keyedLimit = TryGetIntHeader(keyedResponse, "X-RateLimit-Limit");

            using var anonymousRequest = new HttpRequestMessage(HttpMethod.Get, safeUrl);
            anonymousRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            anonymousRequest.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+credential-test-anonymous)");
            using var anonymousResponse = await http.SendAsync(anonymousRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var anonymousLimit = TryGetIntHeader(anonymousResponse, "X-RateLimit-Limit");

            if (keyedLimit.HasValue && anonymousLimit.HasValue)
            {
                if (keyedLimit.Value > anonymousLimit.Value)
                {
                    return Result(provider, true, "success", $"WhatsOn API key is valid (rate-limit tier: {keyedLimit.Value} requests).", (int)keyedResponse.StatusCode);
                }

                if (keyedLimit.Value == anonymousLimit.Value)
                {
                    return Result(provider, false, "error", "WhatsOn did not recognize this API key; the request remained on the anonymous rate-limit tier.", (int)keyedResponse.StatusCode);
                }
            }

            return Result(provider, null, "warning", "WhatsOn responded successfully, but the rate-limit headers were not sufficient to confirm whether the API key was recognized.", (int)keyedResponse.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(provider, null, "warning", "WhatsOn API check timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(provider, null, "warning", "Could not connect to WhatsOn to verify the API key.");
        }
    }

    private async Task<ApiCredentialTestResponse> TestSimklAsync(string clientId, CancellationToken cancellationToken)
    {
        const string provider = "simkl";
        const string url = "https://api.simkl.com/movies/472214?app-name=jellyfin-mdblist-ratings&app-version=1.0.0.10";

        try
        {
            var http = CreateSecretClient(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0 (+credential-test)");
            request.Headers.TryAddWithoutValidation("simkl-api-key", clientId);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Result(provider, true, "success", "Simkl Client ID is valid.", (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(provider, false, "error", "Simkl rejected the Client ID.", (int)response.StatusCode);
            }

            if ((int)response.StatusCode == 412)
            {
                return Result(provider, false, "error", "Simkl did not accept this Client ID (HTTP 412 client_id_failed).", (int)response.StatusCode);
            }

            if ((int)response.StatusCode == 429)
            {
                return Result(provider, true, "warning", "Simkl accepted the Client ID, but its request rate limit is currently active.", (int)response.StatusCode);
            }

            return Result(provider, null, "warning", $"Simkl returned HTTP {(int)response.StatusCode}; the Client ID could not be confirmed.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(provider, null, "warning", "Simkl API check timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(provider, null, "warning", "Could not connect to Simkl to verify the Client ID.");
        }
    }

    private static int? TryGetIntHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            if (int.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private HttpClient CreateSecretClient(TimeSpan timeout)
    {
        var http = _httpClientFactory.CreateClient(SecretHttpClient.Name);
        http.Timeout = timeout;
        return http;
    }

    private static bool LooksLikeBearerToken(string auth)
    {
        var trimmed = auth.Trim();
        return trimmed.Contains('.') || trimmed.StartsWith("eyJ", StringComparison.Ordinal);
    }

    private static bool Contains(string value, string fragment)
        => value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

    private static ApiCredentialTestResponse Result(string provider, bool? isValid, string status, string message, int? httpStatusCode = null)
        => new()
        {
            Provider = provider,
            IsValid = isValid,
            Status = status,
            Message = message,
            HttpStatusCode = httpStatusCode
        };

    public sealed class ApiCredentialTestRequest
    {
        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("credential")]
        public string? Credential { get; set; }
    }

    public sealed class ApiCredentialTestResponse
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("isValid")]
        public bool? IsValid { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "warning";

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("httpStatusCode")]
        public int? HttpStatusCode { get; set; }
    }

    private sealed class OmdbTestPayload
    {
        public string? Response { get; set; }
        public string? Error { get; set; }
    }
}
