using System.Globalization;
using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Client;
using Jellyfin.Plugin.CsfdRatingOverlay.Matching;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Services;

public class CsfdRatingService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICsfdCacheStore _cacheStore;
    private readonly CsfdFetchQueue _queue;
    private readonly CsfdClient _csfdClient;
    private readonly ILogger<CsfdRatingService> _logger;

    public CsfdRatingService(
        ILibraryManager libraryManager,
        ICsfdCacheStore cacheStore,
        CsfdFetchQueue queue,
        CsfdClient csfdClient,
        ILogger<CsfdRatingService> logger)
    {
        _libraryManager = libraryManager;
        _cacheStore = cacheStore;
        _queue = queue;
        _csfdClient = csfdClient;
        _logger = logger;
    }

    public async Task<CsfdRatingData> GetAsync(string itemId, bool enqueueIfMissing, CancellationToken cancellationToken)
    {
        var entry = await _cacheStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (entry == null)
        {
            if (enqueueIfMissing)
            {
                Enqueue(itemId, false, null);
            }

            return new CsfdRatingData
            {
                ItemId = itemId,
                Status = CsfdCacheEntryStatus.Unknown
            };
        }

        return Map(entry);
    }

    public async Task<IReadOnlyDictionary<string, CsfdRatingData>> GetBatchAsync(IEnumerable<string> itemIds, bool enqueueIfMissing, CancellationToken cancellationToken)
    {
        var list = itemIds.ToArray();
        var map = await _cacheStore.GetManyAsync(list, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, CsfdRatingData>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in list)
        {
            if (map.TryGetValue(id, out var entry))
            {
                result[id] = Map(entry);
                continue;
            }

            if (enqueueIfMissing)
            {
                Enqueue(id, false, null);
            }

            result[id] = new CsfdRatingData { ItemId = id, Status = CsfdCacheEntryStatus.Unknown };
        }

        return result;
    }

    public void Enqueue(string itemId, bool force, string? fingerprint)
    {
        _queue.Enqueue(new CsfdFetchRequest
        {
            ItemId = itemId,
            Force = force,
            Fingerprint = fingerprint,
            Attempt = 0
        });
    }

    public async Task<CsfdPluginStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var stats = await _cacheStore.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var totalItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true
        }).Count;

        return new CsfdPluginStatus
        {
            QueueSize = _queue.Count,
            IsPaused = _queue.IsPaused,
            TotalLibraryItems = totalItems,
            CacheStats = stats
        };
    }

    public void SetPaused(bool paused)
    {
        _queue.SetPaused(paused);
    }

    public async Task<int> BackfillLibraryAsync(CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true
        });

        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;
        foreach (var item in items)
        {
            var fingerprint = MetadataFingerprint.Compute(item);
            var entry = await _cacheStore.GetAsync(item.Id.ToString(), cancellationToken).ConfigureAwait(false);
            if (ShouldQueue(item, entry, fingerprint, now))
            {
                Enqueue(item.Id.ToString(), false, fingerprint);
                enqueued++;
            }
        }

        _logger.LogInformation("Backfill enqueued {Count} items", enqueued);
        return enqueued;
    }

    public async Task<int> RetryNotFoundAsync(CancellationToken cancellationToken)
    {
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var entry in entries.Where(e => e.Status == CsfdCacheEntryStatus.NotFound))
        {
            Enqueue(entry.ItemId, true, entry.Fingerprint);
            count++;
        }

        _logger.LogInformation("Retrying {Count} not-found entries", count);
        return count;
    }

    public async Task<int> RetryErrorsAsync(CancellationToken cancellationToken)
    {
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var entry in entries.Where(e => e.Status == CsfdCacheEntryStatus.ErrorTransient || e.Status == CsfdCacheEntryStatus.ErrorPermanent))
        {
            Enqueue(entry.ItemId, true, entry.Fingerprint);
            count++;
        }

        _logger.LogInformation("Retrying {Count} error entries", count);
        return count;
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        return _cacheStore.ClearAllAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnmatchedItem>> GetUnmatchedItemsAsync(CancellationToken cancellationToken)
    {
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var cacheMap = entries.ToDictionary(e => e.ItemId, StringComparer.OrdinalIgnoreCase);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true
        });

        var result = new List<UnmatchedItem>();

        foreach (var item in items)
        {
            var itemId = item.Id.ToString();
            
            if (cacheMap.TryGetValue(itemId, out var entry))
            {
                if (entry.Status == CsfdCacheEntryStatus.Resolved)
                {
                    continue;
                }

                result.Add(new UnmatchedItem
                {
                    ItemId = itemId,
                    Title = item.Name,
                    OriginalTitle = item.OriginalTitle,
                    Year = item.ProductionYear,
                    Status = entry.Status.ToString(),
                    LastError = entry.LastError
                });
            }
            else
            {
                result.Add(new UnmatchedItem
                {
                    ItemId = itemId,
                    Title = item.Name,
                    OriginalTitle = item.OriginalTitle,
                    Year = item.ProductionYear,
                    Status = "NotProcessed",
                    LastError = null
                });
            }
        }
        
        return result.OrderBy(x => x.Title).ToList();
    }

    public Task<CsfdClientResult<IReadOnlyList<CsfdCandidate>>> SearchCsfdAsync(string query, CancellationToken cancellationToken)
    {
        return _csfdClient.SearchAsync(query, cancellationToken);
    }

    public async Task ManualMatchAsync(string itemId, string csfdId, CancellationToken cancellationToken)
    {
        var ratingResult = await _csfdClient.GetRatingPercentAsync(csfdId, cancellationToken).ConfigureAwait(false);
        if (!ratingResult.Success)
        {
            throw new Exception(ratingResult.Error ?? "Failed to fetch rating");
        }

        var percent = ratingResult.Payload;
        var stars = percent / 10.0;
        var display = stars.ToString("0.0", CultureInfo.InvariantCulture) + " ⭐️";

        string? fingerprint = null;
        string? matchedTitle = null;
        int? matchedYear = null;

        if (Guid.TryParse(itemId, out var guid))
        {
            var item = _libraryManager.GetItemById(guid);
            if (item != null)
            {
                fingerprint = MetadataFingerprint.Compute(item);
                matchedTitle = item.Name;
                matchedYear = item.ProductionYear;
            }
        }

        var entry = new CsfdCacheEntry
        {
            ItemId = itemId,
            Status = CsfdCacheEntryStatus.Resolved,
            CsfdId = csfdId,
            Percent = percent,
            Stars = stars,
            DisplayText = display,
            MatchedTitle = matchedTitle,
            MatchedYear = matchedYear,
            CreatedUtc = DateTimeOffset.UtcNow,
            AttemptedUtc = DateTimeOffset.UtcNow,
            Fingerprint = fingerprint,
            AttemptCount = 0,
            LastError = null,
            RetryAfterUtc = null
        };

        await _cacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldQueue(BaseItem item, CsfdCacheEntry? entry, string fingerprint, DateTimeOffset now)
    {
        if (entry == null)
        {
            return true;
        }

        return entry.Status switch
        {
            CsfdCacheEntryStatus.Resolved => false,
            CsfdCacheEntryStatus.NotFound => !string.Equals(entry.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase),
            CsfdCacheEntryStatus.ErrorPermanent => false,
            CsfdCacheEntryStatus.ErrorTransient => !entry.RetryAfterUtc.HasValue || entry.RetryAfterUtc.Value <= now,
            _ => true
        };
    }

    private static CsfdRatingData Map(CsfdCacheEntry entry) => new()
    {
        ItemId = entry.ItemId,
        Status = entry.Status,
        Percent = entry.Percent,
        Stars = entry.Stars,
        DisplayText = entry.DisplayText,
        CsfdId = entry.CsfdId
    };
}
