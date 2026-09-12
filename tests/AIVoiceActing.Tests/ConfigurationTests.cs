namespace AIVoiceActing.Tests;

using System.Text.Json;
using AIVoiceActing;
using AIVoiceActing.Domain.Chat;
using AIVoiceActing.Domain.Pipeline;
using Xunit;

/// <summary>
/// Configuration defaults audited against TextToTalk's PluginConfiguration (the binding
/// digest) and System.Text.Json round-trip persistence — an option losing its value across
/// a save/load would silently revert a user setting.
/// </summary>
public sealed class ConfigurationTests
{
    private static Configuration Mutated()
    {
        var config = new Configuration
        {
            Version = 2,
            Enabled = false,
            GlobalVolume = 1.4f,
            SelectedAudioDeviceIndex = 3,
            UseKeybind = true,
            ModifierKey = VirtualKeys.Shift,
            MajorKey = VirtualKeys.Key0,
            ReadFromQuestTalkAddon = false,
            CancelSpeechOnTextAdvance = false,
            SkipVoicedQuestText = false,
            ReadFromBattleTalkAddon = false,
            SkipVoicedBattleText = false,
            SkipMessagesFromYou = true,
            OnlyMessagesFromYou = true,
            EnableNameWithSay = true,
            NameNpcWithSay = true,
            SayPlayerWorldName = true,
            DisallowMultipleSay = true,
            SayPartialName = true,
            OnlySayFirstOrLastName = FirstOrLastName.Last,
            UsePlayerRateLimiter = true,
            MessagesPerSecond = 12.5f,
            RemoveStutter = false,
            UseRaceVoicePresets = false,
            AdHocStyleTagsEnabled = true,
            StyleTag = "!!",
            CurrentPresetId = 7,
            SelectedEp = "coreml",
            DefaultExaggeration = 0.7f,
            DirectorEnabled = true,
            Lexicons = { "/tmp/words.pls", "/tmp/more.xml" },
            Triggers = { new TriggerSpec("gift", IsRegex: false) },
            Exclusions = { new TriggerSpec(@"spoiler\d+", IsRegex: true) },
            EnabledChatTypesPresets =
            {
                new EnabledChatTypesPreset
                {
                    Id = 7,
                    Name = "Battle",
                    EnableAllChatTypes = false,
                    EnabledChatTypes = new List<int> { ChatChannels.NpcDialogue, (int)AdditionalChatType.DamageDealtByYou },
                    UseKeybind = true,
                    ModifierKey = VirtualKeys.Control,
                    MajorKey = VirtualKeys.N,
                },
                new EnabledChatTypesPreset { Id = 9, Name = "All", EnableAllChatTypes = true },
            },
        };
        return config;
    }

    [Fact]
    public void Defaults_MatchTextToTalkParity()
    {
        var config = new Configuration();

        Assert.Equal(1, config.Version);
        Assert.True(config.Enabled);
        Assert.Equal(1.0f, config.GlobalVolume);
        Assert.Equal(0, config.SelectedAudioDeviceIndex);

        Assert.False(config.UseKeybind);
        Assert.Equal(VirtualKeys.Control, config.ModifierKey);
        Assert.Equal(VirtualKeys.N, config.MajorKey);

        Assert.True(config.ReadFromQuestTalkAddon);
        Assert.True(config.CancelSpeechOnTextAdvance);
        Assert.True(config.SkipVoicedQuestText);

        Assert.True(config.ReadFromBattleTalkAddon);
        Assert.True(config.SkipVoicedBattleText);

        Assert.False(config.SkipMessagesFromYou);
        Assert.False(config.OnlyMessagesFromYou);

        Assert.False(config.EnableNameWithSay);
        Assert.False(config.NameNpcWithSay);
        Assert.False(config.SayPlayerWorldName);
        Assert.False(config.DisallowMultipleSay);
        Assert.False(config.SayPartialName);
        Assert.Equal(FirstOrLastName.First, config.OnlySayFirstOrLastName);

        Assert.False(config.UsePlayerRateLimiter);
        Assert.Equal(5f, config.MessagesPerSecond);

        Assert.True(config.RemoveStutter);
        Assert.True(config.UseRaceVoicePresets);
        Assert.Empty(config.Lexicons);

        Assert.False(config.AdHocStyleTagsEnabled);
        Assert.Equal("|", config.StyleTag);

        Assert.False(config.DirectorEnabled);
        Assert.Equal("auto", config.SelectedEp);
        Assert.Equal(0.5f, config.DefaultExaggeration);

        Assert.Empty(config.Triggers);
        Assert.Empty(config.Exclusions);
    }

    [Fact]
    public void DefaultPreset_IsNpcDialogueOnly_NoKeybind()
    {
        var config = new Configuration();

        var preset = Assert.Single(config.EnabledChatTypesPresets);
        Assert.Equal(0, preset.Id);
        Assert.Equal("Default", preset.Name);
        Assert.False(preset.EnableAllChatTypes);
        Assert.Equal([ChatChannels.NpcDialogue], preset.EnabledChatTypes);
        Assert.False(preset.UseKeybind);
        Assert.Equal(VirtualKeys.Shift, preset.ModifierKey);
        Assert.Equal(VirtualKeys.Key0, preset.MajorKey);
        Assert.Equal(0, config.CurrentPresetId);
        Assert.Same(preset, config.CurrentPreset);
    }

    [Fact]
    public void NpcDialogueChannel_WireValue_Is61()
    {
        // Verified against the installed Dalamud API; presets persist ints, so a value
        // drift would silently point the default preset at the wrong channel.
        Assert.Equal(61, ChatChannels.NpcDialogue);
    }

