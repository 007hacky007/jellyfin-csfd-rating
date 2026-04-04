using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Providers;

/// <summary>
/// Shared logic for applying CSFD ratings to Jellyfin items.
/// Used by both ICustomMetadataProvider implementations and the sync task.
/// </summary>
public static class CsfdNativeRatingHelper
{
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

        if (entry.Status != CsfdCacheEntryStatus.Resolved || entry.Percent is null)
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

        // Store CSFD ID in ProviderIds so it's visible in item metadata
        if (entry.CsfdId is not null
            && (!item.ProviderIds.TryGetValue("Csfd", out var existing) || existing != entry.CsfdId))
        {
            item.ProviderIds["Csfd"] = entry.CsfdId;
            changed = true;
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
