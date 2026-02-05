using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

internal sealed class RateLimitStateStore
{
    private sealed class StateDoc
    {
        public DateTimeOffset? NotBeforeUtc { get; set; }
        public int? LastLimit { get; set; }
        public int? LastRemaining { get; set; }
        public DateTimeOffset? LastResetUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private StateDoc _state = new() { UpdatedAtUtc = DateTimeOffset.UtcNow };

    public RateLimitStateStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public DateTimeOffset? NotBeforeUtc => _state.NotBeforeUtc;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var fs = File.OpenRead(_path);
                var doc = await JsonSerializer.DeserializeAsync<StateDoc>(fs, GetJsonOptions(), cancellationToken).ConfigureAwait(false);
                if (doc is not null)
                {
                    _state = doc;
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read rate-limit state file");
        }
    }

    public async Task UpdateAsync(int? limit, int? remaining, DateTimeOffset? resetUtc, bool rateLimited, CancellationToken cancellationToken)
    {
        // If we are rate limited, we should not send more requests until reset time.
        if (rateLimited)
        {
            // If reset is unknown, back off for 24h.
            _state.NotBeforeUtc = resetUtc ?? DateTimeOffset.UtcNow.AddHours(24);
        }
        else
        {
            // Clear cooldown if it's in the past.
            if (_state.NotBeforeUtc.HasValue && _state.NotBeforeUtc.Value <= DateTimeOffset.UtcNow)
            {
                _state.NotBeforeUtc = null;
            }
        }

        _state.LastLimit = limit ?? _state.LastLimit;
        _state.LastRemaining = remaining ?? _state.LastRemaining;
        _state.LastResetUtc = resetUtc ?? _state.LastResetUtc;
        _state.UpdatedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var fs = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(fs, _state, GetJsonOptions(), cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                File.Move(tmp, _path);
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write rate-limit state file");
        }
    }

    private static JsonSerializerOptions GetJsonOptions() => new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
