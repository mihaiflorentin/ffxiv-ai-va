namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

public sealed class RulesEmotionDirectorTests
{
    private static EmotionContext Context(string line) =>
        new(new SpeakerIdentity("npc: test", "Test", null, null, null, null), line, []);

    [Theory]
    [InlineData("Hello there!")]
    [InlineData("WHERE IS MY WIFE")]
    [InlineData("I don't know...")]
    [InlineData("haha nice one")]
    [InlineData("Where are you?")]
    [InlineData("Plain line.")]
    public async Task Plan_MatchesEmotionRulesExactly(string line)
    {
        var plan = await RulesEmotionDirector.Instance
            .PlanAsync(Context(line), CancellationToken.None);

        var expected = EmotionRules.Plan(line);
        Assert.Equal(expected.Emotion, plan.Emotion);
        Assert.Equal(expected.Exaggeration, plan.Exaggeration);
        Assert.Equal(expected.PauseBeforeMs, plan.PauseBeforeMs);
        Assert.Equal(expected.Tags, plan.Tags);
        Assert.Equal(expected.Volume, plan.Volume);
    }

    [Fact]
    public async Task Plan_NeutralLine_ReturnsTheNeutralSingleton() =>
        Assert.Same(
            EmotionPlan.Neutral,
            await RulesEmotionDirector.Instance
                .PlanAsync(Context("Plain line."), CancellationToken.None));

    [Theory]
    [InlineData("angry", 0.8f, "STOP THAT!")]
    [InlineData("sad", 0.3f, "I suppose...")]
    [InlineData("amused", 0.6f, "hahaha sure")]
    [InlineData("excited", 0.75f, "Run!")]
    [InlineData("curious", 0.6f, "what?")]
    [InlineData("neutral", 0.5f, "fine.")]
    public async Task Plan_DirectionTable(string emotion, float exaggeration, string line)
    {
        var plan = await RulesEmotionDirector.Instance
            .PlanAsync(Context(line), CancellationToken.None);

        Assert.Equal(emotion, plan.Emotion);
        Assert.Equal(exaggeration, plan.Exaggeration);
    }

    [Fact]
    public async Task Plan_EmptyHistory_IsAccepted() =>
        Assert.Equal(
            0.75f,
            (await RulesEmotionDirector.Instance.PlanAsync(
                new EmotionContext(
                    new SpeakerIdentity("pc:a@b", "A", 1, 1, 1, 66),
                    "Hello!",
                    History: []),
                CancellationToken.None)).Exaggeration);

    [Fact]
    public async Task Plan_IsDelegation_NotRestatedRules()
    {
        // Pause/tag details must come from the shared table, byte-for-byte.
        var plan = await RulesEmotionDirector.Instance
            .PlanAsync(Context("oh sigh, whatever"), CancellationToken.None);

        Assert.Equal("sad", plan.Emotion);
        Assert.Equal(250, plan.PauseBeforeMs);
        Assert.Equal(["sigh"], plan.Tags);
    }
}
