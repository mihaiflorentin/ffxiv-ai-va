namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Text;
using Xunit;

/// <summary>Table tests porting TextToTalk's TalkUtils.RemoveStutters cases, plus chained
/// s-s-s-style stuttering and non-mutation guards.</summary>
public sealed class StutterRemoverTests
{
    [Theory]
    [InlineData("So th-there w-was", "So there was")]
    [InlineData("S-s-s-so cold", "So cold")]
    [InlineData("s-s-s-stutter", "stutter")]
    [InlineData("I-I-I know what I saw", "I know what I saw")]
    public void RemoveStutters_RemovesRepeatedHyphenatedLetters(string input, string expected) =>
        Assert.Equal(expected, StutterRemover.Remove(input));

    [Theory]
    [InlineData("Th-this has different c-capitalization", "This has different capitalization")]
    [InlineData("É-é-é", "É")]
    [InlineData("S-s-s-so cold", "So cold")]
    public void RemoveStutters_MaintainsCapitalization(string input, string expected) =>
        Assert.Equal(expected, StutterRemover.Remove(input));

    [Theory]
    [InlineData("A-Ruhn?", "A-Ruhn?")]
    [InlineData("e-mail me", "e-mail me")]
    [InlineData("a long-standing feud", "a long-standing feud")]
    public void RemoveStutters_DoesNotRemoveDifferentHyphenatedLetters(string input, string expected) =>
        Assert.Equal(expected, StutterRemover.Remove(input));

    [Theory]
    [InlineData("", "")]
    [InlineData("お礼がてら、あなたもいかがかしら？", "お礼がてら、あなたもいかがかしら？")] // quest/006/TstPln905_00659
    [InlineData("Plain line, no stuttering.", "Plain line, no stuttering.")]
    public void RemoveStutters_EdgeCasesWork(string input, string expected) =>
        Assert.Equal(expected, StutterRemover.Remove(input));
}
