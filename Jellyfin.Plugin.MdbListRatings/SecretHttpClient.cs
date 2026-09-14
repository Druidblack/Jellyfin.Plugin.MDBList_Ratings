namespace Jellyfin.Plugin.MdbListRatings;

/// <summary>
/// Names of HttpClient instances that can carry API credentials in request URLs or headers.
/// These clients are registered without the default IHttpClientFactory loggers so secrets are
/// never emitted as part of request URIs.
/// </summary>
internal static class SecretHttpClient
{
    public const string Name = "MdbListRatings.SecretApi";
    public const string SimklNoRedirectName = "MdbListRatings.SimklNoRedirect";
}
