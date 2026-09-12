namespace AIVoiceActing.Domain;

/// <summary>A single captured dialogue utterance, as fed into the rolling context window.</summary>
public sealed record DialogueLine(
    string SpeakerKey,
    string SpeakerName,
    string Text,
    DateTimeOffset CapturedAtUtc);
