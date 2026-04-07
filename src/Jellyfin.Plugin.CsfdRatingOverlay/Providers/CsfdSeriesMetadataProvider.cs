using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Providers;

/// <summary>
/// Runs after TMDb/OMDb during metadata refresh and overwrites the configured
/// rating field with the CSFD value from cache.
/// </summary>
public class CsfdSeriesMetadataProvider : ICustomMetadataProvider<Series>
{
    private readonly ICsfdCacheStore _cacheStore;
    private readonly ILogger<CsfdSeriesMetadataProvider> _logger;

    public CsfdSeriesMetadataProvider(ICsfdCacheStore cacheStore, ILogger<CsfdSeriesMetadataProvider> logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public string Name => "CSFD Rating";

    public async Task<ItemUpdateType> FetchAsync(Series item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.Enabled)
        {
            return ItemUpdateType.None;
        }

        var entry = await _cacheStore.GetAsync(item.Id.ToString(), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return ItemUpdateType.None;
        }

        var changed = false;

        // Always persist CSFD provider ID regardless of native rating setting
        if (!string.IsNullOrWhiteSpace(entry.CsfdId)
            && entry.Status is CsfdCacheEntryStatus.Resolved or CsfdCacheEntryStatus.ResolvedNoRating
            && (!item.ProviderIds.TryGetValue("Csfd", out var existing) || !string.Equals(existing, entry.CsfdId, StringComparison.Ordinal)))
        {
            item.ProviderIds["Csfd"] = entry.CsfdId;
            changed = true;
        }

        if (config.NativeRatingTarget != NativeRatingTarget.None)
        {
            changed = CsfdNativeRatingHelper.ApplyRating(item, entry, config.NativeRatingTarget, _logger) || changed;
        }

        return changed ? ItemUpdateType.MetadataEdit : ItemUpdateType.None;
    }
}
