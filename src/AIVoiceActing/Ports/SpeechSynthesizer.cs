namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Thrown by <see cref="ISpeechSynthesizer.SynthesizeAsync"/> when speech synthesis fails.</summary>
public class SpeechSynthesisException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Thrown when the synthesizer was disposed mid-flight (engine switched or plugin stopped). The request may be retried against the current engine.</summary>
public sealed class SpeechSynthesisEngineDisposedException : SpeechSynthesisException
{
    public SpeechSynthesisEngineDisposedException()
        : base("The speech engine was switched or stopped while this line was starting.")
    {
    }
}

/// <summary>One requested utterance for the synthesizer.</summary>
/// <param name="ReferenceVoiceId">Reference clip id resolving to a bundled voice; the timbre seed.</param>
/// <param name="Text">Text to perform. Style tags must be supplied via <see cref="Tags"/>, not inline.</param>
/// <param name="Exaggeration">Performance intensity, 0..1 (0 = flat read, 1 = theatrical).</param>
/// <param name="Tags">Paralinguistic directions, e.g. "laughs", "sighs".</param>
/// <param name="Pitch">Playback pitch multiplier (1 = natural).</param>
/// <param name="Speed">Pace multiplier (1 = natural).</param>
/// <param name="Volume">Linear loudness multiplier (1 = natural, up to 2 = boosted).</param>
public sealed record SynthesisRequest(
    string ReferenceVoiceId,
    string Text,
    float Exaggeration,
    IReadOnlyList<string> Tags,
    float Pitch = 1f,
    float Speed = 1f,
    float Volume = 1f);

/// <summary>Rendered speech: mono PCM samples at a fixed sample rate.</summary>
public sealed record SynthesisResult(float[] Samples, int SampleRate);

/// <summary>Where an engine's heavy work really runs right now.</summary>
/// <param name="Adapter">GPU name when known; empty for the CPU.</param>
/// <param name="Api">Graphics API for a WebGPU session; <see cref="GpuApi.Auto"/> otherwise.</param>
public sealed record ActiveDevice(DeviceEp Ep, string Adapter = "", GpuApi Api = GpuApi.Auto)
{
    public static ActiveDevice Cpu { get; } = new(DeviceEp.Cpu);

    public bool IsGpu => this.Ep != DeviceEp.Cpu;
}

public enum EngineState
{
    /// <summary>A model file is missing or the engine is not configured.</summary>
    NotInstalled,

    /// <summary>Sessions are being built (the first GPU line can take seconds to compile shaders).</summary>
    Loading,

    /// <summary>Running where it was asked to run.</summary>
    Ready,

    /// <summary>Speaking, but on the CPU although the GPU was wanted; see <see cref="EngineStatus.Reason"/>.</summary>
    FellBack,

    /// <summary>Cannot speak; see <see cref="EngineStatus.Reason"/>.</summary>
    Failed,
}

/// <summary>
/// How fast the language model of an engine has been making speech on each device lately, as a running average of
/// the median time of one speech token in milliseconds (a voice keeps up with real time at under 40 ms). 0 means
/// that device has not been measured.
/// </summary>
public sealed record LmDeviceRates(double GpuMsPerToken, double CpuMsPerToken);

/// <summary>
/// Immutable snapshot of an engine's state, polled per frame. <see cref="Reason"/> is final
/// user-facing text (empty when there is nothing to explain): the UI must not invent its own.
/// </summary>
/// <param name="RequestedDevice">What the settings asked for.</param>
/// <param name="ActiveDevice">What actually runs.</param>
/// <param name="CanRetryGpu">A retry on the GPU is worth offering (crash guard tripped or GPU failed).</param>
/// <param name="OnProcessorForNow">
/// The engine is set up for the graphics card and it works, but its lines are running on the processor for now
/// because that is faster right now (the game is keeping the card busy); it moves back by itself. Not a fault:
/// <see cref="State"/> stays <see cref="EngineState.Ready"/>.
/// </param>
/// <param name="Rates">Measured speed of each device when the engine chooses between them; null otherwise.</param>
public sealed record EngineStatus(
    EngineState State,
    DeviceMode RequestedDevice,
    ActiveDevice ActiveDevice,
    string Reason,
    bool CanRetryGpu,
    bool OnProcessorForNow = false,
    LmDeviceRates? Rates = null)
{
    /// <summary>Status for engines that only know "ready or not" and always run on the CPU.</summary>
    public static EngineStatus FromReadiness(bool isReady, string notReadyReason) => isReady
        ? new EngineStatus(EngineState.Ready, DeviceMode.Auto, ActiveDevice.Cpu, string.Empty, false)
        : new EngineStatus(EngineState.NotInstalled, DeviceMode.Auto, ActiveDevice.Cpu, notReadyReason, false);
}

/// <summary>Driven port over the local speech model.</summary>
public interface ISpeechSynthesizer
{
    /// <summary>True when every required model asset is downloaded and the engine can synthesize.</summary>
    bool IsReady { get; }

    /// <summary>Human-readable reason <see cref="IsReady"/> is false; empty when ready.</summary>
    string NotReadyReason { get; }

    /// <summary>
    /// Builds engine sessions ahead of the first line (login pre-warm). Idempotent;
    /// asset problems surface here exactly as in <see cref="SynthesizeAsync"/>.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken);

    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// True when <see cref="SynthesizeStreamingAsync"/> really delivers audio piece by piece, early enough
    /// to start playing before the line is done (it can depend on where the engine runs right now). The
    /// default hands over the whole line at once, so callers only use it when this is true.
    /// </summary>
    bool SupportsStreaming => false;

    /// <summary>
    /// Whether this particular request would stream. Engines that route between others (the expressive
    /// router) answer per request; everyone else answers <see cref="SupportsStreaming"/>.
    /// </summary>
    bool SupportsStreamingFor(SynthesisRequest request) => this.SupportsStreaming;

    /// <summary>Sample rate of the pieces <see cref="SynthesizeStreamingAsync"/> delivers.</summary>
    int StreamingSampleRate => 24000;

    /// <summary>
    /// Renders a line and hands each finished piece of audio to <paramref name="onSamples"/> in order, as
    /// soon as it is ready. Returns the whole line (the pieces joined), which is what the render cache
    /// stores. The callback runs on the engine's worker thread and must not block for long.
    /// </summary>
    async Task<SynthesisResult> SynthesizeStreamingAsync(
        SynthesisRequest request,
        Action<ReadOnlyMemory<float>> onSamples,
        CancellationToken cancellationToken)
    {
        var whole = await this.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        onSamples(whole.Samples);
        return whole;
    }

    /// <summary>
    /// Snapshot of readiness and device use, cheap enough to poll every frame. Engines that
    /// never touch the GPU keep this default (ready or not installed, always on the CPU).
    /// </summary>
    EngineStatus Status => EngineStatus.FromReadiness(this.IsReady, this.NotReadyReason);

    /// <summary>
    /// What, besides the request, shapes this engine's audio right now (for example "q4f16/gpu": the model
    /// precision and the device class, never the adapter). Part of the render-cache key, so a switch
    /// between precisions or between CPU and GPU never serves stale audio. Empty when nothing varies.
    /// </summary>
    string RenderVariant => string.Empty;

    /// <summary>
    /// Rebuilds the engine's sessions with <paramref name="mode"/> for this instance (the setting
    /// itself is persisted by the caller). A GPU retry also clears the crash-guard flag. No-op for
    /// engines without a device choice.
    /// </summary>
    Task RetryAsync(DeviceMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
