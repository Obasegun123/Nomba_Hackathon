namespace Nomba_Hackathon.Service;

public class FuzzyMatchingService
{
    private readonly ILogger<FuzzyMatchingService> _logger;
    private const float DefaultThreshold = 0.85f;

    public FuzzyMatchingService(ILogger<FuzzyMatchingService> logger)
    {
        _logger = logger;
    }

    public NameMatchResult Match(string incomingName, string expectedName, float threshold = DefaultThreshold)
    {
        if (string.IsNullOrWhiteSpace(incomingName) || string.IsNullOrWhiteSpace(expectedName))
            return new NameMatchResult { IsMatch = false, Confidence = 0f, Reason = "Empty name(s)" };

        var incoming = NormalizeName(incomingName);
        var expected = NormalizeName(expectedName);

        if (incoming == expected)
            return new NameMatchResult { IsMatch = true, Confidence = 1f, Reason = "Exact match" };

        var confidence = CalculateSimilarity(incoming, expected);

        var isMatch = confidence >= threshold;
        var reason = isMatch
            ? $"Fuzzy match at {confidence:P0} confidence (threshold: {threshold:P0})"
            : $"Below threshold: {confidence:P0} < {threshold:P0}";

        return new NameMatchResult
        {
            IsMatch = isMatch,
            Confidence = confidence,
            Reason = reason,
            IncomingNameNormalized = incoming,
            ExpectedNameNormalized = expected
        };
    }

    public float CalculateSimilarity(string str1, string str2)
    {
        var distance = LevenshteinDistance(str1, str2);
        var maxLength = Math.Max(str1.Length, str2.Length);
        return maxLength == 0 ? 1f : 1f - (float)distance / maxLength;
    }

    private int LevenshteinDistance(string s1, string s2)
    {
        var len1 = s1.Length;
        var len2 = s2.Length;
        var d = new int[len1 + 1, len2 + 1];

        for (int i = 0; i <= len1; i++)
            d[i, 0] = i;

        for (int j = 0; j <= len2; j++)
            d[0, j] = j;

        for (int i = 1; i <= len1; i++)
        {
            for (int j = 1; j <= len2; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[len1, len2];
    }

    private string NormalizeName(string name)
    {
        return System.Text.RegularExpressions.Regex.Replace(name.ToUpperInvariant().Trim(), @"\s+", " ");
    }
}

public class NameMatchResult
{
    public bool IsMatch { get; set; }
    public float Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? IncomingNameNormalized { get; set; }
    public string? ExpectedNameNormalized { get; set; }
}
