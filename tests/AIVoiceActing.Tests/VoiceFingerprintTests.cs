namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class VoiceFingerprintTests
{
    [Fact]
    public void Heights_AreTwelveAndDeterministic()
    {
        var a = VoiceFingerprintMath.Heights("af_heart");
        var b = VoiceFingerprintMath.Heights("af_heart");
        Assert.Equal(12, VoiceFingerprintMath.BarCount);
        Assert.Equal(VoiceFingerprintMath.BarCount, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Heights_AreBounded()
    {
        foreach (var id in new[] { "", "a", "af_heart", "bm_george", "voice-with-a-very-long-identifier-0123456789", "voix-été" })
        {
            foreach (var h in VoiceFingerprintMath.Heights(id))
            {
                Assert.InRange(h, VoiceFingerprintMath.MinHeight, VoiceFingerprintMath.MaxHeight);
            }
        }
    }

    [Fact]
    public void DifferentIds_GiveDifferentShapes()
    {
        var shapes = new[] { "af_heart", "af_bella", "am_adam", "bf_emma", "bm_lewis" }
            .Select(id => string.Join(',', VoiceFingerprintMath.Heights(id)))
            .Distinct()
            .Count();
        Assert.Equal(5, shapes);
    }

    [Fact]
    public void Heights_AreCaseSensitiveButStable()
    {
        Assert.Equal(VoiceFingerprintMath.Heights("af_heart"), VoiceFingerprintMath.Heights("af_heart"));
        Assert.NotEqual(VoiceFingerprintMath.Heights("af_heart"), VoiceFingerprintMath.Heights("AF_HEART"));
    }

    [Fact]
    public void Pulse_StaticWhenNotPlayingOrReduced()
    {
        Assert.Equal(1f, VoiceFingerprintMath.PulseScale(3, 1.2, playing: false, reduceMotion: false));
        Assert.Equal(1f, VoiceFingerprintMath.PulseScale(3, 1.2, playing: true, reduceMotion: true));
    }

    [Fact]
    public void Pulse_StaysInRangeAndMovesWhilePlaying()
    {
        var seen = new HashSet<float>();
        for (var t = 0.0; t < 2.0; t += 0.05)
        {
            for (var i = 0; i < VoiceFingerprintMath.BarCount; i++)
            {
                var s = VoiceFingerprintMath.PulseScale(i, t, playing: true, reduceMotion: false);
                Assert.InRange(s, 0.7f, 1.0f);
                seen.Add(s);
            }
        }

        Assert.True(seen.Count > 10);
    }
}
