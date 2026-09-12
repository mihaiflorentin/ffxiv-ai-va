namespace AIVoiceActing.Domain.Pipeline;

using AIVoiceActing.Domain;

/// <summary>
/// Per-speaker message throttle (pure port of TextToTalk's RateLimiter): the first message
/// from a speaker passes; further messages inside the limit window are dropped. The limit
/// window is read live on every call so configuration changes apply immediately. The clock
/// is injectable for deterministic tests.
/// </summary>
public sealed class RateLimiter : IDisposable
{
    private readonly Dictionary<string, long> times = new(StringComparer.Ordinal);
    private readonly Func<long> getLimitMs;
    private readonly Func<long> nowMs;

    public RateLimiter(Func<long> getLimitMs, Func<long>? nowMs = null)
    {
        this.getLimitMs = getLimitMs;
        this.nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Returns true when this message should be rate-limited (dropped), false when it may
    /// pass; passing refreshes the speaker's timestamp. Behavior is identical to
    /// TextToTalk's background-pruned table: an expired entry and a missing entry are
    /// observably the same, so pruning happens inline once the table grows (no timer).
    /// </summary>
    public bool TryRateLimit(string speaker)
    {
        var now = this.nowMs();
        var limit = this.getLimitMs();

        lock (this.times)
        {
            this.PruneExpiredUnlocked(now, limit);

            if (!this.times.TryGetValue(speaker, out var last))
            {
                this.times.Add(speaker, now);
                return false;
            }

            var shouldLimit = now - last <= limit;
            if (!shouldLimit)
            {
                this.times[speaker] = now;
            }

            return shouldLimit;
        }
    }

    private void PruneExpiredUnlocked(long now, long limit)
    {
        if (this.times.Count < 128)
        {
            return;
        }

        List<string>? expired = null;
        foreach (var (speaker, last) in this.times)
        {
            if (now - last > limit)
            {
                (expired ??= []).Add(speaker);
            }
        }

        if (expired is not null)
        {
            foreach (var speaker in expired)
            {
                this.times.Remove(speaker);
            }
        }
    }

    public void Dispose()
    {
        lock (this.times)
        {
            this.times.Clear();
        }
    }
}

/// <summary>
/// Rate limiter with configuration (port of ConfiguredRateLimiter): applies ONLY to player
/// characters — TextToTalk checks ObjectKind.Pc, we check the "pc:" key prefix — and only
/// when the feature is enabled. A configured rate of 0 means "limit everything after the
/// first message" (limit window = forever), ported exactly.
/// </summary>
public sealed class ConfiguredRateLimiter : IDisposable
{
    private readonly RateLimiter limiter;
    private readonly Func<bool> shouldRateLimit;
    private readonly Func<double> messagesPerSecond;

    public ConfiguredRateLimiter(
        Func<bool> shouldRateLimit,
        Func<double> messagesPerSecond,
        Func<long>? nowMs = null)
    {
        this.shouldRateLimit = shouldRateLimit;
        this.messagesPerSecond = messagesPerSecond;
        this.limiter = new RateLimiter(GetLimitDuration(messagesPerSecond), nowMs);
    }

    /// <summary>True when this speaker's message should be dropped.</summary>
    public bool TryRateLimit(SpeakerIdentity speaker) =>
        this.shouldRateLimit() && speaker.Key.StartsWith("pc:", StringComparison.Ordinal) &&
        this.limiter.TryRateLimit(speaker.Key);

    private static Func<long> GetLimitDuration(Func<double> messagesPerSecond) => () =>
    {
        var rate = messagesPerSecond();
        return rate == 0 ? long.MaxValue : (long)(1000f / rate);
    };

    public void Dispose() => this.limiter.Dispose();
}
