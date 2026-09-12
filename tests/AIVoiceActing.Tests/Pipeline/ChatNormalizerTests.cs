namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class ChatNormalizerTests
{
    [Fact]
    public void NormalizePunctuation_PortCases()
    {
        Assert.Equal("a - b", ChatTextNormalizer.NormalizePunctuation("a─b"));
        Assert.Equal("a - b", ChatTextNormalizer.NormalizePunctuation("a—b"));
        Assert.Equal("Kan-e-Senna", ChatTextNormalizer.NormalizePunctuation("Kan–e–Senna"));
        Assert.Equal("", ChatTextNormalizer.NormalizePunctuation(null));
    }

    [Fact]
    public void StripWorldFromText_RemovesWorldSuffixes()
    {
        Assert.Equal(
            "Tell Jenesis hello",
            ChatTextNormalizer.StripWorldFromText("Tell Jenesis»Jenesis hello", "Jenesis"));
    }

    [Fact]
    public void StripWorldFromText_LeavesTextWithoutSuffixes()
    {
        Assert.Equal("plain message", ChatTextNormalizer.StripWorldFromText("plain message", "Jenesis"));
    }

    [Fact]
    public void StripWorldFromText_EmptyWorld_IsIdentity()
    {
        Assert.Equal("a»b", ChatTextNormalizer.StripWorldFromText("a»b", ""));
    }
}
