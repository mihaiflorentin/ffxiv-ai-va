namespace AIVoiceActing.Domain;

/// <summary>
/// Knobs of the pre-roll. <see cref="MaxMs"/> is the longest the sound is held back (0 turns the hold off:
/// playback starts with the first audio). <see cref="MinMs"/> is the least audio buffered before the sound
/// starts, <see cref="MarginMs"/> spare room for a window that comes in late, and
/// <see cref="TokensPerChar"/> turns the text length into an expected number of speech tokens until the
/// language model has finished (156 tokens for 231 characters in the measured example).
/// </summary>
public sealed record PrerollOptions(
    int MaxMs = 6000,
    int MinMs = 1000,
    int MarginMs = 300,
    double TokensPerChar = 0.68)
{
    public static PrerollOptions Default { get; } = new();
}

/// <summary>
/// Decides when a streamed line may start to sound. Starting on the first decoded window is only safe when
/// the rest of the line is made faster than it plays; otherwise the sound runs dry mid-sentence. With
/// <c>R</c> the audio still to be made, <c>q</c> the rate it is made at (seconds of speech per second of
/// work: the slower of the language model and the decoder, against 25 speech tokens per second) and
/// <c>tail</c> the time the final window takes to decode once the model has stopped, playback never runs dry
/// when the audio already buffered is at least <c>R * (1 / q - 1) + tail + margin</c>. With q at or above 1
/// that is just <c>tail + margin</c>; as q falls it grows toward the whole line, which is right: a clean line
/// after a wait beats a stuttering one. The wait is capped (<see cref="PrerollOptions.MaxMs"/>) and never
/// below <see cref="PrerollOptions.MinMs"/>. Once the language model has stopped it no longer limits q and
/// the minimum and margin drop away (nothing is left that could be late but the decoder's own windows).
/// Pure and clock-injected; the language model feeds it from its own thread.
/// </summary>
public sealed class PrerollGate
{
    /// <summary>Speech tokens per second of audio.</summary>
    public const double TokensPerSecond = 25.0;

    private const int RateWindowTokens = 20;
    private const int MinRateSamples = 6;

    private readonly object gate = new();
    private readonly PrerollOptions options;
    private readonly int textChars;
    private readonly Func<long> nowMs;
    private readonly long startedAt;
    private readonly Queue<long> stamps = new();
    private int tokens;
    private bool lmFinished;
    private double decoderCost; // decoder ms per ms of audio (smoothed); 0 until a window was timed
    private long lastWindowMs;
    private long releasedAfterMs = -1;

    public PrerollGate(PrerollOptions options, int textChars, Func<long> nowMs)
    {
        this.options = options;
        this.textChars = Math.Max(0, textChars);
        this.nowMs = nowMs;
        this.startedAt = nowMs();
        if (options.MaxMs <= 0)
        {
            this.releasedAfterMs = 0;
        }
    }

    public bool Released
    {
        get
        {
            lock (this.gate)
            {
                return this.releasedAfterMs >= 0;
            }
        }
    }

    /// <summary>How long the sound was held back; -1 while it still is.</summary>
    public long WaitedMs
    {
        get
        {
            lock (this.gate)
            {
                return this.releasedAfterMs;
            }
        }
    }

    /// <summary>The audio the last check asked for, in ms; for logs and tests.</summary>
    public long RequiredMs { get; private set; }

    /// <summary>The language model picked one more speech token.</summary>
    public void TokenProduced()
    {
        lock (this.gate)
        {
            this.tokens++;
            this.stamps.Enqueue(this.nowMs());
            while (this.stamps.Count > RateWindowTokens + 1)
            {
                this.stamps.Dequeue();
            }
        }
    }

    /// <summary>The language model stopped; the token count is now exact.</summary>
    public void LanguageModelFinished()
    {
        lock (this.gate)
        {
            this.lmFinished = true;
        }
    }

    /// <summary>One decoder window took <paramref name="decodeMs"/> and gave <paramref name="audioMs"/> of audio.</summary>
    public void WindowDecoded(long decodeMs, long audioMs)
    {
        if (audioMs <= 0)
        {
            return;
        }

        lock (this.gate)
        {
            var cost = Math.Max(0, decodeMs) / (double)audioMs;
            this.decoderCost = this.decoderCost == 0 ? cost : (this.decoderCost * 0.5) + (cost * 0.5);
            this.lastWindowMs = Math.Max(0, decodeMs);
        }
    }

