using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Client;

public class CsfdClient
{
    private static readonly Regex CandidateRegex = new(@"/(?:film|serial)/(?<id>\d+)[^""]*""\s*[^>]*>(?<title>[^<]+)<", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YearRegex = new(@"(?<year>19|20)\\d{2}", RegexOptions.Compiled);
    // Updated regex to be more robust against HTML changes and attributes
    private static readonly Regex PercentRegex = new(@"film-rating-average.*?>(?:\s*<[^>]*>)*\s*(?<percent>\d{1,3})%", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly DebugLogger _debugLogger;
    private readonly ILogger<CsfdClient> _logger;
    
    // Real browser User-Agents to avoid bot detection
    private static readonly string[] UserAgents = new[]
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0"
    };

    public CsfdClient(HttpClient httpClient, DebugLogger debugLogger, ILogger<CsfdClient> logger)
    {
        _httpClient = httpClient;
        _debugLogger = debugLogger;
        _logger = logger;
    }

    public async Task<CsfdClientResult<IReadOnlyList<CsfdCandidate>>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"https://www.csfd.cz/hledat/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Use a random real User-Agent
        request.Headers.UserAgent.ParseAdd(UserAgents[Random.Shared.Next(UserAgents.Length)]);
        
        _logger.LogDebug("Fetching CSFD search: {Url}", url);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (IsThrottle(response.StatusCode))
        {
            _logger.LogWarning("CSFD search throttled. Status: {Status}, Url: {Url}", response.StatusCode, url);
            return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Throttle(GetRetryAfter(response), $"Search throttled: {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("CSFD search failed. Status: {Status}, Url: {Url}", response.StatusCode, url);
            _debugLogger.LogFailure($"Search:{query}", $"HTTP {(int)response.StatusCode}", url, null);
            return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Fail($"HTTP {(int)response.StatusCode}");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ParseCandidates(html);
        
        // Log if no candidates found, might be useful
        if (candidates.Count == 0)
        {
             _debugLogger.LogFailure($"Search:{query}", "No candidates found", url, html);
        }

        return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Ok(candidates);
    }

    public async Task<CsfdClientResult<int>> GetRatingPercentAsync(string csfdId, CancellationToken cancellationToken)
    {
        // Append /prehled/ to match standard browser behavior and node-csfd-api
        var url = $"https://www.csfd.cz/film/{csfdId}/prehled/";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Use a random real User-Agent
        request.Headers.UserAgent.ParseAdd(UserAgents[Random.Shared.Next(UserAgents.Length)]);
        
        _logger.LogDebug("Fetching CSFD rating: {Url}", url);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (IsThrottle(response.StatusCode))
        {
            _logger.LogWarning("CSFD rating throttled. Status: {Status}, Url: {Url}", response.StatusCode, url);
            return CsfdClientResult<int>.Throttle(GetRetryAfter(response), $"Details throttled: {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("CSFD rating failed. Status: {Status}, Url: {Url}", response.StatusCode, url);
            _debugLogger.LogFailure($"Rating:{csfdId}", $"HTTP {(int)response.StatusCode}", url, null);
            return CsfdClientResult<int>.Fail($"HTTP {(int)response.StatusCode}");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        
        if (html.Contains("g-recaptcha") || html.Contains("robot") || html.Contains("Jste robot?"))
        {
             _logger.LogError("CSFD returned captcha/bot check for {Url}", url);
             _debugLogger.LogFailure($"Rating:{csfdId}", "Captcha detected", url, html);
             return CsfdClientResult<int>.Fail("Captcha detected");
        }

        var percent = ParsePercent(html);
        if (percent is null)
        {
            _logger.LogError("Failed to parse rating percent for {Url}. HTML length: {Length}", url, html.Length);
            _debugLogger.LogFailure($"Rating:{csfdId}", "Rating percent not found", url, html);
            
            var idx = html.IndexOf("film-rating-average");
            if (idx >= 0)
            {
                 var start = Math.Max(0, idx - 100);
                 var len = Math.Min(html.Length - start, 500);
                 _logger.LogError("Context around 'film-rating-average': {Context}", html.Substring(start, len));
            }
            else
            {
                _logger.LogError("String 'film-rating-average' not found in HTML. Snippet: {Snippet}", html.Length > 500 ? html.Substring(0, 500) : html);
            }

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
