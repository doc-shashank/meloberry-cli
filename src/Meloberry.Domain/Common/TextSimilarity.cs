namespace Meloberry.Domain.Common;

public static class TextSimilarity
{
    public static string? FindClosest(string? unknown, IEnumerable<string> candidates, int maxDistance = 2)
    {
        if (string.IsNullOrWhiteSpace(unknown)) return null;
        string? best = null;
        int bestDist = maxDistance + 1;
        var normUnknown = unknown.ToLowerInvariant();
        foreach (var candidate in candidates)
        {
            int dist = Levenshtein(normUnknown, candidate.ToLowerInvariant());
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }
        return best;
    }

    public static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }
}
