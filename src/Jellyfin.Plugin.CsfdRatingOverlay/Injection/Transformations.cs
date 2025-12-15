using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Injection;

public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

public static class Transformations
{
    private const string Marker = "<!-- csfd-overlay -->";
    private const string ScriptTag = "<script src=\"/Plugins/CsfdRatingOverlay/web/overlay.js\"></script>";

    public static string IndexTransformation(PatchRequestPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Contents))
        {
            return string.Empty;
        }

        var html = payload.Contents;
        
        // Check if already injected
        if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase) || html.Contains("/Plugins/CsfdRatingOverlay/web/overlay.js", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var injection = $"\n    {Marker}{ScriptTag}";
        
        // Try to insert before </head>
        var insertAt = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (insertAt >= 0)
        {
            return html.Insert(insertAt, injection + "\n");
        }
        
        // Fallback: append
        return html + injection;
    }
}
