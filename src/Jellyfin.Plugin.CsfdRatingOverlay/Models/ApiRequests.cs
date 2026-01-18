namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class MatchRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string CsfdId { get; set; } = string.Empty;
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
}

public class CacheOverrideRequest
{
    public string ItemIdOrTerm { get; set; } = string.Empty;

    public string CsfdId { get; set; } = string.Empty;

    public string? QueryUsed { get; set; }
}
