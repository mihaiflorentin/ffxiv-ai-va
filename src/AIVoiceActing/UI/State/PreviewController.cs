namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

public enum PreviewState
{
    Idle,
    Synthesizing,
    Playing,
    Error,
}

/// <summary>
/// Owns the one preview/test utterance the settings window can have in flight:
/// <c>Idle -> Synthesizing -> Playing -> Idle</c>, or <c>Error</c> when rendering fails. Starting
/// a preview cancels the previous one (its render token, and the queue's current item when that
/// one is already playing), so there is a single flight at a time and a stale task can never
/// overwrite the newer state. <see cref="Tick"/> (once per frame) returns Playing to Idle when
/// the queue has drained.
/// </summary>
public sealed class PreviewController
{
    public const string FailureKey = "preview-failed";

    /// <summary>How long Playing may last without the queue ever showing the item before it counts as done.</summary>
    public static readonly TimeSpan PlayGrace = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly Func<ISpeechSynthesizer> synthesizer;
    private readonly ISpeechQueue queue;
    private readonly StatusCenter status;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string> logInfo;
    private readonly Action<string, Exception?> logError;

    private CancellationTokenSource? cts;
    private long generation;
    private volatile PreviewState state;
    private volatile string? key;
    private volatile string? error;
    private DateTimeOffset playingSince;
    private bool sawPlaying;

    public PreviewController(
        Func<ISpeechSynthesizer> synthesizer,
        ISpeechQueue queue,
        StatusCenter status,
        Func<DateTimeOffset>? clock = null,
        Action<string>? logInfo = null,
        Action<string, Exception?>? logError = null)
    {
        this.synthesizer = synthesizer;
        this.queue = queue;
        this.status = status;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.logInfo = logInfo ?? (_ => { });
        this.logError = logError ?? ((_, _) => { });
    }

    public PreviewState State => this.state;

    /// <summary>Caller-chosen id of the current preview (for example a voice id), so a row can show its own spinner.</summary>
    public string? Key => this.key;

    /// <summary>Message of the last failure while <see cref="State"/> is Error.</summary>
    public string? Error => this.error;

    /// <summary>True from the press until the audio has been handed to the queue.</summary>
    public bool Busy => this.state == PreviewState.Synthesizing;

    /// <summary>The direct synthesizer + queue path, the exact tail of the game pipeline.</summary>
    public Task StartDirect(string previewKey, SpeakerIdentity speaker, SynthesisRequest request) =>
        this.Start(previewKey, async ct =>
        {
            var audio = await this.synthesizer().SynthesizeAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            this.queue.Enqueue(new SpeechItem(speaker, request, audio));
        });

    /// <summary>
    /// Runs <paramref name="render"/> (which must enqueue the audio) as the single current
    /// preview. <paramref name="onSettled"/> always runs when this render ends, superseded or not.
    /// </summary>
    public Task Start(string previewKey, Func<CancellationToken, Task> render, Action? onSettled = null)
    {
        long mine;
        CancellationToken token;
        lock (this.gate)
        {
            this.cts?.Cancel();
            if (this.state == PreviewState.Playing)
            {
                this.queue.CancelCurrent();
            }

            mine = ++this.generation;
            this.cts = new CancellationTokenSource();
            token = this.cts.Token;
            this.state = PreviewState.Synthesizing;
            this.key = previewKey;
            this.error = null;
        }

        return this.RunAsync(mine, token, render, onSettled);
    }

    /// <summary>Stops the current preview, whatever phase it is in.</summary>
    public void Cancel()
    {
        lock (this.gate)
        {
            this.generation++;
            this.cts?.Cancel();
            if (this.state == PreviewState.Playing)
            {
                this.queue.CancelCurrent();
            }

            this.state = PreviewState.Idle;
            this.key = null;
        }
    }

    /// <summary>Once per frame: Playing becomes Idle when the queue has drained.</summary>
    public void Tick()
    {
        if (this.state != PreviewState.Playing)
        {
            return;
        }

        lock (this.gate)
        {
            if (this.state != PreviewState.Playing)
            {
                return;
            }

            if (this.queue.NowPlaying is not null || this.queue.Depth > 0)
            {
                this.sawPlaying = true;
                return;
            }

            if (this.sawPlaying || this.clock() - this.playingSince >= PlayGrace)
            {
                this.state = PreviewState.Idle;
                this.key = null;
            }
        }
    }

    private async Task RunAsync(long mine, CancellationToken token, Func<CancellationToken, Task> render, Action? onSettled)
    {
        var started = this.clock();
        try
        {
            await render(token);
            var seconds = (this.clock() - started).TotalSeconds;
            lock (this.gate)
            {
                if (this.generation != mine)
                {
                    return;
                }

                this.state = PreviewState.Playing;
                this.playingSince = this.clock();
                this.sawPlaying = false;
            }

            this.logInfo($"Test line finished in {seconds:0.0} s.");
            this.status.Post(Severity.Info, $"Spoke test line in {seconds:0.0} s.");
        }
        catch (OperationCanceledException)
        {
            lock (this.gate)
            {
                if (this.generation == mine)
                {
                    this.state = PreviewState.Idle;
                    this.key = null;
                }
            }
        }
        catch (Exception ex)
        {
            this.logError($"Speech test failed: {ex.Message}", ex);
            lock (this.gate)
            {
                if (this.generation != mine)
                {
                    return;
                }

                this.state = PreviewState.Error;
                this.error = ex.Message;
            }

            this.status.Post(Severity.Error, "Speech test failed", ex.Message, key: FailureKey);
        }
        finally
        {
            onSettled?.Invoke();
        }
    }
}
