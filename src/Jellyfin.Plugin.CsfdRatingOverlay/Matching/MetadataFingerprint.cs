using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Matching;

public static class MetadataFingerprint
{
    public static string Compute(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var primaryTitle = Normalize(item.Name);
        var originalTitle = Normalize(item.OriginalTitle ?? string.Empty);
        var year = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var type = item.GetType().Name;
        var providerIds = item.ProviderIds?.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}:{kvp.Value}") ?? Array.Empty<string>();

        var payload = string.Join("|", primaryTitle, originalTitle, year, type, string.Join(",", providerIds));
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch))
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }
}
