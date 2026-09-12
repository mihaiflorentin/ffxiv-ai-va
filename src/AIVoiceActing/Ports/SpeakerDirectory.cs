namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Everything known about a speaker at capture time; every identity field is optional.</summary>
/// <param name="Name">Displayed speaker name (may include world/fold suffixes).</param>
/// <param name="World">Home world id for players, if known.</param>
/// <param name="ObjectIndex">Object table index, if the speaker is a visible game object.</param>
/// <param name="ModelCharaId">Character model id, for ungendered-model override checks.</param>
/// <param name="Race">CustomizeData race, if resolvable.</param>
/// <param name="Tribe">CustomizeData tribe, if resolvable.</param>
/// <param name="Sex">CustomizeData sex, if resolvable.</param>
public sealed record SpeakerHint(
    string Name,
    ushort? World,
    int? ObjectIndex,
    int? ModelCharaId,
    byte? Race,
    byte? Tribe,
    byte? Sex);

/// <summary>Driven port resolving speaker hints into stable <see cref="SpeakerIdentity"/> values.</summary>
public interface ISpeakerDirectory
{
    SpeakerIdentity Resolve(SpeakerHint hint);
}
