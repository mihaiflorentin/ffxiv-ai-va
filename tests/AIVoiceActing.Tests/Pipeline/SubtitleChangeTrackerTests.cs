namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class SubtitleChangeTrackerTests
{
    [Fact]
    public void FirstLine_Emits()
    {
        var tracker = new SubtitleChangeTracker();

        Assert.True(tracker.ShouldEmit("(cutscene)", "Hello"));
    }

    [Fact]
    public void SameLineAcrossTicks_EmitsOnce()
    {
        var tracker = new SubtitleChangeTracker();

        Assert.True(tracker.ShouldEmit("(cutscene)", "Hello"));
        Assert.False(tracker.ShouldEmit("(cutscene)", "Hello"));
        Assert.False(tracker.ShouldEmit("(cutscene)", "Hello"));
    }

    [Fact]
    public void ChangedText_Emits()
    {
        var tracker = new SubtitleChangeTracker();
        tracker.ShouldEmit("(cutscene)", "Hello");

        Assert.True(tracker.ShouldEmit("(cutscene)", "Goodbye"));
    }

    [Fact]
    public void ChangedSpeaker_Emits()
    {
        var tracker = new SubtitleChangeTracker();
        tracker.ShouldEmit("Y'shtola", "Hello");

        Assert.True(tracker.ShouldEmit("Thancred", "Hello"));
    }

    [Fact]
    public void RepeatedAlternatingLines_EmitOnChangeOnly()
    {
        var tracker = new SubtitleChangeTracker();
        var emissions = 0;
        foreach (var line in new[] { "one", "two", "one", "one", "two" })
        {
            if (tracker.ShouldEmit("(cutscene)", line))
            {
                emissions++;
            }
        }

        // one (emit) two (emit) one (emit) one (dup) two (emit) = 4
        Assert.Equal(4, emissions);
    }
}
