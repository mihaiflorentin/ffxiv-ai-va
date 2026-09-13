namespace AIVoiceActing;

using AIVoiceActing.Container;
using AIVoiceActing.Infrastructure.Audio;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Infrastructure.Dalamud;
using AIVoiceActing.UI;
using Dalamud.Interface.Windowing;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

/// <summary>
/// Driving adapter: loads the persisted configuration, builds the ServiceContainer with
/// live config-backed option delegates, wires the Dalamud capture sources, the voiced-line
/// detector, the speech queue/sink and the slash commands together, and manages lifetimes.
/// All decisions live in Domain/adapters; this type only composes.
/// </summary>
public sealed class AIVoiceActingPlugin : IDalamudPlugin, IDisposable
{
    [PluginService]
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    [PluginService]
    public static ICondition Condition { get; private set; } = null!;

    [PluginService]
    public static IPluginLog PluginLog { get; private set; } = null!;

    [PluginService]
    public static IFramework Framework { get; private set; } = null!;

    [PluginService]
    public static IKeyState KeyState { get; private set; } = null!;

    [PluginService]
    public static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    public static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    public static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    public static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    public static ISigScanner SigScanner { get; private set; } = null!;

    private static readonly string[] CommandNames =
    [
        "/aivaconfig", "/aiva", "/cancelspeech", "/toggletts", "/enabletts",
        "/disabletts", "/aivapreset", "/aivavolume", "/aivastyles",
    ];

    private readonly PluginConfiguration pluginConfig;
    private readonly ServiceContainer services;
    private readonly CommandRouter commands;
    private readonly KeybindMonitor keybinds = new();
    private readonly NAudioSink audioSink;
    private readonly TalkAddonPoller talkPoller;
    private readonly TalkAddonPoller battleTalkPoller;
    private readonly ObjectTableHintProvider hints;
    private readonly TalkDialogueSource talkSource;
    private readonly BattleTalkDialogueSource battleTalkSource;
    private readonly ChatDialogueSource chatSource;
    private readonly CutsceneSubtitleSource subtitleSource;
    private readonly VoiceLineDetector voiceLineDetector;
    private readonly WindowSystem windowSystem = new("AIVoiceActing");
    private readonly ConfigurationWindow configWindow;
    private readonly StylesWindow stylesWindow;
    private readonly Action openConfigUiHook;

