namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>One queued utterance: who speaks, the rendered request, and its audio.</summary>
/// <param name="Speaker">Resolved speaker identity.</param>
/// <param name="Request">The synthesis request the audio was rendered from (for diagnostics).</param>
/// <param name="Audio">Rendered speech; the queue plays it through the audio sink.</param>
public sealed record SpeechItem(
    SpeakerIdentity Speaker,
    SynthesisRequest Request,
    SynthesisResult Audio);

/// <summary>Driven port serializing speech so overlapping lines play in order instead of mixing.</summary>
public interface ISpeechQueue
{
    void Enqueue(SpeechItem item);

    /// <summary>Stops the item currently being spoken; queued items are kept.</summary>
    void CancelCurrent();

    /// <summary>Clears all queued items, including any current playback.</summary>
    void Clear();
}
