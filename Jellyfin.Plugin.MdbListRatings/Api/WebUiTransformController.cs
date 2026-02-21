using System;
using Jellyfin.Plugin.MdbListRatings;
using Jellyfin.Plugin.MdbListRatings.Web;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Optional fallback endpoint for jellyfin-plugin-file-transformation.
/// The primary path is the callback-based transformation (no HTTP roundtrip),
/// but the File Transformation plugin requires an endpoint field in its payload model.
/// </summary>
[ApiController]
[Route("Plugins/MdbListRatings/Transform")]
public sealed class WebUiTransformController : ControllerBase
{
    [HttpPost("index.html")]
    [Produces("text/html")]
    public ActionResult TransformIndexHtml([FromBody] FileTransformationCallbacks.TransformationPayload payload)
    {
        var html = payload?.Contents ?? string.Empty;
        var pluginId = Plugin.Instance?.Id ?? Guid.Parse("ab96f8b5-45ef-44be-81d6-99bc01e26b9d");
        var transformed = WebUiInjector.TransformIndexHtml(html, pluginId);
        return Content(transformed, "text/html; charset=utf-8");
    }
}
