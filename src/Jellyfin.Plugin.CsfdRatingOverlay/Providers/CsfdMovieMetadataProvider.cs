using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Providers;

/// <summary>
/// Runs after TMDb/OMDb during metadata refresh and overwrites the configured
/// rating field with the CSFD value from cache.
/// </summary>
public class CsfdMovieMetadataProvider : ICustomMetadataProvider<Movie>
{
    private readonly ICsfdCacheStore _cacheStore;
    private readonly ILogger<CsfdMovieMetadataProvider> _logger;

    public CsfdMovieMetadataProvider(ICsfdCacheStore cacheStore, ILogger<CsfdMovieMetadataProvider> logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public string Name => "CSFD Rating";

    public async Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.Enabled || config.NativeRatingTarget == NativeRatingTarget.None)
        {
            return ItemUpdateType.None;
        }

        var entry = await _cacheStore.GetAsync(item.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return ItemUpdateType.None;
        }

        return CsfdNativeRatingHelper.ApplyRating(item, entry, config.NativeRatingTarget, _logger)
            ? ItemUpdateType.MetadataEdit
            : ItemUpdateType.None;
    }
}
