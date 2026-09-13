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
    private readonly Func<ISpeechSynthesizer> synthesizer;

    /// <summary>Live synthesizer for the selected engine (re-resolved every call, so
    /// engine switches apply to in-flight pipelines without a plugin reload).</summary>
    private ISpeechSynthesizer Synth => this.synthesizer();
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
        Func<ISpeechSynthesizer> synthesizer,
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
        // Staleness is measured from ARRIVAL (pre-synthesis): slow engines must not
        // push finished audio for a conversation that already moved on.
        var requestedAtTicks = Environment.TickCount64;
        this.log?.Info($"Speak requested: \"{speaker.Key}\" ({text.Length} chars): {Truncate(text)}");
        var profile = this.profileLookup(speaker);
        if (profile is null)
        {
            this.log?.Warn($"No voice profile for \"{speaker.Key}\"; skipping line.");
            return;
        }

        var (processed, styleTags) = this.PrepareText(text);
        this.log?.Info(
            $"Text prepared for \"{speaker.Key}\": {processed.Length} chars" +
            (styleTags.Count > 0 ? $" (tags: {string.Join(", ", styleTags)})" : string.Empty));

        var plan = await this.PlanAsync(sessionId ?? "adhoc", speaker, processed, cancellationToken);
        this.log?.Info(
            $"Plan for \"{speaker.Key}\": {plan.Emotion} @ {plan.Exaggeration:0.00}" +
            (plan.Tags.Count > 0 ? $" [{string.Join(", ", plan.Tags)}]" : string.Empty));

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

        var synth = this.Synth;
        if (!synth.IsReady)
        {
            this.log?.Warn($"Speech engine not ready ({synth.NotReadyReason}); skipping line for \"{speaker.Key}\".");
            return;
        }

        var request = SpeechRequestMapper.ToRequest(
            plan,
            profile.ReferenceVoiceId,
            processed,
            profile.ExaggerationBias,
            profile.Pitch,
            profile.Speed);

        SynthesisResult audio;
        try
        {
            audio = await synth.SynthesizeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpeechSynthesisEngineDisposedException)
        {
            // The old engine was retired mid-line (engine switch); the Synth property
            // re-resolves the fresh instance, so one retry lands the line.
            this.log?.Info("Engine switched mid-line; retrying on the new engine.");
            try
            {
                audio = await this.Synth.SynthesizeAsync(request, cancellationToken);
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
        }
        catch (Exception ex)
        {
            this.log?.Error($"Synthesis failed for \"{speaker.Key}\".", ex);
            return;
        }

        this.log?.Info(
            $"Queued line for \"{speaker.Key}\": {audio.Samples.Length} samples @ {audio.SampleRate} Hz " +
            $"(voice \"{request.ReferenceVoiceId}\").");
        this.queue.Enqueue(new SpeechItem(speaker, request, audio, requestedAtTicks));
    }

    /// <summary>Log-safe preview of a line: single-line, bounded.</summary>
    private static string Truncate(string text)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }

    /// <summary>
    /// Lexicon → stutter removal (config-gated) → ad-hoc style-tag extraction
    /// (config-gated). One implementation of the game-path text chain; the Test tab's
    /// forced-emotion audition reuses it so what you hear matches what the game says.
    /// </summary>
    public (string Processed, IReadOnlyList<string> Tags) PrepareText(string text)
    {
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

        return (processed, styleTags);
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
