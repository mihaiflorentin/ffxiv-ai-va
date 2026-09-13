namespace AIVoiceActing.Ports;

/// <summary>Driving port: a game surface that captures dialogue lines as they appear.</summary>
public interface IDialogueSource
{
    void Start();

    void Dispose();
}
