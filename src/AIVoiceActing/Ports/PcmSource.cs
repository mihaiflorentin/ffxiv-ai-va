namespace AIVoiceActing.Ports;

/// <summary>
/// Mono float PCM that is still being produced, read by an audio sink as the device asks for it.
/// A read never blocks: it returns what is ready (possibly nothing) so the device callback can fill
/// the gap with silence and carry on.
/// </summary>
public interface IPcmSource
{
    int SampleRate { get; }

    /// <summary>Copies ready samples into <paramref name="buffer"/> and returns how many (0 when none are ready).</summary>
    int Read(Span<float> buffer);

    /// <summary>True once everything has been read and no more will come (finished, failed or cancelled).</summary>
    bool IsFinished { get; }

    /// <summary>
    /// Blocks until samples are ready or the stream ended, at most <paramref name="timeoutMs"/>.
    /// Returns true when there is something to read or the stream is finished.
    /// </summary>
    bool WaitForData(int timeoutMs);
}
