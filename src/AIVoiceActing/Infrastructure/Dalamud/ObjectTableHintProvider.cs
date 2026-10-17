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
    private readonly Func<NpcIdentityIndex?>? npcSheets;

    public ObjectTableHintProvider(
        IObjectTable objects,
        IReadOnlySet<int>? ungenderedModelIds = null,
        Func<NpcIdentityIndex?>? npcSheets = null)
    {
        this.objects = objects;
        this.ungenderedModelIds = ungenderedModelIds ?? UngenderedModelIds.Load();
        this.npcSheets = npcSheets;
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
        if (obj is null)
        {
            return this.FromNpcSheets(speakerName, world: null);
        }

        var (race, tribe, sex, modelCharaId, bodyType) = this.ReadCustomize(obj);
        return new SpeakerHint(
            obj.Name.TextValue,
            World: null,
            ObjectIndex: obj.ObjectIndex,
            ModelCharaId: modelCharaId,
            Race: race,
            Tribe: tribe,
            Sex: sex,
            BodyType: bodyType,
            Source: IdentitySource.Live);
    }

    /// <summary>
    /// Speaker not spawned (cutscene actors, BattleTalk portraits, far-away NPCs): look the
    /// name up in the game's NPC sheets. The ungendered-model override applies here too.
    /// </summary>
    private SpeakerHint FromNpcSheets(string speakerName, ushort? world)
    {
        var name = SpeakerKey.StripWorldSuffix(speakerName);
        if (world is null && this.npcSheets?.Invoke() is { } sheets && sheets.TryGet(name, out var npc))
        {
            var sex = npc.ModelCharaId is { } model && this.ungenderedModelIds.Contains(model) ? null : npc.Sex;
            return new SpeakerHint(
                speakerName, World: null, ObjectIndex: null, npc.ModelCharaId,
                npc.Race, npc.Tribe, sex, npc.BodyType, IdentitySource.NpcSheet);
        }

        return new SpeakerHint(speakerName, world, null, null, null, null, null);
    }

    /// <summary>
    /// Chat hint: identity facts from the matched object when present; the world comes from
    /// the player-link payload (or the matched player's home world), so cross-world players
    /// still form stable "pc:" keys.
    /// </summary>
    public SpeakerHint FromChat(IGameObject? speakerObject, SeString sender)
    {
        var world = SeStringUtils.GetPlayerWorldRowId(sender, speakerObject);
        if (speakerObject is null)
        {
            // NPC dialogue echoed to chat (cutscenes) has no object; players keep their world.
            return this.FromNpcSheets(sender.TextValue, world);
        }

        var (race, tribe, sex, modelCharaId, bodyType) = this.ReadCustomize(speakerObject);
        return new SpeakerHint(
            speakerObject.Name.TextValue,
            World: world,
            ObjectIndex: speakerObject.ObjectIndex,
            ModelCharaId: modelCharaId,
            Race: race,
            Tribe: tribe,
            Sex: sex,
            BodyType: bodyType,
            Source: IdentitySource.Live);
    }

    /// <summary>
    /// CustomizeData + model id read (CharacterGenderUtils port): the game object's
    /// character struct carries DrawData.CustomizeData and ModelContainer.ModelCharaId.
    /// The ungendered override applies to non-PC actors only and nulls the sex byte.
    /// </summary>
    private (byte? Race, byte? Tribe, byte? Sex, int? ModelCharaId, byte? BodyType) ReadCustomize(
        IGameObject? gameObject)
    {
        if (gameObject is null || gameObject.Address == nint.Zero)
        {
            return (null, null, null, null, null);
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

            byte? bodyType = customize.BodyType == 0 ? null : customize.BodyType;
            return ((byte)customize.Race, (byte)customize.Tribe, sex, modelCharaId, bodyType);
        }
    }
}
