namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class BritishFallbackTests
{
    [Theory]
    [InlineData("am_michael", null, "bm_george")]
    [InlineData("bm_lewis", null, "bm_george")]
    [InlineData("af_heart", null, "bf_emma")]
    [InlineData("isles.sc_m_01", "male", "bm_george")]
    [InlineData("isles.sc_f_01", "female", "bf_emma")]
    [InlineData("isles.unknown", null, "bf_emma")]
    [InlineData("am_michael", "female", "bf_emma")]
    [InlineData(null, null, "bf_emma")]
    public void FallbackFor_IsAlwaysBritishOfTheKnownSex(string? id, string? sex, string expected) =>
        Assert.Equal(expected, VoiceCatalog.FallbackFor(id, sex));

    [Theory]
    [InlineData(null, "neutral")]
    [InlineData("", "neutral")]
    [InlineData("  ", "neutral")]
    [InlineData("Angry", "angry")]
    public void NowSpeakingMood_ReadsAsLowerCaseWord(string? emotion, string expected) =>
        Assert.Equal(expected, NowSpeakingModel.Mood(emotion));
}
