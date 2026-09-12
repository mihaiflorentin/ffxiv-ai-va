namespace AIVoiceActing.Domain;

/// <summary>
/// Bucket of reference voices a speaker draws from (TextToTalk gender-bucket parity:
/// male / female / ungendered presets). <see cref="Unknown"/> speakers share the
/// ungendered slot set.
/// </summary>
public enum VoiceGroup
{
    Male,
    Female,
    Ungendered,
    Unknown,
}
