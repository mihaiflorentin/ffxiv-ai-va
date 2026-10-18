namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>One queued utterance: who speaks, the rendered request, and its audio.</summary>
/// <param name="Speaker">Resolved speaker identity.</param>
/// <param name="Request">The synthesis request the audio was rendered from (for diagnostics).</param>
/// <param name="Audio">Rendered speech; the queue plays it through the audio sink.</param>
/// <param name="RequestedAtTicks">
/// Environment.TickCount64 when the line arrived (pre-synthesis), for staleness drops.
/// Zero means "never stale" (explicit user-initiated tests).
/// </param>
/// <param name="LineId">Chunks of one streamed line share an id (0: unstreamed); skipping
/// the line drops its remaining chunks too.</param>
/// <param name="Emotion">The delivery the line was planned with (shown while it plays).</param>
/// <param name="Stream">
/// When set, the line is still being synthesized: the queue plays the stream as it fills and
/// <paramref name="Audio"/> is empty. Cancelling playback cancels the stream, which tells the synthesizer to stop.
/// </param>
/// <param name="Timer">Collects this line's stage timings; the queue adds what playback measured.</param>
public sealed record SpeechItem(
    SpeakerIdentity Speaker,
    SynthesisRequest Request,
    SynthesisResult Audio,
    long RequestedAtTicks = 0,
    long LineId = 0,
    string? Emotion = null,
    SpeechAudioStream? Stream = null,
    LineTimer? Timer = null);

/// <summary>Driven port serializing speech so overlapping lines play in order instead of mixing.</summary>
public interface ISpeechQueue
{
    void Enqueue(SpeechItem item);

    /// <summary>True when the queue plays items that carry a <see cref="SpeechItem.Stream"/> while they fill.</summary>
    bool SupportsStreaming => false;

    /// <summary>Items waiting to play (excludes the one currently playing).</summary>
    int Depth { get; }

    /// <summary>Stops the item currently being spoken (and the rest of its streamed line);
    /// other queued items are kept.</summary>
    void CancelCurrent();

    /// <summary>The item playing right now, if any (for the now-speaking overlay).</summary>
    SpeechItem? NowPlaying => null;

    /// <summary>Clears all queued items, including any current playback.</summary>
    void Clear();
}
