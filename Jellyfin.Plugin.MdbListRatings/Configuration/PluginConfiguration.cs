using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MdbListRatings.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public enum CacheIntervalPreset
    {
        /// <summary>
        /// Not set (legacy configs may only have <see cref="CacheHours"/>).
        /// </summary>
        Unset = 0,

        Day = 1,
        Week = 2,
        Month = 3
    }

    /// <summary>
    /// Gets or sets the MDBList API key.
    /// </summary>
    public string MdbListApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which MDBList rating source is written to Jellyfin Community Rating for Movies.
    /// Example: imdb, tmdb, trakt, tomatoes, popcorn, letterboxd...
    /// </summary>
    public string MovieCommunitySource { get; set; } = "imdb";

    /// <summary>
    /// Optional fallback rating source for Movie Community Rating.
    /// Used when the primary source has no data (null).
    /// Set to "none" (or empty) to disable.
    /// </summary>
    public string MovieCommunityFallbackSource { get; set; } = "none";

    /// <summary>
    /// Gets or sets which MDBList rating source is written to Jellyfin Critic Rating for Movies.
    /// Example: metacritic, rogerebert...
    /// </summary>
    public string MovieCriticSource { get; set; } = "metacritic";

    /// <summary>
    /// Optional fallback rating source for Movie Critic Rating.
    /// Used when the primary source has no data (null).
    /// Set to "none" (or empty) to disable.
    /// </summary>
    public string MovieCriticFallbackSource { get; set; } = "none";

    /// <summary>
    /// Gets or sets which MDBList rating source is written to Jellyfin Community Rating for Series.
    /// </summary>
    public string ShowCommunitySource { get; set; } = "tmdb";

    /// <summary>
    /// Optional fallback rating source for Series Community Rating.
    /// Used when the primary source has no data (null).
    /// Set to "none" (or empty) to disable.
    /// </summary>
    public string ShowCommunityFallbackSource { get; set; } = "none";

    /// <summary>
    /// If enabled, the plugin will only write ratings when the target field is empty/null/0.
    /// </summary>
    public bool UpdateOnlyWhenEmpty { get; set; } = true;

    /// <summary>
    /// Cache duration (hours) for MDBList responses to avoid unnecessary requests.
    /// </summary>
    public int CacheHours { get; set; } = 24;

    /// <summary>
    /// Cache refresh interval preset. Preferred over <see cref="CacheHours"/>.
    /// </summary>
    public CacheIntervalPreset CacheInterval { get; set; } = CacheIntervalPreset.Unset;

    /// <summary>
    /// Optional delay between requests to help avoid rate limits when updating many items.
    /// </summary>
    public int RequestDelayMs { get; set; } = 0;

    /// <summary>
    /// If enabled, Jellyfin Web UI will display a small icon of the rating provider
    /// (e.g. IMDb/TMDb/MAL) next to the built-in rating star.
    /// This affects only the web interface; native clients are not modified.
    /// </summary>
    public bool EnableWebRatingSourceIcon { get; set; } = true;

    /// <summary>
    /// If enabled, Jellyfin Web "Details" page will hide the standard Community/Critic
    /// rating blocks and show all available ratings found in the MDBList cache instead.
    /// This affects only the web interface; no metadata is written.
    /// </summary>
    public bool EnableWebAllRatingsFromCache { get; set; } = false;

    /// <summary>
    /// Controls which ratings are shown in the Web "Details" all-ratings panel.
    /// - "all": show all cached ratings returned by MDBList.
    /// - "custom": show only sources listed in <see cref="WebAllRatingsOrderCsv"/> (in that order).
    /// </summary>
    public string WebAllRatingsMode { get; set; } = "all";

    /// <summary>
    /// Custom order/filter list for the Web "Details" all-ratings panel.
    /// Comma/newline separated list of rating sources (e.g. "imdb,tmdb,metacritic,metacriticuser,rotten_tomatoes").
    /// Used only when <see cref="WebAllRatingsMode"/> is "custom".
    /// </summary>
    public string WebAllRatingsOrderCsv { get; set; } = string.Empty;

    

/// <summary>
/// (Web-only) In the Details "All ratings" panel:
/// show RottenTomatoes "Certified Fresh" badge for Tomatoes when available.
/// Visual only; nothing is saved.
/// </summary>
public bool EnableWebExtraTomatoesCertified { get; set; } = false;

/// <summary>
/// (Web-only) In the Details "All ratings" panel:
/// show RottenTomatoes "Verified Hot" badge for audience (popcorn) when available.
/// Visual only; nothing is saved.
/// </summary>
public bool EnableWebExtraRottenVerified { get; set; } = false;

/// <summary>
/// (Web-only) In the Details "All ratings" panel:
/// show Metacritic "Must-See" badge when heuristics match.
/// Visual only; nothing is saved.
/// </summary>
public bool EnableWebExtraMetacriticMustSee { get; set; } = false;

/// <summary>
/// (Web-only) In the Details "All ratings" panel:
/// show AniList meanScore (0..100). Visual only; nothing is saved.
/// </summary>
public bool EnableWebExtraAniList { get; set; } = false;

    /// <summary>
    /// (Web-only) Make rating icons in the Details "All ratings" panel clickable and link to the provider page when a URL/ID is available.
    /// Visual only; nothing is saved.
    /// </summary>
    public bool EnableWebClickableRatingIcons { get; set; } = false;

/// <summary>
    /// Optional per-library overrides.
    /// If an item belongs to a library listed here, the corresponding mapping will be used
    /// instead of the global mapping.
    /// </summary>
    public List<LibraryRatingOverride> LibraryOverrides { get; set; } = new();

    /// <summary>
    /// Per-library rating mapping override.
    /// </summary>
    public sealed class LibraryRatingOverride
    {
        /// <summary>
        /// Jellyfin library (collection folder) Id.
        /// This is usually the <c>ItemId</c> from <c>/Library/VirtualFolders</c>.
        /// </summary>
        public string LibraryId { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable name (optional; used only for display).
        /// </summary>
        public string LibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this override is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Override for Movie community rating source.
        /// Leave empty to use the global setting.
        /// </summary>
        public string MovieCommunitySource { get; set; } = string.Empty;

        /// <summary>
        /// Override for Movie community rating fallback source.
        /// Leave empty to use the global setting.
        /// </summary>
        public string MovieCommunityFallbackSource { get; set; } = string.Empty;

        /// <summary>
        /// Override for Movie critic rating source.
        /// Leave empty to use the global setting.
        /// </summary>
        public string MovieCriticSource { get; set; } = string.Empty;

        /// <summary>
        /// Override for Movie critic rating fallback source.
        /// Leave empty to use the global setting.
        /// </summary>
        public string MovieCriticFallbackSource { get; set; } = string.Empty;

        /// <summary>
        /// Override for Show/Series community rating source.
        /// Leave empty to use the global setting.
        /// </summary>
        public string ShowCommunitySource { get; set; } = string.Empty;

        /// <summary>
        /// Override for Show/Series community rating fallback source.
        /// Leave empty to use the global setting.
        /// </summary>
        public string ShowCommunityFallbackSource { get; set; } = string.Empty;
    }
}
