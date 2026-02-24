using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MdbListRatings.Web;

/// <summary>
/// Stream-level transformer used when registering transformations directly via
/// <c>IWebFileTransformationWriteService</c> (from jellyfin-plugin-file-transformation).
/// </summary>
internal static class WebUiStreamTransformer
{
    public static async Task TransformIndexHtmlStream(string path, Stream contents)
    {
        if (contents is null)
        {
            throw new ArgumentNullException(nameof(contents));
        }

        // Safety guard:
        // Some versions/configurations of jellyfin-plugin-file-transformation treat the provided
        // match string as a regex (where '.' matches any character). In that case, a pattern like
        // "index.html" may unintentionally match files such as "playback-video-index-html.*.js".
        // We must *only* transform the real Jellyfin Web entry file: "index.html".
        if (!IsExactIndexHtmlPath(path))
        {
            if (contents.CanSeek)
            {
                contents.Seek(0, SeekOrigin.Begin);
            }

            return;
        }

        // Read current HTML.
        string html;
        contents.Seek(0, SeekOrigin.Begin);
        using (var reader = new StreamReader(contents, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        // Transform.
        var pluginId = Plugin.Instance?.Id ?? Guid.Parse("ab96f8b5-45ef-44be-81d6-99bc01e26b9d");
        var transformed = WebUiInjector.TransformIndexHtml(html, pluginId);

        // Write back.
        if (!contents.CanWrite)
        {
            return;
        }

        contents.Seek(0, SeekOrigin.Begin);
        try
        {
            contents.SetLength(0);
        }
        catch
        {
            // Some stream implementations may not support SetLength; best effort.
        }

        using (var writer = new StreamWriter(contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true))
        {
            await writer.WriteAsync(transformed).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        contents.Seek(0, SeekOrigin.Begin);
    }

    private static bool IsExactIndexHtmlPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Strip query/hash just in case we get URL-like paths.
        var p = path;
        var q = p.IndexOfAny(new[] { '?', '#' });
        if (q >= 0)
        {
            p = p.Substring(0, q);
        }

        // Normalize separators and take the last segment.
        p = p.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        var file = slash >= 0 ? p.Substring(slash + 1) : p;

        return string.Equals(file, "index.html", StringComparison.OrdinalIgnoreCase);
    }
}
