namespace AIVoiceActing.Domain;

/// <summary>
/// Resolves (race, tribe, sex, model id) into a <see cref="VoiceGroup"/> — a pure port of
/// TextToTalk's CharacterGenderUtils semantics:
/// <list type="bullet">
/// <item>the customize sex byte drives the group: 0 = male, 1 = female (TTT Gender.Male/Female);</item>
/// <item>a ModelCharaId listed in the ungendered overrides forces <see cref="VoiceGroup.Ungendered"/> —
/// callers pass model ids for non-player actors only (TTT returns a player's customize sex
/// before the override check);</item>
/// <item>Hrothgar (race 7) females are canonically ungendered for voice purposes;</item>
/// <item>anything else (missing sex, sex ≥ 2) is <see cref="VoiceGroup.Unknown"/>.</item>
/// </list>
/// Tribe never influences the group (identity passthrough only); race matters solely for the
/// Hrothgar rule and the <see cref="RaceVoiceMap"/> slot nuances.
/// </summary>
public static class VoiceGroupResolver
{
    public const byte RaceHrothgar = 7;

    public static VoiceGroup Resolve(
        byte? race,
        byte? tribe,
        byte? sex,
        int? modelCharaId,
        IReadOnlySet<int>? ungenderedModelIds)
    {
        if (modelCharaId is { } modelId
            && ungenderedModelIds is { } ungendered
            && ungendered.Contains(modelId))
        {
            return VoiceGroup.Ungendered;
        }

        return sex switch
        {
            0 => VoiceGroup.Male,
            1 when race == RaceHrothgar => VoiceGroup.Ungendered,
            1 => VoiceGroup.Female,
            _ => VoiceGroup.Unknown,
        };
    }
}
