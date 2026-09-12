namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class RateLimiterTests
{
    private static SpeakerIdentity Pc(int id = 0) =>
        new($"pc:Test Player@66{id}", "Test Player", 1, 1, 0, 66);

    private static SpeakerIdentity Npc =>
        new("npc:merlwyb", "Merlwyb", 1, 1, 0, null);

    [Fact]
    public void FirstMessage_Passes_SecondInsideWindowIsLimited()
    {
        var now = 1_000L;
        var limiter = new RateLimiter(() => 500, () => now);

        Assert.False(limiter.TryRateLimit("pc:a@66"));
        Assert.True(limiter.TryRateLimit("pc:a@66"));

        now = 1_400;
        Assert.True(limiter.TryRateLimit("pc:a@66"));

        now = 1_501; // past the 500 ms window
        Assert.False(limiter.TryRateLimit("pc:a@66"));
    }

    [Fact]
    public void SpeakersAreLimitedIndependently()
    {
        var now = 0L;
        var limiter = new RateLimiter(() => 1_000, () => now);

        Assert.False(limiter.TryRateLimit("pc:a@66"));
        Assert.False(limiter.TryRateLimit("pc:b@66"));
        Assert.True(limiter.TryRateLimit("pc:a@66"));
    }

    [Fact]
    public void DisabledLimiter_NeverLimits_AnySpeaker()
    {
        var now = 0L;
        var limiter = new ConfiguredRateLimiter(() => false, () => 100, () => now);
        for (var i = 0; i < 5; i++)
        {
            Assert.False(limiter.TryRateLimit(Pc()));
        }
    }

    [Fact]
    public void NpcSpeakers_AreNeverLimited()
    {
        var now = 0L;
        var limiter = new ConfiguredRateLimiter(() => true, () => 1, () => now);
        for (var i = 0; i < 5; i++)
        {
            Assert.False(limiter.TryRateLimit(Npc));
        }
    }

    [Fact]
    public void PcSpeaker_IsLimitedAfterFirstMessageWithinWindow()
    {
        var now = 0L;
        var limiter = new ConfiguredRateLimiter(() => true, () => 1 /* 1000 ms */, () => now);

        Assert.False(limiter.TryRateLimit(Pc()));
        now = 500;
        Assert.True(limiter.TryRateLimit(Pc()));
        now = 1_001;
        Assert.False(limiter.TryRateLimit(Pc()));
    }

    [Fact]
    public void ZeroRate_LimitsEverythingAfterTheFirstMessage()
    {
        var now = 0L;
        var limiter = new ConfiguredRateLimiter(() => true, () => 0, () => now);

        Assert.False(limiter.TryRateLimit(Pc()));
        now = 1_000_000;
        Assert.True(limiter.TryRateLimit(Pc()));
    }

    [Fact]
    public void Pruning_IsObservablyIdentical()
    {
        var now = 0L;
        var limiter = new RateLimiter(() => 10, () => now);

        // Grow past the prune threshold with stale entries, then verify semantics hold.
        for (var i = 0; i < 200; i++)
        {
            limiter.TryRateLimit($"pc:player{i}@66");
        }

        now = 1_000_000;
        Assert.False(limiter.TryRateLimit("pc:player0@66")); // stale = not limited
        Assert.True(limiter.TryRateLimit("pc:player0@66"));  // refreshed = limited
    }
}
