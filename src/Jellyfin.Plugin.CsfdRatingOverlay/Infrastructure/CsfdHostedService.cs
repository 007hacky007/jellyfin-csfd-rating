using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Injection;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Infrastructure;

/// <summary>
/// Starts background queue and performs overlay injection on server startup.
/// </summary>
public class CsfdHostedService : IHostedService, IAsyncDisposable
{
    private readonly CsfdFetchQueue _queue;
    private readonly CsfdRatingService _ratingService;
    private readonly ILogger<CsfdHostedService> _logger;

    public CsfdHostedService(
        CsfdFetchQueue queue,
        CsfdRatingService ratingService,
        ILogger<CsfdHostedService> logger)
    {
        _queue = queue;
        _ratingService = ratingService;
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
        
        // Register transformation instead of manual injection
        RegisterTransformation();
        
        _queue.Start();
        _logger.LogInformation("ČSFD overlay services started");
        return Task.CompletedTask;
    }

    private void RegisterTransformation()
    {
        try
        {
                        var transformationJson = $@"{{
    \"id\": \"b9643a4b-5b92-4f09-94c4-45ce6bfc57e9\",
    \"fileNamePattern\": \"index.html\",
    \"callbackAssembly\": \"{typeof(Transformations).Assembly.FullName}\",
    \"callbackClass\": \"{typeof(Transformations).FullName}\",
    \"callbackMethod\": \"{nameof(Transformations.IndexTransformation)}\"
}}";

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
                    }
                    else
                    {
                        _logger.LogWarning("File Transformation plugin found but could not construct transformation payload");
                    }
                }
                else
                {
                    _logger.LogWarning("File Transformation plugin found but PluginInterface type missing");
                }
            }
            else
            {
                _logger.LogWarning("File Transformation plugin not found. Overlay script will not be injected.");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to register transformation");
        }
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
