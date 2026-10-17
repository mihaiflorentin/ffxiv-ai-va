namespace AIVoiceActing.UI.State;

using System.Text;

/// <summary>
/// The voice fingerprint's maths: twelve bar heights derived from a stable hash of the voice
/// id, so a voice looks the same on every machine and release. Heights are fractions of the
/// drawing box.
/// </summary>
public static class VoiceFingerprintMath
{
    public const int BarCount = 12;
    public const float MinHeight = 0.25f;
    public const float MaxHeight = 1.0f;

    /// <summary>Seconds for one full pulse cycle.</summary>
    public const double PulsePeriod = 0.9;

    public static float[] Heights(string voiceId)
    {
        var state = Fnv1a(voiceId);
        var result = new float[BarCount];
        for (var i = 0; i < BarCount; i++)
        {
            state = SplitMix(ref state);
            var unit = (state >> 40) / (double)(1UL << 24);
            result[i] = (float)(MinHeight + (unit * (MaxHeight - MinHeight)));
        }

        return result;
    }

    /// <summary>Per-bar height multiplier in 0.7..1 while playing; exactly 1 when idle or motion is reduced.</summary>
    public static float PulseScale(int bar, double seconds, bool playing, bool reduceMotion)
    {
        if (!playing || reduceMotion)
        {
            return 1f;
        }

        var phase = (2 * Math.PI * seconds / PulsePeriod) + (bar * 0.55);
        return (float)(0.85 + (0.15 * Math.Sin(phase)));
    }

    private static ulong Fnv1a(string s)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        var z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
