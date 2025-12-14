using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool OverlayInjectionEnabled { get; set; } = true;

    public int RequestDelayMs { get; set; } = 2000;

    public int MaxRetries { get; set; } = 5;

    public int CooldownMinMinutes { get; set; } = 10;
}
