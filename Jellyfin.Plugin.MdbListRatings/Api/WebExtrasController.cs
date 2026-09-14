using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Configuration;
using Jellyfin.Plugin.MdbListRatings.Ratings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Web-only "extras" for the Details all-ratings panel.
/// These values are fetched for the web UI and cached on disk to reduce repeated network requests.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/MdbListRatings")]
public sealed class WebExtrasController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public sealed class WebExtrasResponse
    {
        [JsonPropertyName("rtCriticsCertified")]
        public bool RtCriticsCertified { get; set; }

        [JsonPropertyName("rtAudienceVerified")]
        public bool RtAudienceVerified { get; set; }

        [JsonPropertyName("metacriticMustSee")]
        public bool MetacriticMustSee { get; set; }

        /// <summary>
        /// AniList meanScore (0..100).
        /// </summary>
        [JsonPropertyName("anilistScore")]
        public int? AniListScore { get; set; }
    }

    /// <summary>
    /// Returns web-only extras for a TMDb id.
    /// - Rotten Tomatoes certified/verified and Metacritic Must-See come directly from cached WhatsOn JSON.
    /// - AniList still uses title+year search against AniList GraphQL (cached).
    /// </summary>
    [HttpGet("WebExtrasByTmdb")]
    [Produces("application/json")]
    public async Task<ActionResult<WebExtrasResponse>> Get(
        [FromQuery] string type,
        [FromQuery] string tmdbId,
        [FromQuery] string? title,
        [FromQuery] int? year,
        [FromQuery] int? tc,
        [FromQuery] int? rv,
        [FromQuery] int? mc,
        [FromQuery] int? al,
        CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new WebExtrasResponse());
        }

        var cfg = plugin.Configuration;

        // This endpoint is intended ONLY for the Web all-ratings panel.
        if (cfg.EnableWebAllRatingsFromCache != true)
        {
            return Ok(new WebExtrasResponse());
        }

        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return BadRequest("Missing required query parameters: type, tmdbId");
        }

        type = type.Trim().ToLowerInvariant();
        if (type != "movie" && type != "show")
        {
            return BadRequest("Invalid type. Expected: movie|show");
        }

        var wantTc = (tc == 1) && cfg.EnableWebExtraTomatoesCertified;
        var wantRv = (rv == 1) && cfg.EnableWebExtraRottenVerified;
        var wantMc = (mc == 1) && cfg.EnableWebExtraMetacriticMustSee;
        var wantAl = (al == 1) && cfg.EnableWebExtraAniList;

        if (!wantTc && !wantRv && !wantMc && !wantAl)
        {
            return Ok(new WebExtrasResponse());
        }

        var env = await plugin.Updater.TryGetCacheEnvelopeAsync(type, tmdbId.Trim(), cancellationToken).ConfigureAwait(false);
        if (env is null)
        {
            return Ok(new WebExtrasResponse());
        }

        var log = plugin.LoggerFactory.CreateLogger<WebExtrasController>();
        var http = plugin.HttpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.MdbListRatings/1.0");

        var res = new WebExtrasResponse();
        var extrasCacheKey = $"{type}:{tmdbId.Trim()}";
        var extrasCache = plugin.WebExtrasCache;
        var extrasTtl = GetWebExtrasTtl(cfg);
        var now = DateTimeOffset.UtcNow;

        var cachedExtras = await extrasCache.TryGetAsync(extrasCacheKey, cancellationToken).ConfigureAwait(false)
            ?? new WebExtrasCacheStore.CacheEnvelope();

        // Prefer structured flags from WhatsOn. The legacy methods are retained strictly as
        // fallbacks for individual fields that WhatsOn did not return (null / missing).
        ApplyCachedExtras(res, cachedExtras.Data);
        var features = env.Data?.WhatsOnFeatures;

        var needRtCriticsFallback = wantTc && features?.RottenTomatoesCriticsCertified is null;
        var needRtAudienceFallback = wantRv && features?.RottenTomatoesUsersCertified is null;
        var needMetacriticFallback = wantMc && features?.MetacriticMustSee is null;

        if (wantTc && features?.RottenTomatoesCriticsCertified is bool whatsOnCriticsCertified)
        {
            res.RtCriticsCertified = whatsOnCriticsCertified;
        }

        if (wantRv && features?.RottenTomatoesUsersCertified is bool whatsOnUsersCertified)
        {
            res.RtAudienceVerified = whatsOnUsersCertified;
        }

        if (wantMc && features?.MetacriticMustSee is bool whatsOnMustSee)
        {
            res.MetacriticMustSee = whatsOnMustSee;
        }

        var needRtRefresh = (needRtCriticsFallback || needRtAudienceFallback)
            && !HasFreshRottenTomatoesExtras(
                cachedExtras.Data,
                needRtCriticsFallback,
                needRtAudienceFallback,
                now,
                extrasTtl);
        var needAniRefresh = wantAl && !HasFreshAniListExtra(cachedExtras.Data, now, extrasTtl);
        var extrasChanged = false;

        // ---- Rotten Tomatoes legacy fallback --------------------------------
        // Use this only when the corresponding WhatsOn field is absent. We still use the
        // MDBList tomatoes/popcorn URL, but only permit rottentomatoes.com hosts.
        if (needRtRefresh)
        {
            string? rtPath = TryGetRottenTomatoesPath(env, log);
            var rtUrl = BuildSafeRottenTomatoesUrl(rtPath);

            if (!string.IsNullOrWhiteSpace(rtUrl))
            {
                try
                {
                    var html = await http.GetStringAsync(rtUrl, cancellationToken).ConfigureAwait(false);
                    var m = Regex.Match(
                        html,
                        @"<script\s+id=""media-scorecard-json""[^>]*>([\s\S]*?)</script>",
                        RegexOptions.IgnoreCase);

                    if (m.Success)
                    {
                        var jsonStr = m.Groups[1].Value;
                        var rtCriticsCertified = false;
                        var rtAudienceVerified = false;

                        try
                        {
                            var obj = JsonSerializer.Deserialize<RtScorecard>(jsonStr, JsonOptions);
                            rtCriticsCertified = obj?.CriticsScore?.Certified == true;
                        }
                        catch
                        {
                            // Keep the old tolerant behavior: malformed scorecard JSON simply
                            // leaves the flag false.
                        }

                        rtAudienceVerified = jsonStr.Contains(
                            "POSITIVE\",\"certified\":true",
                            StringComparison.OrdinalIgnoreCase);

                        cachedExtras.Data.RottenTomatoesCachedAtUtc = now;
                        cachedExtras.Data.HasRtCriticsCertified = true;
                        cachedExtras.Data.RtCriticsCertified = rtCriticsCertified;
                        cachedExtras.Data.HasRtAudienceVerified = true;
                        cachedExtras.Data.RtAudienceVerified = rtAudienceVerified;

                        if (needRtCriticsFallback)
                        {
                            res.RtCriticsCertified = rtCriticsCertified;
                        }

                        if (needRtAudienceFallback)
                        {
                            res.RtAudienceVerified = rtAudienceVerified;
                        }

                        extrasChanged = true;
                    }
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "WebExtras: RottenTomatoes legacy fallback failed for {Url}", rtUrl);
                }
            }
        }

        // If the old Rotten Tomatoes values are already cached and WhatsOn omitted the field,
        // ApplyCachedExtras above has already populated the response.

        // ---- Metacritic legacy fallback --------------------------------------
        // Historical behavior: MDBList Metacritic score > 80 with at least 14 critic votes.
        if (needMetacriticFallback)
        {
            try
            {
                var (mcScore, mcVotes) = TryGetMetacriticScoreVotes(env);
                res.MetacriticMustSee = (mcScore > 80) && (mcVotes >= 14);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "WebExtras: Metacritic Must-See legacy fallback failed for type={Type} tmdbId={TmdbId}", type, tmdbId);
            }
        }

        // ---- AniList meanScore (0..100) ----
        // We deliberately DO NOT use Wikidata. We query AniList live using title+year.
        if (needAniRefresh)
        {
            int? anilistScore = null;

            if (!string.IsNullOrWhiteSpace(title) && year.HasValue && year.Value > 0)
            {
                try
                {
                    var score = await AniListTrySearchMeanScoreAsync(http, title!, year.Value, cancellationToken).ConfigureAwait(false);
                    if (score.HasValue && score.Value > 0)
                    {
                        anilistScore = score.Value;
                    }
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "WebExtras: AniList failed for {Title} ({Year})", title, year);
                }
            }

            cachedExtras.Data.AniListCachedAtUtc = now;
            cachedExtras.Data.HasAniListScore = true;
            cachedExtras.Data.AniListScore = anilistScore;

            res.AniListScore = anilistScore;
            extrasChanged = true;
        }

        if (extrasChanged)
        {
            await extrasCache.SaveAsync(extrasCacheKey, cachedExtras, cancellationToken).ConfigureAwait(false);
        }

        return Ok(res);
    }


