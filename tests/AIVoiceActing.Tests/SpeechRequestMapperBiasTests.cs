namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>Bias handling of <see cref="SpeechRequestMapper.ToRequest"/> (added in Step 4);
/// the zero-bias defaults are pinned by <see cref="SpeechRequestMappingTests"/>.</summary>
public sealed class SpeechRequestMapperBiasTests
{
    [Fact]
    public void Bias_IsAddedBeforeClamping()
    {
        var plan = new EmotionPlan("excited", 0.75f, [], 0, 1f);

        Assert.Equal(1f, SpeechRequestMapper.ToRequest(plan, "default", "hi", 0.4f).Exaggeration);
        Assert.Equal(0f, SpeechRequestMapper.ToRequest(plan, "default", "hi", -3f).Exaggeration);
        Assert.Equal(1f, SpeechRequestMapper.ToRequest(plan, "default", "hi", 0.25f).Exaggeration);
    }

    [Fact]
    public void Bias_DefaultIsZero()
    {
        var request = SpeechRequestMapper.ToRequest(EmotionPlan.Neutral, "default", "hi");

        Assert.Equal(EmotionPlan.Neutral.Exaggeration, request.Exaggeration);
    }
}
