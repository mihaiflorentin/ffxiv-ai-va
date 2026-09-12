namespace AIVoiceActing.Container;

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

    private ILogSink? logSink;
    private IProfileStore? profileStore;

    /// <summary>
    /// <paramref name="logSinkFactory"/> is lazy (evaluated once on first use);
    /// <paramref name="logSinkOverride"/> pins an already-built sink (tests, tools);
    /// <paramref name="profileStorePathFactory"/> supplies the voice-assignments.json path
    /// (ConfigDirectory in-game, temp dirs in tests).
    /// </summary>
    public ServiceContainer(
        ILogSink? logSinkOverride = null,
        Func<ILogSink>? logSinkFactory = null,
        Func<string>? profileStorePathFactory = null)
    {
        this.logSinkOverride = logSinkOverride;
        this.logSinkFactory = logSinkFactory;
        this.profileStorePathFactory = profileStorePathFactory;
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
