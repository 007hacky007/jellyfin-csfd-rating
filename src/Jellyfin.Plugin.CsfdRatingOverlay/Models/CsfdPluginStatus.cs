namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public class CsfdCacheStats
{
    public int TotalEntries { get; set; }
    public int Resolved { get; set; }
    public int ResolvedNoRating { get; set; }
    public int NotFound { get; set; }
    public int Errors { get; set; }
}

public class CsfdLibraryDiagnostics
{
    public int SupportedMediaLibraries { get; set; }
    public int RawQueryItems { get; set; }
    public int UnsupportedLibraryExcluded { get; set; }
    public int NonLibrarySourceExcluded { get; set; }
    public int UnsupportedTypeExcluded { get; set; }
    public int VirtualExcluded { get; set; }
    public int MissingExcluded { get; set; }
    public int PlaceholderExcluded { get; set; }
    public int DeadParentExcluded { get; set; }
    public int DuplicatePresentationExcluded { get; set; }
    public int FinalItems { get; set; }
}

public class CsfdPluginStatus
{
    public int QueueSize { get; set; }
    public bool IsPaused { get; set; }
    public int TotalLibraryItems { get; set; }
    public CsfdCacheStats CacheStats { get; set; } = new();
    public CsfdLibraryDiagnostics LibraryDiagnostics { get; set; } = new();

    public OverlayInjectionStatus InjectionStatus { get; set; }
    public string? InjectionMessage { get; set; }
}
