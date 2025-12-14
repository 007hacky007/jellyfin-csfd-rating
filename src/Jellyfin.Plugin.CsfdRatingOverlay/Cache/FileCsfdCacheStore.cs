using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Cache;

public class FileCsfdCacheStore : ICsfdCacheStore, IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ILogger<FileCsfdCacheStore> _logger;
    private readonly string _cachePath;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public FileCsfdCacheStore(IApplicationPaths appPaths, ILogger<FileCsfdCacheStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appPaths.PluginConfigurationsPath);
        _cachePath = Path.Combine(appPaths.PluginConfigurationsPath, "csfd-rating-overlay-cache.json");
    }

    public async Task<CsfdCacheEntry?> GetAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return map.TryGetValue(itemId, out var entry) ? entry : null;
    }

    public async Task<IReadOnlyDictionary<string, CsfdCacheEntry>> GetManyAsync(IEnumerable<string> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.ToArray();
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, CsfdCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (map.TryGetValue(id, out var entry))
            {
                result[id] = entry;
            }
        }

        return result;
    }

    public async Task<IReadOnlyCollection<CsfdCacheEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return map.Values.ToArray();
    }

    public async Task UpsertAsync(CsfdCacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ItemId))
        {
            throw new ArgumentException("ItemId is required", nameof(entry));
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            entry.UpdatedUtc = now;
            if (entry.CreatedUtc == default)
            {
                entry.CreatedUtc = now;
            }

            map[entry.ItemId] = entry;
            await PersistInternalAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteAsync(string itemId, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            if (map.Remove(itemId))
            {
                await PersistInternalAsync(map, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistInternalAsync(new Dictionary<string, CsfdCacheEntry>(StringComparer.OrdinalIgnoreCase), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, CsfdCacheEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, CsfdCacheEntry>> LoadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return new Dictionary<string, CsfdCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.Open(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var map = await JsonSerializer.DeserializeAsync<Dictionary<string, CsfdCacheEntry>>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return map ?? new Dictionary<string, CsfdCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize CSFD cache; starting with empty cache");
            return new Dictionary<string, CsfdCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistInternalAsync(Dictionary<string, CsfdCacheEntry> map, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await using var stream = File.Open(_cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, map, _serializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _mutex.Dispose();
    }
}
