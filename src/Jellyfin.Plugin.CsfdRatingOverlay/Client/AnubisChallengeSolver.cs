using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Client;

/// <summary>
/// Detects and solves Anubis proof-of-work challenges that protect csfd.cz.
/// </summary>
public class AnubisChallengeSolver
{
    private static readonly Regex ChallengeJsonRegex = new(
        @"<script\s+id=""anubis_challenge""\s+type=""application/json"">(?<json>.+?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly ILogger<AnubisChallengeSolver> _logger;

    public AnubisChallengeSolver(ILogger<AnubisChallengeSolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the HTML response is an Anubis challenge page.
    /// </summary>
    public static bool IsAnubisChallenge(string html)
    {
        return html.Contains("anubis_challenge", StringComparison.Ordinal);
    }

    /// <summary>
    /// Solves the Anubis challenge and submits the solution, returning the real page content.
    /// </summary>
    public async Task<HttpResponseMessage?> SolveAndSubmitAsync(
        HttpClient httpClient,
        string challengeHtml,
        Uri originalRequestUri,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var challenge = ParseChallenge(challengeHtml);
        if (challenge is null)
        {
            _logger.LogError("Failed to parse Anubis challenge from HTML");
            return null;
        }

        _logger.LogInformation(
            "Anubis challenge detected (id={Id}, difficulty={Difficulty}, method={Method}). Solving...",
            challenge.Id, challenge.Difficulty, challenge.Method);

        var solution = SolveProofOfWork(challenge.RandomData, challenge.Difficulty);
        if (solution is null)
        {
            _logger.LogError("Failed to solve Anubis PoW (difficulty={Difficulty})", challenge.Difficulty);
            return null;
        }

        _logger.LogInformation(
            "Anubis PoW solved (nonce={Nonce}, hash={Hash})",
            solution.Value.Nonce, solution.Value.Hash[..8] + "...");

        var baseUrl = originalRequestUri.GetLeftPart(UriPartial.Authority);
        var redir = originalRequestUri.PathAndQuery;
        var passUrl = $"{baseUrl}/.within.website/x/cmd/anubis/api/pass-challenge"
            + $"?id={Uri.EscapeDataString(challenge.Id)}"
            + $"&response={Uri.EscapeDataString(solution.Value.Hash)}"
            + $"&nonce={solution.Value.Nonce}"
            + $"&redir={Uri.EscapeDataString(redir)}"
            + $"&elapsedTime=250";

        using var passRequest = new HttpRequestMessage(HttpMethod.Get, passUrl);
        passRequest.Headers.UserAgent.ParseAdd(userAgent);

        _logger.LogDebug("Submitting Anubis solution to {Url}", passUrl);
        var response = await httpClient.SendAsync(passRequest, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Anubis challenge passed successfully");
            return response;
        }

        _logger.LogError(
            "Anubis pass-challenge returned {Status}",
            (int)response.StatusCode);
        response.Dispose();
        return null;
    }

    private AnubisChallenge? ParseChallenge(string html)
    {
        var match = ChallengeJsonRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var json = match.Groups["json"].Value;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var challenge = root.GetProperty("challenge");

            return new AnubisChallenge
            {
                Id = challenge.GetProperty("id").GetString()!,
                Method = challenge.GetProperty("method").GetString()!,
                RandomData = challenge.GetProperty("randomData").GetString()!,
                Difficulty = challenge.GetProperty("difficulty").GetInt32()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize Anubis challenge JSON");
            return null;
        }
    }

    /// <summary>
    /// Brute-forces the SHA-256 nonce: find nonce where SHA256(randomData + nonce) starts with
    /// <paramref name="difficulty"/> leading hex zeros.
    /// </summary>
    private static (string Hash, long Nonce)? SolveProofOfWork(string randomData, int difficulty, long maxIterations = 10_000_000)
    {
        var prefix = new string('0', difficulty);
        var randomDataBytes = Encoding.UTF8.GetBytes(randomData);

        for (long nonce = 0; nonce < maxIterations; nonce++)
        {
            var nonceStr = nonce.ToString();
            var nonceBytes = Encoding.UTF8.GetBytes(nonceStr);

            var inputLength = randomDataBytes.Length + nonceBytes.Length;
            var input = new byte[inputLength];
            Buffer.BlockCopy(randomDataBytes, 0, input, 0, randomDataBytes.Length);
            Buffer.BlockCopy(nonceBytes, 0, input, randomDataBytes.Length, nonceBytes.Length);

            var hashBytes = SHA256.HashData(input);
            var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (hashHex.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (hashHex, nonce);
            }
        }

        return null;
    }

    private sealed class AnubisChallenge
    {
        public required string Id { get; init; }
        public required string Method { get; init; }
        public required string RandomData { get; init; }
        public required int Difficulty { get; init; }
    }
}
