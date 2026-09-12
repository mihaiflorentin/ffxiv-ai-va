namespace AIVoiceActing.Container;

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

    private ILogSink? logSink;

    /// <summary>
    /// Prefer <paramref name="logSinkFactory"/> (lazy, evaluated once on first use);
    /// <paramref name="logSinkOverride"/> pins an already-built sink (tests, tools).
    /// </summary>
    public ServiceContainer(ILogSink? logSinkOverride = null, Func<ILogSink>? logSinkFactory = null)
    {
        this.logSinkOverride = logSinkOverride;
        this.logSinkFactory = logSinkFactory;
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

    private T RegisterDisposable<T>(T port) where T : notnull
    {
        this.disposables.Add(port);
        return port;
    }
}
