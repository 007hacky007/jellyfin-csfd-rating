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
            _logger.LogInformation("Injected CSFD overlay script tag into {IndexPath}", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject overlay script into index.html");
        }
    }

    private string? ResolveWebRoot()
    {
        if (!string.IsNullOrWhiteSpace(_appPaths.WebPath))
        {
            if (Directory.Exists(_appPaths.WebPath))
            {
                return _appPaths.WebPath;
            }

            // Some installs report a non-existent web path (often under /var/lib/jellyfin/wwwroot)
            // while the real web client is elsewhere. If we can locate the real web dir, attempt
            // to create a symlink so both Jellyfin and the injector have a stable path.
            foreach (var fallback in FallbackWebRoots)
            {
                if (!Directory.Exists(fallback))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_appPaths.WebPath) ?? "/");
                    Directory.CreateSymbolicLink(_appPaths.WebPath, fallback);
                    _logger.LogInformation("Created web root symlink {WebPath} -> {Fallback}", _appPaths.WebPath, fallback);
                    return _appPaths.WebPath;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create web root symlink {WebPath} -> {Fallback}", _appPaths.WebPath, fallback);
                    break;
                }
            }
        }

        foreach (var path in FallbackWebRoots)
        {
            if (Directory.Exists(path))
            {
                _logger.LogInformation("Using fallback web root path {WebRoot}", path);
                return path;
            }
        }

        return null;
    }
}
