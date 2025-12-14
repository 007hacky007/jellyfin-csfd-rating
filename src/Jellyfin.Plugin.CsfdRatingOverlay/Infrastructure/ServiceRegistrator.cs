using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Client;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Injection;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using System.Composition;

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
        services.AddHttpClient<CsfdClient>();
        services.AddSingleton<ICsfdFetchProcessor, CsfdFetchProcessor>();
        services.AddSingleton<CsfdFetchQueue>();
        services.AddSingleton<CsfdRatingService>();
        services.AddSingleton<OverlayFileInjector>();
        services.AddHostedService<CsfdHostedService>();
    }
}
