using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Web;

/// <summary>
/// Registers an in-memory transformation for jellyfin-web/index.html via
/// <c>jellyfin-plugin-file-transformation</c>.
///
/// This avoids writing to <c>/usr/share/jellyfin/web/index.html</c> (which fails when Jellyfin
/// runs under a non-root account, e.g. systemd hardening, containers, NAS packages, etc.).
/// </summary>
internal static class FileTransformationIntegration
{
    private const string FileTransformationAssemblyName = "Jellyfin.Plugin.FileTransformation";

    // Used to select the *actual* File Transformation plugin assembly in environments where
    // multiple AssemblyLoadContexts are involved.
    private const string FileTransformationPluginTypeName = "Jellyfin.Plugin.FileTransformation.FileTransformationPlugin";

    // Types from jellyfin-plugin-file-transformation.
    private const string WebWriteServiceTypeName = "Jellyfin.Plugin.FileTransformation.Library.IWebFileTransformationWriteService";
    private const string TransformFileDelegateTypeName = "Jellyfin.Plugin.FileTransformation.Library.TransformFile";

    /// <summary>
    /// Attempts to register/update a transformation for <c>index.html</c> using the File Transformation plugin.
    /// </summary>
    /// <returns><c>true</c> if registration was attempted (plugin found), otherwise <c>false</c>.</returns>
    public static bool TryRegisterIndexHtmlTransformation(Guid transformationId, IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            // Resolve IWebFileTransformationWriteService from the *host* service provider.
            // IMPORTANT: Jellyfin loads each plugin in its own AssemblyLoadContext. If we accidentally load a second copy
            // of Jellyfin.Plugin.FileTransformation into *our* context, DI won't match the service type and GetService will
            // return null. Therefore, we only ever use already-loaded assemblies and we prefer the assembly that actually
            // contains FileTransformationPlugin.Instance.
            var ftAssembly = FindFileTransformationAssembly();

            var writeServiceType = ftAssembly?.GetType(WebWriteServiceTypeName, throwOnError: false, ignoreCase: false)
                                ?? GetTypeFromLoadedAssemblies(WebWriteServiceTypeName, FileTransformationAssemblyName);
            var transformDelegateType = ftAssembly?.GetType(TransformFileDelegateTypeName, throwOnError: false, ignoreCase: false)
                                       ?? GetTypeFromLoadedAssemblies(TransformFileDelegateTypeName, FileTransformationAssemblyName);

            if (writeServiceType is null || transformDelegateType is null)
            {
                logger.LogInformation(
                    "MDBListRatings: File Transformation plugin is not installed; Web UI enhancements are disabled. " +
                    "Install 'jellyfin-plugin-file-transformation' to enable in-memory index.html modifications.");
                return false;
            }

            var writeService = serviceProvider.GetService(writeServiceType);
            if (writeService is null)
            {
                logger.LogWarning(
                    "MDBListRatings: File Transformation types found, but IWebFileTransformationWriteService is not available from the host container. " +
                    "This usually indicates an AssemblyLoadContext type mismatch or an incompatible File Transformation plugin build. " +
                    "Web UI enhancements are disabled.");
                return false;
            }

            var method = typeof(WebUiStreamTransformer).GetMethod(
                nameof(WebUiStreamTransformer.TransformIndexHtmlStream),
                BindingFlags.Public | BindingFlags.Static);

            if (method is null)
            {
                logger.LogWarning("MDBListRatings: WebUiStreamTransformer.TransformIndexHtmlStream not found; Web UI enhancements are disabled.");
                return false;
            }

            var transformDelegate = Delegate.CreateDelegate(transformDelegateType, method);

            // Prefer UpdateTransformation (idempotent). Fallback to AddTransformation.
            var update = writeServiceType.GetMethod("UpdateTransformation", BindingFlags.Public | BindingFlags.Instance);
            if (update is not null)
            {
                update.Invoke(writeService, new object[] { transformationId, "index.html", transformDelegate });
            }
            else
            {
                var add = writeServiceType.GetMethod("AddTransformation", BindingFlags.Public | BindingFlags.Instance);
                add?.Invoke(writeService, new object[] { transformationId, "index.html", transformDelegate });
            }

            logger.LogInformation("MDBListRatings: registered in-memory index.html transformation via File Transformation plugin.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MDBListRatings: failed to register index.html transformation via File Transformation plugin.");
            return false;
        }
    }

    /// <summary>
    /// Attempts to unregister the previously-registered transformation.
    /// </summary>
    public static void TryUnregisterIndexHtmlTransformation(Guid transformationId, IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            var writeServiceType = GetTypeFromLoadedAssemblies(WebWriteServiceTypeName, FileTransformationAssemblyName);
            if (writeServiceType is null)
            {
                return;
            }

            var writeService = serviceProvider.GetService(writeServiceType);
            if (writeService is null)
            {
                return;
            }

            var remove = writeServiceType.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Instance);
            remove?.Invoke(writeService, new object[] { transformationId });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MDBListRatings: failed to unregister index.html transformation (non-fatal).");
        }
    }

    private static Type? GetTypeFromLoadedAssemblies(string fullName, string assemblyName)
    {
        // ONLY scan already-loaded assemblies.
        // Do NOT call Type.GetType("..., Jellyfin.Plugin.FileTransformation") because that may load a second copy
        // of the assembly into a different AssemblyLoadContext, causing DI resolution to fail.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (t is not null)
            {
                return t;
            }
        }

        return null;
    }

    private static Assembly? FindFileTransformationAssembly()
    {
        // Prefer the assembly that actually owns FileTransformationPlugin.Instance (real plugin assembly).
        // This helps in cases where multiple ALCs exist and the same assembly name could theoretically appear more than once.
        try
        {
            foreach (var alc in AssemblyLoadContext.All)
            {
                foreach (var asm in alc.Assemblies)
                {
                    if (!string.Equals(asm.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pluginType = asm.GetType(FileTransformationPluginTypeName, throwOnError: false, ignoreCase: false);
                    if (pluginType is null)
                    {
                        continue;
                    }

                    var instanceProp = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    var instance = instanceProp?.GetValue(null);
                    if (instance is not null)
                    {
                        return asm;
                    }
                }
            }
        }
        catch
        {
            // ignore and fall back
        }

        // Fallback: first loaded assembly with the expected name.
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, FileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase));
    }
}
