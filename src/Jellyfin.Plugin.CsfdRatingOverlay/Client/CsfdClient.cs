using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Client;

public class CsfdClient
{
    private static readonly Regex CandidateRegex = new(@"/(?:film|serial)/(?<id>\d+)[^""]*""\s*[^>]*>(?<title>[^<]+)<", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YearRegex = new(@"(?<year>19|20)\\d{2}", RegexOptions.Compiled);
    private static readonly Regex PercentRegex = new(@"(?<percent>\\d{1,3})%", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<CsfdClient> _logger;

    public CsfdClient(HttpClient httpClient, ILogger<CsfdClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CsfdClientResult<IReadOnlyList<CsfdCandidate>>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"https://www.csfd.cz/hledat/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-Csfd", "1.0"));
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (IsThrottle(response.StatusCode))
        {
            return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Throttle(GetRetryAfter(response), $"Search throttled: {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Fail($"HTTP {(int)response.StatusCode}");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ParseCandidates(html);
        return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Ok(candidates);
    }

    public async Task<CsfdClientResult<int>> GetRatingPercentAsync(string csfdId, CancellationToken cancellationToken)
    {
        var url = $"https://www.csfd.cz/film/{csfdId}/";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-Csfd", "1.0"));
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (IsThrottle(response.StatusCode))
        {
            return CsfdClientResult<int>.Throttle(GetRetryAfter(response), $"Details throttled: {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            return CsfdClientResult<int>.Fail($"HTTP {(int)response.StatusCode}");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var percent = ParsePercent(html);
        if (percent is null)
        {
            return CsfdClientResult<int>.Fail("Rating percent not found");
        }

        return CsfdClientResult<int>.Ok(percent.Value);
    }

    private static IReadOnlyList<CsfdCandidate> ParseCandidates(string html)
    {
        var matches = CandidateRegex.Matches(html);
        var list = new List<CsfdCandidate>();
        foreach (Match match in matches)
        {
            var id = match.Groups["id"].Value;
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            var slice = html.AsSpan(match.Index, Math.Min(200, html.Length - match.Index));
            int? year = null;
            var yearMatch = YearRegex.Match(slice.ToString());
            if (yearMatch.Success && int.TryParse(yearMatch.Groups["year"].Value, out var parsedYear))
            {
                year = parsedYear;
            }

            var isSeries = html.Substring(Math.Max(0, match.Index - 20), Math.Min(20, match.Index)).Contains("serial", StringComparison.OrdinalIgnoreCase);

            list.Add(new CsfdCandidate
            {
                CsfdId = id,
                Title = title,
                Year = year,
                IsSeries = isSeries,
                Score = 0
            });
        }

        return list;
    }

    private static int? ParsePercent(string html)
    {
        var match = PercentRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        if (int.TryParse(match.Groups["percent"].Value, out var percent))
        {
            return percent;
        }

        return null;
    }

    private static bool IsThrottle(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.ServiceUnavailable;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var deltaDate = date - DateTimeOffset.UtcNow;
            return deltaDate > TimeSpan.Zero ? deltaDate : null;
        }

        return null;
    }
}
