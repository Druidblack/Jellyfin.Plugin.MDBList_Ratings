using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MdbListRatings.Ratings.Models;

public class WhatsOnApiResult
{
    public MdbListTitleResponse Data { get; set; } = new();
    public bool IsRateLimited { get; set; }
    public int RetryAfterSeconds { get; set; }
    public int StatusCode { get; set; }
}

public class WhatsOnTitleResponse
{
    [JsonPropertyName("item_type")]
    public string? ItemType { get; set; }

    [JsonPropertyName("imdb")]
    public WhatsOnSimpleRating? Imdb { get; set; }

    [JsonPropertyName("tmdb")]
    public WhatsOnSimpleRating? Tmdb { get; set; }

    [JsonPropertyName("trakt")]
    public WhatsOnSimpleRating? Trakt { get; set; }

    [JsonPropertyName("rotten_tomatoes")]
    public WhatsOnRottenTomatoesRating? RottenTomatoes { get; set; }

    [JsonPropertyName("metacritic")]
    public WhatsOnMetacriticRating? Metacritic { get; set; }

    [JsonPropertyName("letterboxd")]
    public WhatsOnSimpleRating? Letterboxd { get; set; }

    [JsonPropertyName("senscritique")]
    public WhatsOnSimpleRating? SensCritique { get; set; }

    [JsonPropertyName("allocine")]
    public WhatsOnAllocineRating? Allocine { get; set; }

    [JsonPropertyName("betaseries")]
    public WhatsOnSimpleRating? BetaSeries { get; set; }

    [JsonPropertyName("tv_time")]
    public WhatsOnSimpleRating? TvTime { get; set; }

    [JsonPropertyName("episodes_details")]
    public WhatsOnEpisodeDetails[]? EpisodesDetails { get; set; }
}

public class WhatsOnEpisodeDetails
{
    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    [JsonPropertyName("id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("users_rating")]
    public float? UsersRating { get; set; }

    [JsonPropertyName("users_rating_count")]
    public int? UsersRatingCount { get; set; }
}

public class WhatsOnSimpleRating
{
    [JsonPropertyName("users_rating")]
    public float? UsersRating { get; set; }
}

public class WhatsOnRottenTomatoesRating
{
    [JsonPropertyName("critics_rating")]
    public float? CriticsRating { get; set; }

    [JsonPropertyName("users_rating")]
    public float? UsersRating { get; set; }
}

public class WhatsOnMetacriticRating
{
    [JsonPropertyName("critics_rating")]
    public float? CriticsRating { get; set; }

    [JsonPropertyName("users_rating")]
    public float? UsersRating { get; set; }
}

public class WhatsOnAllocineRating
{
    [JsonPropertyName("critics_rating")]
    public float? CriticsRating { get; set; }

    [JsonPropertyName("users_rating")]
    public float? UsersRating { get; set; }
}

public class WhatsOnSeasonResponse
{
    [JsonPropertyName("seasons")]
    public WhatsOnSeasonItem[] Seasons { get; set; } = Array.Empty<WhatsOnSeasonItem>();
}

public class WhatsOnSeasonItem
{
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("average_users_rating")]
    public float? AverageUsersRating { get; set; }
}

public class WhatsOnSearchResponse
{
    [JsonPropertyName("results")]
    public WhatsOnTitleResponse[] Results { get; set; } = Array.Empty<WhatsOnTitleResponse>();
}

