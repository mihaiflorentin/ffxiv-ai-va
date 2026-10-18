namespace AIVoiceActing.Domain;

/// <summary>
/// Knobs of the pre-roll. <see cref="MaxMs"/> is the longest the sound is held back (0 turns the hold off:
/// playback starts with the first audio). <see cref="MinMs"/> is the least audio buffered before the sound
/// starts, <see cref="MarginMs"/> spare room for a window that comes in late, and
/// <see cref="TokensPerChar"/> turns the text length into an expected number of speech tokens until the
/// language model has finished. 1.8 is on the long side on purpose (fp32 measured 1.6: a 97-character line took
/// 156 tokens, 6.4 s of audio; q4f16 speaks 10 to 15 percent faster): a line that turns out shorter only
/// costs a longer hold when the model is slow, a line that turns out longer costs gaps mid-sentence.
/// </summary>
public sealed record PrerollOptions(
    int MaxMs = 6000,
    int MinMs = 1000,
    int MarginMs = 300,
    double TokensPerChar = 1.8)
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
/// after a wait beats a stuttering one. q is measured, and near real time it is noisy (a graphics card shared
/// with the game: the median step is not the mean, and a stall lasts longer than any one step), so it is the
/// slower of the language model's recent pace, its pace over the whole line so far and the decoder's, a pause
/// since the last token counts against it, and below <see cref="ComfortableRate"/> a share of <c>R</c> is
/// added on top. Audio also arrives in chunks (see <see cref="ChunkSchedule"/>), and a chunk exists only once
/// its last token does: when the schedule is known the gate walks the chunks still to come, predicts when each
/// will be decoded (the model's pace, the decoder working one chunk at a time) and holds the sound until none
/// is predicted to arrive after the moment playback needs it. The wait is capped (<see cref="PrerollOptions.MaxMs"/>) and never
/// below <see cref="PrerollOptions.MinMs"/>. Once the language model has stopped it no longer limits q and
/// the minimum and margin drop away (nothing is left that could be late but the decoder's own windows).
/// Pure and clock-injected; the language model feeds it from its own thread.
/// </summary>
public sealed class PrerollGate
{
    /// <summary>Speech tokens per second of audio.</summary>
    public const double TokensPerSecond = 25.0;

    /// <summary>Above this production rate (against real time) the estimate has headroom and needs no extra margin.</summary>
    public const double ComfortableRate = 1.3;

    /// <summary>The share of the still-missing audio added per unit the rate is short of <see cref="ComfortableRate"/>.</summary>
    public const double HeadroomShare = 0.3;

    /// <summary>When the model passes the length the text suggested, it is assumed to go on for at least this many more tokens (a second of speech).</summary>
    private const int OvershootTokens = 25;

    /// <summary>The predicted arrival of a chunk is stretched by this share before it is compared with the moment it is needed.</summary>
    private const double ArrivalSafety = 0.1;

    private const double MsPerToken = 1000.0 / TokensPerSecond;

    private const int RateWindowTokens = 20;
    private const int MinRateSamples = 6;

    private readonly object gate = new();
    private readonly PrerollOptions options;
    private readonly int textChars;
    private readonly Func<long> nowMs;
    private readonly ChunkScheduleOptions? chunks;
    private readonly long startedAt;
    private readonly Queue<long> stamps = new();
    private int tokens;
    private bool lmFinished;
    private double decoderCost; // decoder ms per ms of audio (smoothed); 0 until a window was timed
    private double lastDecoderCost; // the latest window alone
    private int windowsDecoded;
    private double frozenLmRate = double.NaN; // the model's pace when it stopped
    private long firstTokenAt = -1;
    private long lastWindowMs;
    private long releasedAfterMs = -1;

