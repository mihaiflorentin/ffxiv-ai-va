namespace AIVoiceActing.Container;

using AIVoiceActing.Infrastructure.Net;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
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

    private ILogSink? logSink;
    private IProfileStore? profileStore;
    private IModelStore? modelStore;
    private IModelProvisioner? modelProvisioner;
    private ISpeechSynthesizer? speechSynthesizer;

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
        Func<string>? modelsDirFactory = null)
    {
        this.logSinkOverride = logSinkOverride;
        this.logSinkFactory = logSinkFactory;
        this.profileStorePathFactory = profileStorePathFactory;
        this.modelsDirFactory = modelsDirFactory;
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
                        voicePathResolver: voiceId => Path.Combine(
                            this.ModelsDir, "voices", $"{voiceId}.wav"),
                        executionProvider: "auto",
                        log: this.LogSinkUnlocked()));
            }
        }
    }

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
