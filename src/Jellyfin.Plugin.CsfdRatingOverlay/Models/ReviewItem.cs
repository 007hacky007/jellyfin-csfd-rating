namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class ReviewItem
{
    public string ItemId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public string? CsfdId { get; set; }
    public string? MatchedTitle { get; set; }
    public int? MatchedYear { get; set; }
    public string? QueryUsed { get; set; }
}
