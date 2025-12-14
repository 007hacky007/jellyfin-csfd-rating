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

        var webRoot = _appPaths.WebPath;
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
}
