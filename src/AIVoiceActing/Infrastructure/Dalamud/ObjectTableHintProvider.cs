namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Ports;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Game.Text.SeStringHandling;
using global::Dalamud.Plugin.Services;
using AIVoiceActing.Domain;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

/// <summary>
/// Builds <see cref="SpeakerHint"/>s from the object table (port of ObjectTableUtils'
/// name lookup plus TextToTalk's CharacterGenderUtils customize/model read). The
/// ungendered-model override is applied here — for non-player actors whose ModelCharaId is
/// listed, Sex is nulled so the voice-group resolver lands the speaker in the ungendered
/// slot pool (Unknown and Ungendered share that set; see RaceVoiceMap). PCs keep their raw
/// customize sex, exactly like TextToTalk.
/// </summary>
public sealed class ObjectTableHintProvider
{
    private readonly IObjectTable objects;
    private readonly IReadOnlySet<int> ungenderedModelIds;

    public ObjectTableHintProvider(IObjectTable objects, IReadOnlySet<int>? ungenderedModelIds = null)
    {
        this.objects = objects;
        this.ungenderedModelIds = ungenderedModelIds ?? UngenderedModelIds.Load();
    }

    /// <summary>ObjectTableUtils.GetGameObjectByName for a plain addon name: names are
    /// compared after stripping the "»World" suffix from both sides.</summary>
    public IGameObject? FindObject(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var parsedName = SpeakerKey.StripWorldSuffix(name);
        return this.objects.FirstOrDefault(gObj =>
            SpeakerKey.StripWorldSuffix(gObj.Name.TextValue) == parsedName);
    }

    /// <summary>ObjectTableUtils.GetGameObjectByName for a chat SeString sender.</summary>
    public IGameObject? FindObject(SeString? name)
    {
        if (name is null || string.IsNullOrEmpty(name.TextValue))
        {
            return null;
        }

        return !SeStringUtils.TryGetEntityName(name, out var parsedName)
            ? null
            : this.FindObject(parsedName);
    }

    /// <summary>
    /// Talk/BattleTalk hint: the matched entity's customize data when found, else just the
    /// addon's speaker name (still resolvable via the name fallback key). World stays null —
    /// talk lines are NPC dialogue for our purposes (TTT carries no world here).
    /// </summary>
    public SpeakerHint ForTalk(string speakerName)
    {
        var obj = this.FindObject(speakerName);
        var (race, tribe, sex, modelCharaId) = ReadCustomize(obj);
        return new SpeakerHint(
            obj?.Name.TextValue ?? speakerName,
            World: null,
            ObjectIndex: obj?.ObjectIndex,
            ModelCharaId: modelCharaId,
            Race: race,
            Tribe: tribe,
            Sex: sex);
    }

    /// <summary>
    /// Chat hint: identity facts from the matched object when present; the world comes from
    /// the player-link payload (or the matched player's home world), so cross-world players
    /// still form stable "pc:" keys.
    /// </summary>
    public SpeakerHint FromChat(IGameObject? speakerObject, SeString sender)
    {
        var (race, tribe, sex, modelCharaId) = ReadCustomize(speakerObject);
        return new SpeakerHint(
            speakerObject?.Name.TextValue ?? sender.TextValue,
            World: SeStringUtils.GetPlayerWorldRowId(sender, speakerObject),
            ObjectIndex: speakerObject?.ObjectIndex,
            ModelCharaId: modelCharaId,
            Race: race,
            Tribe: tribe,
            Sex: sex);
    }

    /// <summary>
    /// CustomizeData + model id read (CharacterGenderUtils port): the game object's
    /// character struct carries DrawData.CustomizeData and ModelContainer.ModelCharaId.
    /// The ungendered override applies to non-PC actors only and nulls the sex byte.
    /// </summary>
    private (byte? Race, byte? Tribe, byte? Sex, int? ModelCharaId) ReadCustomize(
        IGameObject? gameObject)
    {
        if (gameObject is null || gameObject.Address == nint.Zero)
        {
            return (null, null, null, null);
        }

        unsafe
        {
            var chara = (Character*)gameObject.Address;
            var customize = chara->DrawData.CustomizeData;
            var modelCharaId = (int?)chara->ModelContainer.ModelCharaId;
            byte? sex = (byte)customize.Sex;

            // TTT: a PC's customize sex is authoritative; a non-PC on an ungendered model
            // (e.g. Feo Ul, model 2520) is forced out of the male/female groups.
            if (gameObject is not IPlayerCharacter &&
                this.ungenderedModelIds.Contains(modelCharaId ?? -1))
            {
                sex = null;
            }

            return ((byte)customize.Race, (byte)customize.Tribe, sex, modelCharaId);
        }
    }
}
