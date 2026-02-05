using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MdbListRatings.Api;

/// <summary>
/// Serves plugin-bundled image assets (rating provider icons) so Jellyfin Web can load them locally.
/// </summary>
[ApiController]
[Route("Plugins/MdbListRatings/Assets")]
public sealed class AssetsController : ControllerBase
{
    private static readonly Lazy<Dictionary<string, string>> ResourceMap = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in names)
        {
            // Resources embedded from /Assets/*.*
            if (!n.Contains(".Assets.", StringComparison.Ordinal))
            {
                continue;
            }

            var idx = n.LastIndexOf(".Assets.", StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var file = n.Substring(idx + ".Assets.".Length);
            if (!dict.ContainsKey(file))
            {
                dict[file] = n;
            }
        }

        return dict;
    });

    [HttpGet("{fileName}")]
    public IActionResult Get([FromRoute] string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 128)
        {
            return NotFound();
        }

        // Simple traversal protection
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        // Allow only a safe character set
        foreach (var ch in fileName)
        {
            var ok = char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-';
            if (!ok)
            {
                return NotFound();
            }
        }

        var map = ResourceMap.Value;
        if (!map.TryGetValue(fileName, out var resName))
        {
            return NotFound();
        }

        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream(resName);
        if (stream is null)
        {
            return NotFound();
        }

        var contentType = GetContentType(fileName) ?? "application/octet-stream";

        // Cache aggressively; icons are versioned by plugin update.
        Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        return File(stream, contentType);
    }

    private static string? GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => null
        };
    }
}
