namespace AIVoiceActing.Domain;

/// <summary>
/// Last-mile shaping of a synthesized line before it reaches the device: optional
/// loudness levelling (voices in the bank differ by several dB), the playback gain, a
/// soft limiter so gains above 1 never hard-clip, and short edge fades so lines never
/// start or stop with a click. Pure and allocation-light: works in place on a copy.
/// </summary>
public static class AudioPolish
{
    /// <summary>Target RMS for levelled speech (about -20 dBFS).</summary>
    public const float TargetRms = 0.1f;

    /// <summary>Levelling never boosts more than this (near-silent lines stay quiet).</summary>
    public const float MaxLevelGain = 4f;

    /// <summary>Limiter knee: samples below this pass untouched.</summary>
    public const float LimiterThreshold = 0.8f;

    /// <summary>Edge fade length in milliseconds.</summary>
    public const int FadeMs = 6;

    /// <summary>Returns a shaped copy of <paramref name="samples"/>.</summary>
    public static float[] Finish(float[] samples, int sampleRate, float gain, bool levelLoudness)
    {
        var output = (float[])samples.Clone();
        if (output.Length == 0)
        {
            return output;
        }

        var total = Math.Max(0f, gain);
        if (levelLoudness)
        {
            var rms = Rms(output);
            if (rms > 1e-5f)
            {
                total *= Math.Min(MaxLevelGain, TargetRms / rms);
            }
        }

        for (var i = 0; i < output.Length; i++)
        {
            output[i] = SoftLimit(output[i] * total);
        }

        var fade = Math.Min(output.Length / 2, Math.Max(1, sampleRate * FadeMs / 1000));
        for (var i = 0; i < fade; i++)
        {
            var ramp = i / (float)fade;
            output[i] *= ramp;
            output[output.Length - 1 - i] *= ramp;
        }

        return output;
    }

    /// <summary>Root-mean-square level of the whole buffer.</summary>
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Identity below the knee, then a tanh curve that approaches but never reaches ±1.</summary>
    public static float SoftLimit(float x)
    {
        var magnitude = Math.Abs(x);
        if (magnitude <= LimiterThreshold)
        {
            return x;
        }

        var headroom = 1f - LimiterThreshold;
        var over = (magnitude - LimiterThreshold) / headroom;
        var limited = LimiterThreshold + (headroom * MathF.Tanh(over));
        return Math.Sign(x) * limited;
    }
}