    public AIVoiceActingPlugin()
    {
        var configDir = PluginInterface.ConfigDirectory.FullName;
        this.pluginConfig = PluginInterface.GetPluginConfig() as PluginConfiguration
            ?? new PluginConfiguration();
        var config = this.pluginConfig;
        // Audio sink first so the queue factory closure never sees a null field.
        this.audioSink = new NAudioSink(
            () => config.SelectedAudioDeviceIndex, new DalamudLogSink(PluginLog));
        var sink = this.audioSink;
        var conditions = new ConditionFlagsAdapter(Condition, ClientState);

        this.talkPoller = new TalkAddonPoller(ClientState, Condition, GameGui, "Talk");
        this.battleTalkPoller = new TalkAddonPoller(ClientState, Condition, GameGui, "_BattleTalk");

        this.services = new ServiceContainer(
            logSinkFactory: () => new DalamudLogSink(PluginLog),
            profileStorePathFactory: () => Path.Combine(configDir, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(configDir, "models"),
            voicesManifestFactory: () => ExtractVoicesManifest(configDir),
            // Re-read on first use and whenever the processor sees changed files; the
            // loader caches by file stamps, so unchanged files cost nothing.
            lexiconEntriesFactory: () => Infrastructure.Text.LexiconFileLoader.Load(
                config.Lexicons, new DalamudLogSink(PluginLog)),
            speechQueueFactory: () => new PlaybackSpeechQueue(
                sink, () => config.GlobalVolume, new DalamudLogSink(PluginLog)),
            enabledChatTypesFactory: () => config.CurrentPreset?.EnabledChatTypes as IReadOnlyCollection<int>,
            enableAllChatTypesFactory: () => config.CurrentPreset?.EnableAllChatTypes ?? false,
            triggersFactory: () => config.Triggers.AsReadOnly(),
            exclusionsFactory: () => config.Exclusions.AsReadOnly(),
            pipelineEnabledFactory: () => config.Enabled,
            skipMessagesFromYouFactory: () => config.SkipMessagesFromYou,
            onlyMessagesFromYouFactory: () => config.OnlyMessagesFromYou,
            playerRateLimitEnabledFactory: () => config.UsePlayerRateLimiter,
            messagesPerSecondFactory: () => config.MessagesPerSecond,
            localPlayerNameFactory: () => ObjectTable.LocalPlayer?.Name.TextValue,
            nameWithSayEnabledFactory: () => config.EnableNameWithSay,
            nameNpcWithSayFactory: () => config.NameNpcWithSay,
            disallowMultipleSayFactory: () => config.DisallowMultipleSay,
            sayPartialNameFactory: () => config.SayPartialName,
            onlySayFirstOrLastNameFactory: () => config.OnlySayFirstOrLastName,
            // Voiced-cutscene courtesy: a cutscene owns one shared context window.
            cutsceneActiveFactory: () => conditions.OccupiedInCutscene || conditions.WatchingCutscene,
            talkVisibleFactory: () => this.talkPoller.IsVisible(),
            defaultExaggerationFactory: () => config.DefaultExaggeration,
            selectedEpFactory: () => config.SelectedEp,
            llmDirectorFactory: () => config.DirectorEnabled ? this.TryGetLlmDirector() : null,
            useRaceVoicePresetsFactory: () => config.UseRaceVoicePresets,
            adHocStyleTagsFactory: () => config.AdHocStyleTagsEnabled,
            styleTagRegexFactory: () => config.StyleRegex);
        this.hints = new ObjectTableHintProvider(ObjectTable);

        this.talkSource = new TalkDialogueSource(
            Framework, this.talkPoller, this.hints, this.services.SpeakerDirectory,
            enabled: () => config.Enabled,
            readFromAddon: () => config.ReadFromQuestTalkAddon,
            skipVoicedQuestText: () => config.SkipVoicedQuestText,
            this.services.Announcer, this.services.FromYou, this.services.PipelineSink);
        this.battleTalkSource = new BattleTalkDialogueSource(
            Framework, this.battleTalkPoller, this.hints, this.services.SpeakerDirectory,
            enabled: () => config.Enabled,
            readFromAddon: () => config.ReadFromBattleTalkAddon,
            skipVoicedBattleText: () => config.SkipVoicedBattleText,
            this.services.Announcer, this.services.FromYou, this.services.PipelineSink);
        this.chatSource = new ChatDialogueSource(
            ChatGui, this.talkPoller, this.battleTalkPoller, this.hints,
            this.services.Announcer, this.services.FromYou,
            enabled: () => config.Enabled,
            sayPlayerWorldName: () => config.SayPlayerWorldName,
            readFromQuestTalkAddon: () => config.ReadFromQuestTalkAddon,
            readFromBattleTalkAddon: () => config.ReadFromBattleTalkAddon,
            skipMessagesFromYou: () => config.SkipMessagesFromYou,
            this.services.PipelineSink);
        this.subtitleSource = new CutsceneSubtitleSource(
            Framework, GameGui, this.services.SpeakerDirectory, conditions, PluginLog,
            // Flipped on only after in-game verification of the subtitle addon name.
            enabled: () => false,
            this.services.PipelineSink);
        this.voiceLineDetector = new VoiceLineDetector(SigScanner, GameInteropProvider, PluginLog);

        this.commands = new CommandRouter(
            enabled: () => config.Enabled,
            setEnabled: value =>
            {
                config.Enabled = value;
                this.SaveConfig();
            },
            // TTT parity: /cancelspeech and /disabletts drop the WHOLE backlog
            // (CancelTts -> CancelAllSpeech); text advance stays current-line only.
            cancelSpeech: () => this.services.SpeechQueue.Clear(),
            currentPresetId: () => config.CurrentPresetId,
            presets: () => [.. config.EnabledChatTypesPresets.Select(p => new PresetSummary(p.Id, p.Name))],
            switchPreset: id =>
            {
                config.CurrentPresetId = id;
                this.SaveConfig();
            },
            volume: () => config.GlobalVolume,
            setVolume: value =>
            {
                config.GlobalVolume = value;
                this.SaveConfig();
            },
            // Step 7 sets these when the windows exist; until then the commands log.
            openConfig: () => this.services.OpenConfigurationUi,
            openStyles: () => this.services.OpenStylesUi);


        // UI (Step 7): the windows receive container-resolved ports only — the UI layer
        // never references Infrastructure types directly.
        this.configWindow = new ConfigurationWindow(
            config,
            save: this.SaveConfig,
            speech: this.services.SpeechHandler,
            synthesizer: this.services.SpeechSynthesizer,
            queue: this.services.SpeechQueue,
            profiles: this.services.ProfileStore,
            modelAssets: () => [.. ModelCatalog.Assets.Select(a => a.Asset)],
            provisioner: () => this.services.ModelProvisioner,
            modelStore: () => this.services.ModelStore,
            voiceMap: () => this.services.VoiceMap,
            sessions: () => this.services.DialogueSessions,
            modelsDir: () => this.services.ModelsDir,
            voicesDir: () => Path.Combine(this.services.ModelsDir, "voices"),
            localPlayer: () => ObjectTable.LocalPlayer is { } player
                ? (player.Name.TextValue, (ushort)player.HomeWorld.RowId)
                : null,
            openDirectory: path =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"Could not open \"{path}\": {ex.Message}");
                    return false;
                }
            },
            reportError: message => this.services.LogSink.Warn(message));
        this.stylesWindow = new StylesWindow(config, this.SaveConfig);
        this.windowSystem.AddWindow(this.configWindow);
        this.windowSystem.AddWindow(this.stylesWindow);

        // /aivaconfig + /aivastyles route through the container hooks (headless-safe);
        // the game's own OpenConfigUi (system menu) opens our configuration window.
        this.services.OpenConfigurationUi = this.configWindow.Toggle;
        this.services.OpenStylesUi = this.stylesWindow.Toggle;
        this.openConfigUiHook = () => this.configWindow.IsOpen = true;
        var uiBuilder = PluginInterface.UiBuilder;
        uiBuilder.Draw += this.windowSystem.Draw;
        uiBuilder.OpenConfigUi += this.openConfigUiHook;

        this.services.LogSink.Info("AIVoiceActing UI: configuration and styles windows registered.");
        this.RegisterCommands();

        // Voiced-cutscene courtesy: the detector cancels current speech and re-samples the
        // talk addons so the game's own voice line is never synthesized over; text advance
        // cancels speech only when the courtesy option is on (both config-gated).
        this.services.VoiceLinePlaybackObserved += this.OnVoiceLinePlaybackObserved;
        // The detector's hooks feed the container's single courtesy cancel path.
        this.services.WireVoiceLineDetector(this.voiceLineDetector);
        this.talkSource.SpeechInterrupted += this.OnSpeechInterrupted;
        this.battleTalkSource.SpeechInterrupted += this.OnSpeechInterrupted;

        Framework.Update += this.OnFrameworkUpdate;

        _ = this.services.Pipeline;
        this.talkSource.Start();
        this.battleTalkSource.Start();
        this.chatSource.Start();
        this.subtitleSource.Start();
        this.voiceLineDetector.Start();

        this.services.LogSink.Info(
            $"AIVoiceActing loaded: capture sources and pipeline armed (volume " +
            $"{config.GlobalVolume * 100f:0}%, EP \"{config.SelectedEp}\").");
    }

    private void RegisterCommands()
    {
        void Add(string name, string help) => CommandManager.AddHandler(name, new CommandInfo((_, args) =>
        {
            var result = this.commands.Execute(name, args);
            foreach (var line in result.Output)
            {
                ChatGui.Print(line);
            }
        })
        {
            HelpMessage = help,
            ShowInHelp = true,
        });

        Add("/aivaconfig", "Toggle AIVoiceActing's configuration window.");
        Add("/aiva", "Alias for /aivaconfig.");
        Add("/cancelspeech", "Cancel all queued TTS messages.");
        Add("/toggletts", "Toggle AIVoiceActing's text-to-speech.");
        Add("/disabletts", "Disable AIVoiceActing's text-to-speech.");
        Add("/enabletts", "Enable AIVoiceActing's text-to-speech.");
        Add("/aivapreset", "Switch the active channel settings preset by name. Run with no arguments to show the current preset and list available presets.");
        Add("/aivavolume", "Adjust the global TTS volume. Pass a percentage (0-200) to set, or +N/-N to adjust relatively. Run with no arguments to show the current volume.");
        Add("/aivastyles", "Toggle AIVoiceActing's styles window.");
    }

    private void OnSpeechInterrupted()
    {
        if (this.pluginConfig.CancelSpeechOnTextAdvance)
        {
            this.services.SpeechHandler.CancelCurrent();
        }
    }

    private IEmotionDirector? TryGetLlmDirector()
    {
        // The LLM director asset (Qwen3-0.6B ONNX) has no verified export yet; the rules
        // table always covers delivery until it lands. Log once so the option is auditable.
        if (!this.llmDirectorUnavailableLogged)
        {
            this.llmDirectorUnavailableLogged = true;
            this.services.LogSink.Info(
                "DirectorEnabled is on but no LLM director asset is available; using the rules director.");
        }

        return null;
    }

    private bool llmDirectorUnavailableLogged;

    /// <summary>Framework tick: keybind checking (TextToTalk's toggle semantics) — Ctrl+N
    /// toggles TTS, per-preset keybinds switch presets.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var config = this.pluginConfig;
        if (!config.UseKeybind && !config.EnabledChatTypesPresets.Any(p => p.UseKeybind))
        {
            return; // nothing bound: skip the per-tick key-state reads and list materialization
        }

        var result = this.keybinds.Tick(
            vkey => KeyState[vkey],
            useTtsToggle: config.UseKeybind,
            config.ModifierKey,
            config.MajorKey,
            [.. config.EnabledChatTypesPresets.Select(
                p => new PresetKeybind(p.Id, p.Name, p.UseKeybind, p.ModifierKey, p.MajorKey))]);

        if (result.TtsToggled)
        {
            foreach (var line in this.commands.Execute("/toggletts", "").Output)

            {
                ChatGui.Print(line);
            }
        }
        else if (result.PresetSwitched is { } preset)
        {
            config.CurrentPresetId = preset.PresetId;
            this.SaveConfig();
            ChatGui.Print($"AIVoiceActing preset -> {preset.Name ?? $"#{preset.PresetId}"}");
        }
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(this.pluginConfig);

    private void OnVoiceLinePlaybackObserved()
    {
        this.talkSource.PollOnVoiceLine();
        this.battleTalkSource.PollOnVoiceLine();
    }

    /// <summary>Materializes the embedded race/voice manifest into the config directory
    /// once, so the profile store can read it like any on-disk asset.</summary>
    private static string ExtractVoicesManifest(string configDir)
    {
        var path = Path.Combine(configDir, "voices.json");
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(configDir);
        using var stream = typeof(AIVoiceActingPlugin).Assembly.GetManifestResourceStream(
            "AIVoiceActing.Domain.voices.json")
            ?? throw new InvalidOperationException("Embedded voices.json manifest is missing.");
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    public void Dispose()
    {
        // Hooks first (UI draw + keybinds), then sources, then queue/sink state inside
        // the container.
        var uiBuilder = PluginInterface.UiBuilder;
        uiBuilder.Draw -= this.windowSystem.Draw;
        uiBuilder.OpenConfigUi -= this.openConfigUiHook;
        this.windowSystem.RemoveAllWindows();
        Framework.Update -= this.OnFrameworkUpdate;
        foreach (var name in CommandNames)
        {
            CommandManager.RemoveHandler(name);
        }

        this.services.VoiceLinePlaybackObserved -= this.OnVoiceLinePlaybackObserved;
        this.services.UnwireVoiceLineDetector();
        this.talkSource.SpeechInterrupted -= this.OnSpeechInterrupted;
        this.battleTalkSource.SpeechInterrupted -= this.OnSpeechInterrupted;

        this.voiceLineDetector.Dispose();
        this.subtitleSource.Dispose();
        this.chatSource.Dispose();
        this.battleTalkSource.Dispose();
        this.talkSource.Dispose();
        this.services.Dispose();
    }
}