    /// <param name="chunks">The chunk schedule of the line; null skips the chunk-by-chunk prediction (only the buffered amount counts).</param>
    public PrerollGate(PrerollOptions options, int textChars, Func<long> nowMs, ChunkScheduleOptions? chunks = null)
    {
        this.chunks = chunks;
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

    /// <summary>How far past the moment it is needed the last check predicted the latest chunk to arrive, in ms (at most 0: none is late); for logs and tests.</summary>
    public long LateByMs { get; private set; }

    /// <summary>The language model picked one more speech token.</summary>
    public void TokenProduced()
    {
        lock (this.gate)
        {
            this.tokens++;
            var now = this.nowMs();
            if (this.firstTokenAt < 0)
            {
                this.firstTokenAt = now;
            }

            this.stamps.Enqueue(now);
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
            if (!this.lmFinished)
            {
                this.frozenLmRate = this.LanguageModelPace(Math.Max(1, this.nowMs() - this.startedAt)) / TokensPerSecond;
            }

            this.lmFinished = true;
        }
    }

    /// <summary>
    /// How fast the language model makes speech against real time (1 = as fast as it plays), as the gate sees it;
    /// the pace it had at the end once it has stopped; NaN while there is nothing to go by. The chunk sizes follow it
    /// (<see cref="ChunkSchedule.CapFor"/>).
    /// </summary>
    public double LanguageModelRate
    {
        get
        {
            lock (this.gate)
            {
                if (this.lmFinished)
                {
                    return this.frozenLmRate;
                }

                var pace = this.LanguageModelPace(Math.Max(1, this.nowMs() - this.startedAt));
                return double.IsInfinity(pace) ? double.NaN : pace / TokensPerSecond;
            }
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
            this.lastDecoderCost = cost;
            this.windowsDecoded++;
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
            this.LateByMs = this.ChunkLateness(bufferedMs, waited);
            if (waited >= this.options.MaxMs || (bufferedMs >= this.RequiredMs && this.LateByMs <= 0))
            {
                this.releasedAfterMs = Math.Max(0, waited);
                return true;
            }

            return false;
        }
    }

    private double ExpectedTokens()
    {
        if (this.lmFinished)
        {
            return this.tokens;
        }

        var suggested = Math.Ceiling(this.textChars * this.options.TokensPerChar);
        // Past the length the text suggested the model is still going: do not treat the line as done.
        return this.tokens >= suggested
            ? this.tokens + Math.Max(OvershootTokens, this.tokens * 0.1)
            : suggested;
    }

    private long Required(long bufferedMs, long waited)
    {
        var remainingMs = Math.Max(0.0, (this.ExpectedTokens() * MsPerToken) - bufferedMs);

        var q = this.ProductionRate(waited);
        var shortfall = remainingMs * Math.Max(0.0, (1.0 / q) - 1.0);
        var headroom = double.IsInfinity(q) ? 0.0 : remainingMs * HeadroomShare * Math.Clamp(ComfortableRate - q, 0.0, 1.0);
        var required = shortfall + headroom + this.lastWindowMs;
        if (!this.lmFinished)
        {
            required = Math.Max(this.options.MinMs, required + this.options.MarginMs);
        }

        return (long)Math.Ceiling(required);
    }

    /// <summary>
    /// Walks the chunks that are still to be decoded and predicts, for each, when it will be ready (its last token plus
    /// the lookahead exists, and the decoder is free of the chunk before) against when playback, started now, needs its
    /// first sample. The largest overshoot, in ms; at most 0 when every chunk is in time.
    /// </summary>
    private long ChunkLateness(long bufferedMs, long waited)
    {
        if (this.chunks is not { } plan)
        {
            return 0;
        }

        var expected = (int)Math.Ceiling(this.ExpectedTokens());
        var start = (int)Math.Round(Math.Min(bufferedMs, 10_000_000L) / MsPerToken);
        if (start >= expected)
        {
            return 0;
        }

        var lmPerSecond = this.lmFinished ? double.PositiveInfinity : this.LanguageModelPace(waited);
        var cap = ChunkSchedule.CapFor(plan, this.lmFinished ? this.frozenLmRate : lmPerSecond / TokensPerSecond);
        var cost = this.decoderCost > 0 ? Math.Max(this.decoderCost, this.lastDecoderCost) : 0.5;
        var index = Math.Max(1, this.windowsDecoded);
        double decoderFreeMs = 0;
        var late = double.NegativeInfinity;
        while (start < expected && index < 200)
        {
            var end = Math.Min(start + ChunkSchedule.SizeFor(plan, index, cap), expected);
            if (expected - end < plan.MinTailTokens)
            {
                end = expected;
            }

            var modelReadyMs = 0.0;
            if (!this.lmFinished)
            {
                var needed = Math.Min(end + plan.LookaheadTokens, expected) - this.tokens;
                modelReadyMs = needed <= 0
                    ? 0.0
                    : double.IsInfinity(lmPerSecond) ? 0.0 : needed * 1000.0 / Math.Max(lmPerSecond, 0.001);
            }

            var readyMs = Math.Max(decoderFreeMs, modelReadyMs) + (cost * (end - start) * MsPerToken);
            decoderFreeMs = readyMs;
            late = Math.Max(late, (readyMs * (1.0 + ArrivalSafety)) + (this.options.MarginMs / 2.0) - (start * MsPerToken));
            start = end;
            index++;
        }

        return (long)Math.Ceiling(late);
    }

    /// <summary>Speech tokens per second the model makes: the slower of its recent pace and its pace over the line so far; infinite when it cannot be told yet.</summary>
    private double LanguageModelPace(long waited)
    {
        var now = this.nowMs();
        if (this.stamps.Count >= MinRateSamples)
        {
            // Both paces run to now, so a stall since the last token pulls them down at once.
            var recentSpan = now - this.stamps.First();
            var recent = recentSpan <= 0 ? double.PositiveInfinity : (this.stamps.Count - 1) * 1000.0 / recentSpan;
            var wholeSpan = now - this.firstTokenAt;
            var whole = wholeSpan <= 0 ? double.PositiveInfinity : (this.tokens - 1) * 1000.0 / wholeSpan;
            return Math.Min(recent, whole);
        }

        // Too few tokens to see a pace: the average since the line began (prefill included) is the safe guess.
        return waited <= 0 ? double.PositiveInfinity : this.tokens * 1000.0 / waited;
    }

    /// <summary>Seconds of speech made per second of work; infinite when nothing limits it yet.</summary>
    private double ProductionRate(long waited)
    {
        var rate = double.PositiveInfinity;
        if (!this.lmFinished)
        {
            rate = this.LanguageModelPace(waited) / TokensPerSecond;
        }

        var cost = Math.Max(this.decoderCost, this.lastDecoderCost);
        if (cost > 0)
        {
            rate = Math.Min(rate, 1.0 / cost);
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
