using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MdbListRatings.Ratings.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class MdbListCacheStore
{
    private readonly string _cacheDir;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEnvelope> _mem = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    internal sealed class CacheEnvelope
    {
        public DateTimeOffset CachedAtUtc { get; set; }
        public MdbListTitleResponse Data { get; set; } = new();
        public string? RawJson { get; set; }
    }

    public MdbListCacheStore(string cacheDir, ILogger logger)
    {
        _cacheDir = cacheDir;
        _logger = logger;
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<CacheEnvelope?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        if (_mem.TryGetValue(key, out var env))
        {
            return env;
        }

        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var fs = File.OpenRead(path);
                var loaded = await JsonSerializer.DeserializeAsync<CacheEnvelope>(fs, GetJsonOptions(), cancellationToken).ConfigureAwait(false);
                if (loaded is null)
                {
                    return null;
                }

                _mem[key] = loaded;
                return loaded;
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read MDBList cache entry: {Key}", key);
            return null;
        }
    }

    public async Task SaveAsync(string key, CacheEnvelope env, CancellationToken cancellationToken)
    {
        _mem[key] = env;

        var path = GetPath(key);
        var tmp = path + ".tmp";

        try
        {
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_cacheDir);
                await using (var fs = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(fs, env, GetJsonOptions(), cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tmp, path);
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write MDBList cache entry: {Key}", key);
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private string GetPath(string key)
    {
        // Make a reasonably safe filename.
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return Path.Combine(_cacheDir, safe + ".json");
    }

    private static JsonSerializerOptions GetJsonOptions() => new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
