namespace Jellyfin.Plugin.CsfdRatingOverlay.Cache;

public enum CsfdCacheEntryStatus
{
    Unknown = 0,
    Resolved = 1,
    NotFound = 2,
    ErrorTransient = 3,
    ErrorPermanent = 4
}
