namespace AIVoiceActing.Domain.Pipeline;

using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Ports;

/// <summary>
/// The speech pipeline (port of TextToTalk's Observable composition, rebuilt on plain C#
/// events per controller ruling):
/// merge(capture sources) → DistinctUntilChanged (semantic dedupe) → enabled check →
/// channel preset (chat only) → exclusions → triggers (an exclusion wins) → PC-only rate
/// limit → SpeechRequestHandler. Cutscene/Talk lines also feed the rolling context window
/// via <see cref="SessionIdDeriver"/> so the emotion director sees the exchange.
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
        ILogSink? log = null)
    {
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
    }

    /// <summary>Capture sources push their lines here (the merged stream head).</summary>
    public PipelineSource<TextEmitEvent> Sink => this.sink;

    /// <summary>
    /// The game's own voice acting just started: the line currently being spoken is stale,
    /// so stop it immediately (voiced-cutscene courtesy; the capture sources also re-sample
    /// so the voiced line itself is suppressed).
    /// </summary>
    public void NotifyVoiceLinePlayback() => this.handler.CancelCurrent();

    private void Handle(TextEmitEvent ev)
    {
        // Synchronous gates first (dedupe/gating must not reorder); synthesis runs detached.
        try
        {
            _ = this.DispatchAsync(ev);
        }
        catch (Exception ex)
        {
            this.log?.Error("Speech pipeline dispatch failed.", ex);
        }
    }

    private async Task DispatchAsync(TextEmitEvent ev)
    {
        try
        {
            if (!this.enabled())
            {
                return;
            }

            if (ev.Source == TextSource.Chat && !this.chatGate.IsEnabled(ev.ChatType))
            {
                return;
            }

            // Exclusions are checked first so they win over triggers (TTT Where order).
            if (this.textGate.IsTextBad(ev.Text))
            {
                return;
            }

            if (!this.textGate.IsTextGood(ev.Text))
            {
                return;
            }

            var speaker = this.resolveSpeaker(ev.Hint);
            if (this.rateLimiter.TryRateLimit(speaker))
            {
                return;
            }

            var sessionId = SessionIdDeriver.Derive(
                this.cutsceneActive(), this.talkVisible(), speaker.Key);
            if (sessionId is { } session)
            {
                this.dialogueSessions.Append(session, new DialogueLine(
                    speaker.Key, speaker.DisplayName, ev.Text, DateTimeOffset.UtcNow));
            }

            await this.handler.SpeakAsync(
                sessionId ?? "adhoc", speaker, ev.Text, CancellationToken.None);
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

    public void Dispose()
    {
        // Pipeline composition lives for the plugin's lifetime; the capture sources and
        // the handler's dependencies are disposed by their owner (ServiceContainer).
        this.rateLimiter.Dispose();
    }
}
