namespace AIVoiceActing.UI.State;

using System.Diagnostics;
using AIVoiceActing.Ports;

/// <summary>
/// Warm-up state of the speech engine, extracted from the settings window: whether a warm-up is
/// running (the shared "loading" state), which engine it is for, and what happened. A superseded
/// warm-up (engine switch) is an info message; a real failure is a sticky error.
/// </summary>
public sealed class EngineSessionModel
{
    public const string FailureKey = "engine-start-failed";

    private readonly object gate = new();
    private readonly StatusCenter status;
    private readonly Action<string> logInfo;
    private readonly Action<string, Exception?> logError;
    private volatile bool warming;
    private volatile string? warmingKey;

    public EngineSessionModel(
        StatusCenter status,
        Action<string>? logInfo = null,
        Action<string, Exception?>? logError = null)
    {
        this.status = status;
        this.logInfo = logInfo ?? (_ => { });
        this.logError = logError ?? ((_, _) => { });
    }

    public bool Warming => this.warming;

    /// <summary>Key of the engine being warmed; null when idle.</summary>
    public string? WarmingKey => this.warmingKey;

    /// <summary>The running (or last) warm-up task; tests await it.</summary>
    public Task? Completion { get; private set; }

    /// <summary>Why the engine cannot speak: its own reason, else the generic download hint.</summary>
    public static string Reason(ISpeechSynthesizer synth) =>
        synth.IsReady || string.IsNullOrWhiteSpace(synth.NotReadyReason)
            ? ModelsTabModel.EngineNotReadyHint
            : synth.NotReadyReason;

    /// <summary>
    /// Warms <paramref name="synth"/> on the thread pool. False when a warm-up is already
    /// running. The caller resolves the synthesizer once so the whole warm-up targets one instance.
    /// </summary>
    public bool StartWarmUp(string engineKey, ISpeechSynthesizer synth)
    {
        lock (this.gate)
        {
            if (this.warming)
            {
                return false;
            }

            this.warming = true;
            this.warmingKey = engineKey;
            this.logInfo($"Engine warm-up started: {engineKey} ({synth.GetType().Name}).");
            this.Completion = Task.Run(() => this.RunAsync(engineKey, synth));
            return true;
        }
    }

    private async Task RunAsync(string engineKey, ISpeechSynthesizer synth)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await synth.WarmUpAsync(CancellationToken.None);
            this.logInfo($"Engine warm-up finished: {engineKey} in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
            this.status.Resolve(FailureKey);
        }
        catch (SpeechSynthesisEngineDisposedException)
        {
            this.logInfo("Engine warm-up superseded by an engine switch.");
            this.status.Post(Severity.Info, "Warm-up superseded by an engine switch.");
        }
        catch (Exception ex)
        {
            this.logError($"Engine start failed for {engineKey}: {ex.Message}", ex);
            this.status.Post(Severity.Error, "Engine start failed", ex.Message, key: FailureKey);
        }
        finally
        {
            lock (this.gate)
            {
                this.warming = false;
                this.warmingKey = null;
            }
        }
    }
}
