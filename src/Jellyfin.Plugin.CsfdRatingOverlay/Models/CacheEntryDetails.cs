using Jellyfin.Plugin.CsfdRatingOverlay.Cache;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class CacheEntryDetails
{
    public required CsfdCacheEntry Entry { get; init; }

    public string? LibraryTitle { get; init; }

    public int? LibraryYear { get; init; }
}
