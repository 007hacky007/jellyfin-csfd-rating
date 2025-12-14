using System.Text.Json;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Services;

public class DebugLogger
{
    private readonly string _logPath;
    private static readonly object _lock = new();

    public DebugLogger()
    {
        var path = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
        _logPath = Path.Combine(path, "csfd_debug_failures.jsonl");
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
                ResponsePreview = responseContent, // Log full content as requested by user ("Log whole request and response")
                ExtraData = extraData
            };

            var line = JsonSerializer.Serialize(entry);
            
            lock (_lock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Ignore logging errors
        }
    }
}
