using System.Text.Json.Serialization;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Read-only subset of plugin settings that is safe to expose to authenticated web clients.
/// This avoids using the admin-only plugin configuration endpoint from the browser.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/MdbListRatings")]
public sealed class WebClientSettingsController : ControllerBase
{
    [HttpGet("WebClientSettings")]
    [Produces("application/json")]
    public ActionResult<WebClientSettingsResponse> Get()
    {
        var plugin = Plugin.Instance;
        var cfg = plugin?.Configuration;

        if (cfg is null)
        {
            return Ok(new WebClientSettingsResponse());
        }

        return Ok(new WebClientSettingsResponse
        {
            EnableWebRatingSourceIcon = cfg.EnableWebRatingSourceIcon,
            EnableWebAllRatingsFromCache = cfg.EnableWebAllRatingsFromCache,
            WebAllRatingsMode = cfg.WebAllRatingsMode,
            WebAllRatingsOrderCsv = cfg.WebAllRatingsOrderCsv,
            EnableWebExtraTomatoesCertified = cfg.EnableWebExtraTomatoesCertified,
            EnableWebExtraRottenVerified = cfg.EnableWebExtraRottenVerified,
            EnableWebExtraMetacriticMustSee = cfg.EnableWebExtraMetacriticMustSee,
            EnableWebExtraAniList = cfg.EnableWebExtraAniList,
            EnableWebClickableRatingIcons = cfg.EnableWebClickableRatingIcons,
            EnableImdbTop250Icon = cfg.EnableImdbTop250Icon,
            EnableWebAwardBadges = cfg.EnableWebAwardBadges,
            WebAwardKeysCsv = cfg.WebAwardKeysCsv,
            EnableWebAwardNominationsBadge = cfg.EnableWebAwardNominationsBadge
        });
    }

    public sealed class WebClientSettingsResponse
    {
        [JsonPropertyName("enableWebRatingSourceIcon")]
        public bool EnableWebRatingSourceIcon { get; set; } = true;

        [JsonPropertyName("enableWebAllRatingsFromCache")]
        public bool EnableWebAllRatingsFromCache { get; set; }

        [JsonPropertyName("webAllRatingsMode")]
        public string WebAllRatingsMode { get; set; } = "all";

        [JsonPropertyName("webAllRatingsOrderCsv")]
        public string WebAllRatingsOrderCsv { get; set; } = string.Empty;

        [JsonPropertyName("enableWebExtraTomatoesCertified")]
        public bool EnableWebExtraTomatoesCertified { get; set; }

        [JsonPropertyName("enableWebExtraRottenVerified")]
        public bool EnableWebExtraRottenVerified { get; set; }

        [JsonPropertyName("enableWebExtraMetacriticMustSee")]
        public bool EnableWebExtraMetacriticMustSee { get; set; }

        [JsonPropertyName("enableWebExtraAniList")]
        public bool EnableWebExtraAniList { get; set; }

        [JsonPropertyName("enableWebClickableRatingIcons")]
        public bool EnableWebClickableRatingIcons { get; set; }

        [JsonPropertyName("enableImdbTop250Icon")]
        public bool EnableImdbTop250Icon { get; set; }

        [JsonPropertyName("enableWebAwardBadges")]
        public bool EnableWebAwardBadges { get; set; }

        [JsonPropertyName("webAwardKeysCsv")]
        public string WebAwardKeysCsv { get; set; } = string.Empty;

        [JsonPropertyName("enableWebAwardNominationsBadge")]
        public bool EnableWebAwardNominationsBadge { get; set; }
    }
}
