namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Thrown by <see cref="ISpeechSynthesizer.SynthesizeAsync"/> when speech synthesis fails.</summary>
public sealed class SpeechSynthesisException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>One requested utterance for the synthesizer.</summary>
/// <param name="ReferenceVoiceId">Reference clip id resolving to a bundled voice; the timbre seed.</param>
/// <param name="Text">Text to perform. Style tags must be supplied via <see cref="Tags"/>, not inline.</param>
/// <param name="Exaggeration">Performance intensity, 0..1 (0 = flat read, 1 = theatrical).</param>
/// <param name="Tags">Paralinguistic directions, e.g. "laughs", "sighs".</param>
public sealed record SynthesisRequest(
    string ReferenceVoiceId,
    string Text,
    float Exaggeration,
    IReadOnlyList<string> Tags);

/// <summary>Rendered speech: mono PCM samples at a fixed sample rate.</summary>
public sealed record SynthesisResult(float[] Samples, int SampleRate);

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
}
