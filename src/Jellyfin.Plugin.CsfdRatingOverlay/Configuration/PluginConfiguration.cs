using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Configuration;

public enum NativeRatingTarget
{
    CommunityRating = 0,
    CriticRating = 1,
    None = 2
}

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        Enabled = true;
        OverlayInjectionEnabled = true;
        RequestDelayMs = 2000;
        MaxRetries = 5;
        CooldownMinMinutes = 10;
        NativeRatingTarget = NativeRatingTarget.CommunityRating;
    }

    public bool Enabled { get; set; }

    public bool OverlayInjectionEnabled { get; set; }

    public int RequestDelayMs { get; set; }

    public int MaxRetries { get; set; }

    public int CooldownMinMinutes { get; set; }

    public long ClientCacheVersion { get; set; }

    public NativeRatingTarget NativeRatingTarget { get; set; }
}
