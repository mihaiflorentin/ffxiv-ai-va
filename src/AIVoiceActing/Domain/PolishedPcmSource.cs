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
    private readonly int stallFadeSamples;
    private readonly int rebufferSamples;
    private readonly long rebufferMaxWaitSamples;
    private float gain;
    private bool started;
    private bool rebuffering;
    private long rebufferWaited;
    private int fadeInDone = int.MaxValue;
    private long position;

    /// <summary>
    /// Once playback ran dry mid-line, it stays quiet until this much has built up again, so a slow stretch
    /// is one clean pause instead of a stutter of single words.
    /// </summary>
    public const int RebufferMs = 800;

    /// <summary>The longest such a pause is stretched: after this much silence it carries on with what it has.</summary>
    public const int RebufferMaxWaitMs = 3000;

    /// <summary>
    /// The sound fades out over this long when it has used up everything that was made, and fades back in over
    /// the same time when it resumes, so a stall is a short dip and not a hard cut and a click.
    /// </summary>
    public const int StallFadeMs = 25;

    public PolishedPcmSource(SpeechAudioStream stream, float gain, bool levelLoudness)
    {
        this.rebufferSamples = Math.Max(1, stream.SampleRate * RebufferMs / 1000);
        this.rebufferMaxWaitSamples = (long)stream.SampleRate * RebufferMaxWaitMs / 1000;
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.gain = Math.Max(0f, gain);
        this.levelLoudness = levelLoudness;
        this.fadeSamples = Math.Max(1, stream.SampleRate * AudioPolish.FadeMs / 1000);
        this.stallFadeSamples = Math.Max(1, stream.SampleRate * StallFadeMs / 1000);
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
        if (allowed == 0 && completed)
        {
            return 0;
        }

        if (this.started && !completed)
        {
            if (allowed == 0 && !this.rebuffering)
            {
                // Ran dry in the middle of the line: wait for a cushion instead of playing scraps as they come.
                this.rebuffering = true;
                this.rebufferWaited = 0;
            }

            if (this.rebuffering)
            {
                if (allowed < this.rebufferSamples && this.rebufferWaited < this.rebufferMaxWaitSamples)
                {
                    this.rebufferWaited += buffer.Length;
                    return 0;
                }

                this.rebuffering = false;
                this.fadeInDone = 0; // the sound picks up again with a short ramp, not a click
            }
        }
        else if (completed)
        {
            if (this.rebuffering)
            {
                // The producer finished while the sound was stopped: what is left picks up with a ramp as well.
                this.fadeInDone = 0;
            }

            this.rebuffering = false;
        }

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

        // A read that takes the last of what was made, with the line still going, leaves the sound at a dead stop
        // until more arrives: end it on a ramp, and wait for a cushion before it starts again.
        // The ramp covers the last stallFadeSamples of what was made, counted from the end of it, so it carries across
        // reads: a short read (a few samples left over) continues the ramp instead of starting a steep one of its own.
        var drains = !completed && count == allowed;
        for (var i = 0; i < count; i++)
        {
            var value = AudioPolish.SoftLimit(buffer[i] * this.gain);
            if (this.position + i < this.fadeSamples)
            {
                value *= (this.position + i) / (float)this.fadeSamples;
            }

            if (this.fadeInDone < this.stallFadeSamples)
            {
                value *= this.fadeInDone / (float)this.stallFadeSamples;
                this.fadeInDone++;
            }

            if (!completed)
            {
                var toEnd = allowed - i;
                if (toEnd <= this.stallFadeSamples)
                {
                    value *= toEnd / (float)(this.stallFadeSamples + 1);
                }
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

        if (drains)
        {
            this.rebuffering = true;
            this.rebufferWaited = 0;
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
