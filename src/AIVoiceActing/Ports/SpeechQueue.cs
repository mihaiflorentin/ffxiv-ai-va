namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>One queued speech request: who speaks, and what the synthesizer should perform.</summary>
public sealed record SpeechItem(
    SpeakerIdentity Speaker,
    SynthesisRequest Request);

/// <summary>Driven port serializing speech so overlapping lines play in order instead of mixing.</summary>
public interface ISpeechQueue
{
    void Enqueue(SpeechItem item);

    /// <summary>Stops the item currently being spoken; queued items are kept.</summary>
    void CancelCurrent();

    /// <summary>Clears all queued items, including any current playback.</summary>
    void Clear();
}
