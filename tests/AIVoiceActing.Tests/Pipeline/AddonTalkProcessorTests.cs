namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class AddonTalkProcessorTests
{
    private static AddonTalkState Visible(
        string speaker, string text, AddonPollSource source = AddonPollSource.FrameworkUpdate) =>
        new(speaker, text, source);

    private static AddonTalkProcessor Processor(bool skipVoiced = true) => new(() => skipVoiced);

    [Fact]
    public void FirstVisibleSample_Speaks_AndFlagsAdvance()
    {
        var result = Processor().Poll(Visible("Y'shtola", "Hello."));
        Assert.Equal(TalkDecision.Speak, result.Decision);
        Assert.True(result.Advanced);
        Assert.Equal("Y'shtola", result.Speaker);
        Assert.Equal("Hello.", result.Text);
        Assert.Equal("Hello.", result.RawText);
    }

    [Fact]
    public void SameSampleAgain_IsDuplicate_WithoutSpeak()
    {
        var processor = Processor();
        Assert.Equal(TalkDecision.Speak, processor.Poll(Visible("Y'shtola", "Hello.")).Decision);
        var second = processor.Poll(Visible("Y'shtola", "Hello."));
        Assert.Equal(TalkDecision.Duplicate, second.Decision);
        // TTT: an identical sample never reaches HandleChange (ComponentUpdateState),
        // so no advance fires; only a CHANGED sample (voice-line round-trip) does.
    }

    [Fact]
    public void ChangedText_SpeaksAgain()
    {
        var processor = Processor();
        processor.Poll(Visible("Y'shtola", "Hello."));
        var next = processor.Poll(Visible("Y'shtola", "Goodbye."));
        Assert.Equal(TalkDecision.Speak, next.Decision);
        Assert.Equal("Goodbye.", next.Text);
    }

    [Fact]
    public void OpenToClosed_IsACloseTransition_ThatAdvances()
    {
        var processor = Processor();
        processor.Poll(Visible("Y'shtola", "Hello."));
        var closed = processor.Poll(AddonTalkState.Closed);
        Assert.Equal(TalkDecision.Closed, closed.Decision);
        Assert.True(closed.Advanced); // the line just ended — interrupt exactly once
    }

    [Fact]
    public void ClosedToClosed_IsANoOp()
    {
        var processor = Processor();
        processor.Poll(AddonTalkState.Closed); // initial closed sample: nothing open ended
        var closed = processor.Poll(AddonTalkState.Closed);
        Assert.Equal(TalkDecision.Closed, closed.Decision);
        Assert.False(closed.Advanced);
    }

    [Fact]
    public void VoiceLinePoll_WithSkipOn_SuppressesTheLine()
    {
        var processor = Processor(skipVoiced: true);
        var result = processor.Poll(Visible("Y'shtola", "Voiced line.", AddonPollSource.VoiceLinePlayback));
        Assert.Equal(TalkDecision.SkipVoiced, result.Decision);
    }

    [Fact]
    public void VoiceLinePoll_WithSkipOff_StillSpeaks()
    {
        var processor = Processor(skipVoiced: false);
        var result = processor.Poll(Visible("Y'shtola", "Voiced line.", AddonPollSource.VoiceLinePlayback));
        Assert.Equal(TalkDecision.Speak, result.Decision);
    }

    [Fact]
    public void FrameworkPollAfterVoiceLinePoll_IsDuplicateSuppressed()
    {
        // TTT: the voice-line poll and the framework poll arrive back-to-back for the same
        // line; the second invocation must not be spoken again.
        var processor = Processor();
        var first = processor.Poll(Visible("Y'shtola", "Line.", AddonPollSource.VoiceLinePlayback));
        var second = processor.Poll(Visible("Y'shtola", "Line.", AddonPollSource.FrameworkUpdate));
        Assert.Equal(TalkDecision.SkipVoiced, first.Decision);
        Assert.Equal(TalkDecision.Duplicate, second.Decision);
        Assert.True(second.Advanced); // the sample changed (poll source differs)
    }

    [Fact]
    public void Normalization_AppliesBeforeDedupe()
    {
        var processor = Processor();
        processor.Poll(Visible("N", "a─b"));
        var second = processor.Poll(Visible("N", "a - b"));
        Assert.Equal(TalkDecision.Duplicate, second.Decision); // same after normalization
    }

    [Fact]
    public void NullText_ReadsAsEmpty()
    {
        var result = Processor().Poll(new AddonTalkState("Speaker", null, AddonPollSource.FrameworkUpdate));
        Assert.Equal(TalkDecision.Speak, result.Decision);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void ReplayedDialogue_AfterClose_SpeaksAgain()
    {
        var processor = Processor();
        Assert.Equal(TalkDecision.Speak, processor.Poll(Visible("Feo Ul", "Hello!")).Decision);

        // The double poll while the same line is on screen is still suppressed.
        Assert.Equal(TalkDecision.Duplicate, processor.Poll(Visible("Feo Ul", "Hello!")).Decision);

        // Dialogue closes; replaying the identical line must speak again, not dedupe.
        Assert.Equal(TalkDecision.Closed, processor.Poll(AddonTalkState.Closed).Decision);
        Assert.Equal(TalkDecision.Speak, processor.Poll(Visible("Feo Ul", "Hello!")).Decision);
    }
}
