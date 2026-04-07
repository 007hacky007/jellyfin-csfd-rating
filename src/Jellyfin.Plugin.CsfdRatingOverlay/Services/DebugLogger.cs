using System.Text.Json;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Services;

public class DebugLogger
{
    private static readonly object _lock = new();
    private string? _logPath;

    private string LogPath
    {
        get
        {
            if (_logPath is null)
            {
                var path = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
                Directory.CreateDirectory(path);
                _logPath = Path.Combine(path, "csfd_debug_failures.jsonl");
            }

            return _logPath;
        }
    }

    public void LogFailure(string context, string reason, string url, string? responseContent, object? extraData = null)
    {
        try
        {
            var entry = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                Context = context,
                Reason = reason,
                Url = url,
                ResponsePreview = responseContent,
                ExtraData = extraData
            };

            var line = JsonSerializer.Serialize(entry);

            lock (_lock)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Ignore logging errors
        }
    }
}