    [Fact]
    public void StyleRegex_IsDerivedFromTag()
    {
        Assert.Equal(@"\|(.*?)\|", new Configuration().StyleRegex);
        Assert.Equal(@"<style>(.*?)<style>", new Configuration { StyleTag = "<style>" }.StyleRegex);
        Assert.Equal(string.Empty, new Configuration { StyleTag = "" }.StyleRegex);
    }

    [Fact]
    public void RoundTrip_PreservesEveryValue()
    {
        var original = Mutated();
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.Equal(2, restored.Version);
        Assert.False(restored.Enabled);
        Assert.Equal(1.4f, restored.GlobalVolume);
        Assert.Equal(3, restored.SelectedAudioDeviceIndex);
        Assert.True(restored.UseKeybind);
        Assert.Equal(VirtualKeys.Shift, restored.ModifierKey);
        Assert.Equal(VirtualKeys.Key0, restored.MajorKey);
        Assert.False(restored.ReadFromQuestTalkAddon);
        Assert.False(restored.CancelSpeechOnTextAdvance);
        Assert.False(restored.SkipVoicedQuestText);
        Assert.False(restored.ReadFromBattleTalkAddon);
        Assert.False(restored.SkipVoicedBattleText);
        Assert.True(restored.SkipMessagesFromYou);
        Assert.True(restored.OnlyMessagesFromYou);
        Assert.True(restored.EnableNameWithSay);
        Assert.True(restored.NameNpcWithSay);
        Assert.True(restored.SayPlayerWorldName);
        Assert.True(restored.DisallowMultipleSay);
        Assert.True(restored.SayPartialName);
        Assert.Equal(FirstOrLastName.Last, restored.OnlySayFirstOrLastName);
        Assert.True(restored.UsePlayerRateLimiter);
        Assert.Equal(12.5f, restored.MessagesPerSecond);
        Assert.False(restored.RemoveStutter);
        Assert.False(restored.UseRaceVoicePresets);
        Assert.Equal(["/tmp/words.pls", "/tmp/more.xml"], restored.Lexicons);
        Assert.True(restored.AdHocStyleTagsEnabled);
        Assert.Equal("!!", restored.StyleTag);
        Assert.Equal(@"!!(.*?)!!", restored.StyleRegex);
        Assert.Equal(7, restored.CurrentPresetId);
        Assert.Equal("coreml", restored.SelectedEp);
        Assert.Equal(0.7f, restored.DefaultExaggeration);
        Assert.True(restored.DirectorEnabled);
        Assert.Equal([new TriggerSpec("gift", false)], restored.Triggers);
        Assert.Equal([new TriggerSpec(@"spoiler\d+", true)], restored.Exclusions);

        var battle = restored.EnabledChatTypesPresets.Single(p => p.Id == 7);
        Assert.Equal("Battle", battle.Name);
        Assert.False(battle.EnableAllChatTypes);
        Assert.Equal(
            [ChatChannels.NpcDialogue, (int)AdditionalChatType.DamageDealtByYou],
            battle.EnabledChatTypes);
        Assert.True(battle.UseKeybind);
        Assert.Equal(VirtualKeys.Control, battle.ModifierKey);
        Assert.Equal(VirtualKeys.N, battle.MajorKey);
        Assert.True(restored.EnabledChatTypesPresets.Single(p => p.Id == 9).EnableAllChatTypes);
    }

    [Fact]
    public void RoundTrip_MissingFields_KeepDefaults()
    {
        // An older config file without the local-AI fields must not reset them to zero.
        const string json = """{"Version":1,"Enabled":true,"GlobalVolume":1.0}""";

        var restored = JsonSerializer.Deserialize<Configuration>(json)!;

        Assert.Equal("auto", restored.SelectedEp);
        Assert.Equal(0.5f, restored.DefaultExaggeration);
        Assert.False(restored.DirectorEnabled);
        Assert.Equal(VirtualKeys.Control, restored.ModifierKey);
        Assert.Equal(VirtualKeys.N, restored.MajorKey);
        Assert.True(restored.ReadFromQuestTalkAddon);
        Assert.NotNull(restored.CurrentPreset);
        Assert.Equal("Default", restored.CurrentPreset!.Name);
    }

    [Fact]
    public void CurrentPreset_MissingId_ResolvesNull()
    {
        var config = new Configuration { CurrentPresetId = 42 };

        Assert.Null(config.CurrentPreset);
    }

    [Fact]
    public void ChannelGate_OverCurrentPreset_IncludingEnableAll()
    {
        // The container consumes the config through live delegates; this exercises the
        // preset-filter path end to end (selection + EnableAllChatTypes short-circuit).
        var config = new Configuration
        {
            CurrentPresetId = 1,
            EnabledChatTypesPresets =
            {
                new EnabledChatTypesPreset { Id = 1, EnabledChatTypes = new List<int> { 10 } },
                new EnabledChatTypesPreset { Id = 2, EnableAllChatTypes = true },
            },
        };
        var gate = new AIVoiceActing.Domain.Pipeline.ChatChannelGate(
            () => (IReadOnlyCollection<int>?)config.CurrentPreset?.EnabledChatTypes,
            () => config.CurrentPreset?.EnableAllChatTypes ?? false);

        Assert.True(gate.IsEnabled(10));
        Assert.False(gate.IsEnabled(11));

        config.CurrentPresetId = 2;
        Assert.True(gate.IsEnabled(11));

        config.CurrentPresetId = 99; // dangling id gates everything off
        Assert.False(gate.IsEnabled(10));
    }
}
