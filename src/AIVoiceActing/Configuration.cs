namespace AIVoiceActing;

using System.Text.RegularExpressions;
using AIVoiceActing.Domain.Pipeline;

/// <summary>
/// The full plugin option surface (TextToTalk-parity names per the plan, plus the local-AI
/// additions). Deliberately Dalamud-free — a plain JSON DTO so the unit tests serialize it —
/// while <see cref="PluginConfiguration"/> adds the <c>IPluginConfiguration</c> marker at
/// the Dalamud boundary.
///
/// Defaults mirror TextToTalk where the option is copied (audit against
/// TextToTalk/src/TextToTalk/PluginConfiguration.cs): Enabled/quest/battle/courtesy toggles
/// ON, from-you and name-with-say OFF, rate limiter OFF with 5 msg/s, stutter removal ON,
/// volume 1.0 (a linear multiplier; the UI exposes 0–200%), keybind values Ctrl+N (only
/// active when UseKeybind), and a single "Default" channel preset enabling NPCDialogue only.
/// Key values are the game's VirtualKey wire values (CONTROL=17, N=78, SHIFT=16, KEY_0=48 —
/// verified against the installed API) so this type needs no Dalamud enums.
/// </summary>
public class Configuration
{
    public const string DefaultPresetName = "Default";

    /// <summary>Current schema version; <see cref="ConfigMigrator"/> upgrades older files to it.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Schema version (IPluginConfiguration contract). Fresh installs start current;
    /// loaded files carry their own until migrated.</summary>
    public int Version { get; set; } = CurrentVersion;

    // ---- v2 sections (see ConfigurationSections.cs) ----
    public EngineSettings Engine { get; set; } = new();

    public DeviceSettings Device { get; set; } = new();

    public VoiceSettings Voices { get; set; } = new();

    public MigrationState Migration { get; set; } = new();

    // ---- UI-owned fields (declared here so the schema has one owner) ----
    public bool ShowAdvanced { get; set; }

    public bool ReduceMotion { get; set; }

    public string LastPage { get; set; } = "";

    public int OnboardingCompletedVersion { get; set; }

    public int OnboardingStep { get; set; }

    public int MigrationNoticeAckedVersion { get; set; }

    // ---- v1 fields, kept one generation so old files still deserialize ----
    // Read only by ConfigMigrator and written as null. Remove in schema v3; nothing else may touch them.
    [Obsolete("v1: use Engine.Primary")]
    public string? SelectedEngine { get; set; }

    [Obsolete("v1: use Device.Mode")]
    public string? SelectedEp { get; set; }

    [Obsolete("v1: use Device.CpuImpact")]
    public string? CpuImpact { get; set; }

    [Obsolete("v1: use Engine.Expressive.Enabled")]
    public bool? ExpressiveEngine { get; set; }

    [Obsolete("v1: dropped with the legacy Chatterbox engine")]
    public bool? UseFp32LanguageModel { get; set; }

    // ---- General / speech ----
    public bool Enabled { get; set; } = true;

    /// <summary>Global multiplier (1.0 = no change, 0.0 mute, 2.0 double); UI shows 0–200%.</summary>
    public float GlobalVolume { get; set; } = 1.0f;

    /// <summary>Index into the output-device enumeration (0 = system default).</summary>
    public int SelectedAudioDeviceIndex { get; set; }

    // ---- Keybind (Ctrl+N toggles TTS when UseKeybind) ----
    public bool UseKeybind { get; set; }
    public int ModifierKey { get; set; } = VirtualKeys.Control;
    public int MajorKey { get; set; } = VirtualKeys.N;

    // ---- Quest / Talk ----
    public bool ReadFromQuestTalkAddon { get; set; } = true;
    public bool CancelSpeechOnTextAdvance { get; set; } = true;
    public bool SkipVoicedQuestText { get; set; } = true;

    // ---- BattleTalk ----
    public bool ReadFromBattleTalkAddon { get; set; } = true;
    public bool SkipVoicedBattleText { get; set; } = true;

    // ---- Cutscenes ----
    public bool ReadCutsceneSubtitles { get; set; } = true;

    // ---- Chat filters ----
    public bool SkipMessagesFromYou { get; set; }
    public bool OnlyMessagesFromYou { get; set; }

    // ---- Name-with-say ----
    public bool EnableNameWithSay { get; set; }
    public bool NameNpcWithSay { get; set; }
    public bool SayPlayerWorldName { get; set; }
    public bool DisallowMultipleSay { get; set; }
    public bool SayPartialName { get; set; }
    public FirstOrLastName OnlySayFirstOrLastName { get; set; } = FirstOrLastName.First;

    // ---- Rate limiting ----
    public bool UsePlayerRateLimiter { get; set; }
    public float MessagesPerSecond { get; set; } = 5f;

