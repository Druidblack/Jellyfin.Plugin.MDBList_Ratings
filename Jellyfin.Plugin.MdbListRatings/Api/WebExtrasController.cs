using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Web-only "extras" for the Details all-ratings panel.
/// These values are fetched live and are NOT saved anywhere.
/// </summary>
[ApiController]
[Route("Plugins/MdbListRatings")]
public sealed class WebExtrasController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
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
    /// Returns live web-only extras for a TMDb id, based on cached MDBList response.
    /// - RottenTomatoes certified/verified badges: uses MDBList rating "url" to build RT page url.
    /// - Metacritic Must-See: uses MDBList ids.imdb (IMDb criticreviews).
    /// - AniList: uses title+year search against AniList GraphQL (not saved).
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

        // ---- RottenTomatoes: get page url from MDBList ratings[].url (tomatoes/popcorn) ----
        string? rtPath = null;
        try
        {
            if (env.Data?.Ratings is not null)
            {
                foreach (var r in env.Data.Ratings)
                {
                    var src = (r.Source ?? string.Empty).Trim();
                    if (!src.Equals("tomatoes", StringComparison.OrdinalIgnoreCase) &&
                        !src.Equals("popcorn", StringComparison.OrdinalIgnoreCase))
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

            // Backward compatibility: old cache entries may not have Url in Data, but RawJson is available.
            if (string.IsNullOrWhiteSpace(rtPath) && !string.IsNullOrWhiteSpace(env.RawJson))
            {
                using var doc = JsonDocument.Parse(env.RawJson);
                if (doc.RootElement.TryGetProperty("ratings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var src = el.TryGetProperty("source", out var s) ? s.GetString() : null;
                        if (src is null) continue;

                        if (!src.Equals("tomatoes", StringComparison.OrdinalIgnoreCase) &&
                            !src.Equals("popcorn", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            rtPath = u.GetString();
                            if (!string.IsNullOrWhiteSpace(rtPath)) break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "WebExtras: failed to extract RottenTomatoes url path from cache");
        }

        // Defensive: sometimes providers may return numeric "url" values. For RottenTomatoes we only accept
        // URL-like strings (e.g. "/m/..." or full "https://...").
        if (!string.IsNullOrWhiteSpace(rtPath))
        {
            var t = rtPath.Trim();
            if (Regex.IsMatch(t, "^[0-9]+$"))
            {
                rtPath = null;
            }
        }

        string? rtUrl = null;
        if (!string.IsNullOrWhiteSpace(rtPath))
        {
            rtUrl = rtPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? rtPath
                : "https://www.rottentomatoes.com" + (rtPath.StartsWith("/") ? rtPath : "/" + rtPath);
        }

        if ((wantTc || wantRv) && !string.IsNullOrWhiteSpace(rtUrl))
        {
            try
            {
                var html = await http.GetStringAsync(rtUrl, cancellationToken).ConfigureAwait(false);

                // <script id="media-scorecard-json">...</script>
                var m = Regex.Match(html, "<script\\s+id=\"media-scorecard-json\"[^>]*>([\\s\\S]*?)</script>", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var jsonStr = m.Groups[1].Value;

                    if (wantTc)
                    {
                        try
                        {
                            var obj = JsonSerializer.Deserialize<RtScorecard>(jsonStr, JsonOptions);
                            res.RtCriticsCertified = obj?.CriticsScore?.Certified == true;
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    if (wantRv)
                    {
                        // Same heuristic as the original userscript.
                        res.RtAudienceVerified = jsonStr.Contains("POSITIVE\",\"certified\":true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "WebExtras: RottenTomatoes extras failed for {Url}", rtUrl);
            }
        }
// ---- Metacritic Must-See (from MDBList metacritic rating: score + votes) ----
// MDBList typically provides:
//   { "source":"metacritic", "score":100, "votes":16, ... }
// We use: score > 80 AND votes >= 14.
if (wantMc)
{
    try
    {
        var (mcScore, mcVotes) = TryGetMetacriticScoreVotes(env);
        res.MetacriticMustSee = (mcScore > 80) && (mcVotes >= 14);
    }
    catch (Exception ex)
    {
        log.LogDebug(ex, "WebExtras: Metacritic Must-See extraction failed for type={Type} tmdbId={TmdbId}", type, tmdbId);
    }
}

// ---- AniList meanScore (0..100) ----
        // We deliberately DO NOT use Wikidata. We query AniList live using title+year.
        if (wantAl && !string.IsNullOrWhiteSpace(title) && year.HasValue && year.Value > 0)
        {
            try
            {
                var score = await AniListTrySearchMeanScoreAsync(http, title!, year.Value, cancellationToken).ConfigureAwait(false);
                if (score.HasValue && score.Value > 0)
                {
                    res.AniListScore = score.Value;
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "WebExtras: AniList failed for {Title} ({Year})", title, year);
            }
        }

        return Ok(res);
    }


private static (double score, int votes) TryGetMetacriticScoreVotes(MdbListCacheStore.CacheEnvelope env)
{
    // Prefer strongly-typed cached data.
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
        // ignore and fall back to RawJson
    }

    // Fallback: parse RawJson to survive schema drift.
    if (!string.IsNullOrWhiteSpace(env.RawJson))
    {
        try
        {
            using var doc = JsonDocument.Parse(env.RawJson);
            if (doc.RootElement.TryGetProperty("ratings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var src = el.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
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
            // ignore
        }
    }

    return (0d, 0);
}

private static double? ReadDouble(JsonElement obj, string prop)
{
    if (!obj.TryGetProperty(prop, out var v))
    {
        return null;
    }

    try
    {
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : (double?)null,
            JsonValueKind.String => double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2) ? d2 : (double?)null,
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
    if (!obj.TryGetProperty(prop, out var v))
    {
        return null;
    }

    try
    {
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int?)null,
            JsonValueKind.String => int.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var i2) ? i2 : (int?)null,
            _ => null
        };
    }
    catch
    {
        return null;
    }
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

    private static int TryExtractImdbMetascore(string html)
    {
        foreach (var rx in new[]
        {
            // New-ish IMDb markup (hashed class names)
			new Regex(@"<div[^>]*class=""[^""]*sc-88e7efde-1[^""]*""[^>]*>\s*(\d{1,3})\s*</div>", RegexOptions.IgnoreCase),

            // Older / alternative patterns
			new Regex(@"data-testid=""critic-reviews-metascore""[^>]*>\s*(\d{1,3})\s*<", RegexOptions.IgnoreCase),
			new Regex(@"aria-label=""Metascore\s*(\d{1,3})""", RegexOptions.IgnoreCase),
			new Regex(@"""metascore""\s*:\s*(\d{1,3})", RegexOptions.IgnoreCase),
			new Regex(@"Metascore\s*</[^>]+>\s*<[^>]+>\s*(\d{1,3})\s*<", RegexOptions.IgnoreCase),
        })
        {
            var m = rx.Match(html);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) return v;
        }
        return 0;
    }

    private static int TryExtractImdbCriticCount(string html)
    {
        foreach (var rx in new[]
        {
            // New-ish IMDb markup (hashed class names) containing e.g. "22 reviews · Provided by Metacritic.com"
			new Regex(@"<div[^>]*class=""[^""]*sc-88e7efde-4[^""]*""[^>]*>\s*(\d+)\s*reviews", RegexOptions.IgnoreCase),

            // More stable: look for the Metacritic attribution link and capture the preceding review count
			new Regex(@"(\d+)\s*reviews\s*·\s*Provided by\s*<a[^>]*href=""https://www\.metacritic\.com/", RegexOptions.IgnoreCase),

            // Older / alternative patterns
			new Regex(@"Based on\s*(\d+)\s*critic reviews", RegexOptions.IgnoreCase),
			new Regex(@"(\d+)\s*critic reviews", RegexOptions.IgnoreCase),
			new Regex(@"""criticReviewCount""\s*:\s*(\d+)", RegexOptions.IgnoreCase),
        })
        {
            var m = rx.Match(html);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) return v;
        }
        return 0;
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
