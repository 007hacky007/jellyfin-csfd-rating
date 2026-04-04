using System.Composition;
using System.Net;
using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Client;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Injection;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Infrastructure;

/// <summary>
/// Registers plugin services into Jellyfin's DI container.
/// </summary>
// MEF export allows Jellyfin to discover this registrator without the PluginServiceRegistration attribute.
[Export(typeof(IPluginServiceRegistrator))]
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, MediaBrowser.Controller.IServerApplicationHost applicationHost)
    {
        services.AddSingleton<ICsfdCacheStore, FileCsfdCacheStore>();
        services.AddSingleton<DebugLogger>();
        services.AddSingleton(provider =>
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CsfdRateLimiter>>();
            return new CsfdRateLimiter(TimeSpan.FromMilliseconds(cfg.RequestDelayMs), TimeSpan.FromMinutes(cfg.CooldownMinMinutes), logger);
        });
        services.AddSingleton<AnubisChallengeSolver>();
        services.AddSingleton<CookieContainer>();
        services.AddHttpClient<CsfdClient>()
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var cookieContainer = provider.GetRequiredService<CookieContainer>();
                return new HttpClientHandler
                {
                    CookieContainer = cookieContainer,
                    UseCookies = true,
                    AllowAutoRedirect = true
                };
            });
        services.AddSingleton<ICsfdFetchProcessor, CsfdFetchProcessor>();
        services.AddSingleton<CsfdFetchQueue>();
        services.AddSingleton<CsfdRatingService>();
        services.AddHostedService<CsfdHostedService>();
    }
}
