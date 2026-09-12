namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Resolves capture-time hints into stable speaker identities. Deliberately pure over the
/// hint: the Dalamud side that fills <see cref="SpeakerHint"/> from the object table /
/// CustomizeData arrives with the capture sources (Step 5). Keys follow TextToTalk's scheme:
/// hints with a known world are players (<c>pc:{First} {Last}@{world}</c>, title-case
/// preserved); everything else is an NPC (<c>npc:{full name lowercase}</c>). Names that
/// cannot be read still resolve via an object-index/anonymous fallback key, so profile
/// lookup (IProfileStore.GetOrCreate + VoiceAssigner) can hand out a name-hash profile for
/// every speaker.
/// </summary>
public sealed class GameObjectSpeakerDirectory : ISpeakerDirectory
{
    public SpeakerIdentity Resolve(SpeakerHint hint)
    {
        var name = hint.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            var fallbackKey = hint.ObjectIndex is { } index ? $"npc:object-{index}" : "npc:anonymous";
            return new SpeakerIdentity(fallbackKey, "(unknown)", hint.Race, hint.Tribe, hint.Sex, hint.World);
        }

        var key = hint.World is { } world
            ? SpeakerKey.ForPlayer(name, world)
            : SpeakerKey.ForNpc(name);
        return new SpeakerIdentity(key, name, hint.Race, hint.Tribe, hint.Sex, hint.World);
    }
}
