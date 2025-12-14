using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CsfdRatingOverlay;

public class CsfdRatingOverlayPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public CsfdRatingOverlayPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static CsfdRatingOverlayPlugin Instance { get; private set; } = null!;

    public override string Name => "ČSFD Rating Overlay";

    public override Guid Id { get; } = new("b9643a4b-5b92-4f09-94c4-45ce6bfc57e9");

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "csfdratingoverlay",
            DisplayName = "ČSFD Rating Overlay",
            EnableInMainMenu = false,
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
        },
        new PluginPageInfo
        {
            Name = "csfdratingoverlayjs",
            EnableInMainMenu = false,
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.js"
        }
    };
}
