namespace AIVoiceActing.Domain;

/// <summary>
/// Naive pitch shift by resampling (output[i] = input[i * pitch], linear interpolation): the same colouring the
/// Kokoro adapter gives a pitched line, applied to a real reference clip so the cloning engine hears the voice
/// at the pitch the profile asks for.
/// </summary>
public static class ClipPitch
{
    public static float[] Resample(float[] samples, float pitch)
    {
        if (samples.Length == 0 || Math.Abs(pitch - 1f) < 0.01f)
        {
            return samples;
        }

        var length = Math.Max(1, (int)(samples.Length / pitch));
        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var at = i * (double)pitch;
            var lo = Math.Min((int)at, samples.Length - 1);
            var hi = Math.Min(lo + 1, samples.Length - 1);
            var frac = (float)(at - lo);
            result[i] = samples[lo] + ((samples[hi] - samples[lo]) * frac);
        }

        return result;
    }
}
