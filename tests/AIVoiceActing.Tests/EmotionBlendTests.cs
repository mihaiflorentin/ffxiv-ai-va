namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>Classifier scores → delivery plan, blended with the surface rules.</summary>
public sealed class EmotionBlendTests
{
    private static float[] Scores(params (string Label, float Score)[] hits)
    {
        var scores = new float[EmotionBlend.Labels.Length];
        foreach (var (label, score) in hits)
        {
            scores[Array.IndexOf(EmotionBlend.Labels, label)] = score;
        }

        return scores;
    }

    [Fact]
    public void Labels_MatchTheGoEmotionsOrder()
    {
        Assert.Equal(28, EmotionBlend.Labels.Length);
        Assert.Equal("admiration", EmotionBlend.Labels[0]);
        Assert.Equal("sadness", EmotionBlend.Labels[25]);
        Assert.Equal("neutral", EmotionBlend.Labels[27]);
    }

    [Fact]
    public void ConfidentGrief_IsSad_WithSighAndPause_EvenWithoutPunctuation()
    {
        var plan = EmotionBlend.Plan(Scores(("grief", 0.8f)), EmotionRules.Plan("I lost her"));
        Assert.Equal("sad", plan.Emotion);
        Assert.Contains("sigh", plan.Tags);
        Assert.Equal(250, plan.PauseBeforeMs);
        Assert.True(plan.Exaggeration < 0.25f);
    }

    [Fact]
    public void ConfidentAnger_IsAngry_AndLouder()
    {
        var plan = EmotionBlend.Plan(Scores(("anger", 0.9f)), EmotionPlan.Neutral);
        Assert.Equal("angry", plan.Emotion);
        Assert.True(plan.Volume > 1f);
        Assert.True(plan.Exaggeration > 0.8f);
    }

    [Fact]
    public void Fear_IsAfraid_WithGaspWhenStrong()
    {
        var plan = EmotionBlend.Plan(Scores(("fear", 0.7f)), EmotionPlan.Neutral);
        Assert.Equal("afraid", plan.Emotion);
        Assert.Contains("gasp", plan.Tags);
    }

    [Fact]
    public void LowConfidence_KeepsTheRulesPlan()
    {
        var rules = EmotionRules.Plan("Where are you?");
        Assert.Same(rules, EmotionBlend.Plan(Scores(("anger", 0.2f), ("neutral", 0.9f)), rules));
    }

    [Fact]
    public void LiteralLaughInText_BeatsTheModel()
    {
        var rules = EmotionRules.Plan("hahaha, you fool");
        Assert.Same(rules, EmotionBlend.Plan(Scores(("anger", 0.9f)), rules));
    }

    [Fact]
    public void WrongVectorLength_KeepsTheRulesPlan()
    {
        Assert.Same(EmotionPlan.Neutral, EmotionBlend.Plan([0.9f, 0.1f], EmotionPlan.Neutral));
    }

    [Fact]
    public void Sigmoid_MapsLogitsToProbabilities()
    {
        var p = EmotionBlend.Sigmoid([0f, 10f, -10f]);
        Assert.Equal(0.5f, p[0], 4);
        Assert.True(p[1] > 0.99f);
        Assert.True(p[2] < 0.01f);
    }
}
