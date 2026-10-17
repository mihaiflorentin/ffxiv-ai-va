namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// The streaming counterpart of <see cref="AudioPolish.Finish"/>: reads a <see cref="SpeechAudioStream"/>
/// and applies the same shaping (gain, soft limiter, short fades at both ends) as the audio goes by.
/// Loudness levelling cannot see the whole line, so it measures the first half second that is ready and
/// holds that gain for the rest of the line. The last fade's worth of samples is held back until the
/// producer completes, so the closing fade lands on the real end of the line.
/// </summary>
public sealed class PolishedPcmSource : IPcmSource
{
    /// <summary>How much of the start of the line the levelling measurement looks at.</summary>
    public const int LevelWindowMs = 500;

    private readonly SpeechAudioStream stream;
    private readonly bool levelLoudness;
    private readonly int fadeSamples;
    private float gain;
    private bool started;
    private long position;

    public PolishedPcmSource(SpeechAudioStream stream, float gain, bool levelLoudness)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.gain = Math.Max(0f, gain);
        this.levelLoudness = levelLoudness;
        this.fadeSamples = Math.Max(1, stream.SampleRate * AudioPolish.FadeMs / 1000);
    }

    public int SampleRate => this.stream.SampleRate;

    public bool IsFinished => this.stream.IsFinished;

    public bool WaitForData(int timeoutMs) => this.stream.WaitForData(timeoutMs);

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Completed first: once it is true nothing more is appended, so Buffered is exact.
        var completed = this.stream.IsCompleted;
        var buffered = this.stream.Buffered;
        var allowed = completed ? buffered : Math.Max(0, buffered - this.fadeSamples);
        if (allowed == 0)
        {
            return 0;
        }

        if (!this.started)
        {
            this.started = true;
            if (this.levelLoudness)
            {
                this.gain *= this.LevelGain(Math.Min(allowed, this.SampleRate * LevelWindowMs / 1000));
            }
        }

        var count = this.stream.Read(buffer[..Math.Min(buffer.Length, allowed)]);
        var remainingAfter = completed ? this.stream.Buffered : int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var value = AudioPolish.SoftLimit(buffer[i] * this.gain);
            if (this.position + i < this.fadeSamples)
            {
                value *= (this.position + i) / (float)this.fadeSamples;
            }

            if (completed)
            {
                var leftAfter = (long)(count - 1 - i) + remainingAfter;
                if (leftAfter < this.fadeSamples)
                {
                    value *= leftAfter / (float)this.fadeSamples;
                }
            }

            buffer[i] = value;
        }

        this.position += count;
        return count;
    }

    private float LevelGain(int window)
    {
        var probe = new float[window];
        var got = this.stream.Peek(probe);
        var rms = AudioPolish.Rms(probe.AsSpan(0, got));
        return rms > 1e-5f ? Math.Min(AudioPolish.MaxLevelGain, AudioPolish.TargetRms / rms) : 1f;
    }
}
