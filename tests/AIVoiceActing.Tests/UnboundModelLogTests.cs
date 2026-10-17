namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class UnboundModelLogTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Records_CountsLines_AndKeepsTheLatestSpeaker()
    {
        var log = new UnboundModelLog();
        log.Record(4071, "Yuyuhase", T0);
        log.Record(4071, "Hoobigo trader", T0.AddSeconds(5));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(2, entry.Lines);
        Assert.Equal("Hoobigo trader", entry.LastSpeaker);
    }

    [Fact]
    public void HumanModelZero_IsNeverRecorded()
    {
        var log = new UnboundModelLog();
        log.Record(0, "Tataru", T0);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Dismissed_StaysOutForTheSession()
    {
        var log = new UnboundModelLog();
        log.Record(2954, "a", T0);
        log.Dismiss(2954);
        log.Record(2954, "a", T0.AddSeconds(1));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Entries_AreMostRecentFirst()
    {
        var log = new UnboundModelLog();
        log.Record(1, "a", T0);
        log.Record(2, "b", T0.AddSeconds(1));
        Assert.Equal([2, 1], log.Entries.Select(e => e.ModelId));
    }

    [Fact]
    public void PlanVolume_ScalesTheProfileVolume()
    {
        var plan = EmotionPlan.Neutral with { Volume = 0.8f };
        var request = SpeechRequestMapper.ToRequest(plan, "bf_emma", "line", volume: 1.5f);
        Assert.Equal(1.2f, request.Volume, 4);
    }
}
