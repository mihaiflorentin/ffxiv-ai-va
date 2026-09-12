namespace AIVoiceActing.Container;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Infrastructure.Net;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Infrastructure.Text;
using AIVoiceActing.Ports;

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
    private readonly Func<string>? modelsDirFactory;
    private readonly Func<string>? voicesManifestFactory;
    private readonly Func<IReadOnlyDictionary<string, string>>? lexiconEntriesFactory;
    private readonly Func<bool>? removeStutterEnabledFactory;
    private readonly Func<ISpeechQueue>? speechQueueFactory;

    private ILogSink? logSink;
    private IProfileStore? profileStore;
    private IModelStore? modelStore;
    private IModelProvisioner? modelProvisioner;
    private ISpeechSynthesizer? speechSynthesizer;
    private RaceVoiceMap? voiceMap;
    private DialogueSessionFactory? dialogueSessions;
    private IEmotionDirector? emotionDirector;
    private ILexicon? lexicon;
    private ISpeechQueue? speechQueue;
    private SpeechRequestHandler? speechHandler;

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
        Func<string>? profileStorePathFactory = null,
        Func<string>? modelsDirFactory = null,
        Func<string>? voicesManifestFactory = null,
        Func<IReadOnlyDictionary<string, string>>? lexiconEntriesFactory = null,
        Func<bool>? removeStutterEnabledFactory = null,
        Func<ISpeechQueue>? speechQueueFactory = null)
    {
        this.logSinkOverride = logSinkOverride;
        this.logSinkFactory = logSinkFactory;
        this.profileStorePathFactory = profileStorePathFactory;
        this.modelsDirFactory = modelsDirFactory;
        this.voicesManifestFactory = voicesManifestFactory;
        this.lexiconEntriesFactory = lexiconEntriesFactory;
        this.removeStutterEnabledFactory = removeStutterEnabledFactory;
        this.speechQueueFactory = speechQueueFactory;
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
                return this.modelStore ??= this.RegisterDisposable(
                    new FileModelStore(this.ModelsDir));
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
                    new HuggingFaceProvisioner(this.ModelsDir, log: this.LogSinkUnlocked()));
            }
        }
    }

    /// <summary>Local Chatterbox synthesizer; sessions load lazily on first synthesis.</summary>
    public ISpeechSynthesizer SpeechSynthesizer
    {
        get
        {
            lock (this.gate)
            {
                return this.speechSynthesizer ??= this.RegisterDisposable(
                    new ChatterboxSynthesizer(
                        this.ModelsDir,
                        // Convention: the provisioner writes catalog assets flat, so the
                        // bundled fallback clip id "default" resolves to
                        // ModelsDir/default_voice.wav; plugin-installed clips live under
                        // ModelsDir/voices/{id}.wav (wired in Steps 5-6).
                        voicePathResolver: voiceId => voiceId == "default"
                            ? Path.Combine(this.ModelsDir, "default_voice.wav")
                            : Path.Combine(this.ModelsDir, "voices", $"{voiceId}.wav"),
                        executionProvider: "auto",
                        log: this.LogSinkUnlocked()));
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
    /// Emotion director: the rules table by default (zero footprint); an LLM-backed
    /// director can slot in behind the same port later without touching the pipeline.
    /// </summary>
    public IEmotionDirector EmotionDirector
    {
        get
        {
            lock (this.gate)
            {
                return this.emotionDirector ??= RulesEmotionDirector.Instance;
            }
        }
    }

    /// <summary>User lexicon substitutions (passthrough until configuration supplies entries).</summary>
    public ILexicon Lexicon
    {
        get
        {
            lock (this.gate)
            {
                return this.lexicon ??= new LexiconProcessor(this.lexiconEntriesFactory?.Invoke());
            }
        }
    }

    /// <summary>Speech playback serializer (speechQueueFactory or a hard error; the audio
    /// sink adapter lands in a later step).</summary>
    public ISpeechQueue SpeechQueue
    {
        get
        {
            lock (this.gate)
            {
                return this.speechQueue ??= this.speechQueueFactory?.Invoke()
                    ?? throw new InvalidOperationException(
                        "No speech queue configured: pass speechQueueFactory (in-game) or wire a fake (tests).");
            }
        }
    }

    /// <summary>Speech pipeline: lexicon → stutter removal → director → synthesis → queue.</summary>
    public SpeechRequestHandler SpeechHandler
    {
        get
        {
            lock (this.gate)
            {
                return this.speechHandler ??= new SpeechRequestHandler(
                    lexicon: this.Lexicon,
                    dialogueSessions: this.DialogueSessions,
                    synthesizer: this.SpeechSynthesizer,
                    queue: this.SpeechQueue,
                    profileLookup: this.ResolveProfileUnlocked,
                    directorFactory: () => this.EmotionDirector,
                    removeStutters: this.removeStutterEnabledFactory is null
                        ? null
                        : text => this.removeStutterEnabledFactory() ? StutterRemover.Remove(text) : text,
                    log: this.LogSinkUnlocked());
            }
        }
    }

    /// <summary>
    /// Persistent profile resolution (Step 2 store): group resolved from customize data,
    /// slots from the race map — unknown speakers still resolve via the store's
    /// deterministic fallback.
    /// </summary>
    private VoiceProfile? ResolveProfileUnlocked(SpeakerIdentity speaker)
    {
        var group = VoiceGroupResolver.Resolve(speaker.Race, speaker.Tribe, speaker.Sex, null, null);
        return this.ProfileStoreUnlocked().GetOrCreate(
            speaker.Key,
            () => this.VoiceMapUnlocked().SlotsFor(group, speaker.Race),
            speaker.Race,
            speaker.Tribe,
            speaker.Sex);
    }

    private RaceVoiceMap VoiceMapUnlocked() =>
        this.voiceMap ??= RaceVoiceMap.FromJson(File.ReadAllText(
            this.voicesManifestFactory?.Invoke()
            ?? throw new InvalidOperationException(
                "No voices manifest configured: pass voicesManifestFactory (in-game) or a temp path (tests).")));


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

    public void Dispose()
    {
        lock (this.gate)
        {
            foreach (var disposable in this.disposables.OfType<IDisposable>())
            {
                disposable.Dispose();
            }

            this.disposables.Clear();
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
}
