namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Age colouring on top of the race/gender pick: children speak higher and a touch
/// quicker, elders lower and slower. Applied at lookup time (never persisted), so the
/// stored voice stays the speaker's own and turning the option off restores it. Manual
/// overrides are left exactly as the user set them.
/// </summary>
public static class AgeCasting
{
    public const float ChildPitch = 1.12f;
    public const float ChildSpeed = 1.04f;
    public const float ElderPitch = 0.95f;
    public const float ElderSpeed = 0.92f;
    public const float MaxPitch = 1.4f;

    public static VoiceProfile Apply(VoiceProfile profile, byte? bodyType)
    {
        if (profile.Custom)
        {
            return profile;
        }

        return bodyType switch
        {
            BodyTypes.Child => profile with
            {
                Pitch = Math.Min(MaxPitch, profile.Pitch * ChildPitch),
                Speed = profile.Speed * ChildSpeed,
            },
            BodyTypes.Elderly => profile with
            {
                Pitch = profile.Pitch * ElderPitch,
                Speed = profile.Speed * ElderSpeed,
            },
            _ => profile,
        };
    }
}
