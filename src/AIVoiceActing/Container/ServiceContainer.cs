namespace AIVoiceActing.Container;

using System.Globalization;
using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Infrastructure.Net;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Infrastructure.Text;
using AIVoiceActing.Ports;
using AIVoiceActing.Infrastructure.Dalamud;

/// <summary>
/// Composition root (census container pattern): lazy, lock-guarded accessors returning
/// port types, cached after first resolution. This type is deliberately Dalamud-free so it
/// can be exercised by tests on any OS; the plugin entry (driving adapter) wires the Dalamud
/// adapters into it, e.g. <c>new ServiceContainer(logSinkFactory: () => new DalamudLogSink(PluginLog))</c>.
/// </summary>
public sealed class ServiceContainer : IDisposable
{
    private readonly object gate = new();
    private readonly List<object> disposables = [];

    private readonly ILogSink? logSinkOverride;
    private readonly Func<ILogSink>? logSinkFactory;
    private readonly Func<string>? profileStorePathFactory;
    private readonly Func<string>? castingPresetsPathFactory;
    private readonly Func<string>? modelsDirFactory;
    private readonly Func<string>? voicesManifestFactory;
    private readonly Func<IReadOnlyDictionary<string, string>>? lexiconEntriesFactory;
    private readonly Func<bool>? removeStutterEnabledFactory;
    private readonly Func<ISpeechQueue>? speechQueueFactory;
    private readonly Func<IReadOnlyCollection<int>?>? enabledChatTypesFactory;
    private readonly Func<bool>? enableAllChatTypesFactory;
    private readonly Func<IReadOnlyList<TriggerSpec>>? triggersFactory;
    private readonly Func<IReadOnlyList<TriggerSpec>>? exclusionsFactory;
    private readonly Func<bool>? pipelineEnabledFactory;
    private readonly Func<bool>? skipMessagesFromYouFactory;
    private readonly Func<bool>? onlyMessagesFromYouFactory;
    private readonly Func<bool>? playerRateLimitEnabledFactory;
    private readonly Func<double>? messagesPerSecondFactory;
    private readonly Func<long>? rateLimiterClockMs;
    private readonly Func<string?>? localPlayerNameFactory;
    private readonly Func<bool>? nameWithSayEnabledFactory;
    private readonly Func<float>? defaultExaggerationFactory;
    private readonly Func<string>? selectedEpFactory;
    private readonly Func<bool>? useFp32LanguageModelFactory;
    private readonly Func<string>? selectedEngineFactory;
    private readonly Func<int>? cpuThreadsFactory;
    private readonly Func<string?>? kokoroVoicesDirFactory;
    private readonly Func<string?>? f5VoicesDirFactory;
    private readonly Func<IEmotionDirector?>? llmDirectorFactory;
    private readonly Func<bool>? nameNpcWithSayFactory;
    private readonly Func<bool>? disallowMultipleSayFactory;
    private readonly Func<bool>? sayPartialNameFactory;
    private readonly Func<FirstOrLastName>? onlySayFirstOrLastNameFactory;
    private readonly Func<bool>? cutsceneActiveFactory;
    private readonly Func<bool>? talkVisibleFactory;

    private readonly Func<bool>? useRaceVoicePresetsFactory;

    private readonly Func<bool>? adHocStyleTagsFactory;

    private readonly Func<string>? styleTagRegexFactory;

    private IVoiceLineDetector? voiceLineDetector;

    private ILogSink? logSink;
    private IProfileStore? profileStore;
    private ICastingPresetStore? castingPresetStore;
    private IModelStore? modelStore;
    private IModelProvisioner? modelProvisioner;
    private ISpeechSynthesizer? speechSynthesizer;
    private RaceVoiceMap? voiceMap;
    private DialogueSessionFactory? dialogueSessions;
    private IEmotionDirector? emotionDirector;
    private ILexicon? lexicon;
    private ISpeechQueue? speechQueue;
    private SpeechRequestHandler? speechHandler;
    private ISpeakerDirectory? speakerDirectory;
    private SpeakerAnnouncer? announcer;
    private FromYouGate? fromYou;
    private TextGate? textGate;
    private ChatChannelGate? chatGate;
    private ConfiguredRateLimiter? rateLimiter;
    private SpeechPipeline? pipeline;

