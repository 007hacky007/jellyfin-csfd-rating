namespace Jellyfin.Plugin.CsfdRatingOverlay.Queue;

public class CsfdFetchRequest
{
    public required string ItemId { get; init; }

    public string? Fingerprint { get; init; }

    public bool Force { get; init; }

    public int Attempt { get; init; }

    public DateTimeOffset EnqueuedUtc { get; init; } = DateTimeOffset.UtcNow;
}
