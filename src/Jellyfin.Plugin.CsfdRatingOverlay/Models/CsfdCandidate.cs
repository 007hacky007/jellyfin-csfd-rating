namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class CsfdCandidate
{
    public required string CsfdId { get; init; }

    public required string Title { get; init; }

    public int? Year { get; init; }

    public bool IsSeries { get; init; }

    public double Score { get; init; }
}
