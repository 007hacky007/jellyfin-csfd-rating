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
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (config.NativeRatingTarget == NativeRatingTarget.None)
        {
            _logger.LogInformation("Native rating override is disabled, skipping sync");
            return;
        }

        var entries = await _cacheStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var resolved = entries.Where(e => e.Status == CsfdCacheEntryStatus.Resolved && e.Percent.HasValue).ToList();

        _logger.LogInformation("Syncing {Count} resolved CSFD ratings to {Target}", resolved.Count, config.NativeRatingTarget);

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

            if (CsfdNativeRatingHelper.ApplyRating(item, entry, config.NativeRatingTarget, _logger))
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                updated++;
            }

            progress.Report((double)(i + 1) / resolved.Count * 100);
        }

        _logger.LogInformation("Native rating sync complete: {Updated}/{Total} items updated", updated, resolved.Count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
