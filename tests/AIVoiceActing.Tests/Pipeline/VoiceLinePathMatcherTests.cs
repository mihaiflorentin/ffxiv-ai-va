namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class VoiceLinePathMatcherTests
{
    [Theory]
    [InlineData("cut/ex1/vo_1001.scd")]
    [InlineData("cut/exdepth/anothertag/vo_abc.scd")]
    [InlineData("cut/ex1/voice/xxx.scd")]
    [InlineData("sound/voice/Vo_Line/anything.scd")]
    public void VoiceLinePaths_AreDetected(string path) => Assert.True(VoiceLinePathMatcher.IsVoiceLine(path));

    [Theory]
    [InlineData("sound/voice/Vo_Emote/laugh.scd")]
    [InlineData("bgcommon/xxx.scd")]
    [InlineData("music/field.scd")]
    [InlineData("sound/battle/xxx.scd")]
    [InlineData("sound/foot/xxx.scd")]
    [InlineData("sound/strm/xxx.scd")]
    [InlineData("sound/vfx/xxx.scd")]
    [InlineData("sound/zingle/xxx.scd")]
    [InlineData("cut/ex1/other/file.scd")]
    [InlineData("")]
    public void NonVoicePaths_AreNotVoiceLines(string path) => Assert.False(VoiceLinePathMatcher.IsVoiceLine(path));

    [Theory]
    [InlineData("bgcommon/xxx.scd")]
    [InlineData("sound/voice/Vo_Emote/laugh.scd")]
    [InlineData("music/field.scd")]
    public void IgnoredSoundTrees_AreRecognized(string path) => Assert.True(VoiceLinePathMatcher.IsIgnoredSound(path));

    [Theory]
    [InlineData("cut/ex1/vo_1.scd")]
    [InlineData("music/field.scd")]
    [InlineData("readme.txt")]
    public void SoundContainerDetection(string path) =>
        Assert.Equal(path.EndsWith(".scd"), VoiceLinePathMatcher.IsSoundContainer(path));
}
