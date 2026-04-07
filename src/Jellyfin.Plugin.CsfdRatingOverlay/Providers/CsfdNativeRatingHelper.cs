using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Providers;

/// <summary>
/// Shared logic for applying CSFD ratings and provider IDs to Jellyfin items.
/// Used by metadata providers, the sync task, and the fetch processor.
/// </summary>
public static class CsfdNativeRatingHelper
{
    /// <summary>
    /// Persists the CSFD provider ID and (optionally) native rating to the Jellyfin item.
    /// Call this after resolving a cache entry to keep Jellyfin metadata in sync.
    /// </summary>
    public static async Task PersistMetadataAsync(BaseItem? item, CsfdCacheEntry entry, ILogger? logger, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return;
        }

        var changed = false;
        if (!string.IsNullOrWhiteSpace(entry.CsfdId)
            && (!item.ProviderIds.TryGetValue("Csfd", out var existingCsfdId) || !string.Equals(existingCsfdId, entry.CsfdId, StringComparison.Ordinal)))
        {
            item.ProviderIds["Csfd"] = entry.CsfdId;
            changed = true;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (config.NativeRatingTarget != NativeRatingTarget.None)
        {
            changed = ApplyRating(item, entry, config.NativeRatingTarget, logger) || changed;
        }

        if (!changed)
        {
            return;
        }

        logger?.LogDebug("Persisting metadata for {ItemName}: CsfdId={CsfdId}", item.Name, entry.CsfdId);
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the CSFD rating to the item based on the configured target field.
    /// Returns true if any field was changed.
    /// </summary>
    public static bool ApplyRating(BaseItem item, CsfdCacheEntry entry, NativeRatingTarget target, ILogger? logger = null)
    {
        if (target == NativeRatingTarget.None)
        {
            return false;
        }

        // ResolvedNoRating: clear any stale native ratings from a previous match
        if (entry.Status == CsfdCacheEntryStatus.ResolvedNoRating || entry.Percent is null)
        {
            var cleared = false;
            if ((target is NativeRatingTarget.CommunityRating or NativeRatingTarget.Both) && item.CommunityRating.HasValue)
            {
                item.CommunityRating = null;
                cleared = true;
            }

            if ((target is NativeRatingTarget.CriticRating or NativeRatingTarget.Both) && item.CriticRating.HasValue)
            {
                item.CriticRating = null;
                cleared = true;
            }

            if (cleared)
            {
                logger?.LogDebug("Cleared stale CSFD native rating from {ItemName} (status={Status})", item.Name, entry.Status);
            }

            return cleared;
        }

        if (entry.Status != CsfdCacheEntryStatus.Resolved)
        {
            return false;
        }

        var changed = false;

        if (target is NativeRatingTarget.CommunityRating or NativeRatingTarget.Both)
        {
            var stars = (float)(entry.Percent.Value / 10.0);
            if (item.CommunityRating != stars)
            {
                item.CommunityRating = stars;
                changed = true;
            }
        }

        if (target is NativeRatingTarget.CriticRating or NativeRatingTarget.Both)
        {
            var percent = (float)entry.Percent.Value;
            if (item.CriticRating != percent)
            {
                item.CriticRating = percent;
                changed = true;
            }
        }

        if (changed)
        {
            logger?.LogDebug(
                "Applied CSFD rating to {ItemName}: {Target}, Percent={Percent}, CsfdId={CsfdId}",
                item.Name,
                target,
                entry.Percent.Value,
                entry.CsfdId);
        }

        return changed;
    }
}
