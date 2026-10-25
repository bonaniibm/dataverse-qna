namespace DvQna.Analysis;

public static class Stats
{
    /// <summary>Nearest-rank percentile (p in 0..100). Returns 0 for an empty set.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    public sealed record Summary(int Count, double P50, double P95, double Max, double Avg);

    public static Summary Summarize(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count == 0
            ? new Summary(0, 0, 0, 0, 0)
            : new Summary(s.Count, Percentile(s, 50), Percentile(s, 95), s[^1], Math.Round(s.Average(), 1));
    }
}
