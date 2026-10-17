namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>Last-mile audio shaping: gain, levelling, the soft limiter and edge fades.</summary>
public sealed class AudioPolishTests
{
    private static float[] Tone(int length, float amplitude) =>
        [.. Enumerable.Range(0, length).Select(i => amplitude * MathF.Sin(i * 0.05f))];

    [Fact]
    public void Gain_AppliesBelowTheLimiterKnee()
    {
        var shaped = AudioPolish.Finish([0f, 0.2f, 0.2f, 0.2f, 0f], 24000, 2f, levelLoudness: false);
        Assert.Equal(0.4f, shaped[2], 4);
    }

    [Fact]
    public void LoudGain_NeverReachesFullScale()
    {
        var shaped = AudioPolish.Finish(Tone(4800, 0.9f), 24000, 2f, levelLoudness: false);
        Assert.All(shaped, s => Assert.InRange(s, -1f, 1f));
        Assert.True(shaped.Max() < 1f);
    }

    [Fact]
    public void Levelling_BringsQuietAndLoudVoicesTogether()
    {
        var quiet = AudioPolish.Finish(Tone(24000, 0.05f), 24000, 1f, levelLoudness: true);
        var loud = AudioPolish.Finish(Tone(24000, 0.3f), 24000, 1f, levelLoudness: true);
        Assert.Equal(AudioPolish.Rms(quiet), AudioPolish.Rms(loud), 2);
    }

    [Fact]
    public void Levelling_CapsTheBoostForNearSilence()
    {
        var input = Tone(24000, 0.001f);
        var shaped = AudioPolish.Finish(input, 24000, 1f, levelLoudness: true);
        Assert.True(AudioPolish.Rms(shaped) <= (AudioPolish.Rms(input) * AudioPolish.MaxLevelGain) + 1e-6f);
    }

    [Fact]
    public void Edges_FadeFromAndToSilence()
    {
        var shaped = AudioPolish.Finish([.. Enumerable.Repeat(0.5f, 2400)], 24000, 1f, levelLoudness: false);
        Assert.Equal(0f, shaped[0]);
        Assert.True(Math.Abs(shaped[^1]) < 0.01f);
        Assert.Equal(0.5f, shaped[1200], 4);
    }

    [Fact]
    public void Input_IsNotMutated()
    {
        float[] input = [0.3f, 0.3f, 0.3f];
        _ = AudioPolish.Finish(input, 24000, 2f, levelLoudness: true);
        Assert.Equal([0.3f, 0.3f, 0.3f], input);
    }
}
