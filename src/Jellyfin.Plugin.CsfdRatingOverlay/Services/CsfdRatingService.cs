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

    private OverlayInjectionStatus _injectionStatus = OverlayInjectionStatus.NotAttempted;
    private string? _injectionMessage;

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
        var normalizedItemId = CsfdItemIds.Normalize(itemId);
        var entry = await _cacheStore.GetAsync(normalizedItemId, cancellationToken).ConfigureAwait(false);
        if (entry == null)
        {
            if (enqueueIfMissing)
            {
                Enqueue(normalizedItemId, false, null);
            }

            return new CsfdRatingData
            {
                ItemId = normalizedItemId,
                Status = CsfdCacheEntryStatus.Unknown
            };
        }

        return Map(entry);
    }

    public async Task<IReadOnlyDictionary<string, CsfdRatingData>> GetBatchAsync(IEnumerable<string> itemIds, bool enqueueIfMissing, CancellationToken cancellationToken)
    {
        var list = itemIds.ToArray();
        var normalizedIds = list.Select(CsfdItemIds.Normalize).ToArray();
        var map = await _cacheStore.GetManyAsync(normalizedIds, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, CsfdRatingData>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Length; i++)
        {
            var id = list[i];
            var normalizedId = normalizedIds[i];

            if (map.TryGetValue(normalizedId, out var entry))
            {
                result[id] = Map(entry);
                continue;
            }

            if (enqueueIfMissing)
            {
                Enqueue(normalizedId, false, null);
            }

            result[id] = new CsfdRatingData { ItemId = normalizedId, Status = CsfdCacheEntryStatus.Unknown };
        }

        return result;
    }

    public void Enqueue(string itemId, bool force, string? fingerprint)
    {
        _queue.Enqueue(new CsfdFetchRequest
        {
            ItemId = CsfdItemIds.Normalize(itemId),
            Force = force,
            Fingerprint = fingerprint,
            Attempt = 0
        });
    }

    public void SetOverlayInjectionStatus(OverlayInjectionStatus status, string? message)
    {
        _injectionStatus = status;
        _injectionMessage = message;
    }

    public async Task<CsfdPluginStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var items = GetSupportedLibraryItems();
        var stats = await _cacheStore.GetStatsAsync(cancellationToken).ConfigureAwait(false);

        return new CsfdPluginStatus
        {
            QueueSize = _queue.Count,
            IsPaused = _queue.IsPaused,
            TotalLibraryItems = items.Count,
            CacheStats = stats,
            InjectionStatus = _injectionStatus,
            InjectionMessage = _injectionMessage
        };
    }

    public void SetPaused(bool paused)
    {
        _queue.SetPaused(paused);
    }

    public async Task<int> BackfillLibraryAsync(CancellationToken cancellationToken)
    {
        var items = GetSupportedLibraryItems();
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var surviving = await PruneStaleCacheEntriesAsync(items, entries, cancellationToken).ConfigureAwait(false);
        var byId = surviving.ToDictionary(e => CsfdItemIds.Normalize(e.ItemId), StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;
        foreach (var item in items)
        {
            var fingerprint = MetadataFingerprint.Compute(item);
            var itemId = CsfdItemIds.Normalize(item.Id.ToString());
            byId.TryGetValue(itemId, out var entry);
            if (ShouldQueue(item, entry, fingerprint, now))
            {
                Enqueue(itemId, false, fingerprint);
                enqueued++;
            }
        }

        _logger.LogInformation("Backfill enqueued {Count} items", enqueued);
        return enqueued;
    }

    public Task<int> RetryNotFoundAsync(CancellationToken cancellationToken)
    {
        return RetryByStatusAsync(
            "not-found",
            entry => entry.Status == CsfdCacheEntryStatus.NotFound,
            cancellationToken);
    }

    public Task<int> RetryErrorsAsync(CancellationToken cancellationToken)
    {
        return RetryByStatusAsync(
            "error",
            entry => entry.Status == CsfdCacheEntryStatus.ErrorTransient || entry.Status == CsfdCacheEntryStatus.ErrorPermanent,
            cancellationToken);
    }

    public Task<int> RetryNoRatingAsync(CancellationToken cancellationToken)
    {
        return RetryByStatusAsync(
            "no-rating",
            entry => entry.Status == CsfdCacheEntryStatus.ResolvedNoRating,
            cancellationToken);
    }

    private async Task<int> RetryByStatusAsync(
        string label,
        Func<CsfdCacheEntry, bool> predicate,
        CancellationToken cancellationToken)
    {
        var items = GetSupportedLibraryItems();
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        entries = await PruneStaleCacheEntriesAsync(items, entries, cancellationToken).ConfigureAwait(false);

        var count = 0;
        foreach (var entry in entries.Where(predicate))
        {
            Enqueue(entry.ItemId, true, entry.Fingerprint);
            count++;
        }

        _logger.LogInformation("Retrying {Count} {Label} entries", count, label);
        return count;
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        return _cacheStore.ClearAllAsync(cancellationToken);
    }

    public async Task<CacheEntryDetails?> FindCacheEntryAsync(string term, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var normalizedTerm = term.Trim();
        CsfdCacheEntry? entry = null;

        if (Guid.TryParse(normalizedTerm, out var guid))
        {
            entry = await _cacheStore.GetAsync(CsfdItemIds.Normalize(guid.ToString()), cancellationToken).ConfigureAwait(false);
        }

        if (entry == null)
        {
            var normalizedNoDashes = normalizedTerm.Replace("-", string.Empty, StringComparison.Ordinal);
            var allEntries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
            entry = allEntries.FirstOrDefault(e =>
            {
                var entryIdNormalized = e.ItemId.Replace("-", string.Empty, StringComparison.Ordinal);
                if (string.Equals(entryIdNormalized, normalizedNoDashes, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return !string.IsNullOrWhiteSpace(e.CsfdId) && string.Equals(e.CsfdId, normalizedTerm, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (entry == null)
        {
            return null;
        }

        var (title, year) = TryGetLibraryInfo(entry.ItemId);
        return new CacheEntryDetails
        {
            Entry = entry,
            LibraryTitle = title,
            LibraryYear = year
        };
    }

    public Task<IReadOnlyList<ReviewItem>> GetUnmatchedItemsAsync(CancellationToken cancellationToken)
    {
        return GetReviewItemsAsync(
            new[]
            {
                CsfdCacheEntryStatus.NotFound,
                CsfdCacheEntryStatus.ErrorTransient,
                CsfdCacheEntryStatus.ErrorPermanent
            },
            includeUncached: true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ReviewItem>> GetReviewItemsAsync(
        IEnumerable<CsfdCacheEntryStatus> statuses,
        bool includeUncached,
        CancellationToken cancellationToken)
    {
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var cacheMap = entries.ToDictionary(e => e.ItemId, StringComparer.OrdinalIgnoreCase);
        var allowedStatuses = new HashSet<CsfdCacheEntryStatus>(statuses);

        var items = GetSupportedLibraryItems();

        var result = new List<ReviewItem>();

        foreach (var item in items)
        {
            var itemId = CsfdItemIds.Normalize(item.Id.ToString());

            if (cacheMap.TryGetValue(itemId, out var entry))
            {
                if (!allowedStatuses.Contains(entry.Status))
                {
                    continue;
                }

                result.Add(new ReviewItem
                {
                    ItemId = itemId,
                    Title = item.Name,
                    OriginalTitle = item.OriginalTitle,
                    Year = item.ProductionYear,
                    Status = entry.Status.ToString(),
                    LastError = entry.LastError,
                    CsfdId = entry.CsfdId,
                    MatchedTitle = entry.MatchedTitle,
                    MatchedYear = entry.MatchedYear,
                    QueryUsed = entry.QueryUsed
                });
            }
            else if (includeUncached)
            {
                result.Add(new ReviewItem
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

    public async Task<CsfdCacheEntry> OverrideCacheEntryAsync(string itemIdOrTerm, string csfdId, string? queryUsed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemIdOrTerm))
        {
            throw new ArgumentException("ItemId or search term is required", nameof(itemIdOrTerm));
        }

        if (string.IsNullOrWhiteSpace(csfdId))
        {
            throw new ArgumentException("CsfdId is required", nameof(csfdId));
        }

        var entryDetails = await FindCacheEntryAsync(itemIdOrTerm, cancellationToken).ConfigureAwait(false);
        var targetItemId = entryDetails?.Entry.ItemId;

        if (targetItemId == null && Guid.TryParse(itemIdOrTerm, out var parsedGuid))
        {
            targetItemId = CsfdItemIds.Normalize(parsedGuid.ToString());
        }

        if (targetItemId == null)
        {
            throw new InvalidOperationException("Item not found in cache and term is not a valid itemId");
        }

        var ratingResult = await _csfdClient.GetRatingPercentAsync(csfdId, cancellationToken).ConfigureAwait(false);
        if (!ratingResult.Success)
        {
            throw new Exception(ratingResult.Error ?? "Failed to fetch rating");
        }

        var hasRating = ratingResult.Payload is not null;
        var percent = ratingResult.Payload;
        var stars = hasRating ? percent!.Value / 10.0 : (double?)null;
        var display = hasRating ? stars!.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ⭐️" : null;

        BaseItem? libraryItem = null;
        if (Guid.TryParse(targetItemId, out var libraryGuid))
        {
            libraryItem = _libraryManager.GetItemById(libraryGuid);
        }

        var cacheEntry = entryDetails?.Entry ?? new CsfdCacheEntry
        {
            ItemId = targetItemId,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        cacheEntry.Status = hasRating ? CsfdCacheEntryStatus.Resolved : CsfdCacheEntryStatus.ResolvedNoRating;
        cacheEntry.CsfdId = csfdId;
        cacheEntry.Percent = percent;
        cacheEntry.Stars = stars;
        cacheEntry.DisplayText = display;
        cacheEntry.MatchedTitle = libraryItem?.Name ?? cacheEntry.MatchedTitle;
        cacheEntry.MatchedYear = libraryItem?.ProductionYear ?? cacheEntry.MatchedYear;
        cacheEntry.Fingerprint = libraryItem != null ? MetadataFingerprint.Compute(libraryItem) : cacheEntry.Fingerprint;
        cacheEntry.AttemptedUtc = DateTimeOffset.UtcNow;
        cacheEntry.AttemptCount = entryDetails?.Entry.AttemptCount ?? 0;
        cacheEntry.LastError = null;
        cacheEntry.RetryAfterUtc = hasRating ? null : DateTimeOffset.UtcNow + TimeSpan.FromHours(24);
        cacheEntry.QueryUsed = queryUsed ?? cacheEntry.QueryUsed;

        await _cacheStore.UpsertAsync(cacheEntry, cancellationToken).ConfigureAwait(false);
        await PersistLibraryMetadataAsync(libraryItem, cacheEntry, cancellationToken).ConfigureAwait(false);
        InvalidateClientCacheVersion();
        return cacheEntry;
    }

    public async Task ManualMatchAsync(string itemId, string csfdId, CancellationToken cancellationToken)
    {
        var ratingResult = await _csfdClient.GetRatingPercentAsync(csfdId, cancellationToken).ConfigureAwait(false);
        if (!ratingResult.Success)
        {
            throw new Exception(ratingResult.Error ?? "Failed to fetch rating");
        }

        var hasRating = ratingResult.Payload is not null;
        var percent = ratingResult.Payload;
        var stars = hasRating ? percent!.Value / 10.0 : (double?)null;
        var display = hasRating ? stars!.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ⭐️" : null;

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

        var normalizedItemId = CsfdItemIds.Normalize(itemId);

        var entry = new CsfdCacheEntry
        {
            ItemId = normalizedItemId,
            Status = hasRating ? CsfdCacheEntryStatus.Resolved : CsfdCacheEntryStatus.ResolvedNoRating,
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
            RetryAfterUtc = hasRating ? null : DateTimeOffset.UtcNow + TimeSpan.FromHours(24)
        };

        await _cacheStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ManualMatch: cache upserted for {ItemId}, CsfdId={CsfdId}, HasRating={HasRating}", normalizedItemId, csfdId, hasRating);

        if (Guid.TryParse(normalizedItemId, out var normalizedGuid))
        {
            var item = _libraryManager.GetItemById(normalizedGuid);
            _logger.LogDebug("ManualMatch: GetItemById({Guid}) returned {Result}", normalizedGuid, item is null ? "null" : item.Name);
            await PersistLibraryMetadataAsync(item, entry, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("ManualMatch: could not parse normalizedItemId={ItemId} as GUID, skipping metadata persist", normalizedItemId);
        }

        InvalidateClientCacheVersion();
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
            CsfdCacheEntryStatus.ResolvedNoRating => !entry.RetryAfterUtc.HasValue || entry.RetryAfterUtc.Value <= now,
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

    private async Task PersistLibraryMetadataAsync(BaseItem? item, CsfdCacheEntry entry, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            _logger.LogWarning("PersistLibraryMetadata: item is null for CsfdId={CsfdId}, skipping", entry.CsfdId);
            return;
        }

        await Providers.CsfdNativeRatingHelper.PersistMetadataAsync(item, entry, _logger, cancellationToken).ConfigureAwait(false);
    }

    private static void InvalidateClientCacheVersion()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.ClientCacheVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        plugin.UpdateConfiguration(plugin.Configuration);
    }

    private IReadOnlyList<BaseItem> GetSupportedLibraryItems()
    {
        // Jellyfin's repository only collapses duplicate-row "presentations" of the
        // same logical item (multi-version files, BoxSet/virtual-folder cross-references)
        // when the query carries a User. Hosted-service callers like ours have no User,
        // so the raw result can include hundreds of duplicates. Mirror the UI's behavior
        // by grouping on PresentationUniqueKey ourselves.
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true
        })
        .GroupBy(item => item.GetPresentationUniqueKey())
        .Select(group => group.First())
        .ToList();
    }

    public async Task<int> PruneStaleCacheEntriesAsync(CancellationToken cancellationToken)
    {
        var items = GetSupportedLibraryItems();
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var beforeCount = entries.Count;
        var surviving = await PruneStaleCacheEntriesAsync(items, entries, cancellationToken).ConfigureAwait(false);
        return beforeCount - surviving.Count;
    }

    private async Task<IReadOnlyCollection<CsfdCacheEntry>> PruneStaleCacheEntriesAsync(
        IReadOnlyCollection<BaseItem> currentItems,
        IReadOnlyCollection<CsfdCacheEntry> entries,
        CancellationToken cancellationToken)
    {
        var currentIds = currentItems
            .Select(item => CsfdItemIds.Normalize(item.Id.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // If the bulk library snapshot is empty we cannot tell whether the library is
        // genuinely empty or still loading. Bail out rather than risk wiping the cache;
        // there is no per-id evidence to fall back on either.
        if (currentIds.Count == 0)
        {
            _logger.LogWarning("Skipping stale CSFD cache prune because the library returned 0 supported items");
            return entries;
        }

        // For every entry not in the bulk snapshot, confirm it is actually gone via
        // ILibraryManager.GetItemById before deleting. The bulk query can return a
        // partial set during indexing/startup; an entry that is missing from the bulk
        // result but found by per-id lookup is still in the library and must be kept.
        var staleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = CsfdItemIds.Normalize(entry.ItemId);
            if (currentIds.Contains(normalized))
            {
                continue;
            }

            if (!Guid.TryParse(normalized, out var guid))
            {
                continue;
            }

            if (_libraryManager.GetItemById(guid) is null)
            {
                staleIds.Add(normalized);
            }
        }

        if (staleIds.Count == 0)
        {
            return entries;
        }

        // Refuse to wipe most of the cache in a single pass. A scenario where >50% of
        // cached entries fail both the bulk and per-id checks is a much stronger signal
        // that the library is mid-scan than that the user genuinely deleted that many
        // items at once. Stale entries we miss here will be deleted by the per-item path
        // in CsfdFetchProcessor on their next fetch attempt.
        if (staleIds.Count * 2 > entries.Count)
        {
            _logger.LogWarning(
                "Skipping stale CSFD cache prune: {Stale} of {Total} entries flagged as stale exceeds the 50% safety threshold (library may still be loading)",
                staleIds.Count,
                entries.Count);
            return entries;
        }

        var deleted = await _cacheStore.DeleteManyAsync(staleIds, cancellationToken).ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("Pruned {Count} stale CSFD cache entries for deleted library items", deleted);
        }

        return entries
            .Where(entry => !staleIds.Contains(CsfdItemIds.Normalize(entry.ItemId)))
            .ToArray();
    }

    private (string? Title, int? Year) TryGetLibraryInfo(string itemId)
    {
        if (!Guid.TryParse(itemId, out var guid))
        {
            return (null, null);
        }

        var item = _libraryManager.GetItemById(guid);
        return item == null ? (null, null) : (item.Name, item.ProductionYear);
    }
}
