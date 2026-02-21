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
}