    /// <summary>
    /// <paramref name="logSinkFactory"/> is lazy (evaluated once on first use);
    /// <paramref name="logSinkOverride"/> pins an already-built sink (tests, tools);
    /// <paramref name="profileStorePathFactory"/> supplies the voice-assignments.json path
    /// (ConfigDirectory in-game, temp dirs in tests); <paramref name="modelsDirFactory"/>
    /// supplies the model download directory (ConfigDirectory/models in-game, repo models/
    /// or temp dirs in tests).
    /// </summary>
    public ServiceContainer(
        ILogSink? logSinkOverride = null,
        Func<ILogSink>? logSinkFactory = null,
        Func<string>? castingPresetsPathFactory = null,
        Func<string>? profileStorePathFactory = null,
        Func<string>? modelsDirFactory = null,
        Func<string>? voicesManifestFactory = null,
        Func<IReadOnlyDictionary<string, string>>? lexiconEntriesFactory = null,
        Func<bool>? removeStutterEnabledFactory = null,
        Func<ISpeechQueue>? speechQueueFactory = null,
        // Step 5 pipeline wiring: capture/filter configuration is supplied as live
        // delegates (read-per-call, so in-game config edits apply immediately); missing
        // filter delegates default to permissive passthrough.
        Func<IReadOnlyCollection<int>?>? enabledChatTypesFactory = null,
        Func<bool>? enableAllChatTypesFactory = null,
        Func<IReadOnlyList<TriggerSpec>>? triggersFactory = null,
        Func<IReadOnlyList<TriggerSpec>>? exclusionsFactory = null,
        Func<bool>? pipelineEnabledFactory = null,
        Func<bool>? skipMessagesFromYouFactory = null,
        Func<bool>? onlyMessagesFromYouFactory = null,
        Func<bool>? playerRateLimitEnabledFactory = null,
        Func<double>? messagesPerSecondFactory = null,
        Func<long>? rateLimiterClockMs = null,
        Func<string?>? localPlayerNameFactory = null,
        Func<bool>? nameWithSayEnabledFactory = null,
        Func<bool>? nameNpcWithSayFactory = null,
        Func<bool>? disallowMultipleSayFactory = null,
        Func<bool>? sayPartialNameFactory = null,
        Func<FirstOrLastName>? onlySayFirstOrLastNameFactory = null,
        Func<float>? defaultExaggerationFactory = null,
        Func<string>? selectedEpFactory = null,
        Func<bool>? useFp32LanguageModelFactory = null,
        Func<string>? selectedEngineFactory = null,
        Func<int>? cpuThreadsFactory = null,
        Func<string?>? kokoroVoicesDirFactory = null,
        Func<string?>? f5VoicesDirFactory = null,
        Func<IEmotionDirector?>? llmDirectorFactory = null,
        Func<bool>? cutsceneActiveFactory = null,
        Func<bool>? talkVisibleFactory = null,
        Func<bool>? useRaceVoicePresetsFactory = null,
        Func<bool>? adHocStyleTagsFactory = null,
        Func<string>? styleTagRegexFactory = null)
    {
        this.logSinkOverride = logSinkOverride;
        this.logSinkFactory = logSinkFactory;
        this.profileStorePathFactory = profileStorePathFactory;
        this.castingPresetsPathFactory = castingPresetsPathFactory;
        this.modelsDirFactory = modelsDirFactory;
        this.voicesManifestFactory = voicesManifestFactory;
        this.lexiconEntriesFactory = lexiconEntriesFactory;
        this.removeStutterEnabledFactory = removeStutterEnabledFactory;
        this.speechQueueFactory = speechQueueFactory;
        this.enabledChatTypesFactory = enabledChatTypesFactory;
        this.enableAllChatTypesFactory = enableAllChatTypesFactory;
        this.triggersFactory = triggersFactory;
        this.exclusionsFactory = exclusionsFactory;
        this.pipelineEnabledFactory = pipelineEnabledFactory;
        this.skipMessagesFromYouFactory = skipMessagesFromYouFactory;
        this.onlyMessagesFromYouFactory = onlyMessagesFromYouFactory;
        this.playerRateLimitEnabledFactory = playerRateLimitEnabledFactory;
        this.messagesPerSecondFactory = messagesPerSecondFactory;
        this.rateLimiterClockMs = rateLimiterClockMs;
        this.localPlayerNameFactory = localPlayerNameFactory;
        this.nameWithSayEnabledFactory = nameWithSayEnabledFactory;
        this.nameNpcWithSayFactory = nameNpcWithSayFactory;
        this.disallowMultipleSayFactory = disallowMultipleSayFactory;
        this.sayPartialNameFactory = sayPartialNameFactory;
        this.onlySayFirstOrLastNameFactory = onlySayFirstOrLastNameFactory;
        this.defaultExaggerationFactory = defaultExaggerationFactory;
        this.cutsceneActiveFactory = cutsceneActiveFactory;
        this.selectedEpFactory = selectedEpFactory;
        this.useFp32LanguageModelFactory = useFp32LanguageModelFactory;
        this.selectedEngineFactory = selectedEngineFactory;
        this.cpuThreadsFactory = cpuThreadsFactory;
        this.kokoroVoicesDirFactory = kokoroVoicesDirFactory;
        this.f5VoicesDirFactory = f5VoicesDirFactory;
        this.llmDirectorFactory = llmDirectorFactory;
        this.talkVisibleFactory = talkVisibleFactory;
        this.useRaceVoicePresetsFactory = useRaceVoicePresetsFactory;
        this.adHocStyleTagsFactory = adHocStyleTagsFactory;
        this.styleTagRegexFactory = styleTagRegexFactory;
    }

