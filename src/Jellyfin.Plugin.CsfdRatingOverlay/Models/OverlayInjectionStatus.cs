namespace Jellyfin.Plugin.CsfdRatingOverlay.Models;

public enum OverlayInjectionStatus
{
    NotAttempted,
    Registered,
    Injected,
    PluginNotFound,
    Failed
}
