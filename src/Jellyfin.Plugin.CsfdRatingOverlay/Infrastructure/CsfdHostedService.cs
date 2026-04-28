using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Injection;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Infrastructure;

/// <summary>
/// Starts background queue and performs overlay injection on server startup.
/// </summary>
public class CsfdHostedService : IHostedService, IAsyncDisposable
{
    private readonly CsfdFetchQueue _queue;
    private readonly CsfdRatingService _ratingService;
    private readonly ILibraryManager _libraryManager;
    private readonly ICsfdCacheStore _cacheStore;
    private readonly ILogger<CsfdHostedService> _logger;
    private readonly TimeSpan _startupPruneDelay;

    private CancellationTokenSource? _startupPruneCts;
    private Task? _startupPruneTask;

    public CsfdHostedService(
        CsfdFetchQueue queue,
        CsfdRatingService ratingService,
        ILibraryManager libraryManager,
        ICsfdCacheStore cacheStore,
        ILogger<CsfdHostedService> logger,
        TimeSpan? startupPruneDelay = null)
    {
        _queue = queue;
        _ratingService = ratingService;
        _libraryManager = libraryManager;
        _cacheStore = cacheStore;
        _logger = logger;
        _startupPruneDelay = startupPruneDelay ?? TimeSpan.FromSeconds(30);
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

        _libraryManager.ItemRemoved += OnItemRemoved;
        _startupPruneCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startupPruneTask = PruneStaleCacheAfterStartupAsync(_startupPruneCts.Token);

        // Register transformation instead of manual injection
        RegisterTransformation();

        _queue.Start();
        _logger.LogInformation("ČSFD overlay services started");
        return Task.CompletedTask;
    }

    private async void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Movie and not Series)
        {
            return;
        }

        try
        {
            var itemId = CsfdItemIds.Normalize(e.Item.Id.ToString());
            await _cacheStore.DeleteAsync(itemId, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Deleted CSFD cache entry for removed library item {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete CSFD cache entry for removed library item");
        }
    }

    private async Task PruneStaleCacheAfterStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_startupPruneDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupPruneDelay, cancellationToken).ConfigureAwait(false);
            }

            await _ratingService.PruneStaleCacheEntriesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during plugin shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune stale CSFD cache entries after startup");
        }
    }

    private void RegisterTransformation()
    {
        try
        {
            var payload = new
            {
                id = "b9643a4b-5b92-4f09-94c4-45ce6bfc57e9",
                fileNamePattern = "index.html",
                callbackAssembly = typeof(Transformations).Assembly.FullName,
                callbackClass = typeof(Transformations).FullName,
                callbackMethod = nameof(Transformations.IndexTransformation)
            };
            var transformationJson = JsonSerializer.Serialize(payload);

            // Find the FileTransformation assembly in the loaded assemblies
            Assembly? fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains("Jellyfin.Plugin.FileTransformation") ?? false);

            if (fileTransformationAssembly != null)
            {
                // Get the PluginInterface type
                Type? pluginInterfaceType = fileTransformationAssembly
                    .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

                if (pluginInterfaceType != null)
                {
                    var registerMethod = pluginInterfaceType.GetMethod("RegisterTransformation");
                    var paramType = registerMethod?.GetParameters().FirstOrDefault()?.ParameterType;
                    var parseMethod = paramType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

                    var data = parseMethod?.Invoke(null, new object?[] { transformationJson });

                    if (registerMethod != null && data != null)
                    {
                        registerMethod.Invoke(null, new object?[] { data });
                        _logger.LogInformation("Registered index.html transformation with File Transformation plugin");
                        _ratingService.SetOverlayInjectionStatus(OverlayInjectionStatus.Registered, "Successfully registered with File Transformation plugin");
                    }
                    else
                    {
                        _logger.LogWarning("File Transformation plugin found but could not construct transformation payload");
                        _ratingService.SetOverlayInjectionStatus(OverlayInjectionStatus.Failed, "Could not construct transformation payload");
                    }
                }
                else
                {
                    _logger.LogWarning("File Transformation plugin found but PluginInterface type missing");
                     _ratingService.SetOverlayInjectionStatus(OverlayInjectionStatus.Failed, "File Transformation plugin Interface missing");
                }
            }
            else
            {
                _logger.LogWarning("File Transformation plugin not found. Overlay script will not be injected.");
                _ratingService.SetOverlayInjectionStatus(OverlayInjectionStatus.PluginNotFound, "File Transformation plugin not found");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to register transformation");
             _ratingService.SetOverlayInjectionStatus(OverlayInjectionStatus.Failed, $"Exception: {e.Message}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        await StopStartupPruneAsync().ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        await StopStartupPruneAsync().ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StopStartupPruneAsync()
    {
        var cts = Interlocked.Exchange(ref _startupPruneCts, null);
        var task = Interlocked.Exchange(ref _startupPruneTask, null);
        if (cts != null)
        {
            cts.Cancel();
        }

        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during plugin shutdown.
            }
        }

        cts?.Dispose();
    }
}
