namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Driving port: a game surface that captures dialogue lines as they appear.</summary>
public interface IDialogueSource
{
    event Action<DialogueLine>? LineCaptured;

    void Start();

    void Dispose();
}
