using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Injection;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Infrastructure;

/// <summary>
/// Starts background queue and performs overlay injection on server startup.
/// </summary>
public class CsfdHostedService : IHostedService, IAsyncDisposable
{
    private readonly CsfdFetchQueue _queue;
    private readonly CsfdRatingService _ratingService;
    private readonly OverlayFileInjector _injector;
    private readonly ILogger<CsfdHostedService> _logger;

    public CsfdHostedService(
        CsfdFetchQueue queue,
        CsfdRatingService ratingService,
        OverlayFileInjector injector,
        ILogger<CsfdHostedService> logger)
    {
        _queue = queue;
        _ratingService = ratingService;
        _injector = injector;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(Plugin).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        _logger.LogInformation(
            "Starting ČSFD Rating Overlay. Assembly={AssemblyName} Version={AssemblyVersion} InformationalVersion={InformationalVersion} Location={AssemblyLocation}",
            assembly.FullName,
            assembly.GetName().Version?.ToString() ?? "(unknown)",
            informationalVersion ?? "(none)",
            string.IsNullOrWhiteSpace(assembly.Location) ? "(empty)" : assembly.Location);

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _injector.EnsureInjected(cfg);
        _queue.Start();
        _logger.LogInformation("ČSFD overlay services started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _queue.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync().ConfigureAwait(false);
    }
}
