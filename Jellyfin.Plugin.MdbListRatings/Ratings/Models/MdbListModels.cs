using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MdbListRatings.Ratings.Models;

public sealed class MdbListTitleResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("ids")]
    public MdbListIds? Ids { get; set; }

    [JsonPropertyName("ratings")]
    public List<MdbListRating> Ratings { get; set; } = new();
}

public sealed class MdbListIds
{
    [JsonPropertyName("tmdb")]
    [JsonConverter(typeof(NullableIntLenientConverter))]
    public int? Tmdb { get; set; }

    [JsonPropertyName("imdb")]
    public string? Imdb { get; set; }
}

public sealed class MdbListRating
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Provider native value (may be null; may be 0-10 or 0-100 depending on provider).
    /// </summary>
    [JsonPropertyName("value")]
    [JsonConverter(typeof(NullableDoubleLenientConverter))]
    public double? Value { get; set; }

    /// <summary>
    /// Normalized score (0-100) - best field to use consistently.
    /// </summary>
    [JsonPropertyName("score")]
    [JsonConverter(typeof(NullableDoubleLenientConverter))]
    public double? Score { get; set; }

    [JsonPropertyName("votes")]
    [JsonConverter(typeof(NullableIntLenientConverter))]
    public int? Votes { get; set; }


    [JsonPropertyName("url")]
    [JsonConverter(typeof(NullableStringLenientConverter))]
    public string? Url { get; set; }
}

/// <summary>
/// MDBList иногда возвращает числа как строки, а иногда ""/"N/A".
/// Эти конвертеры парсят всё "мягко" и вместо исключения возвращают null.
/// </summary>
internal sealed class NullableDoubleLenientConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.TryGetDouble(out var d) ? d : (double?)null,
                JsonTokenType.String => TryParseDouble(reader.GetString()),
                JsonTokenType.Null => null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static double? TryParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();
        if (string.Equals(s, "n/a", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "na", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
    }
}

internal sealed class NullableIntLenientConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.TryGetInt32(out var i) ? i : (int?)null,
                JsonTokenType.String => TryParseInt(reader.GetString()),
                JsonTokenType.Null => null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static int? TryParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();
        if (string.Equals(s, "n/a", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "na", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Иногда приходит "123.0" — пробуем как double.
        if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i))
        {
            return i;
        }

        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (int?)Convert.ToInt32(Math.Round(d, MidpointRounding.AwayFromZero)) : null;
    }
}

/// <summary>
/// MDBList may sometimes return values that are expected to be strings as numbers.
/// This converter prevents hard failures by converting numbers/bools to strings.
/// </summary>
internal sealed class NullableStringLenientConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => ReadNumberAsString(ref reader),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => ReadOtherAsString(ref reader)
            };
        }
        catch
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static string? ReadNumberAsString(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (reader.TryGetDouble(out var d))
        {
            return d.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string ReadOtherAsString(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.ToString();
    }
}
