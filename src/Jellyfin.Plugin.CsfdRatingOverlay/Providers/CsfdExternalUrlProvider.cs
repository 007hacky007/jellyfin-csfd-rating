using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Providers;

public class CsfdExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "CSFD";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.ProviderIds.TryGetValue("Csfd", out var csfdId) && !string.IsNullOrEmpty(csfdId))
        {
            yield return $"https://www.csfd.cz/film/{csfdId}";
        }
    }
}
