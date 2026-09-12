namespace AIVoiceActing;

using AIVoiceActing.Container;
using AIVoiceActing.Infrastructure.Dalamud;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

/// <summary>
/// Driving adapter: builds the ServiceContainer, wires the Dalamud capture sources, the
/// voiced-line detector and the pipeline together, and manages lifetimes. All decisions
/// live in Domain/adapters; this type only composes. Configuration-backed option delegates
/// arrive with the configuration step (Step 6) — the literals below carry TextToTalk's
/// defaults until then (SkipVoiced*Text ON, both talk addons ON, subtitle capture OFF
/// pending in-game verification).
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
    public static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    public static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    public static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    public static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    public static ISigScanner SigScanner { get; private set; } = null!;

    private readonly ServiceContainer services;
    private readonly TalkAddonPoller talkPoller;
    private readonly TalkAddonPoller battleTalkPoller;
    private readonly ObjectTableHintProvider hints;
    private readonly TalkDialogueSource talkSource;
    private readonly BattleTalkDialogueSource battleTalkSource;
    private readonly ChatDialogueSource chatSource;
    private readonly CutsceneSubtitleSource subtitleSource;
    private readonly VoiceLineDetector voiceLineDetector;

    public AIVoiceActingPlugin()
    {
        var configDir = PluginInterface.ConfigDirectory.FullName;
        var conditions = new ConditionFlagsAdapter(Condition, ClientState);

        this.talkPoller = new TalkAddonPoller(ClientState, Condition, GameGui, "Talk");
        this.battleTalkPoller = new TalkAddonPoller(ClientState, Condition, GameGui, "_BattleTalk");

        this.services = new ServiceContainer(
            logSinkFactory: () => new DalamudLogSink(PluginLog),
            profileStorePathFactory: () => Path.Combine(configDir, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(configDir, "models"),
            voicesManifestFactory: () => ExtractVoicesManifest(configDir),
            // Voiced-cutscene courtesy: a cutscene owns one shared context window.
            cutsceneActiveFactory: () => conditions.OccupiedInCutscene || conditions.WatchingCutscene,
            talkVisibleFactory: () => this.talkPoller.IsVisible());

        this.hints = new ObjectTableHintProvider(ObjectTable);

        this.talkSource = new TalkDialogueSource(
            Framework, this.talkPoller, this.hints, this.services.SpeakerDirectory,
            enabled: () => true,
            readFromAddon: () => true,
            skipVoicedQuestText: () => true,
            this.services.Announcer, this.services.FromYou, this.services.PipelineSink);
        this.battleTalkSource = new BattleTalkDialogueSource(
            Framework, this.battleTalkPoller, this.hints, this.services.SpeakerDirectory,
            enabled: () => true,
            readFromAddon: () => true,
            skipVoicedBattleText: () => true,
            this.services.Announcer, this.services.FromYou, this.services.PipelineSink);
        this.chatSource = new ChatDialogueSource(
            ChatGui, this.talkPoller, this.battleTalkPoller, this.hints, this.services.SpeakerDirectory,
            this.services.Announcer, this.services.FromYou,
            enabled: () => true,
            sayPlayerWorldName: () => false,
            readFromQuestTalkAddon: () => true,
            readFromBattleTalkAddon: () => true,
            skipMessagesFromYou: () => false,
            this.services.PipelineSink);
        this.subtitleSource = new CutsceneSubtitleSource(
            Framework, GameGui, this.services.SpeakerDirectory, conditions, PluginLog,
            // Flipped on only after in-game verification of the subtitle addon name.
            enabled: () => false,
            this.services.PipelineSink);
        this.voiceLineDetector = new VoiceLineDetector(SigScanner, GameInteropProvider, PluginLog);

        // Voiced-cutscene courtesy: the detector cancels current speech and re-samples the
        // talk addons so the game's own voice line is never synthesized over.
        this.services.VoiceLinePlaybackObserved += this.OnVoiceLinePlaybackObserved;
        this.talkSource.SpeechInterrupted += () => this.services.SpeechHandler.CancelCurrent();
        this.battleTalkSource.SpeechInterrupted += () => this.services.SpeechHandler.CancelCurrent();

        _ = this.services.Pipeline;
        this.talkSource.Start();
        this.battleTalkSource.Start();
        this.chatSource.Start();
        this.subtitleSource.Start();
        this.voiceLineDetector.Start();

        this.services.LogSink.Info("AIVoiceActing loaded: capture sources and pipeline armed.");
    }

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
        this.services.VoiceLinePlaybackObserved -= this.OnVoiceLinePlaybackObserved;
        this.voiceLineDetector.Dispose();
        this.subtitleSource.Dispose();
        this.chatSource.Dispose();
        this.battleTalkSource.Dispose();
        this.talkSource.Dispose();
        this.services.Dispose();
    }
}
