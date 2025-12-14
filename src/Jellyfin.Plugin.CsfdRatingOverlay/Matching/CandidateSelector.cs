using Jellyfin.Plugin.CsfdRatingOverlay.Models;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Matching;

public static class CandidateSelector
{
    public static CsfdCandidate? PickBest(string primaryTitle, string? originalTitle, int? year, bool isSeries, IEnumerable<CsfdCandidate> candidates)
    {
        var normalizedPrimary = MetadataFingerprint.Normalize(primaryTitle);
        var normalizedOriginal = MetadataFingerprint.Normalize(originalTitle ?? string.Empty);

        var best = default(CsfdCandidate);
        double bestScore = 0;

        foreach (var candidate in candidates)
        {
            var normalizedCandidate = MetadataFingerprint.Normalize(candidate.Title);
            var similarity = Math.Max(ComputeSimilarity(normalizedPrimary, normalizedCandidate), ComputeSimilarity(normalizedOriginal, normalizedCandidate));
            var score = similarity;

            if (year.HasValue && candidate.Year.HasValue)
            {
                if (candidate.Year.Value == year.Value)
                {
                    score += 0.5;
                }
                else if (Math.Abs(candidate.Year.Value - year.Value) == 1)
                {
                    score += 0.25;
                }
            }

            if (candidate.IsSeries == isSeries)
            {
                score += 0.2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = new CsfdCandidate
                {
                    CsfdId = candidate.CsfdId,
                    Title = candidate.Title,
                    Year = candidate.Year,
                    IsSeries = candidate.IsSeries,
                    Score = score
                };
            }
        }

        return best;
    }

    private static double ComputeSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var setA = new HashSet<string>(tokensA, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(tokensB, StringComparer.OrdinalIgnoreCase);
        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0;
        }

        var intersection = setA.Count(s => setB.Contains(s));
        var union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
