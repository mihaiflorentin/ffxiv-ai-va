namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class EmotionRulesTests
{
    private static void AssertPlan(
        EmotionPlan plan,
        string emotion,
        float exaggeration,
        int pauseMs = 0,
        string[]? tags = null)
    {
        Assert.Equal(emotion, plan.Emotion);
        Assert.Equal(exaggeration, plan.Exaggeration);
        Assert.Equal(pauseMs, plan.PauseBeforeMs);
        Assert.Equal(1f, plan.Volume);
        Assert.Equal(tags ?? [], plan.Tags);
    }

    [Theory]
    [InlineData("Hello there!")]
    [InlineData("Run!!")]
    public void Exclamation_IsExcited(string line) => AssertPlan(EmotionRules.Plan(line), "excited", 0.75f);

    [Theory]
    [InlineData("WHERE IS MY WIFE")]
    [InlineData("STOP THAT!")] // caps outrank "!" (priority 1 before 4)
    [InlineData("it is an OK answer")] // any 2+ letter caps word
    public void AllCaps_IsAngry_AndWinsOverOtherSignals(string line) => AssertPlan(EmotionRules.Plan(line), "angry", 0.8f);

    [Theory]
    [InlineData("I don't know...")]
    [InlineData("I suppose…")]
    public void Ellipsis_IsSad_WithPause(string line) => AssertPlan(EmotionRules.Plan(line), "sad", 0.3f, pauseMs: 250);

    [Fact]
    public void Sigh_Keyword_IsSad_WithPauseAndTag() =>
        AssertPlan(EmotionRules.Plan("oh sigh, whatever"), "sad", 0.3f, pauseMs: 250, tags: ["sigh"]);

    [Fact]
    public void Ellipsis_Alone_IsSad_WithoutSighTag() =>
        AssertPlan(EmotionRules.Plan("goodbye then..."), "sad", 0.3f, pauseMs: 250, tags: []);

    [Theory]
    [InlineData("haha nice one")]
    [InlineData("hahah, sure")]
    [InlineData("hahaha nice one")]
    [InlineData("Hehe, good point")]
    [InlineData("hehehe, no way")]
    [InlineData("lol sure")]
    public void LaughToken_IsAmused_WithLaughsTag(string line) =>
        AssertPlan(EmotionRules.Plan(line), "amused", 0.6f, tags: ["laughs"]);

    [Theory]
    [InlineData("Where are you?")]
    [InlineData("Any plans??")]
    public void QuestionMark_IsCurious(string line) => AssertPlan(EmotionRules.Plan(line), "curious", 0.6f);

    [Theory]
    [InlineData("Plain line.")]
    [InlineData("No signals here")]
    [InlineData("ha alone is not a laugh")]
    [InlineData("he alone is not a laugh")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NoSignals_IsNeutral(string? line) => Assert.Equal(EmotionRules.Plan(line), EmotionPlan.Neutral);

    [Fact]
    public void Priority_CapsBeatsSad() => AssertPlan(EmotionRules.Plan("WHY would you..."), "angry", 0.8f);

    [Fact]
    public void Priority_SadBeatsAmused() =>
        AssertPlan(EmotionRules.Plan("haha, I guess..."), "sad", 0.3f, pauseMs: 250);

    [Fact]
    public void Priority_AmusedBeatsExcited() =>
        AssertPlan(EmotionRules.Plan("Hahaha, that's great!"), "amused", 0.6f, tags: ["laughs"]);

    [Fact]
    public void Priority_ExcitedBeatsCurious() => AssertPlan(EmotionRules.Plan("You did what?!"), "excited", 0.75f);
}