    /// <summary>Downloaded-model directory (modelsDirFactory or a hard error, like the profile store).</summary>
    public string ModelsDir => this.modelsDirFactory?.Invoke()
        ?? throw new InvalidOperationException(
            "No models directory configured: pass modelsDirFactory (in-game) or wire fakes (tests).");

    /// <summary>On-disk model store over <see cref="ModelsDir"/>.</summary>
    public IModelStore ModelStore
    {
        get
        {
            lock (this.gate)
            {
                // Full catalog: Missing() filters optionals itself, and the shared
                // store also answers size-checked presence for optional assets.
                return this.modelStore ??= this.RegisterDisposable(
                    new FileModelStore(
                        this.ModelsDir,
                        [.. ModelCatalog.Assets.Select(a => a.Asset)]));
            }
        }
    }

    /// <summary>Hugging Face provisioner downloading pinned catalog assets on request.</summary>
    public IModelProvisioner ModelProvisioner
    {
        get
        {
            lock (this.gate)
            {
                return this.modelProvisioner ??= this.RegisterDisposable(
                    new HuggingFaceProvisioner(
                        this.ModelsDir, store: this.ModelStore, log: this.LogSinkUnlocked()));
            }
        }
    }

    /// <summary>
    /// The configured speech engine: "f5" (voice-acting quality, default), "kokoro"
    /// (CPU real-time fallback), or "chatterbox" (legacy cloning). Sessions load lazily
    /// on first use / warm-up.
    /// </summary>
    /// <summary>
    /// Drops the cached synthesizer so the next access rebuilds it from the current
    /// engine/EP selection. The retired instance is disposed on the thread pool, NOT
    /// here: a synchronous dispose while a background line is inside native inference
    /// is the engine-switch AV. The drain bound lives in each adapter's Dispose.
    /// Teardown still disposes it (adapters dispose idempotently).
    /// </summary>
    public void InvalidateSpeechSynthesizer()
    {
        ISpeechSynthesizer? retired;
        lock (this.gate)
        {
            retired = this.speechSynthesizer;
            this.speechSynthesizer = null;
        }

        if (retired is IDisposable disposable)
        {
            this.LogSinkOptional?.Info(
                $"Retiring engine instance {retired.GetType().Name}; dispose continues in background.");
            _ = Task.Run(() =>
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    this.LogSinkOptional?.Warn($"Retired engine dispose failed: {ex.Message}");
                }
            });
        }
    }

    public ISpeechSynthesizer SpeechSynthesizer
    {
        get
        {
            lock (this.gate)
            {
                if (this.speechSynthesizer is { } existing)
                {
                    return existing;
                }

                var threads = this.cpuThreadsFactory?.Invoke();
                var engine = this.selectedEngineFactory?.Invoke() ?? "f5";
                ISpeechSynthesizer created = engine switch
                {
                    "chatterbox" => new ChatterboxSynthesizer(
                        this.ModelsDir,
                        // Convention: the provisioner writes catalog assets flat, so the
                        // bundled fallback clip id "default" resolves to
                        // ModelsDir/default_voice.wav; plugin-installed clips live under
                        // ModelsDir/voices/{id}.wav.
                        voicePathResolver: this.ClippingResolver("default_voice.wav"),
                        executionProvider: this.selectedEpFactory?.Invoke() ?? "auto",
                        // fp32 override replaces the q4 LM session entirely (int4 kernels
                        // and their Zen5/AVX-512 native crashes go with it).
                        languageModelOverride: this.useFp32LanguageModelFactory?.Invoke() == true
                            ? Infrastructure.Onnx.ModelCatalog.LanguageModelFp32FileName
                            : null,
                        intraOpThreads: threads,
                        log: this.LogSinkUnlocked()),
                    "turbo" => Infrastructure.Onnx.ChatterboxSynthesizer.CreateTurbo(
                        this.ModelsDir,
                        voicePathResolver: this.ClippingResolver("turbo-default-voice.wav"),
                        executionProvider: this.selectedEpFactory?.Invoke() ?? "cpu",
                        intraOpThreads: threads,
                        log: this.LogSinkUnlocked()),
                    "kokoro" => new Infrastructure.Kokoro.KokoroSynthesizer(
                        () => this.ModelsDir,
                        () => Math.Max(1, threads ?? 4),
                        this.LogSinkUnlocked(),
                        voicesDirFactory: this.kokoroVoicesDirFactory),
                    _ => new Infrastructure.F5.F5Synthesizer(
                        () => this.ModelsDir,
                        () => Math.Max(1, threads ?? 4),
                        this.LogSinkUnlocked(),
                        voicesDirFactory: this.f5VoicesDirFactory,
                        executionProviderFactory: this.selectedEpFactory),
                };

                this.LogSinkUnlocked().Info(
                    $"Speech engine instance created: {created.GetType().Name} " +
                    $"(engine \"{engine}\", {threads ?? 4} synthesis threads).");
                return this.speechSynthesizer = this.RegisterDisposable(created);
            }

        }
    }

    /// <summary>Race/voice-group slot manifest (voicesManifestFactory or a hard error).</summary>
    public RaceVoiceMap VoiceMap
    {
        get
        {
            lock (this.gate)
            {
                return this.VoiceMapUnlocked();
            }
        }
    }

    /// <summary>Rolling dialogue-context windows feeding the emotion director.</summary>
    public DialogueSessionFactory DialogueSessions
    {
        get
        {
            lock (this.gate)
            {
                return this.dialogueSessions ??= new DialogueSessionFactory();
            }
        }
    }

    /// <summary>
    /// Emotion director: the rules table by default (zero footprint). The plugin's
    /// llmDirectorFactory returns the LLM-backed director only while DirectorEnabled and
    /// its asset exists; null falls back to the rules table.
    /// </summary>
    public IEmotionDirector EmotionDirector
    {
        get
        {
            lock (this.gate)
            {
                return this.emotionDirector ??= this.llmDirectorFactory?.Invoke()
                    ?? RulesEmotionDirector.Instance;
            }
        }
    }

    /// <summary>
    /// Light UI hooks for the command router (Step 7 sets these when the windows exist);
    /// the plugin reads them so commands stay headless-safe without referencing UI types.
    /// </summary>
    public Action? OpenConfigurationUi { get; set; }

    public Action? OpenStylesUi { get; set; }

    /// <summary>User lexicon substitutions (passthrough until configuration supplies entries).</summary>
    public ILexicon Lexicon
    {
        get
        {
            lock (this.gate)
            {
                return this.lexicon ??= this.lexiconEntriesFactory is { } entriesFactory
                    ? new LexiconProcessor(entriesFactory)
                    : LexiconProcessor.Empty;
            }
        }
    }

    /// <summary>
    /// Speech playback serializer: <c>speechQueueFactory</c> when wired, else a no-op queue
    /// with a one-time warning — the plugin must load (and the pipeline must resolve) before
    /// the audio sink lands in a later step; wiring the factory is then a one-line change.
    /// </summary>
    public ISpeechQueue SpeechQueue
    {
        get
        {
            lock (this.gate)
            {
                return this.speechQueue ??= this.RegisterDisposable(
                    this.speechQueueFactory?.Invoke()
                    ?? (ISpeechQueue)new NoopSpeechQueue(this.WarnNoQueueUnlocked));
            }
        }
    }

    private void WarnNoQueueUnlocked() => this.LogSinkOptional?.Warn(
        "No speech queue configured yet; speech playback is disabled until the audio " +
        "sink is wired (pass speechQueueFactory).");

    /// <summary>Speech pipeline: lexicon → stutter removal → director → synthesis → queue.</summary>
    /// <summary>
    /// Reference-clip resolution for the cloning engines (turbo/legacy): profiles carry
    /// Kokoro voice ids, which only exist as files when the user adds clip overrides.
    /// Fall back to the bundled F5 bank clip for that id, then to the engine default —
    /// a missing clip must degrade to the default voice, never error the line.
    /// </summary>
    private Func<string, string?> ClippingResolver(string defaultClipFileName)
    {
        var f5Dir = this.f5VoicesDirFactory?.Invoke();
        return voiceId =>
        {
            if (!string.IsNullOrWhiteSpace(voiceId) && voiceId != "default")
            {
                var userClip = Path.Combine(this.ModelsDir, "voices", $"{voiceId}.wav");
                if (File.Exists(userClip))
                {
                    return userClip;
                }

                if (!string.IsNullOrWhiteSpace(f5Dir))
                {
                    var bundled = Path.Combine(
                        f5Dir, $"{Infrastructure.F5.F5Synthesizer.ClipIdFor(voiceId)}.wav");
                    if (File.Exists(bundled))
                    {
                        return bundled;
                    }
                }
            }

            var defaultClip = Path.Combine(this.ModelsDir, defaultClipFileName);
            if (File.Exists(defaultClip))
            {
                return defaultClip;
            }

            // Plugin-bundled conversion of the pinned default clip ships with the
            // publish output; use it before giving up (mid-reinstall race otherwise
            // hands the engines a path that does not exist).
            var kokoroVoicesDir = this.kokoroVoicesDirFactory?.Invoke();
            if (!string.IsNullOrWhiteSpace(kokoroVoicesDir))
            {
                var bundledDefault = Path.Combine(kokoroVoicesDir, "default_voice.wav");
                if (File.Exists(bundledDefault))
                {
                    return bundledDefault;
                }
            }

            // ChatterboxSynthesizer treats a null resolution as a clean
            // SpeechSynthesisException; a missing clip never errors the line hard.
            return null;
        };
    }

    public SpeechRequestHandler SpeechHandler
    {
        get
        {
            lock (this.gate)
            {
                return this.speechHandler ??= new SpeechRequestHandler(
                    lexicon: this.Lexicon,
                    dialogueSessions: this.DialogueSessions,
                    synthesizer: () => this.SpeechSynthesizer,
                    queue: this.SpeechQueue,
                    profileLookup: this.ResolveProfileUnlocked,
                    directorFactory: () => this.EmotionDirector,
                    removeStutters: this.removeStutterEnabledFactory is null
                        ? null
                        : text => this.removeStutterEnabledFactory() ? StutterRemover.Remove(text) : text,
                    extractStyleTags: this.adHocStyleTagsFactory is null
                        ? null
                        : text => StyleTagExtractor.Extract(
                            text,
                            this.styleTagRegexFactory?.Invoke(),
                            this.adHocStyleTagsFactory()),
                    defaultExaggeration: this.defaultExaggerationFactory,
                    log: this.LogSinkUnlocked());
            }
        }
    }
    // ---- Step 5: capture pipeline (filters are permissive passthrough until configured) ----

    /// <summary>Stable speaker-identity resolution over capture-time hints.</summary>
    public ISpeakerDirectory SpeakerDirectory
    {
        get
        {
            lock (this.gate)
            {
                return this.speakerDirectory ??= new GameObjectSpeakerDirectory();
            }
        }
    }

    /// <summary>"says" prefix policy (EnableNameWithSay / NameNpcWithSay / partial names).</summary>
    public SpeakerAnnouncer Announcer
    {
        get
        {
            lock (this.gate)
            {
                return this.announcer ??= new SpeakerAnnouncer(
                    enableNameWithSay: this.nameWithSayEnabledFactory ?? (() => false),
                    nameNpcWithSay: this.nameNpcWithSayFactory ?? (() => false),
                    disallowMultipleSay: this.disallowMultipleSayFactory ?? (() => false),
                    sayPartialName: this.sayPartialNameFactory ?? (() => false),
                    onlySayFirstOrLastName: this.onlySayFirstOrLastNameFactory ?? (() => FirstOrLastName.First));
            }
        }
    }

    /// <summary>Skip/only-messages-from-you gates over the local player's name.</summary>
    public FromYouGate FromYou
    {
        get
        {
            lock (this.gate)
            {
                return this.fromYou ??= new FromYouGate(
                    skipMessagesFromYou: this.skipMessagesFromYouFactory ?? (() => false),
                    onlyMessagesFromYou: this.onlyMessagesFromYouFactory ?? (() => false),
                    localPlayerName: this.localPlayerNameFactory ?? (() => null));
            }
        }
    }

    /// <summary>Trigger/exclusion gate (an exclusion wins; empty triggers admit all).</summary>
    public TextGate TextGate
    {
        get
        {
            lock (this.gate)
            {
                return this.textGate ??= new TextGate(
                    good: this.triggersFactory ?? (() => []),
                    bad: this.exclusionsFactory ?? (() => []));
            }
        }
    }

    /// <summary>Channel-preset gate over the active preset's enabled channels.</summary>
    public ChatChannelGate ChatGate
    {
        get
        {
            lock (this.gate)
            {
                return this.chatGate ??= new ChatChannelGate(
                    enabledChatTypes: this.enabledChatTypesFactory ?? (() => null),
                    enableAllChatTypes: this.enableAllChatTypesFactory
                        ?? (() => this.enabledChatTypesFactory is null));
            }
        }
    }

    /// <summary>PC-only per-speaker throttle (UsePlayerRateLimiter / MessagesPerSecond).</summary>
    public ConfiguredRateLimiter RateLimiter
    {
        get
        {
            lock (this.gate)
            {
                return this.rateLimiter ??= this.RegisterDisposable(new ConfiguredRateLimiter(
                    shouldRateLimit: this.playerRateLimitEnabledFactory ?? (() => false),
                    messagesPerSecond: this.messagesPerSecondFactory ?? (() => 5d),
                    nowMs: this.rateLimiterClockMs));
            }
        }
    }

    /// <summary>
    /// The composed speech pipeline (merge → dedupe → gates → rate limit → handler).
    /// Capture sources push into <see cref="PipelineSink"/>; cutscene/talk lines feed the
    /// dialogue-context windows through the session id deriver.
    /// </summary>
    public SpeechPipeline Pipeline
    {
        get
        {
            lock (this.gate)
            {
                return this.pipeline ??= this.RegisterDisposable(new SpeechPipeline(
                    captureSources: [],
                    enabled: this.pipelineEnabledFactory ?? (() => true),
                    chatGate: this.ChatGate,
                    textGate: this.TextGate,
                    rateLimiter: this.RateLimiter,
                    resolveSpeaker: this.SpeakerDirectory.Resolve,
                    cutsceneActive: this.cutsceneActiveFactory ?? (() => false),
                    talkVisible: this.talkVisibleFactory ?? (() => false),
                    dialogueSessions: this.DialogueSessions,
                    handler: this.SpeechHandler,
                    log: this.LogSinkOptional));
            }
        }
    }

    /// <summary>Where capture sources push their lines (the pipeline's merged stream head).</summary>
    public PipelineSource<TextEmitEvent> PipelineSink => this.Pipeline.Sink;

    /// <summary>
    /// Raised after the voiced-line courtesy cancels current speech; the plugin polls the
    /// talk-family sources here so the game's own voice line is suppressed (TTT's
    /// AddonPollSource.VoiceLinePlayback round-trip).
    /// </summary>
    public event Action? VoiceLinePlaybackObserved;

    /// <summary>
    /// Subscribes the game's voiced-line detector to the single courtesy cancel path
    /// (<see cref="SpeechPipeline.NotifyVoiceLinePlayback"/> via <see cref="OnVoiceLinePlayback"/>).
    /// Re-wiring replaces a previous detector; <see cref="Dispose"/> unwires.
    /// </summary>
    public void WireVoiceLineDetector(IVoiceLineDetector detector)
    {
        lock (this.gate)
        {
            if (ReferenceEquals(this.voiceLineDetector, detector))
            {
                return;
            }

            if (this.voiceLineDetector is { } previous)
            {
                previous.VoiceLinePlayback -= this.OnVoiceLinePlayback;
            }

            this.voiceLineDetector = detector;
            detector.VoiceLinePlayback += this.OnVoiceLinePlayback;
        }
    }

    /// <summary>Unsubscribes the detector wired by <see cref="WireVoiceLineDetector"/>.</summary>
    public void UnwireVoiceLineDetector()
    {
        lock (this.gate)
        {
            if (this.voiceLineDetector is { } detector)
            {
                this.voiceLineDetector = null;
                detector.VoiceLinePlayback -= this.OnVoiceLinePlayback;
            }
        }
    }

    /// <summary>The game's own voice acting just started: stop current speech, then let
    /// observers re-sample the talk addons so the voiced line is not synthesized.</summary>
    public void OnVoiceLinePlayback()
    {
        // Single cancel path: the pipeline's NotifyVoiceLinePlayback → handler → queue.
        this.Pipeline.NotifyVoiceLinePlayback();
        this.VoiceLinePlaybackObserved?.Invoke();
    }

    /// <summary>
    /// Persistent profile resolution (Step 2 store): group resolved from customize data,
    /// slots from the race map — unknown speakers still resolve via the store's
    /// deterministic fallback. With UseRaceVoicePresets off (TTT default-bucket
    /// semantics), every speaker resolves from the ungendered slot set instead of a
    /// race/gender group; manual overrides still win in the store.
    /// </summary>
    public VoiceProfile ResolveProfile(SpeakerIdentity speaker)
    {
        lock (this.gate)
        {
            return this.ResolveProfileUnlocked(speaker)!;
        }
    }

    private VoiceProfile? ResolveProfileUnlocked(SpeakerIdentity speaker)
    {
        var racePresets = this.useRaceVoicePresetsFactory?.Invoke() ?? true;
        var group = racePresets
            ? VoiceGroupResolver.Resolve(
                speaker.Race, speaker.Tribe, speaker.Sex, speaker.ModelCharaId, this.ModelVoiceMapKeys())
            : VoiceGroup.Ungendered;

        // Beast-tribe/allied-society casting: a model id listed in overridenModelIds.txt
        // with a set key draws from that named set (active or parked) before anything else.
        var modelSet = speaker.ModelCharaId is { } modelId
            && this.ModelVoiceMap().TryGetValue(modelId, out var setKey)
            ? setKey
            : null;
        var map = this.VoiceMapUnlocked();
        var slots = modelSet is { } key
            ? map.SlotsForSet(key)
            : map.SlotsFor(group, racePresets ? speaker.Race : null);

        this.LogSinkUnlocked().Info(
            $"Voice resolution for \"{speaker.Key}\": race={speaker.Race?.ToString() ?? "?"} " +
            $"tribe={speaker.Tribe?.ToString() ?? "?"} sex={speaker.Sex?.ToString() ?? "?"} " +
            $"model={speaker.ModelCharaId?.ToString() ?? "?"} → group {group}" +
            (modelSet is { } activeSet ? $" (model set \"{activeSet}\")" : string.Empty));

        var store = this.ProfileStoreUnlocked();
        var profile = store.GetOrCreate(
            speaker.Key,
            () => slots,
            speaker.Race,
            speaker.Tribe,
            speaker.Sex);

        // Engine migrations (Chatterbox clip ids → Kokoro voice names) retire ids that
        // the store's never-reassign invariant would keep forever; a retired id can
        // never synthesize, so re-derive deterministically from the current bank.
        if (!map.DistinctVoiceIds().Contains(profile.ReferenceVoiceId))
        {
            this.LogSinkUnlocked().Info(
                $"Reassigning \"{speaker.Key}\" from retired voice \"{profile.ReferenceVoiceId}\".");
            store.Remove(speaker.Key);
            profile = store.GetOrCreate(speaker.Key, () => slots, speaker.Race, speaker.Tribe, speaker.Sex)!;
        }

        return profile;
    }

    private IReadOnlyDictionary<int, string>? modelVoiceMap;
    private IReadOnlyDictionary<int, string>? presetModelOverrides;

    /// <summary>
    /// Model id → named voice set. When the active casting preset defines model
    /// overrides, those win; otherwise the embedded overridenModelIds.txt table.
    /// </summary>
    private IReadOnlyDictionary<int, string> ModelVoiceMap()
    {
        var store = this.CastingPresetStoreUnlocked();
        if (store.ActivePresetName != CastingPreset.DefaultPresetName)
        {
            if (this.presetModelOverrides is { } cached)
            {
                return cached;
            }

            try
            {
                var preset = store.Get(store.ActivePresetName);
                if (preset.ModelOverrides.Count > 0)
                {
                    return this.presetModelOverrides ??= preset.ModelOverrides
                        .Where(kv => int.TryParse(kv.Key, out _) && kv.Value.SetKey.Length > 0)
                        .ToDictionary(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value.SetKey);
                }
            }
            catch (CastingPresetException)
            {
                // Unreadable active preset: the voice-map overlay already fell back to
                // the built-in casting; the embedded model table matches it.
            }
        }

        return this.modelVoiceMap ??= Infrastructure.Dalamud.UngenderedModelIds.LoadVoiceMap();
    }

    /// <summary>Model ids that force the Ungendered group (ids that carry a set key also force it, via the resolver's override list).</summary>
    private IReadOnlySet<int>? ModelVoiceMapKeys() => new HashSet<int>(this.ModelVoiceMap().Keys);


    public ILogSink LogSink
    {
        get
        {
            lock (this.gate)
            {
                return this.LogSinkUnlocked();
            }
        }
    }
    private RaceVoiceMap LoadBaseVoiceMapUnlocked() => RaceVoiceMap.FromJson(File.ReadAllText(
        this.voicesManifestFactory?.Invoke()
        ?? throw new InvalidOperationException(
            "No voices manifest configured: pass voicesManifestFactory (in-game) or a temp path (tests).")));

    /// <summary>
    /// The effective voice map: the base manifest, or the manifest overlaid with the
    /// active casting preset (preset sets win per key, variants wholesale, and the
    /// "ungendered" variant row re-points the group's fallback set). Cached like the
    /// bare map was; <see cref="InvalidateVoiceMap"/> drops the cache.
    /// </summary>
    private RaceVoiceMap VoiceMapUnlocked()
    {
        if (this.voiceMap is { } cached)
        {
            return cached;
        }

        var baseMap = this.LoadBaseVoiceMapUnlocked();
        var store = this.CastingPresetStoreUnlocked();
        var active = store.ActivePresetName;
        if (active == CastingPreset.DefaultPresetName)
        {
            return this.voiceMap = baseMap;
        }

        CastingPreset preset;
        try
        {
            preset = store.Get(active);
        }
        catch (CastingPresetException e)
        {
            this.LogSinkUnlocked().Warn($"Active casting preset \"{active}\" is unreadable; using the built-in casting. {e.Message}");
            return this.voiceMap = baseMap;
        }

        var sets = new Dictionary<string, VoiceSlot[]>(baseMap.Sets, StringComparer.Ordinal);
        foreach (var (key, slots) in preset.Sets)
        {
            sets[key] = slots.Select(dto => dto.ToSlot()).ToArray();
        }

        VoiceSlot[]? ResolveAny(string key) =>
            sets.TryGetValue(key, out var activeSet) ? activeSet
            : baseMap.Disabled is { } parked && parked.Sets.TryGetValue(key, out var parkedSet) ? parkedSet
            : null;

        var variants = new Dictionary<string, string>(preset.Variants, StringComparer.Ordinal);
        if (variants.TryGetValue(CastingPreset.UngenderedVariantKey, out var ungenderedKey)
            && ResolveAny(ungenderedKey) is { } ungenderedSlots)
        {
            // Ungendered speakers have no race, so the variant lookup can never fire
            // for them; re-pointing the fallback set is what makes the row live.
            sets[CastingPreset.UngenderedVariantKey] = ungenderedSlots;
        }

        return this.voiceMap = new RaceVoiceMap(sets, variants, baseMap.Disabled);
    }

    /// <summary>Persistent voice-assignment store (JsonProfileStore over voice-assignments.json).</summary>
    public IProfileStore ProfileStore
    {
        get
        {
            lock (this.gate)
            {
                return this.ProfileStoreUnlocked();
            }
        }
    }

    /// <summary>Soft log sink: null when nothing configured (pipeline logging is optional).</summary>
    private ILogSink? LogSinkOptional =>
        this.logSink ??= this.logSinkOverride ?? this.logSinkFactory?.Invoke();

    public void Dispose()
    {
        this.UnwireVoiceLineDetector();
        lock (this.gate)
        {
            foreach (var disposable in this.disposables.OfType<IDisposable>())
            {
                disposable.Dispose();
            }

            this.disposables.Clear();
        }
    }

    /// <summary>Casting preset store (JsonCastingPresetStore over casting-presets.json
    /// next to the profile store by default).</summary>
    public ICastingPresetStore CastingPresetStore
    {
        get
        {
            lock (this.gate)
            {
                return this.CastingPresetStoreUnlocked();
            }
        }
    }

    private ICastingPresetStore CastingPresetStoreUnlocked() =>
        this.castingPresetStore ??= this.RegisterDisposable(new JsonCastingPresetStore(
            this.castingPresetsPathFactory?.Invoke()
            ?? (this.profileStorePathFactory is { } profilePathFactory
                ? Path.Combine(Path.GetDirectoryName(profilePathFactory()) ?? ".", "casting-presets.json")
                : throw new InvalidOperationException(
                    "No casting presets path configured: pass a castingPresetsPathFactory (in-game) or a profileStorePathFactory (tests).")),
            this.LoadBaseVoiceMapUnlocked,
            this.LogSinkUnlocked()));

    /// <summary>
    /// Drops the cached voice map (and any cached preset model overrides) so the next
    /// voice resolution rebuilds from the manifest plus the active casting preset. The
    /// UI calls this after every preset mutation or activation.
    /// </summary>
    public void InvalidateVoiceMap()
    {
        lock (this.gate)
        {
            this.voiceMap = null;
            this.presetModelOverrides = null;
        }
    }

    private ILogSink LogSinkUnlocked() =>
        this.logSink ??= this.RegisterDisposable(
            this.logSinkOverride
            ?? this.logSinkFactory?.Invoke()
            ?? throw new InvalidOperationException(
                "No log sink configured: pass a logSinkFactory (in-game) or logSinkOverride (tests/tools)."));

    private IProfileStore ProfileStoreUnlocked() =>
        this.profileStore ??= this.RegisterDisposable(new JsonProfileStore(
            this.profileStorePathFactory?.Invoke()
            ?? throw new InvalidOperationException(
                "No profile store path configured: pass a profileStorePathFactory (in-game) or wire a fake store (tests)."),
            this.LogSinkUnlocked()));

    private T RegisterDisposable<T>(T port) where T : notnull
    {
        this.disposables.Add(port);
        return port;
    }

    /// <summary>Placeholder queue until the audio sink wires a real one (review round 1):
    /// playback calls are no-ops; the first resolution warns once.</summary>
    private sealed class NoopSpeechQueue : ISpeechQueue
    {
        private readonly Action warnOnce;
        private bool warned;

        public NoopSpeechQueue(Action warnOnce) => this.warnOnce = warnOnce;

        public int Depth => 0;

        public void Enqueue(SpeechItem item) => this.Warn();

        public void CancelCurrent()
        {
        }

        public void Clear()
        {
        }

        private void Warn()
        {
            if (this.warned)
            {
                return;
            }

            this.warned = true;
            this.warnOnce();
        }
    }
}

