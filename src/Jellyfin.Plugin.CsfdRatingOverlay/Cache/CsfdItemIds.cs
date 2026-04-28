namespace Jellyfin.Plugin.CsfdRatingOverlay.Cache;

public static class CsfdItemIds
{
    public static string Normalize(string itemId)
    {
        return Guid.TryParse(itemId, out var guid) ? guid.ToString() : itemId;
    }
}
