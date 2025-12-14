using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CsfdRatingOverlay;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin Instance { get; private set; } = null!;

    public override string Name => "ČSFD Rating Overlay";

    public override Guid Id { get; } = new("b9643a4b-5b92-4f09-94c4-45ce6bfc57e9");

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "csfdratingoverlay",
            DisplayName = "ČSFD Rating Overlay",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configurationpage.html"
        }
    };
}
