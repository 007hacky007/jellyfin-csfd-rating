using Jellyfin.Plugin.CsfdRatingOverlay.Cache;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class CsfdRatingData
{
    public required string ItemId { get; init; }

    public CsfdCacheEntryStatus Status { get; init; }

    public int? Percent { get; init; }

    public double? Stars { get; init; }

    public string? DisplayText { get; init; }

    public string? CsfdId { get; init; }
}
