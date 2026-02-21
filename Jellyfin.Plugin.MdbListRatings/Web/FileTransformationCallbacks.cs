using System;
using Jellyfin.Plugin.MdbListRatings;

namespace Jellyfin.Plugin.MdbListRatings.Web;

/// <summary>
/// Callbacks invoked by jellyfin-plugin-file-transformation.
/// The File Transformation plugin passes a JSON object that contains the raw file contents.
/// </summary>
public static class FileTransformationCallbacks
{
    /// <summary>
    /// Payload sent by jellyfin-plugin-file-transformation when invoking a callback.
    /// Property name must be <c>contents</c> (case-insensitive).
    /// </summary>
    public sealed class TransformationPayload
    {
        public string? Contents { get; set; }
    }

    /// <summary>
    /// Injects/updates the MDBListRatings script block in Jellyfin Web's index.html.
    /// This method is invoked via reflection by jellyfin-plugin-file-transformation.
    /// </summary>
    /// <param name="payload">Object containing <c>contents</c>.</param>
    /// <returns>Transformed HTML.</returns>
    public static string TransformIndexHtml(TransformationPayload payload)
    {
        var html = payload?.Contents ?? string.Empty;
        var pluginId = Plugin.Instance?.Id ?? Guid.Parse("ab96f8b5-45ef-44be-81d6-99bc01e26b9d");
        return WebUiInjector.TransformIndexHtml(html, pluginId);
    }
}