    /// <summary>
    /// True once the sound may start, given <paramref name="bufferedMs"/> of decoded audio held back; stays
    /// true after that. The cap and the language model finishing are checked here too, so a periodic call
    /// releases a line whose next window is slow.
    /// </summary>
    public bool ShouldRelease(long bufferedMs)
    {
        lock (this.gate)
        {
            if (this.releasedAfterMs >= 0)
            {
                return true;
            }

            var waited = this.nowMs() - this.startedAt;
            this.RequiredMs = this.Required(bufferedMs, waited);
            if (waited >= this.options.MaxMs || bufferedMs >= this.RequiredMs)
            {
                this.releasedAfterMs = Math.Max(0, waited);
                return true;
            }

            return false;
        }
    }

    private long Required(long bufferedMs, long waited)
    {
        var expectedTokens = this.lmFinished
            ? this.tokens
            : Math.Max(this.tokens, (int)Math.Ceiling(this.textChars * this.options.TokensPerChar));
        var remainingMs = Math.Max(0.0, (expectedTokens / TokensPerSecond * 1000.0) - bufferedMs);

        var q = this.ProductionRate(waited);
        var shortfall = remainingMs * Math.Max(0.0, (1.0 / q) - 1.0);
        var required = shortfall + this.lastWindowMs;
        if (!this.lmFinished)
        {
            required = Math.Max(this.options.MinMs, required + this.options.MarginMs);
        }

        return (long)Math.Ceiling(required);
    }

    /// <summary>Seconds of speech made per second of work; infinite when nothing limits it yet.</summary>
    private double ProductionRate(long waited)
    {
        var rate = double.PositiveInfinity;
        if (!this.lmFinished)
        {
            double lmPerSecond;
            if (this.stamps.Count >= MinRateSamples)
            {
                var span = this.stamps.Last() - this.stamps.First();
                lmPerSecond = span <= 0 ? double.PositiveInfinity : (this.stamps.Count - 1) * 1000.0 / span;
            }
            else
            {
                // Too few tokens to see a pace: the average since the line began (prefill included) is the safe guess.
                lmPerSecond = waited <= 0 ? double.PositiveInfinity : this.tokens * 1000.0 / waited;
            }

            rate = lmPerSecond / TokensPerSecond;
        }

        if (this.decoderCost > 0)
        {
            rate = Math.Min(rate, 1.0 / this.decoderCost);
        }

        return rate;
    }
}

/// <summary>
/// Holds the decoded windows of a streamed line back until its <see cref="PrerollGate"/> opens, then hands
/// them on in order and passes later audio straight through. Call from the one thread that decodes.
/// </summary>
public sealed class PrerollBuffer
{
    private readonly PrerollGate gate;
    private readonly Action<float[]> emit;
    private readonly int sampleRate;
    private readonly List<float[]> held = [];
    private long heldSamples;

    public PrerollBuffer(PrerollGate gate, int sampleRate, Action<float[]> emit)
    {
        this.gate = gate;
        this.sampleRate = Math.Max(1, sampleRate);
        this.emit = emit;
    }

    public bool Released => this.held.Count == 0 && this.gate.Released;

    /// <summary>The audio the line has made but not yet let through, in ms.</summary>
    public long HeldMs => this.heldSamples * 1000 / this.sampleRate;

    public void Add(float[] pcm)
    {
        if (pcm.Length > 0)
        {
            this.held.Add(pcm);
            this.heldSamples += pcm.Length;
        }

        this.Tick();
    }

    /// <summary>Checks the gate (cap, model finished, enough buffered) and lets the held audio through when it is open.</summary>
    public void Tick()
    {
        if (this.held.Count > 0 && this.gate.ShouldRelease(this.HeldMs))
        {
            this.Flush();
        }
    }

    /// <summary>The line is fully made: whatever is held goes now.</summary>
    public void Flush()
    {
        // Mark the gate open even for an empty line, so the waited time is recorded.
        this.gate.ShouldRelease(long.MaxValue);
        foreach (var pcm in this.held)
        {
            this.emit(pcm);
        }

        this.held.Clear();
        this.heldSamples = 0;
    }
}
