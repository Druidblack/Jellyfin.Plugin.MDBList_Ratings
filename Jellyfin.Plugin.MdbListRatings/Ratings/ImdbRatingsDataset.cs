using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Ratings;

/// <summary>
/// Fallback loader for IMDb user ratings based on the official TSV dataset.
/// Used when MDBList returns HTTP 404 for a TMDb title.
/// </summary>
internal sealed class ImdbRatingsDataset
{
    // Official IMDb dataset.
    internal const string DatasetUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _cacheDir;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private long[]? _keys;
    private float[]? _ratings;
    private int[]? _votes;
    private DateTimeOffset? _loadedFromTsvLastWriteUtc;

    public ImdbRatingsDataset(IHttpClientFactory httpClientFactory, string cacheDir, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheDir = cacheDir;
        _logger = logger;

        Directory.CreateDirectory(_cacheDir);
    }

    private string GzPath => Path.Combine(_cacheDir, "imdb-title.ratings.tsv.gz");
    private string TsvPath => Path.Combine(_cacheDir, "imdb-title.ratings.tsv");
    private string MetaPath => Path.Combine(_cacheDir, "imdb-title.ratings.meta.json");

    public readonly record struct RatingInfo(float AverageRating, int Votes);

    public async Task<RatingInfo?> TryGetRatingInfoAsync(string imdbId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var key = ParseImdbNumericId(imdbId);
        if (!key.HasValue)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureDatasetReadyAsync(ttl, cancellationToken).ConfigureAwait(false);
            await EnsureIndexLoadedAsync(cancellationToken).ConfigureAwait(false);

            var keys = _keys;
            var ratings = _ratings;
            var votes = _votes;
            if (keys is null || ratings is null || votes is null || keys.Length == 0)
            {
                return null;
            }

            var idx = Array.BinarySearch(keys, key.Value);
            if (idx < 0)
            {
                return null;
            }

            return new RatingInfo(ratings[idx], votes[idx]);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureDatasetReadyAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        // If we already have an extracted TSV and it is within TTL, use it.
        if (File.Exists(TsvPath) && IsWithinTtl(ttl))
        {
            return;
        }

        // Download + extract.
        Directory.CreateDirectory(_cacheDir);

        var tmpGz = GzPath + ".tmp";
        var tmpTsv = TsvPath + ".tmp";

        try
        {
            _logger.LogWarning("IMDb fallback: downloading {Url}", DatasetUrl);

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(10);

            using (var response = await http.GetAsync(DatasetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tmpGz, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("IMDb fallback: extracting ratings TSV");

            await using (var input = new FileStream(tmpGz, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var output = new FileStream(tmpTsv, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            ReplaceFile(tmpGz, GzPath);
            ReplaceFile(tmpTsv, TsvPath);

            WriteMeta(DateTimeOffset.UtcNow);

            // Invalidate in-memory index if the TSV changed.
            _loadedFromTsvLastWriteUtc = null;
            _keys = null;
            _ratings = null;
            _votes = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IMDb fallback: failed to download/extract dataset");

            // If we have an older TSV, keep using it.
            if (File.Exists(TsvPath))
            {
                return;
            }

            throw;
        }
        finally
        {
            TryDelete(tmpGz);
            TryDelete(tmpTsv);
        }
    }

    private bool IsWithinTtl(TimeSpan ttl)
    {
        try
        {
            var downloadedAt = ReadMetaDownloadedAtUtc();
            if (downloadedAt.HasValue)
            {
                return (DateTimeOffset.UtcNow - downloadedAt.Value) <= ttl;
            }

            // No meta: fall back to file mtime.
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(TsvPath));
            return (DateTimeOffset.UtcNow - mtime) <= ttl;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
    {
        var tsvLastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(TsvPath));
        if (_keys is not null && _ratings is not null && _votes is not null && _loadedFromTsvLastWriteUtc.HasValue && _loadedFromTsvLastWriteUtc.Value == tsvLastWriteUtc)
        {
            return;
        }

        _logger.LogWarning("IMDb fallback: building in-memory index from TSV (first use)");

        // Dynamic arrays to avoid huge per-item allocations.
        var keys = new long[1_000_000];
        var ratings = new float[1_000_000];
        var votes = new int[1_000_000];
        var count = 0;

        using var fs = new FileStream(TsvPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);

        // Header
        _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseLine(line, out var id, out var rating, out var voteCount))
            {
                continue;
            }

            if (count == keys.Length)
            {
                // Grow.
                var newSize = checked(keys.Length * 2);
                Array.Resize(ref keys, newSize);
                Array.Resize(ref ratings, newSize);
                Array.Resize(ref votes, newSize);
            }

            keys[count] = id;
            ratings[count] = rating;
            votes[count] = voteCount;
            count++;
        }

        if (count == 0)
        {
            _keys = Array.Empty<long>();
            _ratings = Array.Empty<float>();
            _votes = Array.Empty<int>();
            _loadedFromTsvLastWriteUtc = tsvLastWriteUtc;
            return;
        }

        Array.Resize(ref keys, count);
        Array.Resize(ref ratings, count);
        Array.Resize(ref votes, count);

        // Sort for fast binary search while keeping ratings/votes aligned.
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(keys, order);

        var sortedRatings = new float[count];
        var sortedVotes = new int[count];
        for (var i = 0; i < count; i++)
        {
            sortedRatings[i] = ratings[order[i]];
            sortedVotes[i] = votes[order[i]];
        }

        _keys = keys;
        _ratings = sortedRatings;
        _votes = sortedVotes;
        _loadedFromTsvLastWriteUtc = tsvLastWriteUtc;
    }

    private static bool TryParseLine(string line, out long id, out float rating, out int votes)
    {
        id = 0;
        rating = 0;
        votes = 0;

        // Format: tconst \t averageRating \t numVotes
        var t1 = line.IndexOf('\t');
        if (t1 <= 0)
        {
            return false;
        }

        var t2 = line.IndexOf('\t', t1 + 1);
        if (t2 <= t1)
        {
            return false;
        }

        var tconst = line.Substring(0, t1);
        var avg = line.Substring(t1 + 1, t2 - t1 - 1);
        var numVotes = t2 + 1 < line.Length ? line.Substring(t2 + 1) : string.Empty;

        var key = ParseImdbNumericId(tconst);
        if (!key.HasValue)
        {
            return false;
        }

        if (!float.TryParse(avg, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) || f <= 0)
        {
            return false;
        }

        _ = int.TryParse(numVotes, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedVotes);

        id = key.Value;
        rating = f;
        votes = parsedVotes > 0 ? parsedVotes : 0;
        return true;
    }


    private static long? ParseImdbNumericId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        imdbId = imdbId.Trim();
        if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var digits = imdbId.Substring(2);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : (long?)null;
    }

    private void ReplaceFile(string src, string dst)
    {
        try
        {
            if (File.Exists(dst))
            {
                File.Delete(dst);
            }

            File.Move(src, dst);
        }
        catch
        {
            // Fallback to copy.
            File.Copy(src, dst, true);
            TryDelete(src);
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void WriteMeta(DateTimeOffset downloadedAtUtc)
    {
        try
        {
            var meta = new Meta { DownloadedAtUtc = downloadedAtUtc, SourceUrl = DatasetUrl };
            var json = JsonSerializer.Serialize(meta);
            File.WriteAllText(MetaPath, json);
        }
        catch
        {
            // ignore
        }
    }

    private DateTimeOffset? ReadMetaDownloadedAtUtc()
    {
        try
        {
            if (!File.Exists(MetaPath))
            {
                return null;
            }

            var json = File.ReadAllText(MetaPath);
            var meta = JsonSerializer.Deserialize<Meta>(json);
            return meta?.DownloadedAtUtc;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Meta
    {
        public DateTimeOffset DownloadedAtUtc { get; set; }
        public string? SourceUrl { get; set; }
    }
}
