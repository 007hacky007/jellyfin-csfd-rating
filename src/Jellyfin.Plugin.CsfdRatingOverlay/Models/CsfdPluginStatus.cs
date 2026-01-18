namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class CsfdCacheStats
{
    public int TotalEntries { get; set; }
    public int Resolved { get; set; }
    public int NotFound { get; set; }
    public int Errors { get; set; }
}

public class CsfdPluginStatus
{
    public int QueueSize { get; set; }
    public bool IsPaused { get; set; }
    public int TotalLibraryItems { get; set; }
    public CsfdCacheStats CacheStats { get; set; } = new();

    public OverlayInjectionStatus InjectionStatus { get; set; }
    public string? InjectionMessage { get; set; }
}
