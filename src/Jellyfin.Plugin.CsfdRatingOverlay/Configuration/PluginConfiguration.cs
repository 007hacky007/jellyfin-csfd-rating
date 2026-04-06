using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NativeRatingTarget
{
    CommunityRating = 0,
    CriticRating = 1,
    None = 2,
    Both = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetailIconStyle
{
    None = 0,
    LogoSocial = 1,
    LogoWhiteRed = 2
}

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        Enabled = true;
        OverlayInjectionEnabled = true;
        OverlayPosterEnabled = true;
        RequestDelayMs = 2000;
        MaxRetries = 5;
        CooldownMinMinutes = 10;
        NativeRatingTarget = NativeRatingTarget.CommunityRating;
        OverlayDetailEnabled = true;
        DetailIconStyle = DetailIconStyle.None;
    }

    public bool Enabled { get; set; }

    public bool OverlayInjectionEnabled { get; set; }

    public bool OverlayPosterEnabled { get; set; }

    public bool OverlayDetailEnabled { get; set; }

    public int RequestDelayMs { get; set; }

    public int MaxRetries { get; set; }

    public int CooldownMinMinutes { get; set; }

    public long ClientCacheVersion { get; set; }

    public NativeRatingTarget NativeRatingTarget { get; set; }

    public DetailIconStyle DetailIconStyle { get; set; }
}
