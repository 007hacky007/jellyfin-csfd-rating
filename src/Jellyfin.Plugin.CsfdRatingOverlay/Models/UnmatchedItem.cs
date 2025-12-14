namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class UnmatchedItem
{
    public string ItemId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? LastError { get; set; }
}
