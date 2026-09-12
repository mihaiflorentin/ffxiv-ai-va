namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using Xunit;

public sealed class SpeechRequestMappingTests
{
    [Fact]
    public void Mapper_ClampsExaggerationIntoContractRange()
    {
        var high = SpeechRequestMapper.ToRequest(
            new EmotionPlan("angry", 1.7f, [], 0, 1f), "default", "hi");
        Assert.Equal(1f, high.Exaggeration);

        var low = SpeechRequestMapper.ToRequest(
            new EmotionPlan("sad", -0.2f, [], 0, 1f), "default", "hi");
        Assert.Equal(0f, low.Exaggeration);
    }

    [Fact]
    public void Mapper_CarriesTagsUnchanged()
    {
        var request = SpeechRequestMapper.ToRequest(
            EmotionPlan.Neutral with { Tags = ["laughs", "sighs"] }, "default", "well.");
        Assert.Equal(["laughs", "sighs"], request.Tags);
    }

    [Fact]
    public void BuildPromptText_PrefixesTags()
    {
        Assert.Equal("[laughs] [sighs] Hello.", ChatterboxSynthesizer.BuildPromptText(["laughs", "sighs"], "Hello."));
        Assert.Equal("Hello.", ChatterboxSynthesizer.BuildPromptText([], "Hello."));
    }
}
