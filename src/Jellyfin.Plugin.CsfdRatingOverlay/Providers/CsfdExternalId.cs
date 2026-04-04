using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Providers;

public class CsfdExternalId : IExternalId
{
    public string ProviderName => "CSFD";

    public string Key => "Csfd";

    public ExternalIdMediaType? Type => null;

#if NET8_0
    public string? UrlFormatString => "https://www.csfd.cz/film/{0}";
#endif

    public bool Supports(IHasProviderIds item)
        => item is Movie or Series;
}
