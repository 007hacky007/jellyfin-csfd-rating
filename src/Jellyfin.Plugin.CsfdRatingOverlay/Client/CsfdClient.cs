using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Client;

public class CsfdClient
{
    // Captures title link inside <h3> and the following <p class="search-name"> if present
    private static readonly Regex CandidateRegex = new Regex(
        @"<h3\s+class=""film-title-nooverflow"">.*?<a\s+href=""/film/(?<id>\d+)-[^""]*""[^>]*>(?<title>[^<]*)</a>(?<rest>.*?)</h3>(?<after>.*?(?=<h3|<section|$))",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex SearchNameRegex = new Regex(
        @"<p\s+class=""search-name"">\s*\((?<name>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex YearRegex = new(@"(?<year>(?:19|20)\d{2})", RegexOptions.Compiled);
    // Updated regex to be more robust against HTML changes and attributes
    private static readonly Regex PercentRegex = new(@"film-rating-average.*?>(?:\s*<[^>]*>)*\s*(?<percent>\d{1,3})%", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly DebugLogger _debugLogger;
    private readonly ILogger<CsfdClient> _logger;
    private readonly AnubisChallengeSolver _anubisChallengeSolver;

    // Real browser User-Agents to avoid bot detection
    private static readonly string[] UserAgents = new[]
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0"
    };

    public CsfdClient(HttpClient httpClient, DebugLogger debugLogger, ILogger<CsfdClient> logger, AnubisChallengeSolver anubisChallengeSolver)
    {
        _httpClient = httpClient;
        _debugLogger = debugLogger;
        _logger = logger;
        _anubisChallengeSolver = anubisChallengeSolver;
    }

    public async Task<CsfdClientResult<IReadOnlyList<CsfdCandidate>>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"https://www.csfd.cz/hledat/?q={Uri.EscapeDataString(query)}";

        var (html, throttleResult) = await FetchHtmlAsync(url, cancellationToken).ConfigureAwait(false);
        if (throttleResult is not null)
        {
            return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Throttle(throttleResult.Value.RetryAfter, throttleResult.Value.Error);
        }

        if (html is null)
        {
            _debugLogger.LogFailure($"Search:{query}", "Failed to fetch (Anubis or HTTP error)", url, null);
            return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Fail("Failed to fetch search page");
        }

        var candidates = ParseCandidates(html);

        // Log if no candidates found, might be useful
        if (candidates.Count == 0)
        {
             _debugLogger.LogFailure($"Search:{query}", "No candidates found", url, html);
        }

        return CsfdClientResult<IReadOnlyList<CsfdCandidate>>.Ok(candidates);
    }

    public async Task<CsfdClientResult<int?>> GetRatingPercentAsync(string csfdId, CancellationToken cancellationToken)
    {
        // Append /prehled/ to match standard browser behavior and node-csfd-api
        var url = $"https://www.csfd.cz/film/{csfdId}/prehled/";

        var (html, throttleResult) = await FetchHtmlAsync(url, cancellationToken).ConfigureAwait(false);
        if (throttleResult is not null)
        {
            return CsfdClientResult<int?>.Throttle(throttleResult.Value.RetryAfter, throttleResult.Value.Error);
        }

        if (html is null)
        {
            _debugLogger.LogFailure($"Rating:{csfdId}", "Failed to fetch (Anubis or HTTP error)", url, null);
            return CsfdClientResult<int?>.Fail("Failed to fetch rating page");
        }

        if (html.Contains("g-recaptcha") || html.Contains("Jste robot?"))
        {
             _logger.LogError("CSFD returned captcha/bot check for {Url}", url);
             _debugLogger.LogFailure($"Rating:{csfdId}", "Captcha detected", url, html);
             return CsfdClientResult<int?>.Fail("Captcha detected");
        }

        var percent = ParsePercent(html);
        if (percent is null)
        {
            var idx = html.IndexOf("film-rating-average", StringComparison.Ordinal);
            if (idx >= 0)
            {
                 // The element exists but we couldn't parse the percentage - likely a regex/HTML change
                 var start = Math.Max(0, idx - 100);
                 var len = Math.Min(html.Length - start, 500);
                 _logger.LogError("Failed to parse rating percent for {Url}. Context: {Context}", url, html.Substring(start, len));
                 _debugLogger.LogFailure($"Rating:{csfdId}", "Rating percent parse error", url, html);
                 return CsfdClientResult<int?>.Fail("Rating percent parse error");
            }

            // Page loaded fine but has no rating element - movie exists but is unrated
            _logger.LogInformation("CSFD page has no rating for {CsfdId}", csfdId);
            return CsfdClientResult<int?>.Ok(null);
        }

        return CsfdClientResult<int?>.Ok(percent.Value);
    }

    /// <summary>
    /// Fetches HTML from the given URL, automatically solving Anubis challenges if encountered.
    /// Returns (html, null) on success, or (null, throttleInfo) if throttled, or (null, null) on failure.
    /// </summary>
    private async Task<(string? Html, (TimeSpan? RetryAfter, string Error)? ThrottleResult)> FetchHtmlAsync(
        string url, CancellationToken cancellationToken)
    {
        var userAgent = UserAgents[Random.Shared.Next(UserAgents.Length)];

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);

        _logger.LogDebug("Fetching CSFD: {Url}", url);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (IsThrottle(response.StatusCode))
        {
            _logger.LogWarning("CSFD throttled. Status: {Status}, Url: {Url}", response.StatusCode, url);
            return (null, (GetRetryAfter(response), $"Throttled: {(int)response.StatusCode}"));
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("CSFD request failed. Status: {Status}, Url: {Url}", response.StatusCode, url);
            return (null, null);
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!AnubisChallengeSolver.IsAnubisChallenge(html))
        {
            return (html, null);
        }

        // Anubis challenge detected - solve it and retry
        _logger.LogInformation("Anubis challenge detected for {Url}, solving...", url);

        using var solvedResponse = await _anubisChallengeSolver.SolveAndSubmitAsync(
            _httpClient, html, new Uri(url), userAgent, cancellationToken).ConfigureAwait(false);

        if (solvedResponse is null)
        {
            _logger.LogError("Failed to solve Anubis challenge for {Url}", url);
            return (null, null);
        }

        var solvedHtml = await solvedResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Verify we didn't get another challenge
        if (AnubisChallengeSolver.IsAnubisChallenge(solvedHtml))
        {
            _logger.LogError("Got another Anubis challenge after solving for {Url}", url);
            return (null, null);
        }

        return (solvedHtml, null);
    }

    private static List<CsfdCandidate> ParseCandidates(string html)
    {
        var matches = CandidateRegex.Matches(html);
        var list = new List<CsfdCandidate>();
        foreach (Match match in matches)
        {
            var id = match.Groups["id"].Value;
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            var rest = match.Groups["rest"].Value;

            int? year = null;
            var yearMatch = YearRegex.Match(rest);
            if (yearMatch.Success && int.TryParse(yearMatch.Groups["year"].Value, out var parsedYear))
            {
                year = parsedYear;
            }

            var isSeries = rest.Contains("(seriál)", StringComparison.OrdinalIgnoreCase) || rest.Contains("TV seriál", StringComparison.OrdinalIgnoreCase);

            string? originalName = null;
            var after = match.Groups["after"].Value;
            var searchNameMatch = SearchNameRegex.Match(after);
            if (searchNameMatch.Success)
            {
                originalName = WebUtility.HtmlDecode(searchNameMatch.Groups["name"].Value).Trim();
            }

            list.Add(new CsfdCandidate
            {
                CsfdId = id,
                Title = title,
                OriginalName = originalName,
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
