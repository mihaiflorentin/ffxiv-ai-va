namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Sends strongly emotional lines (a style tag such as a laugh or sigh, or an intensity far
/// from the neutral 0.5) to a second, more expressive engine, and everything else to the
/// primary one. The expressive engine clones a reference clip of the speaker's own voice,
/// so <c>prepareVoice</c> returns that clip's path (null: no clip, stay on the primary).
/// Any failure on the expressive side falls back to the primary engine for that line, and the
/// expressive engine then sits out for <see cref="FailureCooldown"/> (a broken engine would
/// otherwise cost every emotional line a failed attempt) or until <see cref="RetryAsync"/>.
/// Which engine is expressive, and on which device, is decided by whoever builds the
/// <c>expressive</c> delegate from the configuration; the router only routes.
/// </summary>
public sealed class ExpressiveRouter : ISpeechSynthesizer
{
    /// <summary>How far from neutral (0.5) a line's intensity must be to go expressive.</summary>
    public const float MinIntensityShift = 0.25f;

    private readonly Func<ISpeechSynthesizer> primary;
    private readonly Func<ISpeechSynthesizer?> expressive;
    private readonly Func<SynthesisRequest, CancellationToken, Task<string?>> prepareVoice;
    private readonly ILogSink? log;
    private readonly Func<long> clockMs;
    private long expressiveDownUntilMs;

    /// <summary>How long the expressive engine is skipped after a failed line.</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    public ExpressiveRouter(
        Func<ISpeechSynthesizer> primary,
        Func<ISpeechSynthesizer?> expressive,
        Func<SynthesisRequest, CancellationToken, Task<string?>> prepareVoice,
        ILogSink? log = null,
        Func<long>? clockMs = null)
    {
        this.primary = primary;
        this.expressive = expressive;
        this.prepareVoice = prepareVoice;
        this.log = log;
        this.clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public static bool WantsExpressive(SynthesisRequest request) =>
        request.Tags.Count > 0 || Math.Abs(request.Exaggeration - 0.5f) >= MinIntensityShift;

    public bool IsReady => this.primary().IsReady;

    public string NotReadyReason => this.primary().NotReadyReason;

    /// <summary>The main engine's status; the expressive engine reports its own through the container.</summary>
    public EngineStatus Status => this.primary().Status;

    /// <summary>The expressive engine's own status, or null while routing is off.</summary>
    public EngineStatus? ExpressiveStatus => this.expressive()?.Status;

    /// <summary>
    /// Primary and expressive variants together: a switch of the expressive engine's precision or device
    /// changes the audio of emotional lines and must change the render-cache key.
    /// </summary>
    public string RenderVariant
    {
        get
        {
            var main = this.primary().RenderVariant;
            if (this.expressive() is not { } engine)
            {
                return main;
            }

            return main.Length == 0 ? $"x:{engine.RenderVariant}" : $"{main}|x:{engine.RenderVariant}";
        }
    }

    /// <summary>
    /// Retries both engines on <paramref name="mode"/> (a no-op for the ones without a device choice)
    /// and lets the expressive engine take lines again right away.
    /// </summary>
    public async Task RetryAsync(DeviceMode mode, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref this.expressiveDownUntilMs, 0);
        await this.primary().RetryAsync(mode, cancellationToken);
        if (this.expressive() is { } engine)
        {
            await engine.RetryAsync(mode, cancellationToken);
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken) => this.primary().WarmUpAsync(cancellationToken);

    // ---- streaming: the line goes to whichever engine the request is routed to ----------------------------

    /// <summary>True when either engine can stream; whether a given line does is <see cref="SupportsStreamingFor"/>.</summary>
    public bool SupportsStreaming => this.primary().SupportsStreaming || this.expressive()?.SupportsStreaming == true;

    public bool SupportsStreamingFor(SynthesisRequest request) =>
        this.ExpressiveEngineFor(request) is { } engine
            ? engine.SupportsStreamingFor(request)
            : this.primary().SupportsStreamingFor(request);

    private ISpeechSynthesizer? ExpressiveEngineFor(SynthesisRequest request) =>
        WantsExpressive(request)
        && this.clockMs() >= Interlocked.Read(ref this.expressiveDownUntilMs)
        && this.expressive() is { IsReady: true } engine
            ? engine
            : null;

    public async Task<SynthesisResult> SynthesizeStreamingAsync(
        SynthesisRequest request,
        Action<ReadOnlyMemory<float>> onSamples,
        CancellationToken cancellationToken)
    {
        if (this.ExpressiveEngineFor(request) is { } engine)
        {
            var started = false;
            try
            {
                if (await this.prepareVoice(request, cancellationToken) is { } clip)
                {
                    var audio = await engine.SynthesizeStreamingAsync(
                        request with { ReferenceVoiceId = clip },
                        samples =>
                        {
                            started = true;
                            onSamples(samples);
                        },
                        cancellationToken);
                    this.log?.Info($"Expressive engine streamed \"{request.ReferenceVoiceId}\" ({audio.Samples.Length} samples).");
                    return audio;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref this.expressiveDownUntilMs, this.clockMs() + (long)FailureCooldown.TotalMilliseconds);
                if (started)
                {
                    // Part of the line was already heard in the expressive voice; the main engine cannot finish it.
                    this.log?.Warn($"Expressive engine failed mid-line ({ex.Message}).");
                    throw;
                }

                this.log?.Warn($"Expressive engine failed ({ex.Message}); using the main engine for this line.");
            }
        }

        var main = this.primary();
        if (main.SupportsStreamingFor(request))
        {
            return await main.SynthesizeStreamingAsync(request, onSamples, cancellationToken);
        }

        var whole = await main.SynthesizeAsync(request, cancellationToken);
        onSamples(whole.Samples);
        return whole;
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        if (WantsExpressive(request)
            && this.clockMs() >= Interlocked.Read(ref this.expressiveDownUntilMs)
            && this.expressive() is { IsReady: true } engine)
        {
            try
            {
                if (await this.prepareVoice(request, cancellationToken) is { } clip)
                {
                    var audio = await engine.SynthesizeAsync(request with { ReferenceVoiceId = clip }, cancellationToken);
                    this.log?.Info($"Expressive engine rendered \"{request.ReferenceVoiceId}\" ({audio.Samples.Length} samples).");
                    return audio;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref this.expressiveDownUntilMs, this.clockMs() + (long)FailureCooldown.TotalMilliseconds);
                this.log?.Warn($"Expressive engine failed ({ex.Message}); using the main engine for this line.");
            }
        }

        return await this.primary().SynthesizeAsync(request, cancellationToken);
    }
}
