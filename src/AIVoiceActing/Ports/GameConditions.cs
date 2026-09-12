namespace AIVoiceActing.Ports;

/// <summary>Driven port over in-game condition flags used to gate the pipeline.</summary>
public interface IGameConditions
{
    /// <summary>Player is occupied in a cutscene event (talk-style cutscene).</summary>
    bool OccupiedInCutscene { get; }

    /// <summary>Player is watching a (fullscreen or fading) cutscene.</summary>
    bool WatchingCutscene { get; }

    /// <summary>Player is logged into a character.</summary>
    bool IsLoggedIn { get; }
}