private static string? TryGetRottenTomatoesPath(MdbListCacheStore.CacheEnvelope env, ILogger log)
{
    string? rtPath = null;

    try
    {
        if (env.Data?.Ratings is not null)
        {
            foreach (var r in env.Data.Ratings)
            {
                var src = (r.Source ?? string.Empty).Trim();
                if (!src.Equals("tomatoes", StringComparison.OrdinalIgnoreCase)
                    && !src.Equals("popcorn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(r.Url))
                {
                    rtPath = r.Url;
                    break;
                }
            }
        }

        // Backward compatibility for old cache entries that did not persist rating URLs.
        if (string.IsNullOrWhiteSpace(rtPath) && !string.IsNullOrWhiteSpace(env.RawJson))
        {
            using var doc = JsonDocument.Parse(env.RawJson);
            if (doc.RootElement.TryGetProperty("ratings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var src = el.TryGetProperty("source", out var sourceEl) && sourceEl.ValueKind == JsonValueKind.String
                        ? sourceEl.GetString()
                        : null;

                    if (!string.Equals(src, "tomatoes", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(src, "popcorn", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (el.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                    {
                        rtPath = urlEl.GetString();
                        if (!string.IsNullOrWhiteSpace(rtPath))
                        {
                            break;
                        }
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        log.LogDebug(ex, "WebExtras: failed to extract RottenTomatoes URL from MDBList cache");
    }

    if (!string.IsNullOrWhiteSpace(rtPath) && Regex.IsMatch(rtPath.Trim(), "^[0-9]+$"))
    {
        return null;
    }

    return rtPath;
}

private static string? BuildSafeRottenTomatoesUrl(string? rtPath)
{
    if (string.IsNullOrWhiteSpace(rtPath))
    {
        return null;
    }

    var value = rtPath.Trim();
    if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
    {
        if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = absolute.Host;
        if (!string.Equals(host, "rottentomatoes.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".rottentomatoes.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return absolute.ToString();
    }

    if (!value.StartsWith("/", StringComparison.Ordinal))
    {
        value = "/" + value;
    }

    return Uri.TryCreate("https://www.rottentomatoes.com" + value, UriKind.Absolute, out var relativeResolved)
        ? relativeResolved.ToString()
        : null;
}

private static (double score, int votes) TryGetMetacriticScoreVotes(MdbListCacheStore.CacheEnvelope env)
{
    // Prefer strongly-typed MDBList cache data.
    try
    {
        var r = env.Data?.Ratings?.FirstOrDefault(x =>
            string.Equals(x.Source, "metacritic", StringComparison.OrdinalIgnoreCase));

        var score = r?.Score ?? r?.Value ?? 0d;
        var votes = r?.Votes ?? 0;

        if (score > 0 && votes > 0)
        {
            return (score, votes);
        }
    }
    catch
    {
        // Ignore and fall back to raw MDBList JSON.
    }

    if (!string.IsNullOrWhiteSpace(env.RawJson))
    {
        try
        {
            using var doc = JsonDocument.Parse(env.RawJson);
            if (doc.RootElement.TryGetProperty("ratings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var src = el.TryGetProperty("source", out var sourceEl) && sourceEl.ValueKind == JsonValueKind.String
                        ? sourceEl.GetString()
                        : null;
                    if (!string.Equals(src, "metacritic", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var score = ReadDouble(el, "score") ?? ReadDouble(el, "value") ?? 0d;
                    var votes = ReadInt(el, "votes") ?? 0;
                    return (score, votes);
                }
            }
        }
        catch
        {
            // Ignore malformed legacy cache JSON.
        }
    }

    return (0d, 0);
}

private static double? ReadDouble(JsonElement obj, string prop)
{
    if (!obj.TryGetProperty(prop, out var value))
    {
        return null;
    }

    try
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var d) ? d : (double?)null,
            JsonValueKind.String => double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : (double?)null,
            _ => null
        };
    }
    catch
    {
        return null;
    }
}

private static int? ReadInt(JsonElement obj, string prop)
{
    if (!obj.TryGetProperty(prop, out var value))
    {
        return null;
    }

    try
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : (int?)null,
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : (int?)null,
            _ => null
        };
    }
    catch
    {
        return null;
    }
}


private static void ApplyCachedExtras(WebExtrasResponse res, WebExtrasCacheStore.WebExtrasCachedData? data)
{
    if (data is null)
    {
        return;
    }

    if (data.HasRtCriticsCertified)
    {
        res.RtCriticsCertified = data.RtCriticsCertified;
    }

    if (data.HasRtAudienceVerified)
    {
        res.RtAudienceVerified = data.RtAudienceVerified;
    }

    if (data.HasAniListScore)
    {
        res.AniListScore = data.AniListScore;
    }
}

private static bool HasFreshRottenTomatoesExtras(
    WebExtrasCacheStore.WebExtrasCachedData? data,
    bool wantTc,
    bool wantRv,
    DateTimeOffset now,
    TimeSpan ttl)
{
    if (!(wantTc || wantRv))
    {
        return true;
    }

    if (data?.RottenTomatoesCachedAtUtc is not { } cachedAt)
    {
        return false;
    }

    if ((now - cachedAt) > ttl)
    {
        return false;
    }

    return (!wantTc || data.HasRtCriticsCertified)
        && (!wantRv || data.HasRtAudienceVerified);
}

private static bool HasFreshAniListExtra(
    WebExtrasCacheStore.WebExtrasCachedData? data,
    DateTimeOffset now,
    TimeSpan ttl)
{
    if (data?.AniListCachedAtUtc is not { } cachedAt)
    {
        return false;
    }

    if ((now - cachedAt) > ttl)
    {
        return false;
    }

    return data.HasAniListScore;
}

private static TimeSpan GetWebExtrasTtl(PluginConfiguration cfg)
{
    if (cfg.CacheInterval != PluginConfiguration.CacheIntervalPreset.Unset)
    {
        return cfg.CacheInterval switch
        {
            PluginConfiguration.CacheIntervalPreset.Week => TimeSpan.FromDays(7),
            PluginConfiguration.CacheIntervalPreset.Month => TimeSpan.FromDays(30),
            PluginConfiguration.CacheIntervalPreset.Custom => TimeSpan.FromDays(Math.Max(1, cfg.CacheCustomDays)),
            _ => TimeSpan.FromDays(1)
        };
    }

    var h = cfg.CacheHours <= 0 ? 24 : cfg.CacheHours;
    if (h >= 24 * 30)
    {
        return TimeSpan.FromDays(30);
    }

    if (h >= 24 * 7)
    {
        return TimeSpan.FromDays(7);
    }

    return TimeSpan.FromDays(1);
}

    private sealed class RtScorecard
    {
        [JsonPropertyName("criticsScore")]
        public RtCriticsScore? CriticsScore { get; set; }
    }

    private sealed class RtCriticsScore
    {
        [JsonPropertyName("certified")]
        public bool Certified { get; set; }
    }

    private static async Task<int?> AniListTrySearchMeanScoreAsync(HttpClient http, string title, int year, CancellationToken ct)
    {
        // Fetch a small page of results, then match by year + (prefer exact title match across romaji/english/native).
        var payload = new
        {
            query =
                "query($search:String){ Page(page:1, perPage:10){ media(search:$search, type:ANIME){ meanScore startDate{year} title{romaji english native} } } }",
            variables = new { search = title }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("Page", out var page)) return null;
        if (!page.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return null;

        static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

        var wanted = Norm(title);

        int? best = null;

        foreach (var m in media.EnumerateArray())
        {
            var y = m.TryGetProperty("startDate", out var sd) &&
                    sd.TryGetProperty("year", out var yy) &&
                    yy.ValueKind == JsonValueKind.Number
                ? yy.GetInt32()
                : (int?)null;

            if (y != year) continue;

            var mean = m.TryGetProperty("meanScore", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 0;
            if (mean <= 0) continue;

            bool exact = false;
            if (m.TryGetProperty("title", out var tt))
            {
                foreach (var key in new[] { "romaji", "english", "native" })
                {
                    if (tt.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        if (Norm(v.GetString() ?? "") == wanted) { exact = true; break; }
                    }
                }
            }

            if (exact) return mean;
            best ??= mean;
        }

        return best;
    }
}
