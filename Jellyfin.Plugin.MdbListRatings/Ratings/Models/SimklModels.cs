using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MdbListRatings.Ratings.Models;

internal sealed class SimklDetailResponse
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("ids")]
    public SimklIds? Ids { get; set; }

    [JsonPropertyName("ratings")]
    public Dictionary<string, SimklRatingValue>? Ratings { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("droprate")]
    public string? DropRate { get; set; }
}

internal sealed class SimklIds
{
    [JsonPropertyName("simkl")]
    [JsonConverter(typeof(NullableIntLenientConverter))]
    public int? Simkl { get; set; }

    [JsonPropertyName("slug")]
    [JsonConverter(typeof(NullableStringLenientConverter))]
    public string? Slug { get; set; }

    [JsonPropertyName("imdb")]
    [JsonConverter(typeof(NullableStringLenientConverter))]
    public string? Imdb { get; set; }

    [JsonPropertyName("tmdb")]
    [JsonConverter(typeof(NullableStringLenientConverter))]
    public string? Tmdb { get; set; }

    [JsonPropertyName("tvdb")]
    [JsonConverter(typeof(NullableStringLenientConverter))]
    public string? Tvdb { get; set; }

    [JsonPropertyName("mal")]
    [JsonConverter(typeof(NullableStringLenientConverter))]
    public string? Mal { get; set; }
}

internal sealed class SimklRatingValue
{
    [JsonPropertyName("rating")]
    [JsonConverter(typeof(NullableDoubleLenientConverter))]
    public double? Rating { get; set; }

    [JsonPropertyName("votes")]
    [JsonConverter(typeof(NullableIntLenientConverter))]
    public int? Votes { get; set; }
}
