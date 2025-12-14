using System.Text;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Injection;

/// <summary>
/// Performs best-effort script tag injection into Jellyfin web UI shell by patching index.html on startup.
/// </summary>
public class OverlayFileInjector
{
    private const string Marker = "<!-- csfd-overlay -->";
    private static readonly string[] FallbackWebRoots =
    {
        "/usr/share/jellyfin/web",
        "/usr/lib/jellyfin/web",
        "/config/www",
        "/config/wwwroot"
    };
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<OverlayFileInjector> _logger;

    public OverlayFileInjector(IApplicationPaths appPaths, ILogger<OverlayFileInjector> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    public void EnsureInjected(PluginConfiguration config)
    {
        if (!config.OverlayInjectionEnabled)
        {
            _logger.LogInformation("Overlay injection disabled via configuration");
            return;
        }

        var webRoot = ResolveWebRoot();
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            _logger.LogWarning("Web root path not available; cannot inject overlay script");
            return;
        }

        var indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("index.html not found at {IndexPath}; skipping injection", indexPath);
            return;
        }

        try
        {
            var html = File.ReadAllText(indexPath, Encoding.UTF8);
            if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase) || html.Contains("/Plugins/CsfdRatingOverlay/web/overlay.js", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Overlay script already injected");
                return;
            }

            var scriptTag = $"\n    {Marker}<script src=\"/Plugins/CsfdRatingOverlay/web/overlay.js\"></script>";
            var insertAt = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            string patched;
            if (insertAt >= 0)
            {
                patched = html.Insert(insertAt, scriptTag + "\n");
            }
            else
            {
                patched = html + scriptTag;
            }

            var backupPath = indexPath + ".bak-csfd";
            if (!File.Exists(backupPath))
            {
                File.Copy(indexPath, backupPath, overwrite: false);
            }

            File.WriteAllText(indexPath, patched, Encoding.UTF8);
            _logger.LogInformation("Successfully injected CSFD overlay script tag into {IndexPath}", indexPath);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogError("Injection failed: Access denied to {IndexPath}. The Jellyfin process does not have write permissions to this file. If running in Docker, you must map this file as a volume to allow modification.", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject overlay script into index.html");
        }
    }

    private string? ResolveWebRoot()
    {
        // Prefer known Jellyfin web client locations.
        foreach (var path in FallbackWebRoots)
        {
            if (Directory.Exists(path))
            {
                _logger.LogInformation("Found web root at {Path}", path);
                return path;
            }
        }

        // As a last resort, fall back to Jellyfin-reported path (if it exists).
        if (!string.IsNullOrWhiteSpace(_appPaths.WebPath) && Directory.Exists(_appPaths.WebPath))
        {
            _logger.LogInformation("Found web root at reported path {Path}", _appPaths.WebPath);
            return _appPaths.WebPath;
        }

        _logger.LogWarning("Could not find web root. Checked: {Paths}, Reported: {ReportedPath}", 
            string.Join(", ", FallbackWebRoots), _appPaths.WebPath);

        return null;
    }
}
