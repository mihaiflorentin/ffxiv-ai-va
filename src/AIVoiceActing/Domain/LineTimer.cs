namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Collects the stage timings of one line while it moves through the pipeline, the engine and playback, and
/// hands the result to an <see cref="ILineTimingLog"/>. The handler makes one per line and makes it
/// <see cref="Current"/> for everything it awaits, so the engines add their stages without a port of their own;
/// playback gets it through <see cref="SpeechItem.Timer"/>. Every method may be called from any thread, and
/// <see cref="Commit"/> may be called again as later stages (playback) add to it.
/// </summary>
public sealed class LineTimer
{
    private static readonly AsyncLocal<LineTimer?> CurrentTimer = new();
    private static long nextId;

    private readonly object gate = new();
    private readonly ILineTimingLog? log;
    private readonly Func<long> now;
    private readonly long arrivedAtTicks;
    private readonly long startedAtTicks;
    private readonly long id = Interlocked.Increment(ref nextId);
    private readonly int lineChars;
    private int chunks;
    private long profileMs;
    private long planMs;
    private string referenceKind = string.Empty;
    private long referencePrepMs;
    private long encoderMs;
    private long gateWaitMs;
    private long prefillMs;
    private int lmTokens;
    private long lmMs;
    private long decoderMs;
    private int windows;
    private long firstAudioMs = -1;
    private long deviceOpenMs = -1;
    private long startToSoundMs = -1;
    private int underruns;
    private long silenceMs;
    private long totalRenderMs;
    private long audioMs;
    private string engine = string.Empty;
    private bool streamed;
    private bool discarded;

    /// <param name="arrivedAtTicks"><c>Environment.TickCount64</c> when the line reached the pipeline; the work starts now.</param>
    public LineTimer(int lineChars, long arrivedAtTicks, ILineTimingLog? log = null, Func<long>? now = null)
    {
        this.now = now ?? (() => Environment.TickCount64);
        this.startedAtTicks = this.now();
        this.arrivedAtTicks = Math.Min(arrivedAtTicks, this.startedAtTicks);
        this.lineChars = lineChars;
        this.log = log;
    }

    /// <summary>The line being worked on by this async flow; null outside one.</summary>
    public static LineTimer? Current
    {
        get => CurrentTimer.Value;
        set => CurrentTimer.Value = value;
    }

    /// <summary>Where the language model loop records the duration of each token after the first.</summary>
    public LatencyHistogram StepLatency { get; } = new();

    public long Id => this.id;

    public long ArrivedAtTicks => this.arrivedAtTicks;

    public void SetChunks(int count) => this.With(() => this.chunks = count);

    public void SetProfile(long ms) => this.With(() => this.profileMs = ms);

    public void SetPlan(long ms) => this.With(() => this.planMs = ms);

    public void SetReference(string kind, long prepMs) => this.With(() =>
    {
        this.referenceKind = kind;
        this.referencePrepMs += prepMs;
    });

    public void AddEncoder(long ms) => this.With(() => this.encoderMs += ms);

    public void AddGateWait(long ms) => this.With(() => this.gateWaitMs += ms);

    public void AddPrefill(long ms) => this.With(() => this.prefillMs += ms);

    public void AddLanguageModel(int tokens, long ms) => this.With(() =>
    {
        this.lmTokens += tokens;
        this.lmMs += ms;
    });

    public void AddDecoder(long ms, int calls) => this.With(() =>
    {
        this.decoderMs += ms;
        this.windows += calls;
    });

    public void SetEngine(string name) => this.With(() => this.engine = name);

    public void SetStreamed() => this.With(() => this.streamed = true);

    /// <summary>The first sound of the line reached playback; only the first call counts.</summary>
    public void MarkFirstAudio() => this.With(() =>
    {
        if (this.firstAudioMs < 0)
        {
            this.firstAudioMs = this.now() - this.arrivedAtTicks;
        }
    });

    /// <summary>Another stretch of finished speech, in samples.</summary>
    public void AddAudio(int samples, int sampleRate) => this.With(() =>
    {
        if (sampleRate > 0)
        {
            this.audioMs += samples * 1000L / sampleRate;
        }
    });

    /// <summary>The engine side is done: the time since the work began.</summary>
    public void MarkRenderDone() => this.With(() => this.totalRenderMs = this.now() - this.startedAtTicks);

    /// <summary>Adds what the sound device measured for one chunk.</summary>
    public void AddPlayback(PlaybackReport report) => this.With(() =>
    {
        if (report.DeviceOpenMs >= 0)
        {
            this.deviceOpenMs = Math.Max(0, this.deviceOpenMs) + report.DeviceOpenMs;
        }

        this.underruns += report.UnderrunCount;
        this.silenceMs += report.SilenceMs;
        if (this.startToSoundMs < 0 && report.FirstSoundAtTicks > 0)
        {
            this.startToSoundMs = report.FirstSoundAtTicks - this.arrivedAtTicks;
        }
    });

    /// <summary>The line was dropped before any work (no voice, engine not ready): nothing is recorded for it.</summary>
    public void Discard() => this.With(() => this.discarded = true);

    public LineTiming Snapshot()
    {
        lock (this.gate)
        {
            return new LineTiming(
                this.id,
                this.lineChars,
                this.chunks,
                this.startedAtTicks - this.arrivedAtTicks,
                this.profileMs,
                this.planMs,
                this.referenceKind,
                this.referencePrepMs,
                this.encoderMs,
                this.gateWaitMs,
                this.prefillMs,
                this.lmTokens,
                this.lmMs,
                this.StepLatency.P50,
                this.StepLatency.P95,
                this.decoderMs,
                this.windows,
                this.firstAudioMs,
                this.deviceOpenMs,
                this.startToSoundMs,
                this.underruns,
                this.silenceMs,
                this.totalRenderMs,
                this.audioMs,
                this.engine,
                this.streamed);
        }
    }

    /// <summary>Gives the current picture to the log (replacing this line's earlier entry), unless the line was discarded.</summary>
    public void Commit()
    {
        bool skip;
        lock (this.gate)
        {
            skip = this.discarded;
        }

        if (!skip)
        {
            this.log?.Record(this.Snapshot());
        }
    }

    private void With(Action change)
    {
        lock (this.gate)
        {
            change();
        }
    }
}
