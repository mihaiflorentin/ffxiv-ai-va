namespace AIVoiceActing.Ports;

/// <summary>What the sink measured while it played one line (see <see cref="IAudioSink.LastPlayback"/>).</summary>
/// <param name="DeviceOpenMs">From deciding to play to the device running (-1 when it never ran).</param>
/// <param name="UnderrunCount">Times the device asked for sound that was not there yet.</param>
/// <param name="SilenceMs">Silence played to cover them.</param>
/// <param name="FirstSoundAtTicks"><c>Environment.TickCount64</c> when the device was first fed real sound; 0 if never.</param>
public sealed record PlaybackReport(long DeviceOpenMs, int UnderrunCount, long SilenceMs, long FirstSoundAtTicks);

/// <summary>Driven port rendering <see cref="SynthesisResult"/>s to the audio device.</summary>
public interface IAudioSink
{
    /// <summary>False when the current platform/device cannot play audio (e.g. non-Windows without output).</summary>
    bool IsSupported { get; }

    /// <summary>Plays <paramref name="audio"/> at the given volume, 0..1; cancels any current playback.</summary>
    void Play(SynthesisResult audio, float volume);

    /// <summary>
    /// Plays a line that is still being produced, starting as soon as the first samples are ready and
    /// filling any gap with silence. Blocks until the source finished or <see cref="Cancel"/> stopped it.
    /// The default waits for the whole line and plays it with <see cref="Play"/>, so a sink that cannot
    /// stream still speaks.
    /// </summary>
    void PlayStream(IPcmSource source, float volume)
    {
        var all = new List<float>();
        var chunk = new float[4096];
        while (!source.IsFinished)
        {
            var n = source.Read(chunk);
            if (n > 0)
            {
                all.AddRange(chunk.AsSpan(0, n).ToArray());
            }
            else
            {
                source.WaitForData(100);
            }
        }

        if (all.Count > 0)
        {
            this.Play(new SynthesisResult([.. all], source.SampleRate), volume);
        }
    }

    /// <summary>
    /// What the last <see cref="Play"/> or <see cref="PlayStream"/> measured; null when nothing was measured.
    /// Read it right after the call returns: the sink plays one line at a time.
    /// </summary>
    PlaybackReport? LastPlayback => null;

    /// <summary>Stops current playback immediately (text-advance courtesy).</summary>
    void Cancel();

    /// <summary>Drops every queued item without playing them.</summary>
    void Flush();
}
