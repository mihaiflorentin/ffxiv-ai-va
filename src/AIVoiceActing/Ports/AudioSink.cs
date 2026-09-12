namespace AIVoiceActing.Ports;

/// <summary>Driven port rendering <see cref="SynthesisResult"/>s to the audio device.</summary>
public interface IAudioSink
{
    /// <summary>False when the current platform/device cannot play audio (e.g. non-Windows without output).</summary>
    bool IsSupported { get; }

    /// <summary>Plays <paramref name="audio"/> at the given volume, 0..1; cancels any current playback.</summary>
    void Play(SynthesisResult audio, float volume);

    /// <summary>Stops current playback immediately (text-advance courtesy).</summary>
    void Cancel();

    /// <summary>Drops every queued item without playing them.</summary>
    void Flush();
}
