namespace AIVoiceActing.Domain;

/// <summary>
/// Immutable identity of a speaker resolved from game data. <see cref="Key"/> is stable
/// across sessions: players use "pc:{first} {last}@{world}", NPCs "npc:{full name lowercase}".
/// </summary>
public sealed record SpeakerIdentity(
    string Key,
    string DisplayName,
    byte? Race,
    byte? Tribe,
    byte? Sex,
    ushort? World,
    int? ModelCharaId = null);
