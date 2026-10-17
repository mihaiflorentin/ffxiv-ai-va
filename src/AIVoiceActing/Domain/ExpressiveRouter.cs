namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Sends strongly emotional lines (a style tag such as a laugh or sigh, or an intensity far
/// from the neutral 0.5) to a second, more expressive engine, and everything else to the
/// primary one. The expressive engine clones a reference clip of the speaker's own voice,
/// so <c>prepareVoice</c> returns that clip's path (null: no clip, stay on the primary).
/// Any failure on the expressive side falls back to the primary engine for that line.
/// </summary>
public sealed class ExpressiveRouter : ISpeechSynthesizer
{
    /// <summary>How far from neutral (0.5) a line's intensity must be to go expressive.</summary>
    public const float MinIntensityShift = 0.25f;

    private readonly Func<ISpeechSynthesizer> primary;
    private readonly Func<ISpeechSynthesizer?> expressive;
    private readonly Func<SynthesisRequest, CancellationToken, Task<string?>> prepareVoice;
    private readonly ILogSink? log;

    public ExpressiveRouter(
        Func<ISpeechSynthesizer> primary,
        Func<ISpeechSynthesizer?> expressive,
        Func<SynthesisRequest, CancellationToken, Task<string?>> prepareVoice,
        ILogSink? log = null)
    {
        this.primary = primary;
        this.expressive = expressive;
        this.prepareVoice = prepareVoice;
        this.log = log;
    }

    public static bool WantsExpressive(SynthesisRequest request) =>
        request.Tags.Count > 0 || Math.Abs(request.Exaggeration - 0.5f) >= MinIntensityShift;

    public bool IsReady => this.primary().IsReady;

    public string NotReadyReason => this.primary().NotReadyReason;

    /// <summary>The main engine's status; the expressive engine reports its own through the container.</summary>
    public EngineStatus Status => this.primary().Status;

    public Task RetryAsync(DeviceMode mode, CancellationToken cancellationToken = default) =>
        this.primary().RetryAsync(mode, cancellationToken);

    public Task WarmUpAsync(CancellationToken cancellationToken) => this.primary().WarmUpAsync(cancellationToken);

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        if (WantsExpressive(request) && this.expressive() is { IsReady: true } engine)
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
                this.log?.Warn($"Expressive engine failed ({ex.Message}); using the main engine for this line.");
            }
        }

        return await this.primary().SynthesizeAsync(request, cancellationToken);
    }
}