    // ---- Voice assignment / engine ----
    public bool RemoveStutter { get; set; } = true;
    public bool UseRaceVoicePresets { get; set; } = true;

    /// <summary>User lexicon file paths (.pls/.xml); the file list is the surface — no repository.</summary>
    public IList<string> Lexicons { get; set; } = new List<string>();

    // ---- Style tags ----
    public bool AdHocStyleTagsEnabled { get; set; }

    /// <summary>Ad-hoc style-tag delimiter; <see cref="StyleRegex"/> is always derived from it.</summary>
    public string StyleTag { get; set; } = "|";

    // ---- Channel presets ----
    public IList<EnabledChatTypesPreset> EnabledChatTypesPresets { get; set; } =
        [CreateDefaultPreset()];

    public int CurrentPresetId { get; set; }

    // ---- Local-AI additions ----
    /// <summary>Baseline delivery intensity (0..1) used when the emotion plan is neutral.</summary>
    public float DefaultExaggeration { get; set; } = 0.5f;

    /// <summary>Plans delivery with the optional GoEmotions classifier once its files are
    /// downloaded; the rules table runs otherwise (and whenever the classifier fails).</summary>
    public bool DirectorEnabled { get; set; }

    // ---- Delivery & performance (engine/device choice lives in Engine/Device) ----
    /// <summary>Pitch-shifted voices keep their speaking pace (the shift used to slow deep
    /// voices and speed up high ones). Off restores the old coupled behaviour.</summary>
    public bool KeepTempoWhenPitched { get; set; } = true;

    /// <summary>Levels every line to the same loudness before the per-voice volume applies.</summary>
    public bool LevelVoiceLoudness { get; set; } = true;

    /// <summary>Children get a higher, quicker take on their voice; elders a lower, slower one.</summary>
    public bool AgeAwareCasting { get; set; } = true;

    /// <summary>Long lines start playing after their first sentence instead of after the whole line.</summary>
    public bool StreamSentences { get; set; } = true;

    /// <summary>Shows the small now-speaking panel (speaker, voice, mood, Replay/Skip/Stop).</summary>
    public bool ShowNowSpeaking { get; set; }

    /// <summary>Lines older than this are dropped at playback instead of played late. 0 keeps everything.</summary>
    public int StaleLineSeconds { get; set; } = 20;

    // ---- Triggers / exclusions (an exclusion wins) ----
    public IList<TriggerSpec> Triggers { get; set; } = new List<TriggerSpec>();
    public IList<TriggerSpec> Exclusions { get; set; } = new List<TriggerSpec>();

    /// <summary>Derived match pattern for ad-hoc style tags: esc(tag)(.*?)esc(tag). Never edited directly.</summary>
    public string StyleRegex =>
        this.StyleTag.Length == 0
            ? string.Empty
            : $"{Regex.Escape(this.StyleTag)}(.*?){Regex.Escape(this.StyleTag)}";

    /// <summary>The active channel preset (first preset matching CurrentPresetId; null if none).</summary>
    public EnabledChatTypesPreset? CurrentPreset =>
        this.EnabledChatTypesPresets.FirstOrDefault(p => p.Id == this.CurrentPresetId);

    /// <summary>TextToTalk's first-run preset: "Default", NPCDialogue only, no keybind.</summary>
    public static EnabledChatTypesPreset CreateDefaultPreset() => new()
    {
        Id = 0,
        Name = DefaultPresetName,
        EnableAllChatTypes = false,
        EnabledChatTypes = new List<int> { ChatChannels.NpcDialogue },
        UseKeybind = false,
    };
}

/// <summary>
/// A named set of enabled chat channels (port of TextToTalk's EnabledChatTypesPreset),
/// optionally bound to a keybind that switches to it per framework tick. Channels are ints
/// carrying both XivChatType and AdditionalChatType wire values, keeping this type
/// Dalamud-free.
/// </summary>
public sealed class EnabledChatTypesPreset
{
    public int Id { get; set; }

    public bool EnableAllChatTypes { get; set; }

    public IList<int>? EnabledChatTypes { get; set; }

    public string? Name { get; set; }

    public bool UseKeybind { get; set; }

    public int ModifierKey { get; set; } = VirtualKeys.Shift;
    public int MajorKey { get; set; } = VirtualKeys.Key0;
}

/// <summary>Game wire values shared by XivChatType and AdditionalChatType (channel ints).</summary>
public static class ChatChannels
{
    /// <summary>XivChatType.NPCDialogue (verified against the installed API).</summary>
    public const int NpcDialogue = 61;
}

/// <summary>Windows VirtualKey wire values (verified against the installed API) so the pure
/// configuration type needs no Dalamud enums.</summary>
public static class VirtualKeys
{
    public const int Shift = 16;
    public const int Control = 17;
    public const int N = 78;
    public const int Key0 = 48;
}
