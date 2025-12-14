namespace Jellyfin.Plugin.CsfdRatingOverlay.Cache;

public class CsfdCacheEntry
{
    public string ItemId { get; set; } = string.Empty;

    public CsfdCacheEntryStatus Status { get; set; } = CsfdCacheEntryStatus.Unknown;

    public string? Fingerprint { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset AttemptedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? CsfdId { get; set; }

    public int? Percent { get; set; }

    public double? Stars { get; set; }

    public string? DisplayText { get; set; }

    public int? RatingCount { get; set; }

    public string? MatchedTitle { get; set; }

    public int? MatchedYear { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? RetryAfterUtc { get; set; }

    public int AttemptCount { get; set; }

    public string? QueryUsed { get; set; }
}
