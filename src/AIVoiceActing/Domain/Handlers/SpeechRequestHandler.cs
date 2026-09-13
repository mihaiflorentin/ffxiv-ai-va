namespace AIVoiceActing.Domain.Handlers;

using AIVoiceActing.Ports;

/// <summary>
/// Speech pipeline (driving handler): lexicon → stutter removal (config-gated) →
/// ad-hoc style-tag extraction (config-gated) → emotion director (optional; falls back
/// to the pure rules table when absent) → synthesis (plan exaggeration plus per-speaker
/// bias, clamped) → playback queue. Dalamud-free: every side effect is a port; the
/// composition root wires the adapters.
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
    private readonly Func<string, (string, IReadOnlyList<string>)>? extractStyleTags;
    private readonly Func<float>? defaultExaggeration;
    private readonly ILogSink? log;

    public SpeechRequestHandler(
        ILexicon lexicon,
        DialogueSessionFactory dialogueSessions,
        ISpeechSynthesizer synthesizer,
        ISpeechQueue queue,
        Func<SpeakerIdentity, VoiceProfile?> profileLookup,
        Func<IEmotionDirector?> directorFactory,
        Func<string, string>? removeStutters = null,
        Func<string, (string, IReadOnlyList<string>)>? extractStyleTags = null,
        Func<float>? defaultExaggeration = null,
        ILogSink? log = null)
    {
        this.lexicon = lexicon;
        this.dialogueSessions = dialogueSessions;
        this.synthesizer = synthesizer;
        this.queue = queue;
        this.profileLookup = profileLookup;
        this.directorFactory = directorFactory;
        this.removeStutters = removeStutters;
        this.extractStyleTags = extractStyleTags;
        this.defaultExaggeration = defaultExaggeration;
        this.log = log;
    }

    /// <summary>Stops the line currently being spoken; queued lines are kept.</summary>
    public void CancelCurrent() => this.queue.CancelCurrent();

    /// <summary>
    /// Prepares and speaks one line. The no-profile skip comes before planning so a
    /// profileless speaker never pays for director inference; the rolling context window
    /// fills regardless of engine readiness (the director's context must survive a down
    /// engine). Cancellation propagates into synthesis and aborts the line.
    /// </summary>
    public async Task SpeakAsync(
        string? sessionId,
        SpeakerIdentity speaker,
        string text,
        CancellationToken cancellationToken)
    {
        var profile = this.profileLookup(speaker);
        if (profile is null)
        {
            this.log?.Warn($"No voice profile for \"{speaker.Key}\"; skipping line.");
            return;
        }

        var processed = this.lexicon.Apply(text);
        if (this.removeStutters is not null)
        {
            processed = this.removeStutters(processed);
        }

        IReadOnlyList<string> styleTags = [];
        if (this.extractStyleTags is { } extract)
        {
            (processed, styleTags) = extract(processed);
        }

        var plan = await this.PlanAsync(sessionId ?? "adhoc", speaker, processed, cancellationToken);

        // History excludes the current line (EmotionContext contract): the line lands in
        // the rolling window only after planning, so a context director never sees it twice.
        if (sessionId is { } session)
        {
            this.dialogueSessions.Append(session, new DialogueLine(
                speaker.Key, speaker.DisplayName, text, DateTimeOffset.UtcNow));
        }

        // Extracted ad-hoc directions ride on the plan; the director's tags win on overlap.
        if (styleTags.Count > 0)
        {
            plan = plan with { Tags = MergeTags(plan.Tags, styleTags) };
        }

        if (!this.synthesizer.IsReady)
        {
            this.log?.Warn($"Speech engine not ready ({this.synthesizer.NotReadyReason}); skipping line.");
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

    /// <summary>Director tags keep their order and win duplicates; extracted tags append.</summary>
    private static IReadOnlyList<string> MergeTags(
        IReadOnlyList<string> directorTags,
        IReadOnlyList<string> extractedTags)
    {
        var merged = new List<string>(directorTags);
        foreach (var tag in extractedTags)
        {
            if (!merged.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(tag);
            }
        }

        return merged;
    }

    /// <summary>
    /// Plans the delivery: the context director when one is configured, else the pure
    /// rules table (which ignores history, so the window is not even read). A neutral plan
    /// becomes the configured baseline exaggeration (DefaultExaggeration), so users can
    /// shift the default delivery intensity without touching the emotion table.
    /// </summary>
    private async Task<EmotionPlan> PlanAsync(
        string sessionId,
        SpeakerIdentity speaker,
        string processed,
        CancellationToken cancellationToken)
    {
        var director = this.directorFactory();
        var plan = director is null
            ? EmotionRules.Plan(processed)
            : await director.PlanAsync(
                new EmotionContext(speaker, processed, this.dialogueSessions.GetContext(sessionId)),
                cancellationToken);

        if (this.defaultExaggeration is { } baseline
            && plan.Emotion.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            plan = plan with { Exaggeration = Math.Clamp(baseline(), 0f, 1f) };
        }

        return plan;
    }
}
