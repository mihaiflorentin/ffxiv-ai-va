namespace AIVoiceActing.Domain;

/// <summary>
/// Joins the audio of consecutive decoder windows into one seamless stream. Each window covers its chunk
/// plus context on both sides (see <see cref="ChunkPlan"/>); at every chunk boundary the two windows both
/// hold audio for the same stretch of speech, so the stitcher cross-fades them over a short region centred
/// on the boundary (equal power: the sine and cosine weights keep the energy level, and sum to a smooth
/// handover) and plays the middle of each window untouched. The last <c>2 x halfFade</c> samples of a
/// non-final chunk are held back until the next window arrives.
/// </summary>
public sealed class ChunkStitcher
{
    private readonly int samplesPerToken;
    private readonly int halfFade;
    private float[]? held;

    /// <param name="samplesPerToken">Audio samples one speech token decodes to (960 at 24 kHz).</param>
    /// <param name="halfFadeSamples">Half the cross-fade length; the fade spans the boundary +/- this many samples.</param>
    public ChunkStitcher(int samplesPerToken = 960, int halfFadeSamples = 480)
    {
        if (samplesPerToken < 1 || halfFadeSamples < 0)
        {
            throw new ArgumentException("Samples per token must be positive and the fade cannot be negative.");
        }

        this.samplesPerToken = samplesPerToken;
        this.halfFade = halfFadeSamples;
    }

    /// <summary>Windows whose decoded length differed from the plan (padded or trimmed to fit).</summary>
    public int LengthMismatches { get; private set; }

    /// <summary>
    /// Takes the decoded audio of <paramref name="plan"/>'s window and returns the samples that can play now.
    /// Windows must arrive in plan order. Over the whole stream the returned pieces add up to exactly
    /// <c>tokens x samplesPerToken</c> samples.
    /// </summary>
    public float[] Add(ChunkPlan plan, ReadOnlySpan<float> window)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.LeftContext * this.samplesPerToken < this.halfFade && !plan.IsFirst)
        {
            throw new ArgumentException("The window has less left context than the cross-fade needs.", nameof(plan));
        }

        if (plan.Lookahead * this.samplesPerToken < this.halfFade && !plan.IsLast)
        {
            throw new ArgumentException("The window has less lookahead than the cross-fade needs.", nameof(plan));
        }

        var expected = (plan.WindowEnd - plan.WindowStart) * this.samplesPerToken;
        float[] audio;
        if (window.Length == expected)
        {
            audio = window.ToArray();
        }
        else
        {
            this.LengthMismatches++;
            audio = new float[expected];
            window[..Math.Min(expected, window.Length)].CopyTo(audio);
        }

        var origin = plan.WindowStart * this.samplesPerToken;
        var output = new List<float>(audio.Length);
        int emitFrom;
        if (plan.IsFirst)
        {
            emitFrom = 0;
        }
        else
        {
            var boundary = plan.Start * this.samplesPerToken;
            var head = audio.AsSpan(boundary - this.halfFade - origin, 2 * this.halfFade);
            output.AddRange(CrossFade(this.held ?? new float[2 * this.halfFade], head));
            emitFrom = boundary + this.halfFade;
        }

        if (plan.IsLast)
        {
            output.AddRange(audio.AsSpan(emitFrom - origin));
            this.held = null;
        }
        else
        {
            var boundary = plan.End * this.samplesPerToken;
            var cut = boundary - this.halfFade;
            if (cut < emitFrom)
            {
                throw new ArgumentException("The chunk is shorter than the two cross-fades around it.", nameof(plan));
            }

            output.AddRange(audio.AsSpan(emitFrom - origin, cut - emitFrom));
            this.held = audio.AsSpan(cut - origin, 2 * this.halfFade).ToArray();
        }

        return [.. output];
    }

    /// <summary>
    /// Equal-power cross-fade of two equal-length stretches: <paramref name="outgoing"/> fades out on a
    /// cosine, <paramref name="incoming"/> fades in on a sine, sampled at the sample centres. Powers of the
    /// weights sum to 1 everywhere, and the first/last sample is within a hair of the pure side.
    /// </summary>
    public static float[] CrossFade(ReadOnlySpan<float> outgoing, ReadOnlySpan<float> incoming)
    {
        if (outgoing.Length != incoming.Length)
        {
            throw new ArgumentException("Both stretches must have the same length.");
        }

        var length = outgoing.Length;
        var mixed = new float[length];
        for (var i = 0; i < length; i++)
        {
            var angle = 0.5 * Math.PI * ((i + 0.5) / length);
            mixed[i] = (float)((outgoing[i] * Math.Cos(angle)) + (incoming[i] * Math.Sin(angle)));
        }

        return mixed;
    }
}
