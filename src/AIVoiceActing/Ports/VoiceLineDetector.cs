namespace AIVoiceActing.Ports;

/// <summary>
/// Driving port detecting the game's own voice-line playback (signature-hook based).
/// Raised so the pipeline can cancel synthesized speech for the line being voiced by the game.
/// </summary>
public interface IVoiceLineDetector
{
    event Action? VoiceLinePlayback;

    void Start();

    void Dispose();
}
