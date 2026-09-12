namespace AIVoiceActing.Domain.Pipeline;

/// <summary>What drives an addon re-sample (TextToTalk's AddonPollSource).</summary>
public enum AddonPollSource
{
    None,
    FrameworkUpdate,
    VoiceLinePlayback,
}

/// <summary>
/// One addon sample (TextToTalk's AddonTalkState): null speaker and text with no poll
/// source means the addon is closed or hidden. Value equality gives the change detection
/// TextToTalk got from ComponentUpdateState.
/// </summary>
public readonly record struct AddonTalkState(string? Speaker, string? Text, AddonPollSource Source)
{
    public static AddonTalkState Closed => default;

    public bool IsClosed => this.Speaker is null && this.Text is null && this.Source == AddonPollSource.None;
}

/// <summary>The poll's verdict for one sample.</summary>
public enum TalkDecision
{
    /// <summary>The addon closed or hid (end of the exchange).</summary>
    Closed,

    /// <summary>Same speaker and text as the previous passing sample (framework/voice-line double poll).</summary>
    Duplicate,

    /// <summary>The game's own voice acting covers this line and the courtesy option is on.</summary>
    SkipVoiced,

    /// <summary>Emit the line into the pipeline.</summary>
    Speak,
}

/// <param name="Decision">Verdict.</param>
/// <param name="Advanced">The on-screen line moved on — current speech is stale. Fires for
/// every changed non-closed sample (TTT raises OnAdvance before dedupe), so callers can
/// cancel in-flight speech.</param>
/// <param name="Speaker">Raw addon speaker ("" when unavailable).</param>
/// <param name="Text">Punctuation-normalized text.</param>
/// <param name="RawText">Text before normalization.</param>
public sealed record AddonTalkResult(
    TalkDecision Decision,
    bool Advanced,
    string Speaker,
    string Text,
    string RawText);

/// <summary>
/// Pure poll-diff core for a Talk-like addon (the decision part of TextToTalk's
/// AddonTalkHandler.HandleChange / AddonBattleTalkHandler.HandleChange): close detection,
/// advance flag, punctuation normalization, consecutive-sample dedupe, and the voiced-line
/// courtesy skip. The Dalamud source shells only read nodes and emit. TextToTalk notes the
/// dedupe is what suppresses the double invocation that follows a voice-line poll; the
/// ordering (dedupe updates the last-seen pair before the voiced-line skip) is ported
/// exactly.
/// </summary>
public sealed class AddonTalkProcessor
{
    private readonly Func<bool> skipVoicedLines;
    private AddonTalkState lastSample = AddonTalkState.Closed;
    private string? lastSpokenSpeaker;
    private string? lastSpokenText;

    /// <param name="skipVoicedLines">Live config: SkipVoicedQuestText / SkipVoicedBattleText.</param>
    public AddonTalkProcessor(Func<bool> skipVoicedLines)
    {
        this.skipVoicedLines = skipVoicedLines;
    }

    public AddonTalkResult Poll(AddonTalkState sample)
    {
        var changed = !sample.Equals(this.lastSample);
        this.lastSample = sample;

        if (sample.IsClosed)
        {
            return new AddonTalkResult(TalkDecision.Closed, Advanced: false, "", "", "");
        }

        var advanced = changed;
        var speaker = sample.Speaker ?? "";
        var normalized = ChatTextNormalizer.NormalizePunctuation(sample.Text);

        if (this.lastSpokenSpeaker == speaker && this.lastSpokenText == normalized)
        {
            return new AddonTalkResult(TalkDecision.Duplicate, advanced, speaker, normalized, sample.Text ?? "");
        }

        this.lastSpokenSpeaker = speaker;
        this.lastSpokenText = normalized;

        if (sample.Source == AddonPollSource.VoiceLinePlayback && this.skipVoicedLines())
        {
            return new AddonTalkResult(TalkDecision.SkipVoiced, advanced, speaker, normalized, sample.Text ?? "");
        }

        return new AddonTalkResult(TalkDecision.Speak, advanced, speaker, normalized, sample.Text ?? "");
    }
}
