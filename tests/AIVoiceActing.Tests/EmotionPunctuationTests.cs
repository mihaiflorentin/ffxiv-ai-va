namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class EmotionPunctuationTests
{
    [Theory]
    [InlineData(0.75f)]
    [InlineData(0.9f)]
    [InlineData(1f)]
    public void EnergeticBand_SwapsTerminalPeriodForBang(float exaggeration) =>
        Assert.Equal("Stand back!", EmotionPunctuation.Shape("Stand back.", exaggeration));

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.2f)]
    [InlineData(0f)]
    public void SoftBand_SwapsTerminalPeriodForEllipsis(float exaggeration) =>
        Assert.Equal("I see…", EmotionPunctuation.Shape("I see.", exaggeration));

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.26f)]
    [InlineData(0.74f)]
    public void MidBand_LeavesTextUntouched(float exaggeration) =>
        Assert.Equal("Stand back.", EmotionPunctuation.Shape("Stand back.", exaggeration));

    [Theory]
    [InlineData("Really?", 0.9f)]
    [InlineData("Stop!", 0.9f)]
    [InlineData("Wait…", 0.2f)]
    [InlineData("No terminal punctuation", 0.9f)]
    [InlineData("", 0.9f)]
    public void OtherTerminals_AreNeverModified(string text, float exaggeration) =>
        Assert.Equal(text, EmotionPunctuation.Shape(text, exaggeration));

    [Fact]
    public void Exaggeration_IsClamped()
    {
        Assert.Equal("Go!", EmotionPunctuation.Shape("Go.", 5f));
        Assert.Equal("Go…", EmotionPunctuation.Shape("Go.", -5f));
        Assert.Equal("Go.", EmotionPunctuation.Shape("Go.", 0.5f));
    }
}
