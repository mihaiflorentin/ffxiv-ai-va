namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class VoiceLineAddressTrackerTests
{
    private const nint Address = 0x1234;

    [Fact]
    public void VoiceLine_LoadThenPlay_Raises()
    {
        var tracker = new VoiceLineAddressTracker();
        tracker.OnSoundLoaded("cut/ex1/vo_1001.scd", Address);
        Assert.True(tracker.OnSoundPlayed(Address));
        Assert.False(tracker.OnSoundPlayed(Address)); // plays only once (prune on play)
    }

    [Fact]
    public void VoiceLineAddress_ReusedByIgnoredTreeSound_IsCleared()
    {
        var tracker = new VoiceLineAddressTracker();
        tracker.OnSoundLoaded("cut/ex1/vo_1001.scd", Address);

        // Music (ignored tree) reuses the address: the entry must be cleared even though
        // the classification ran under the ignore gate.
        tracker.OnSoundLoaded("music/field/day.scd", Address);
        Assert.False(tracker.OnSoundPlayed(Address));
    }

    [Theory]
    [InlineData("bgcommon/xxx.scd")]
    [InlineData("sound/vfx/xxx.scd")]
    [InlineData("sound/voice/Vo_Emote/laugh.scd")]
    public void IgnoredTreeSounds_ClearReusedAddresses(string path)
    {
        var tracker = new VoiceLineAddressTracker();
        tracker.OnSoundLoaded("sound/voice/Vo_Line/x.scd", Address);
        tracker.OnSoundLoaded(path, Address);
        Assert.False(tracker.OnSoundPlayed(Address));
    }

    [Fact]
    public void NonVoiceNonIgnoredSound_ClearsReusedAddress()
    {
        var tracker = new VoiceLineAddressTracker();
        tracker.OnSoundLoaded("cut/ex1/vo_1001.scd", Address);
        tracker.OnSoundLoaded("cut/ex1/other/file.scd", Address);
        Assert.False(tracker.OnSoundPlayed(Address));
    }

    [Fact]
    public void NonVoiceSound_NeverRegisters()
    {
        var tracker = new VoiceLineAddressTracker();
        tracker.OnSoundLoaded("sound/vfx/boom.scd", Address);
        Assert.False(tracker.OnSoundPlayed(Address));
    }

    [Fact]
    public void ClearedAddress_ReturnsALogNote()
    {
        var tracker = new VoiceLineAddressTracker();
        tracker.OnSoundLoaded("cut/ex1/vo_1001.scd", Address);
        Assert.NotNull(tracker.OnSoundLoaded("music/field/day.scd", Address));
        Assert.Null(tracker.OnSoundLoaded("music/field/other.scd", Address + 1));
    }
}
