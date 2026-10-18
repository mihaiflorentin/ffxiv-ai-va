namespace AIVoiceActing.Domain;

/// <summary>
/// Remembers the duration of every step of a loop (the language model's tokens) and answers percentiles.
/// A line has at most a few hundred steps, so it keeps them all instead of bucketing; safe to feed from one
/// thread and read from another.
/// </summary>
public sealed class LatencyHistogram
{
    private readonly object gate = new();
    private readonly List<long> samples = [];

    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.samples.Count;
            }
        }
    }

    public void Add(long ms)
    {
        lock (this.gate)
        {
            this.samples.Add(Math.Max(0, ms));
        }
    }

    /// <summary>The nearest-rank percentile (0..100) of the recorded steps; 0 when there are none.</summary>
    public long Percentile(double percent)
    {
        long[] sorted;
        lock (this.gate)
        {
            sorted = [.. this.samples];
        }

        if (sorted.Length == 0)
        {
            return 0;
        }

        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(Math.Clamp(percent, 0, 100) / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public long P50 => this.Percentile(50);

    public long P95 => this.Percentile(95);

    public long Max => this.Percentile(100);
}
