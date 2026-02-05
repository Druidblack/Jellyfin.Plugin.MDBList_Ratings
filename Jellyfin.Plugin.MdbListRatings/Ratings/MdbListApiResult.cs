using System;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class MdbListApiResult
{
    public string Url { get; init; } = string.Empty;
    public int? RateLimitLimit { get; init; }
    public int? RateLimitRemaining { get; init; }
    public DateTimeOffset? RateLimitResetUtc { get; init; }

    public bool IsRateLimited { get; init; }

    public Models.MdbListTitleResponse? Data { get; init; }
    public string? RawJson { get; init; }
}
