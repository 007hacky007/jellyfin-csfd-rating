using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Tasks;

/// <summary>
/// Batch-applies cached CSFD ratings to Jellyfin's native rating fields
/// for all resolved items. Useful for initial population and re-sync.
/// </summary>
public class CsfdNativeRatingSyncTask : IScheduledTask
{
    private readonly ICsfdCacheStore _cacheStore;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CsfdNativeRatingSyncTask> _logger;

    public CsfdNativeRatingSyncTask(
        ICsfdCacheStore cacheStore,
        ILibraryManager libraryManager,
        ILogger<CsfdNativeRatingSyncTask> logger)
    {
        _cacheStore = cacheStore;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "CSFD native rating sync";

    public string Key => "CsfdNativeRatingSync";

    public string Description => "Apply cached CSFD ratings to Jellyfin's native rating fields (Community Rating or Critic Rating).";

    public string Category => "Metadata";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var resolved = entries.Where(e => e.Status is CsfdCacheEntryStatus.Resolved or CsfdCacheEntryStatus.ResolvedNoRating).ToList();

        _logger.LogInformation("Syncing {Count} resolved CSFD entries to library metadata", resolved.Count);

        var updated = 0;
        for (var i = 0; i < resolved.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = resolved[i];
            if (!Guid.TryParse(entry.ItemId, out var guid))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(guid);
            if (item is null)
            {
                continue;
            }

            await CsfdNativeRatingHelper.PersistMetadataAsync(item, entry, _logger, cancellationToken).ConfigureAwait(false);
            updated++;

            progress.Report((double)(i + 1) / resolved.Count * 100);
        }

        _logger.LogInformation("Native rating sync complete: {Updated}/{Total} items processed", updated, resolved.Count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
