namespace AIVoiceActing.Domain;

/// <summary>
/// Immutable identity of a speaker resolved from game data. <see cref="Key"/> is stable
/// across sessions: players use "pc:{first} {last}@{world}", NPCs "npc:{full name lowercase}".
/// </summary>
/// <param name="BodyType">CustomizeData body type: 1 adult, 3 elderly, 4 child (null unknown).</param>
/// <param name="Source">Where the race/sex facts came from.</param>
public sealed record SpeakerIdentity(
    string Key,
    string DisplayName,
    byte? Race,
    byte? Tribe,
    byte? Sex,
    ushort? World,
    int? ModelCharaId = null,
    byte? BodyType = null,
    IdentitySource Source = IdentitySource.Unknown);

/// <summary>How a speaker's race/sex facts were obtained (shown in the UI as confidence).</summary>
public enum IdentitySource
{
    /// <summary>No game data: the speaker was neither spawned nor found in the NPC sheets.</summary>
    Unknown,

    /// <summary>Read from the spawned character's customize data.</summary>
    Live,

    /// <summary>Looked up by name in the game's NPC sheets (speaker not spawned).</summary>
    NpcSheet,
}

/// <summary>CustomizeData body-type values the casting reacts to.</summary>
public static class BodyTypes
{
    public const byte Adult = 1;
    public const byte Elderly = 3;
    public const byte Child = 4;
}
