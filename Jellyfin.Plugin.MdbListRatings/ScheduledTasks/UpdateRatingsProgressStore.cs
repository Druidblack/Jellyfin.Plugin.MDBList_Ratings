using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.ScheduledTasks;

/// <summary>
/// Persists continuation state for quota-limited provider backfills.
/// </summary>
internal sealed class UpdateRatingsProgressStore
{
    private sealed class StateDoc
    {
        public Guid? OmdbEpisodeResumeItemId { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private StateDoc _state = new() { UpdatedAtUtc = DateTimeOffset.UtcNow };

    public UpdateRatingsProgressStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public Guid? OmdbEpisodeResumeItemId => _state.OmdbEpisodeResumeItemId;

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
                var loaded = await JsonSerializer.DeserializeAsync<StateDoc>(fs, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (loaded is not null)
                {
                    _state = loaded;
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ratings task continuation state.");
        }
    }

    public async Task SetOmdbEpisodeResumeAsync(Guid? itemId, CancellationToken cancellationToken)
    {
        _state.OmdbEpisodeResumeItemId = itemId;
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
                    await JsonSerializer.SerializeAsync(fs, _state, JsonOptions, cancellationToken).ConfigureAwait(false);
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
            _logger.LogWarning(ex, "Failed to write ratings task continuation state.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}
