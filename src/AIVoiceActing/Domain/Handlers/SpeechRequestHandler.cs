namespace AIVoiceActing.Domain.Handlers;

using AIVoiceActing.Ports;

/// <summary>
/// Speech pipeline (driving handler): lexicon → stutter removal (config-gated) →
/// emotion director (optional; falls back to the pure rules table when absent) →
/// synthesis (plan exaggeration plus per-speaker bias, clamped) → playback queue.
/// Dalamud-free: every side effect is a port; the composition root wires the adapters.
/// </summary>
public sealed class SpeechRequestHandler
{
    private readonly ILexicon lexicon;
    private readonly DialogueSessionFactory dialogueSessions;
    private readonly ISpeechSynthesizer synthesizer;
    private readonly ISpeechQueue queue;
    private readonly Func<SpeakerIdentity, VoiceProfile?> profileLookup;
    private readonly Func<IEmotionDirector?> directorFactory;
    private readonly Func<string, string>? removeStutters;
    private readonly ILogSink? log;

    public SpeechRequestHandler(
        ILexicon lexicon,
        DialogueSessionFactory dialogueSessions,
        ISpeechSynthesizer synthesizer,
        ISpeechQueue queue,
        Func<SpeakerIdentity, VoiceProfile?> profileLookup,
        Func<IEmotionDirector?> directorFactory,
        Func<string, string>? removeStutters = null,
        ILogSink? log = null)
    {
        this.lexicon = lexicon;
        this.dialogueSessions = dialogueSessions;
        this.synthesizer = synthesizer;
        this.queue = queue;
        this.profileLookup = profileLookup;
        this.directorFactory = directorFactory;
        this.removeStutters = removeStutters;
        this.log = log;
    }

    /// <summary>Stops the line currently being spoken; queued lines are kept.</summary>
    public void CancelCurrent() => this.queue.CancelCurrent();

    /// <summary>
    /// Prepares and speaks one line. Skips (with a warning) when the speaker has no voice
    /// profile or the engine is not ready; synthesis failures are logged, never fatal to
    /// the pipeline. Cancellation propagates into synthesis and aborts the line.
    /// </summary>
    public async Task SpeakAsync(
        string sessionId,
        SpeakerIdentity speaker,
        string text,
        CancellationToken cancellationToken)
    {
        if (!this.synthesizer.IsReady)
        {
            this.log?.Warn("Speech engine not ready (models missing?); skipping line.");
            return;
        }

        var processed = this.lexicon.Apply(text);
        if (this.removeStutters is not null)
        {
            processed = this.removeStutters(processed);
        }

        var plan = await this.PlanAsync(sessionId, speaker, processed, cancellationToken);

        var profile = this.profileLookup(speaker);
        if (profile is null)
        {
            this.log?.Warn($"No voice profile for \"{speaker.Key}\"; skipping line.");
            return;
        }

        var request = SpeechRequestMapper.ToRequest(
            plan,
            profile.ReferenceVoiceId,
            processed,
            profile.ExaggerationBias);

        SynthesisResult audio;
        try
        {
            audio = await this.synthesizer.SynthesizeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.log?.Error($"Synthesis failed for \"{speaker.Key}\".", ex);
            return;
        }

        this.queue.Enqueue(new SpeechItem(speaker, request, audio));
    }

    /// <summary>
    /// Plans the delivery: the context director when one is configured, else the pure
    /// rules table (which ignores history, so the window is not even read).
    /// </summary>
    private async Task<EmotionPlan> PlanAsync(
        string sessionId,
        SpeakerIdentity speaker,
        string processed,
        CancellationToken cancellationToken)
    {
        var director = this.directorFactory();
        if (director is null)
        {
            return EmotionRules.Plan(processed);
        }

        var history = this.dialogueSessions.GetContext(sessionId);
        return await director.PlanAsync(new EmotionContext(speaker, processed, history), cancellationToken);
    }
}
