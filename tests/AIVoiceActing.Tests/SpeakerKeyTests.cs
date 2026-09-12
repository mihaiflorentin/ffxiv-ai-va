namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class SpeakerKeyTests
{
    [Theory]
    [InlineData("Mihai Testa", 66, "pc:Mihai Testa@66")]
    [InlineData("mihai testa", 123, "pc:mihai testa@123")] // title-case preserved as given
    [InlineData("Feo Ul", 66, "pc:Feo Ul@66")]
    public void ForPlayer_FormatsPcKey(string name, ushort world, string expected) =>
        Assert.Equal(expected, SpeakerKey.ForPlayer(name, world));

    [Fact]
    public void ForPlayer_StripsTrailingWorldSuffix() =>
        Assert.Equal("pc:Mihai Testa@66", SpeakerKey.ForPlayer("Mihai Testa»Jenesis", 66));

    [Fact]
    public void ForPlayer_TrimsWhitespace() =>
        Assert.Equal("pc:Mihai Testa@66", SpeakerKey.ForPlayer("  Mihai Testa  ", 66));

    [Theory]
    [InlineData("Feo Ul", "npc:feo ul")]
    [InlineData("  G'raha Tia  ", "npc:g'raha tia")]
    [InlineData("Skstealwxyn", "npc:skstealwxyn")]
    public void ForNpc_FormatsLowercaseNpcKey(string name, string expected) =>
        Assert.Equal(expected, SpeakerKey.ForNpc(name));

    [Fact]
    public void ForNpc_StripsSuffix() =>
        Assert.Equal("npc:feo ul", SpeakerKey.ForNpc("Feo Ul»First"));
}
