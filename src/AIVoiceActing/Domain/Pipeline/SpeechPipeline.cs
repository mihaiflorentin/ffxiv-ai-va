namespace AIVoiceActing.Domain.Pipeline;

using System.Threading.Channels;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Ports;

/// <summary>
/// The speech pipeline (port of TextToTalk's Observable composition, rebuilt on plain C#
/// events per controller ruling):
/// merge(capture sources) → DistinctUntilChanged (semantic dedupe) → enabled check →
/// channel preset (chat only) → exclusions → triggers (an exclusion wins) → PC-only rate
/// limit → SpeechRequestHandler. Cutscene/Talk lines also feed the rolling context window
/// via <see cref="SessionIdDeriver"/> so the emotion director sees the exchange.
/// The gates run synchronously on the caller (the game's framework thread, where the
/// capture-time state they read is valid); everything after — profile lookup, planning,
/// synthesis — runs on one background worker, so lines are spoken in arrival order and
/// the frame never pays for a JSON save or a cold model load. Each queued line carries a
/// cancellation token: <see cref="CancelAll"/> aborts every pending line, and
/// <see cref="InterruptDialogue"/> aborts pending dialogue (not chat) lines.
/// </summary>
public sealed class SpeechPipeline : IDisposable
{
    private readonly PipelineSource<TextEmitEvent> sink = new();
    private readonly Func<bool> enabled;
    private readonly ChatChannelGate chatGate;
    private readonly TextGate textGate;
    private readonly ConfiguredRateLimiter rateLimiter;
    private readonly Func<SpeakerHint, SpeakerIdentity> resolveSpeaker;
    private readonly Func<bool> cutsceneActive;
    private readonly Func<bool> talkVisible;
    private readonly DialogueSessionFactory dialogueSessions;
    private readonly SpeechRequestHandler handler;
    private readonly ILogSink? log;
    private readonly bool backgroundDispatch;
    private readonly Channel<PendingLine> pending =
        Channel.CreateUnbounded<PendingLine>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task? worker;
    private readonly object cancelGate = new();
    private CancellationTokenSource dialogueLines = new();
    private CancellationTokenSource chatLines = new();

    private sealed record PendingLine(string? SessionId, SpeakerIdentity Speaker, string Text, CancellationToken Token);

    public SpeechPipeline(
        IReadOnlyList<PipelineSource<TextEmitEvent>> captureSources,
        Func<bool> enabled,
        ChatChannelGate chatGate,
        TextGate textGate,
        ConfiguredRateLimiter rateLimiter,
        Func<SpeakerHint, SpeakerIdentity> resolveSpeaker,
        Func<bool> cutsceneActive,
        Func<bool> talkVisible,
        DialogueSessionFactory dialogueSessions,
        SpeechRequestHandler handler,
        ILogSink? log = null,
        bool backgroundDispatch = true)
    {
        this.backgroundDispatch = backgroundDispatch;
        this.enabled = enabled;
        this.chatGate = chatGate;
        this.textGate = textGate;
        this.rateLimiter = rateLimiter;
        this.resolveSpeaker = resolveSpeaker;
        this.cutsceneActive = cutsceneActive;
        this.talkVisible = talkVisible;
        this.dialogueSessions = dialogueSessions;
        this.handler = handler;
        this.log = log;

        var merged = Pipeline.Merge([this.sink, .. captureSources]);
        Pipeline
            .DistinctUntilChanged(merged, TextEmitEventComparer.Instance)
            .Subscribe(this.Handle);

        if (backgroundDispatch)
        {
            this.worker = Task.Run(this.RunWorkerAsync);
        }
    }

    /// <summary>Capture sources push their lines here (the merged stream head).</summary>
    public PipelineSource<TextEmitEvent> Sink => this.sink;

    /// <summary>
    /// The game's own voice acting just started: the line currently being spoken is stale,
    /// so stop it immediately (voiced-cutscene courtesy; the capture sources also re-sample
    /// so the voiced line itself is suppressed).
    /// </summary>
    public void NotifyVoiceLinePlayback() => this.InterruptDialogue();

    /// <summary>
    /// The player advanced the dialogue (or the game's own voice took over): stop the line
    /// being spoken and abort dialogue lines still waiting for, or inside, synthesis. Chat
    /// lines are left alone; the playback queue's stale-line rule still covers them.
    /// </summary>
    public void InterruptDialogue()
    {
        this.handler.CancelCurrent();
        lock (this.cancelGate)
        {
            this.dialogueLines = Renew(this.dialogueLines);
        }
    }

    /// <summary>Aborts every pending line (dialogue and chat) and flushes playback.</summary>
    public void CancelAll()
    {
        lock (this.cancelGate)
        {
            this.dialogueLines = Renew(this.dialogueLines);
            this.chatLines = Renew(this.chatLines);
        }

        this.handler.ClearQueue();
    }

    private static CancellationTokenSource Renew(CancellationTokenSource previous)
    {
        // Not disposed: queued lines still hold its token.
        previous.Cancel();
        return new CancellationTokenSource();
    }

    private void Handle(TextEmitEvent ev)
    {
        // Synchronous gates first (dedupe/gating must not reorder); synthesis runs detached.
        try
        {
            if (this.Admit(ev) is not { } line)
            {
                return;
            }

            if (this.backgroundDispatch)
            {
                this.pending.Writer.TryWrite(line);
            }
            else
            {
                _ = this.SpeakAsync(line);
            }
        }
        catch (Exception ex)
        {
            this.log?.Error("Speech pipeline dispatch failed.", ex);
        }
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var line in this.pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!line.Token.IsCancellationRequested)
            {
                await this.SpeakAsync(line).ConfigureAwait(false);
            }
        }
    }

    private async Task SpeakAsync(PendingLine line)
    {
        try
        {
            await this.handler.SpeakAsync(line.SessionId, line.Speaker, line.Text, line.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Line aborted (text advance / engine teardown); expected.
        }
        catch (Exception ex)
        {
            this.log?.Error("Speech pipeline dispatch failed.", ex);
        }
    }

    private PendingLine? Admit(TextEmitEvent ev)
    {
        if (!this.enabled())
        {
            return null;
        }

        if (ev.Source == TextSource.Chat && !this.chatGate.IsEnabled(ev.ChatType))
        {
            return null;
        }

        // Exclusions are checked first so they win over triggers (TTT Where order).
        if (this.textGate.IsTextBad(ev.Text))
        {
            return null;
        }

        if (!this.textGate.IsTextGood(ev.Text))
        {
            return null;
        }

        var speaker = this.resolveSpeaker(ev.Hint);
        if (this.rateLimiter.TryRateLimit(speaker))
        {
            return null;
        }

        var sessionId = SessionIdDeriver.Derive(
            this.cutsceneActive(), this.talkVisible(), speaker.Key);

        CancellationToken token;
        lock (this.cancelGate)
        {
            token = ev.Source == TextSource.Chat ? this.chatLines.Token : this.dialogueLines.Token;
        }

        return new PendingLine(sessionId, speaker, ev.Text, token);
    }

    public void Dispose()
    {
        // Pipeline composition lives for the plugin's lifetime; the capture sources and
        // the handler's dependencies are disposed by their owner (ServiceContainer).
        this.pending.Writer.TryComplete();
        lock (this.cancelGate)
        {
            this.dialogueLines.Cancel();
            this.chatLines.Cancel();
        }

        // Bounded: an in-flight line observes the cancel at its next await.
        this.worker?.Wait(TimeSpan.FromSeconds(2));
        this.rateLimiter.Dispose();
    }
}
