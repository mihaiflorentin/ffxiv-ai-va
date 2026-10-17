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
    private readonly TimeSpan? profileSaveDelay;
    private readonly Func<bool>? keepTempoWhenPitchedFactory;
    private readonly Func<bool>? ageAwareCastingFactory;
    private readonly Func<bool>? emotionClassifierEnabledFactory;
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
    private readonly Func<string>? selectedEngineFactory;
    private readonly Func<bool>? streamSentencesFactory;
    private readonly Func<bool>? expressiveEngineFactory;
    private readonly Func<IEngineCatalog>? engineCatalogFactory;
    private readonly Func<IReadOnlyDictionary<string, string>>? remoteExtraFactory;
    private IEngineCatalog? engineCatalog;
    private ISpeechSynthesizer? expressiveSynthesizer;
    private ExpressiveRouter? expressiveRouter;
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
        Func<string>? selectedEngineFactory = null,
        Func<int>? cpuThreadsFactory = null,
        Func<string?>? kokoroVoicesDirFactory = null,
        Func<string?>? f5VoicesDirFactory = null,
        Func<IEmotionDirector?>? llmDirectorFactory = null,
        Func<bool>? cutsceneActiveFactory = null,
        Func<bool>? talkVisibleFactory = null,
        Func<bool>? useRaceVoicePresetsFactory = null,
        Func<bool>? adHocStyleTagsFactory = null,
        Func<string>? styleTagRegexFactory = null,
        TimeSpan? profileSaveDelay = null,
        Func<bool>? keepTempoWhenPitchedFactory = null,
        Func<bool>? ageAwareCastingFactory = null,
        Func<bool>? emotionClassifierEnabledFactory = null,
        Func<bool>? streamSentencesFactory = null,
        Func<bool>? expressiveEngineFactory = null,
        Func<IEngineCatalog>? engineCatalogFactory = null,
        Func<IReadOnlyDictionary<string, string>>? remoteExtraFactory = null)
    {
        this.remoteExtraFactory = remoteExtraFactory;
        this.engineCatalogFactory = engineCatalogFactory;
        this.expressiveEngineFactory = expressiveEngineFactory;
        this.streamSentencesFactory = streamSentencesFactory;
        this.emotionClassifierEnabledFactory = emotionClassifierEnabledFactory;
        this.ageAwareCastingFactory = ageAwareCastingFactory;
        this.keepTempoWhenPitchedFactory = keepTempoWhenPitchedFactory;
        this.profileSaveDelay = profileSaveDelay;
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

    /// <summary>Recently rendered lines; cleared whenever the engine is rebuilt (a new
    /// engine, EP or voice set can change what the same request sounds like).</summary>
    public SynthesisCache SynthesisCache { get; } = new();

    /// <summary>Engine descriptors, alias resolution and download-group ownership.</summary>
    public IEngineCatalog EngineCatalog => this.engineCatalog ??= this.engineCatalogFactory?.Invoke()
        ?? Infrastructure.Engines.EngineCatalog.Instance;

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

        this.SynthesisCache.Clear();
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

    /// <summary>
    /// The configured speech engine: "kokoro" (CPU real-time, default and the fallback for
    /// unknown values), or the parked "f5", "chatterbox" and "turbo". Sessions load lazily
    /// on first use / warm-up.
    /// </summary>
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
                var engine = this.EngineCatalog.Resolve(this.selectedEngineFactory?.Invoke());
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
                        intraOpThreads: threads,
                        log: this.LogSinkUnlocked()),
                    "chatterbox-turbo" => Infrastructure.Onnx.ChatterboxSynthesizer.CreateTurbo(
                        this.ModelsDir,
                        voicePathResolver: this.ClippingResolver("turbo-default-voice.wav"),
                        executionProvider: this.selectedEpFactory?.Invoke() ?? "cpu",
                        intraOpThreads: threads,
                        log: this.LogSinkUnlocked()),
                    "f5" => new Infrastructure.F5.F5Synthesizer(
                        () => this.ModelsDir,
                        () => Math.Max(1, threads ?? 4),
                        this.LogSinkUnlocked(),
                        voicesDirFactory: this.f5VoicesDirFactory,
                        executionProviderFactory: this.selectedEpFactory),
                    "remote" => new Infrastructure.Net.RemoteSynthesizer(
                        this.remoteExtraFactory ?? (() => new Dictionary<string, string>()),
                        this.LogSinkUnlocked()),
                    // A stale or unknown value (old configs) must never silently pick a
                    // 1.4 GB parked engine.
                    _ => new Infrastructure.Kokoro.KokoroSynthesizer(
                        () => this.ModelsDir,
                        () => Math.Max(1, threads ?? 4),
                        this.LogSinkUnlocked(),
                        voicesDirFactory: this.kokoroVoicesDirFactory,
                        keepTempoWhenPitched: this.keepTempoWhenPitchedFactory),
                };

                this.LogSinkUnlocked().Info(
                    $"Speech engine instance created: {created.GetType().Name} " +
                    $"(engine \"{engine}\", {threads ?? 4} synthesis threads).");
                return this.speechSynthesizer = this.RegisterDisposable(created);
            }

        }
    }

    /// <summary>Kokoro-rendered reference clips the expressive engine clones, one per voice and pitch.</summary>
    public const string ExpressiveClipsFolder = "voices-kokoro-ref";

    /// <summary>A neutral, varied passage (~10 s): enough for the expressive engine to
    /// capture a voice's timbre without carrying any particular mood into the clone.</summary>
    private const string ReferencePassage =
        "The morning light spilled across the harbor as the ships came in. Merchants called out " +
        "their wares, and somewhere a bell rang twice. It was, by all accounts, a perfectly ordinary day.";

    /// <summary>True when every required file of the expressive engine (Turbo) is on disk.</summary>
    public bool ExpressiveEngineInstalled =>
        this.modelsDirFactory is not null
        && Infrastructure.Onnx.ModelCatalog.TurboRequiredAssets
            .Where(a => !a.Optional)
            .All(a => File.Exists(Path.Combine(this.ModelsDir, a.FileName)));

    /// <summary>Expressive routing applies: switched on, installed, and Kokoro is the main engine.</summary>
    public bool ExpressiveRoutingActive =>
        this.expressiveEngineFactory?.Invoke() == true
        && this.EngineCatalog.Resolve(this.selectedEngineFactory?.Invoke()) == "kokoro"
        && this.ExpressiveEngineInstalled;

    /// <summary>
    /// The engine every line goes through: the main engine, or the expressive one for
    /// strongly emotional lines when expressive routing is active (see <see cref="ExpressiveRouter"/>).
    /// </summary>
    public ISpeechSynthesizer RoutedSynthesizer
    {
        get
        {
            lock (this.gate)
            {
                return this.expressiveRouter ??= new ExpressiveRouter(
                    primary: () => this.SpeechSynthesizer,
                    expressive: () => this.ExpressiveSynthesizer,
                    prepareVoice: this.PrepareExpressiveVoiceAsync,
                    log: this.LogSinkUnlocked());
            }
        }
    }

    /// <summary>
    /// The secondary expressive engine (fp32 Chatterbox Turbo on CPU), built lazily while
    /// routing is active; its sessions load on the first expressive line. Switching routing
    /// off retires the instance in the background so its memory is returned.
    /// </summary>
    public ISpeechSynthesizer? ExpressiveSynthesizer
    {
        get
        {
            var active = this.ExpressiveRoutingActive;
            ISpeechSynthesizer? retired = null;
            lock (this.gate)
            {
                if (active)
                {
                    if (this.expressiveSynthesizer is null)
                    {
                        var fallbackClips = this.ClippingResolver(Infrastructure.Onnx.ModelCatalog.TurboDefaultVoiceFileName);
                        this.expressiveSynthesizer = Infrastructure.Onnx.ChatterboxSynthesizer.CreateTurbo(
                            this.ModelsDir,
                            // The router hands over an absolute clip path; anything else
                            // resolves like the parked Turbo engine does.
                            voicePathResolver: id => Path.IsPathRooted(id) && File.Exists(id) ? id : fallbackClips(id),
                            executionProvider: "cpu",
                            intraOpThreads: this.cpuThreadsFactory?.Invoke(),
                            log: this.LogSinkUnlocked());
                        this.LogSinkUnlocked().Info("Expressive engine instance created (Turbo, CPU).");
                    }

                    return this.expressiveSynthesizer;
                }

                retired = this.expressiveSynthesizer;
                this.expressiveSynthesizer = null;
            }

            if (retired is IDisposable disposable)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        this.LogSinkOptional?.Warn($"Expressive engine dispose failed: {ex.Message}");
                    }
                });
            }

            return null;
        }
    }

    /// <summary>
    /// The reference clip the expressive engine clones for this request: the user's own clip
    /// for the voice when there is one, else a clip the main (Kokoro) engine renders once per
    /// voice and pitch, so the expressive take keeps the speaker's timbre. Null when no clip
    /// can be made (the line then stays on the main engine).
    /// </summary>
    private async Task<string?> PrepareExpressiveVoiceAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        var voiceId = request.ReferenceVoiceId;
        if (string.IsNullOrWhiteSpace(voiceId) || voiceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var userClip = Path.Combine(this.ModelsDir, "voices", $"{voiceId}.wav");
        if (File.Exists(userClip))
        {
            return userClip;
        }

        var pitchTag = (int)MathF.Round(Math.Clamp(request.Pitch, 0.5f, 1.5f) * 100f);
        var clip = Path.Combine(this.ModelsDir, ExpressiveClipsFolder, $"{voiceId}_p{pitchTag:000}.wav");
        if (File.Exists(clip))
        {
            return clip;
        }

        var main = this.SpeechSynthesizer;
        if (!main.IsReady)
        {
            return null;
        }

        var reference = await main.SynthesizeAsync(
            new SynthesisRequest(voiceId, ReferencePassage, 0.5f, [], Pitch: pitchTag / 100f),
            cancellationToken);
        if (reference.SampleRate != 24000 || reference.Samples.Length < 24000 * 3)
        {
            return null; // the clone needs a few seconds of 24 kHz audio
        }

        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        var partial = clip + ".part";
        Infrastructure.Onnx.WavCodec.WriteMono24k(partial, reference.Samples);
        File.Move(partial, clip, overwrite: true);
        this.LogSinkOptional?.Info($"Rendered expressive reference clip for \"{voiceId}\" (pitch {pitchTag / 100f:0.00}).");
        return clip;
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
                if (this.llmDirectorFactory?.Invoke() is { } injected)
                {
                    return injected;
                }

                // Re-evaluated per line: the classifier takes over as soon as it is enabled
                // and downloaded, and steps back the moment either changes.
                if (this.emotionClassifierEnabledFactory?.Invoke() == true
                    && this.modelsDirFactory is not null
                    && Infrastructure.Onnx.EmotionClassifierDirector.IsInstalled(this.ModelsDir))
                {
                    return this.emotionDirector ??= this.RegisterDisposable(
                        new Infrastructure.Onnx.EmotionClassifierDirector(() => this.ModelsDir, this.LogSinkUnlocked()));
                }

                return RulesEmotionDirector.Instance;
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
                    synthesizer: () => this.RoutedSynthesizer,
                    queue: this.SpeechQueue,
                    profileLookup: this.ResolveProfile,
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
                    log: this.LogSinkUnlocked(),
                    streamSentences: this.streamSentencesFactory,
                    cache: this.SynthesisCache,
                    engineKey: () => this.EngineCatalog.Resolve(this.selectedEngineFactory?.Invoke())
                        + (this.ExpressiveRoutingActive ? "+turbo" : string.Empty));
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
    /// Persistent profile resolution: group resolved from customize data, slots from
    /// the active casting's grid — the first sight picks randomly and the store pins
    /// it forever. Bound beast-tribe model ids route to their tribe's lists first;
    /// with UseRaceVoicePresets off, every speaker resolves from the Unknown row.
    /// Manual overrides still win in the store.
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

        var map = this.VoiceMapUnlocked();

        // Beast-tribe casting: a model id bound in the active preset routes to the
        // tribe's gendered list when the sex byte names a gender with a filled list,
        // else the tribe pool. Empty lists are skipped by the overlay, so a miss here
        // falls through to normal race/group resolution.
        string? beastTribe = null;
        VoiceSlot[]? beastSlots = null;
        if (speaker.ModelCharaId is { } modelId
            && this.ModelVoiceMap().TryGetValue(modelId, out var tribeKey))
        {
            beastTribe = tribeKey;
            var sexKey = speaker.Sex == 1 ? CastingDefaults.BeastBucketKey(tribeKey, BeastVoiceList.Female)
                : speaker.Sex == 0 ? CastingDefaults.BeastBucketKey(tribeKey, BeastVoiceList.Male)
                : null;
            if (sexKey is { } gendered && map.TrySlotsForSet(gendered, out var genderedSlots))
            {
                beastSlots = genderedSlots;
            }
            else if (map.TrySlotsForSet(CastingDefaults.BeastBucketKey(tribeKey, BeastVoiceList.Pool), out var pool))
            {
                beastSlots = pool;
            }
        }

        if (beastTribe is null && speaker.ModelCharaId is > 0 and var unboundModel)
        {
            this.UnboundModels.Record(unboundModel, speaker.DisplayName, DateTimeOffset.UtcNow);
        }

        var slots = beastSlots
            ?? (racePresets ? map.SlotsFor(group, speaker.Race) : map.SlotsFor(VoiceGroup.Ungendered, null));

        this.LogSinkUnlocked().Info(
            $"Voice resolution for \"{speaker.Key}\": race={speaker.Race?.ToString() ?? "?"} " +
            $"tribe={speaker.Tribe?.ToString() ?? "?"} sex={speaker.Sex?.ToString() ?? "?"} " +
            $"model={speaker.ModelCharaId?.ToString() ?? "?"} body={speaker.BodyType?.ToString() ?? "?"} " +
            $"source={speaker.Source} → group {group}" +
            (beastTribe is { } boundTribe ? $" (beast tribe \"{boundTribe}\")" : string.Empty));

        var store = this.ProfileStoreUnlocked();
        var profile = store.GetOrCreate(
            speaker.Key,
            () => slots,
            speaker.Race,
            speaker.Tribe,
            speaker.Sex);

        // Engine migrations (Chatterbox clip ids → Kokoro voice names) retire ids that
        // the store's never-reassign invariant would keep forever; a retired id can
        // never synthesize, so re-derive a fresh random pick from the current bank.
        if (!map.DistinctVoiceIds().Contains(profile.ReferenceVoiceId))
        {
            this.LogSinkUnlocked().Info(
                $"Reassigning \"{speaker.Key}\" from retired voice \"{profile.ReferenceVoiceId}\".");
            store.Remove(speaker.Key);
            profile = store.GetOrCreate(speaker.Key, () => slots, speaker.Race, speaker.Tribe, speaker.Sex)!;
        }

        return this.ageAwareCastingFactory?.Invoke() == false
            ? profile
            : AgeCasting.Apply(profile, speaker.BodyType);
    }

    /// <summary>Non-human models heard this session that no tribe binding claims (casting UI inbox).</summary>
    public UnboundModelLog UnboundModels { get; } = new();

    private IReadOnlyDictionary<int, string>? presetBeastBindings;
    private IReadOnlyDictionary<int, string>? defaultBeastBindings;

    /// <summary>
    /// Model id → beast-tribe key. A user preset's bindings win; while the Default
    /// preset is active, the shipped manifest bindings apply instead.
    /// </summary>
    private IReadOnlyDictionary<int, string> ModelVoiceMap()
    {
        var store = this.CastingPresetStoreUnlocked();
        if (store.ActivePresetName == CastingPreset.DefaultPresetName)
        {
            if (this.defaultBeastBindings is { } cachedDefault)
            {
                return cachedDefault;
            }

            var shipped = new Dictionary<int, string>();
            foreach (var (tribeKey, modelIds) in this.LoadBaseVoiceMapUnlocked().BeastTribeBindings)
            {
                foreach (var modelId in modelIds)
                {
                    shipped[modelId] = tribeKey;
                }
            }

            return this.defaultBeastBindings = shipped;
        }

        if (this.presetBeastBindings is { } cached)
        {
            return cached;
        }

        try
        {
            var preset = store.Get(store.ActivePresetName);
            var bindings = new Dictionary<int, string>();
            foreach (var tribe in preset.BeastTribes)
            {
                foreach (var modelId in tribe.ModelIds)
                {
                    bindings[modelId] = tribe.Key;
                }
            }

            return this.presetBeastBindings = bindings;
        }
        catch (CastingPresetException)
        {
            // Unreadable active preset: the voice-map overlay already fell back to the
            // built-in casting; no tribe bindings apply.
            return this.presetBeastBindings = new Dictionary<int, string>();
        }
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
    /// The effective voice map: the manifest's shape overlaid with the active casting
    /// preset's flat grid (race/gender buckets, the Unknown row, and one bucket per
    /// beast-tribe list; empty lists are skipped so resolution falls through). Default
    /// participates too — its derived shape carries the shipped tribe pools and
    /// bindings. Cached like the bare map was; <see cref="InvalidateVoiceMap"/> drops
    /// the cache.
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
        CastingPreset preset;
        if (active == CastingPreset.DefaultPresetName)
        {
            preset = store.GetDefault();
        }
        else
        {
            try
            {
                preset = store.Get(active);
            }
            catch (CastingPresetException e)
            {
                this.LogSinkUnlocked().Warn($"Active casting preset \"{active}\" is unreadable; using the built-in casting. {e.Message}");
                return this.voiceMap = baseMap;
            }
        }


        var sets = new Dictionary<string, VoiceSlot[]>(StringComparer.Ordinal);
        foreach (var (key, dtos) in preset.Buckets)
        {
            sets[key] = dtos.ToSlotArray();
        }

        foreach (var tribe in preset.BeastTribes)
        {
            foreach (var (list, slots) in new[]
                     {
                         (BeastVoiceList.Pool, tribe.Voices),
                         (BeastVoiceList.Male, tribe.MaleVoices),
                         (BeastVoiceList.Female, tribe.FemaleVoices),
                     })
            {
                if (slots.Count > 0)
                {
                    sets[CastingDefaults.BeastBucketKey(tribe.Key, list)] = slots.ToSlotArray();
                }
            }
        }

        // The Unknown row must always resolve (race-presets-off path and unknown
        // speakers land here); a preset without it falls back to the manifest's.
        if (!sets.ContainsKey(CastingDefaults.UnknownBucketKey))
        {
            sets[CastingDefaults.UnknownBucketKey] = baseMap.SlotsFor(VoiceGroup.Ungendered, null);
        }

        // Legacy fallback aliases (unknown race bytes / group fallbacks without a race).
        sets["male"] = sets[CastingDefaults.UnknownBucketKey];
        sets["female"] = sets[CastingDefaults.UnknownBucketKey];

        var variants = Enumerable.Range(1, 8)
            .SelectMany(race => (string[])[$"{race}|Male", $"{race}|Female"])
            .ToDictionary(key => key, key => key, StringComparer.Ordinal);

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
            (this.expressiveSynthesizer as IDisposable)?.Dispose();
            this.expressiveSynthesizer = null;
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
    /// Drops the cached voice map (and the cached tribe bindings) so the next voice
    /// resolution rebuilds from the manifest plus the active casting preset. The UI
    /// calls this after every preset mutation or activation.
    /// </summary>
    public void InvalidateVoiceMap()
    {
        lock (this.gate)
        {
            this.voiceMap = null;
            this.presetBeastBindings = null;
            this.defaultBeastBindings = null;
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
            this.LogSinkUnlocked(),
            this.profileSaveDelay));

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

