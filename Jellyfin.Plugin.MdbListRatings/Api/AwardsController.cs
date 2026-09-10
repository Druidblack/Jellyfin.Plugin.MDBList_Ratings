using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Read-only API for award datasets used by the Web UI.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/MdbListRatings")]
public sealed class AwardsController : ControllerBase
{
    [HttpGet("AwardsDefinitions")]
    [Produces("application/json")]
    public ActionResult<AwardsDefinitionsResponse> GetDefinitions()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new AwardsDefinitionsResponse());
        }

        var awards = plugin.Awards.GetDefinitions()
            .Select(x => new AwardDefinitionDto
            {
                Key = x.Key,
                Name = x.Name,
                IconFile = x.IconFile,
                EntryCount = x.EntryCount,
                IsBuiltIn = x.IsBuiltIn
            })
            .ToList();

        return Ok(new AwardsDefinitionsResponse
        {
            Awards = awards,
            CustomAwardsDirectory = plugin.Awards.CustomAwardsDirectory,
            CustomIconsDirectory = plugin.Awards.CustomIconsDirectory
        });
    }

    [HttpGet("AwardsByImdb")]
    [Produces("application/json")]
    public ActionResult<AwardsByImdbResponse> GetAwardsByImdb(
        [FromQuery] string imdbId,
        [FromQuery] string? keys)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new AwardsByImdbResponse());
        }

        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return BadRequest("Missing required query parameter: imdbId");
        }

        var selectedKeys = ParseCsv(keys);
        var nominationSummary = plugin.Awards.GetNominationSummaryByImdb(imdbId, selectedKeys);
        var badges = plugin.Awards.FindAwardsByImdb(imdbId, selectedKeys)
            .Select(x => new AwardBadgeDto
            {
                Key = x.Key,
                Name = x.Name,
                IconFile = x.IconFile,
                Tooltip = x.Tooltip,
                AwardYears = x.AwardYears,
                WonCategories = x.WonCategories
            })
            .ToList();

        return Ok(new AwardsByImdbResponse
        {
            HasAwards = badges.Count > 0,
            HasNominations = nominationSummary is not null && nominationSummary.HasNominations,
            NominationSummary = nominationSummary is null ? null : new AwardNominationSummaryDto
            {
                Name = nominationSummary.Name,
                Tooltip = nominationSummary.Tooltip,
                AwardCount = nominationSummary.AwardCount,
                CategoryCount = nominationSummary.CategoryCount
            },
            Badges = badges
        });
    }

    private static IReadOnlyList<string> ParseCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public sealed class AwardsDefinitionsResponse
    {
        [JsonPropertyName("awards")]
        public List<AwardDefinitionDto> Awards { get; set; } = new();

        [JsonPropertyName("customAwardsDirectory")]
        public string? CustomAwardsDirectory { get; set; }

        [JsonPropertyName("customIconsDirectory")]
        public string? CustomIconsDirectory { get; set; }
    }

    public sealed class AwardDefinitionDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("iconFile")]
        public string? IconFile { get; set; }

        [JsonPropertyName("entryCount")]
        public int EntryCount { get; set; }

        [JsonPropertyName("isBuiltIn")]
        public bool IsBuiltIn { get; set; }
    }

    public sealed class AwardsByImdbResponse
    {
        [JsonPropertyName("hasAwards")]
        public bool HasAwards { get; set; }

        [JsonPropertyName("hasNominations")]
        public bool HasNominations { get; set; }

        [JsonPropertyName("nominationSummary")]
        public AwardNominationSummaryDto? NominationSummary { get; set; }

        [JsonPropertyName("badges")]
        public List<AwardBadgeDto> Badges { get; set; } = new();
    }

    public sealed class AwardNominationSummaryDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("tooltip")]
        public string Tooltip { get; set; } = string.Empty;

        [JsonPropertyName("awardCount")]
        public int AwardCount { get; set; }

        [JsonPropertyName("categoryCount")]
        public int CategoryCount { get; set; }
    }

    public sealed class AwardBadgeDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("iconFile")]
        public string? IconFile { get; set; }

        [JsonPropertyName("tooltip")]
        public string Tooltip { get; set; } = string.Empty;

        [JsonPropertyName("awardYears")]
        public List<string> AwardYears { get; set; } = new();

        [JsonPropertyName("wonCategories")]
        public List<string> WonCategories { get; set; } = new();
    }
}
